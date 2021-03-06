#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Perlang.Attributes;
using Perlang.Exceptions;
using Perlang.Interpreter.Immutability;
using Perlang.Interpreter.Internals;
using Perlang.Interpreter.Resolution;
using Perlang.Interpreter.Typing;
using Perlang.Parser;
using Perlang.Stdlib;
using static Perlang.TokenType;
using static Perlang.Utils;

namespace Perlang.Interpreter
{
    /// <summary>
    /// Interpreter for Perlang code.
    ///
    /// Instances of this class are not thread safe; calling <see cref="Eval"/> on multiple threads simultaneously can
    /// lead to race conditions and is not supported.
    /// </summary>
    public class PerlangInterpreter : IInterpreter, Expr.IVisitor<object?>, Stmt.IVisitor<VoidObject>
    {
        private readonly Action<RuntimeError> runtimeErrorHandler;
        private readonly PerlangEnvironment globals = new();
        private readonly IImmutableDictionary<string, Type> superGlobals;

        /// <summary>
        /// Map from referring expression to global binding (variable or function).
        /// </summary>
        private readonly IDictionary<Expr, Binding> globalBindings = new Dictionary<Expr, Binding>();

        /// <summary>
        /// Map from referring expression to local binding (i.e. in a local scope) for variable or function.
        /// </summary>
        private readonly IDictionary<Expr, Binding> localBindings = new Dictionary<Expr, Binding>();

        /// <summary>
        /// A collection of all currently defined global classes (both native/.NET and classes defined in Perlang code.)
        /// </summary>
        private readonly IDictionary<string, object> globalClasses = new Dictionary<string, object>();

        private readonly ImmutableDictionary<string, Type> nativeClasses;
        private readonly Action<string> standardOutputHandler;
        private readonly bool replMode;

        private ImmutableList<Stmt> previousStatements = ImmutableList.Create<Stmt>();
        private IEnvironment currentEnvironment;

        /// <summary>
        /// Initializes a new instance of the <see cref="PerlangInterpreter"/> class.
        /// </summary>
        /// <param name="runtimeErrorHandler">A callback that will be called on runtime errors. Note that after calling
        ///     this handler, the interpreter will abort the script.</param>
        /// <param name="standardOutputHandler">An optional parameter that will receive output printed to
        ///     standard output. If not provided or null, output will be printed to the standard output of the
        ///     running process.</param>
        /// <param name="arguments">An optional list of runtime arguments.</param>
        /// <param name="replMode">A flag indicating whether REPL mode will be active or not. In REPL mode, statements
        /// without semicolons are accepted.</param>
        public PerlangInterpreter(
            Action<RuntimeError> runtimeErrorHandler,
            Action<string>? standardOutputHandler = null,
            IEnumerable<string>? arguments = null,
            bool replMode = false)
        {
            this.runtimeErrorHandler = runtimeErrorHandler;
            this.standardOutputHandler = standardOutputHandler ?? Console.WriteLine;
            this.replMode = replMode;

            var argumentsList = (arguments ?? Array.Empty<string>()).ToImmutableList();

            currentEnvironment = globals;

            superGlobals = CreateSuperGlobals(argumentsList);

            LoadStdlib();
            nativeClasses = RegisterGlobalFunctionsAndClasses();
        }

        private IImmutableDictionary<string, Type> CreateSuperGlobals(ImmutableList<string> argumentsList)
        {
            // Set up the super-global ARGV variable.
            var result = new Dictionary<string, Type>
            {
                { "ARGV", typeof(Argv) }
            }.ToImmutableDictionary();

            // TODO: Returning a value AND modifying the globals like this feels like a code smell. Try to figure out
            // TODO: a more sensible way.
            globals.Define(new Token(VAR, "ARGV", null, -1), new Argv(argumentsList));

            return result;
        }

        private static void LoadStdlib()
        {
            // Because of implicit dependencies, this is not loaded automatically; we must manually load this
            // assembly to ensure all Callables within it are registered in the global namespace.
            Assembly.Load("Perlang.StdLib");
        }

        private ImmutableDictionary<string, Type> RegisterGlobalFunctionsAndClasses()
        {
            RegisterGlobalClasses();

            // We need to make a copy of this at this early stage, when it _only_ contains native classes, so that
            // we can feed it to the Resolver class.
            return globalClasses.ToImmutableDictionary(kvp => kvp.Key, kvp => (Type)kvp.Value);
        }

        /// <summary>
        /// Registers global classes defined in native .NET code.
        /// </summary>
        /// <exception cref="PerlangInterpreterException">Multiple classes with the same name was encountered.</exception>
        private void RegisterGlobalClasses()
        {
            var globalClassesQueryable = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Select(t => new
                {
                    Type = t,
                    ClassAttribute = t.GetCustomAttribute<GlobalClassAttribute>()
                })
                .Where(t => t.ClassAttribute != null);

            foreach (var globalClass in globalClassesQueryable)
            {
                string name = globalClass.ClassAttribute!.Name ?? globalClass.Type.Name;

                if (globals.Get(name) != null)
                {
                    throw new PerlangInterpreterException(
                        $"Attempted to define global class '{name}', but another identifier with the same name already exists"
                    );
                }

                globalClasses[name] = globalClass.Type;
            }
        }

        /// <summary>
        /// Runs the provided source code, in an `eval()`/REPL fashion.
        ///
        /// If provided an expression, returns the result; otherwise, null.
        /// </summary>
        /// <remarks>
        /// Note that variables, methods and classes defined in an invocation to this method will persist to subsequent
        /// invocations. This might seem inconvenient at times, but it makes it possible to implement the Perlang
        /// REPL in a reasonable way.
        /// </remarks>
        /// <param name="source">The source code to a Perlang program (typically a single line of Perlang code).</param>
        /// <param name="scanErrorHandler">A handler for scanner errors.</param>
        /// <param name="parseErrorHandler">A handler for parse errors.</param>
        /// <param name="resolveErrorHandler">A handler for resolve errors.</param>
        /// <param name="typeValidationErrorHandler">A handler for type validation errors.</param>
        /// <param name="immutabilityValidationErrorHandler">A handler for immutability validation errors.</param>
        /// <returns>If the provided source is an expression, the value of the expression (which can be `null`) is
        /// returned. If a runtime error occurs, <see cref="VoidObject.Void"/> is returned. In all other cases, `null`
        /// is returned.</returns>
        public object? Eval(
            string source,
            ScanErrorHandler scanErrorHandler,
            ParseErrorHandler parseErrorHandler,
            ResolveErrorHandler resolveErrorHandler,
            ValidationErrorHandler typeValidationErrorHandler,
            ValidationErrorHandler immutabilityValidationErrorHandler)
        {
            if (String.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            //
            // Scanning phase
            //

            bool hasScanErrors = false;
            var scanner = new Scanner(source, scanError =>
            {
                hasScanErrors = true;
                scanErrorHandler(scanError);
            });

            var tokens = scanner.ScanTokens();

            if (hasScanErrors)
            {
                // Something went wrong as early as the "scan" stage. Abort the rest of the processing.
                return null;
            }

            //
            // Parsing phase
            //

            bool hasParseErrors = false;
            var parser = new PerlangParser(
                tokens,
                parseError =>
                {
                    hasParseErrors = true;
                    parseErrorHandler(parseError);
                },
                allowSemicolonElision: replMode
            );

            object syntax = parser.ParseExpressionOrStatements();

            if (hasParseErrors)
            {
                // One or more parse errors were encountered. They have been reported upstream, so we just abort
                // the evaluation at this stage.
                return null;
            }

            if (syntax is List<Stmt> statements)
            {
                var previousAndNewStatements = previousStatements.Concat(statements).ToImmutableList();

                //
                // Resolving names phase
                //

                bool hasResolveErrors = false;

                var resolver = new Resolver(
                    nativeClasses,
                    superGlobals,
                    AddLocal,
                    AddGlobal,
                    AddGlobalClass,
                    resolveError =>
                    {
                        hasResolveErrors = true;
                        resolveErrorHandler(resolveError);
                    }
                );

                resolver.Resolve(previousAndNewStatements);

                if (hasResolveErrors)
                {
                    // Resolution errors has been reported back to the provided error handler. Nothing more remains
                    // to be done than aborting the evaluation.
                    return null;
                }

                //
                // Type validation
                //

                bool typeValidationFailed = false;

                TypeValidator.Validate(
                    previousAndNewStatements,
                    typeValidationError =>
                    {
                        typeValidationFailed = true;
                        typeValidationErrorHandler(typeValidationError);
                    },
                    GetVariableOrFunctionBinding
                );

                if (typeValidationFailed)
                {
                    return null;
                }

                //
                // Immutability validation
                //

                bool immutabilityValidationFailed = false;

                ImmutabilityValidator.Validate(
                    previousAndNewStatements,
                    immutabilityValidationError =>
                    {
                        immutabilityValidationFailed = true;
                        immutabilityValidationErrorHandler(immutabilityValidationError);
                    },
                    GetVariableOrFunctionBinding
                );

                if (immutabilityValidationFailed)
                {
                    return null;
                }

                // All validation was successful => add these statements to the list of "previous statements". Recording
                // them like this is necessary to be able to declare a variable in one REPL line and refer to it in
                // another.
                previousStatements = previousAndNewStatements.ToImmutableList();

                try
                {
                    Interpret(statements);
                }
                catch (RuntimeError e)
                {
                    runtimeErrorHandler(e);
                    return VoidObject.Void;
                }

                return null;
            }
            else if (syntax is Expr expr)
            {
                // Even though this is an expression, we need to make it a statement here so we can run the various
                // validation steps on the complete program now (all the statements executed up to now + the expression
                // we just received).
                var previousAndNewStatements = previousStatements
                    .Concat(ImmutableList.Create(new Stmt.ExpressionStmt(expr)))
                    .ToImmutableList();

                //
                // Resolving names phase
                //

                bool hasResolveErrors = false;
                var resolver = new Resolver(
                    nativeClasses,
                    superGlobals,
                    AddLocal,
                    AddGlobal,
                    AddGlobalClass,
                    resolveError =>
                    {
                        hasResolveErrors = true;
                        resolveErrorHandler(resolveError);
                    }
                );

                resolver.Resolve(previousAndNewStatements);

                if (hasResolveErrors)
                {
                    // Resolution errors has been reported back to the provided error handler. Nothing more remains
                    // to be done than aborting the evaluation.
                    return null;
                }

                //
                // Type validation
                //

                bool typeValidationFailed = false;

                TypeValidator.Validate(
                    previousAndNewStatements,
                    typeValidationError =>
                    {
                        typeValidationFailed = true;
                        typeValidationErrorHandler(typeValidationError);
                    },
                    GetVariableOrFunctionBinding
                );

                if (typeValidationFailed)
                {
                    return null;
                }

                //
                // Immutability validation
                //

                bool immutabilityValidationFailed = false;

                ImmutabilityValidator.Validate(
                    previousAndNewStatements,
                    immutabilityValidationError =>
                    {
                        immutabilityValidationFailed = true;
                        immutabilityValidationErrorHandler(immutabilityValidationError);
                    },
                    GetVariableOrFunctionBinding
                );

                // All validation was successful, but unlike for statements, there is no need to mutate the
                // previousStatements field in this case. Think about it for a moment. We know that the line being
                // interpreted is an expression, so it _cannot_ have declared any new variable or anything like that
                // (those are only allowed in statements). Hence, we presume that this expression is, if you will,
                // "side-effect-free" in that sense.

                if (immutabilityValidationFailed)
                {
                    return null;
                }

                try
                {
                    return Evaluate(expr);
                }
                catch (RuntimeError e)
                {
                    runtimeErrorHandler(e);
                    return VoidObject.Void;
                }
            }
            else
            {
                throw new IllegalStateException("syntax was neither Expr nor list of Stmt");
            }
        }

        /// <summary>
        /// Parses the provided source code and returns a string representation of the parsed AST.
        /// </summary>
        /// <param name="source">The source code to a Perlang program (typically a single line of Perlang code).</param>
        /// <param name="scanErrorHandler">A handler for scanner errors.</param>
        /// <param name="parseErrorHandler">A handler for parse errors.</param>
        /// <returns>A string representation of the parsed syntax tree for the given Perlang program, or `null` in case
        /// one or more errors occurred.</returns>
        public string? Parse(string source, Action<ScanError> scanErrorHandler, Action<ParseError> parseErrorHandler)
        {
            //
            // Scanning phase
            //

            bool hasScanErrors = false;
            var scanner = new Scanner(source, scanError =>
            {
                hasScanErrors = true;
                scanErrorHandler(scanError);
            });

            var tokens = scanner.ScanTokens();

            if (hasScanErrors)
            {
                // Something went wrong as early as the "scan" stage. Abort the rest of the processing.
                return null;
            }

            //
            // Parsing phase
            //

            bool hasParseErrors = false;
            var parser = new PerlangParser(
                tokens,
                parseError =>
                {
                    hasParseErrors = true;
                    parseErrorHandler(parseError);
                },
                allowSemicolonElision: replMode
            );

            object syntax = parser.ParseExpressionOrStatements();

            if (hasParseErrors)
            {
                // One or more parse errors were encountered. They have been reported upstream, so we just abort
                // the evaluation at this stage.
                return null;
            }

            if (syntax is List<Stmt> statements)
            {
                StringBuilder result = new();

                foreach (Stmt statement in statements)
                {
                    result.Append(AstPrinter.Print(statement));
                }

                return result.ToString();
            }
            else if (syntax is Expr expr)
            {
                return AstPrinter.Print(expr);
            }
            else
            {
                throw new IllegalStateException("syntax was neither Expr nor list of Stmt");
            }
        }

        /// <summary>
        /// Entry-point for interpreting one or more statements.
        /// </summary>
        /// <param name="statements">An enumerator for a collection of statements.</param>
        private void Interpret(IEnumerable<Stmt> statements)
        {
            foreach (Stmt statement in statements)
            {
                try
                {
                    Execute(statement);
                }
                catch (TargetInvocationException ex)
                {
                    // ex.InnerException should always be non-null at this point, but since it is a nullable property,
                    // I guess it's best to take the unexpected into account and presume it can be null... :-)
                    string message = ex.InnerException?.Message ?? ex.Message;

                    // Setting the token to 'null' here is clearly not optimal, but the problem is that we really don't
                    // know what particular source location triggered the error in question.
                    throw new RuntimeError(null, message);
                }
                catch (SystemException ex)
                {
                    throw new RuntimeError(null, ex.Message);
                }
            }
        }

        public object? VisitLiteralExpr(Expr.Literal expr)
        {
            return expr.Value;
        }

        public object? VisitLogicalExpr(Expr.Logical expr)
        {
            object? left = Evaluate(expr.Left);

            if (expr.Operator.Type == OR)
            {
                if (IsTruthy(left))
                {
                    return left;
                }
            }
            else if (expr.Operator.Type == AND)
            {
                if (!IsTruthy(left))
                {
                    return left;
                }
            }
            else
            {
                throw new RuntimeError(expr.Operator, $"Unsupported logical operator: {expr.Operator.Type}");
            }

            return Evaluate(expr.Right);
        }

        public object? VisitUnaryPrefixExpr(Expr.UnaryPrefix expr)
        {
            object? right = Evaluate(expr.Right);

            switch (expr.Operator.Type)
            {
                case BANG:
                    return !IsTruthy(right);

                case MINUS:
                    // Using 'dynamic' here is arguably a bit weird, but like in VisitBinaryExpr(), it simplifies things
                    // significantly. The other option would be to handle all kind of numeric types here individually,
                    // which is clearly doable but a bit more work. For now, the CheckNumberOperand() method is the
                    // guarantee that the dynamic operation will succeed.
                    CheckNumberOperand(expr.Operator, right);
                    return -(dynamic?)right;
            }

            // Unreachable.
            return null;
        }

        public object VisitUnaryPostfixExpr(Expr.UnaryPostfix expr)
        {
            object? left = Evaluate(expr.Left);

            // We do have a check at the parser side also, but this one covers "null" cases.
            if (!IsValidNumberType(left))
            {
                switch (expr.Operator.Type)
                {
                    case PLUS_PLUS:
                        throw new RuntimeError(expr.Operator, $"++ can only be used to increment numbers, not {StringifyType(left)}");

                    case MINUS_MINUS:
                        throw new RuntimeError(expr.Operator, $"-- can only be used to decrement numbers, not {StringifyType(left)}");

                    default:
                        throw new RuntimeError(expr.Operator, $"Unsupported operator encountered: {expr.Operator.Type}");
                }
            }

            // The nullability check has been taken care of by IsValidNumberType() for us.
            dynamic previousValue = left!;
            var variable = (Expr.Identifier)expr.Left;
            object value;

            switch (expr.Operator.Type)
            {
                case PLUS_PLUS:
                    value = previousValue + 1;
                    break;

                case MINUS_MINUS:
                    value = previousValue - 1;
                    break;

                default:
                    throw new RuntimeError(expr.Operator, $"Unsupported operator encountered: {expr.Operator.Type}");
            }

            if (localBindings.TryGetValue(expr, out Binding? binding))
            {
                if (binding is IDistanceAwareBinding distanceAwareBinding)
                {
                    currentEnvironment.AssignAt(distanceAwareBinding.Distance, expr.Name, value);
                }
                else
                {
                    throw new RuntimeError(expr.Operator, $"Unsupported operator '{expr.Operator.Type}' encountered for non-distance-aware binding '{binding}'");
                }
            }
            else
            {
                globals.Assign(variable.Name, value);
            }

            return previousValue;
        }

        public object VisitIdentifierExpr(Expr.Identifier expr)
        {
            return LookUpVariable(expr.Name, expr);
        }

        public object VisitGetExpr(Expr.Get expr)
        {
            object? obj = Evaluate(expr.Object);

            if (obj == null)
            {
                throw new RuntimeError(expr.Name, "Object reference not set to an instance of an object");
            }

            if (expr.Methods.SingleOrDefault() != null)
            {
                return new TargetAndMethodContainer(obj, expr.Methods.Single());
            }
            else
            {
                throw new RuntimeError(expr.Name, "Internal runtime error: Expected expr.Method to be non-null");
            }
        }

        private Binding? GetVariableOrFunctionBinding(Expr expr)
        {
            if (localBindings.ContainsKey(expr))
            {
                return localBindings[expr];
            }

            if (globalBindings.ContainsKey(expr))
            {
                return globalBindings[expr];
            }

            // The variable does not exist, neither in the list of local nor global bindings.
            return null;
        }

        /// <summary>
        /// Gets the value of a variable, in the current scope or any surrounding scopes.
        /// </summary>
        /// <param name="name">The name of the variable.</param>
        /// <param name="identifier">The expression identifying the variable. For example, in `var foo = bar`, `bar` is
        /// the identifier expression.</param>
        /// <returns>The value of the variable.</returns>
        /// <exception cref="RuntimeError">When the binding found is not a <see cref="IDistanceAwareBinding"/>
        /// instance.</exception>
        private object LookUpVariable(Token name, Expr.Identifier identifier)
        {
            if (localBindings.TryGetValue(identifier, out Binding? localBinding))
            {
                if (localBinding is IDistanceAwareBinding distanceAwareBinding)
                {
                    return currentEnvironment.GetAt(distanceAwareBinding.Distance, name.Lexeme);
                }
                else
                {
                    throw new RuntimeError(name, $"Attempting to lookup variable for non-distance-aware binding '{localBinding}'");
                }
            }
            else if (globalClasses.TryGetValue(name.Lexeme, out object? globalClass))
            {
                // TODO: This probably means we could drop Perlang classes from being registered as globals as well.
                return globalClass;
            }
            else
            {
                return globals.Get(name);
            }
        }

        private static void CheckNumberOperand(Token @operator, object? operand)
        {
            if (IsValidNumberType(operand))
            {
                return;
            }

            throw new RuntimeError(@operator, "Operand must be a number.");
        }

        private static void CheckNumberOperands(Token @operator, object? left, object? right)
        {
            if (IsValidNumberType(left) && IsValidNumberType(right))
            {
                return;
            }

            throw new RuntimeError(@operator, $"Operands must be numbers, not {left?.GetType().Name} and {right?.GetType().Name}");
        }

        private static bool IsValidNumberType(object? value)
        {
            if (value == null)
            {
                return false;
            }

            switch (value)
            {
                case SByte _:
                case Int16 _:
                case Int32 _:
                case Int64 _:
                case Byte _:
                case UInt16 _:
                case UInt32 _:
                case UInt64 _:
                case Single _: // i.e. float
                case Double _:
                case BigInteger _:
                    return true;
            }

            return false;
        }

        private static bool IsTruthy(object? @object)
        {
            if (@object == null)
            {
                return false;
            }

            if (@object is bool b)
            {
                return b;
            }

            return true;
        }

        private static bool IsEqual(object? a, object? b)
        {
            // nil is only equal to nil.
            if (a == null && b == null)
            {
                return true;
            }

            if (a == null)
            {
                return false;
            }

            return a.Equals(b);
        }

        public object? VisitGroupingExpr(Expr.Grouping expr)
        {
            return Evaluate(expr.Expression);
        }

        /// <summary>
        /// Entry-point for evaluating a single expression. This method is also recursively called from the methods
        /// implementing <see cref="Expr.IVisitor{TR}"/>.
        /// </summary>
        /// <param name="expr">An expression.</param>
        /// <returns>The evaluated value of the expression. For example, if the expression is "1 + 1", the return value
        /// is the integer "2" and so forth.</returns>
        /// <exception cref="RuntimeError">When a runtime error is encountered while evaluating.</exception>
        private object? Evaluate(Expr expr)
        {
            try
            {
                return expr.Accept(this);
            }
            catch (TargetInvocationException ex)
            {
                Token? token = (expr as ITokenAware)?.Token;

                // ex.InnerException should always be non-null at this point, but since it is a nullable property,
                // I guess it's best to take the unexpected into account and presume it can be null... :-)
                string message = ex.InnerException?.Message ?? ex.Message;

                throw new RuntimeError(token, message);
            }
            catch (SystemException ex)
            {
                Token? token = (expr as ITokenAware)?.Token;

                throw new RuntimeError(token, ex.Message);
            }
        }

        private void Execute(Stmt stmt)
        {
            stmt.Accept(this);
        }

        private void AddGlobal(Binding binding)
        {
            globalBindings[binding.ReferringExpr] = binding;
        }

        private void AddLocal(Binding binding)
        {
            localBindings[binding.ReferringExpr] = binding;
        }

        private void AddGlobalClass(string name, PerlangClass perlangClass)
        {
            globalClasses[name] = perlangClass;
        }

        public void ExecuteBlock(IEnumerable<Stmt> statements, IEnvironment blockEnvironment)
        {
            IEnvironment previousEnvironment = currentEnvironment;

            try
            {
                currentEnvironment = blockEnvironment;

                foreach (Stmt statement in statements)
                {
                    Execute(statement);
                }
            }
            finally
            {
                currentEnvironment = previousEnvironment;
            }
        }

        public VoidObject VisitBlockStmt(Stmt.Block stmt)
        {
            ExecuteBlock(stmt.Statements, new PerlangEnvironment(currentEnvironment));
            return VoidObject.Void;
        }

        public VoidObject VisitClassStmt(Stmt.Class stmt)
        {
            currentEnvironment.Define(stmt.Name, globalClasses[stmt.Name.Lexeme]);
            return VoidObject.Void;
        }

        public VoidObject VisitExpressionStmt(Stmt.ExpressionStmt stmt)
        {
            Evaluate(stmt.Expression);
            return VoidObject.Void;
        }

        public VoidObject VisitFunctionStmt(Stmt.Function stmt)
        {
            var function = new PerlangFunction(stmt, currentEnvironment);
            currentEnvironment.Define(stmt.Name, function);
            return VoidObject.Void;
        }

        public VoidObject VisitIfStmt(Stmt.If stmt)
        {
            if (IsTruthy(Evaluate(stmt.Condition)))
            {
                Execute(stmt.ThenBranch);
            }
            else if (stmt.ElseBranch != null)
            {
                Execute(stmt.ElseBranch);
            }

            return VoidObject.Void;
        }

        public VoidObject VisitPrintStmt(Stmt.Print stmt)
        {
            object? value = Evaluate(stmt.Expression);
            standardOutputHandler(Stringify(value));
            return VoidObject.Void;
        }

        public VoidObject VisitReturnStmt(Stmt.Return stmt)
        {
            object? value = null;

            if (stmt.Value != null)
            {
                value = Evaluate(stmt.Value);
            }

            throw new Return(value);
        }

        public VoidObject VisitVarStmt(Stmt.Var stmt)
        {
            object? value = null;

            if (stmt.Initializer != null)
            {
                value = Evaluate(stmt.Initializer);
            }

            currentEnvironment.Define(stmt.Name, value);
            return VoidObject.Void;
        }

        public VoidObject VisitWhileStmt(Stmt.While stmt)
        {
            while (IsTruthy(Evaluate(stmt.Condition)))
            {
                Execute(stmt.Body);
            }

            return VoidObject.Void;
        }

        public object? VisitEmptyExpr(Expr.Empty expr)
        {
            return null;
        }

        public object? VisitAssignExpr(Expr.Assign expr)
        {
            object? value = Evaluate(expr.Value);

            if (localBindings.TryGetValue(expr, out Binding? binding))
            {
                if (binding is IDistanceAwareBinding distanceAwareBinding)
                {
                    currentEnvironment.AssignAt(distanceAwareBinding.Distance, expr.Name, value);
                }
                else
                {
                    throw new RuntimeError(expr.Name, $"Unsupported variable assignment encountered for non-distance-aware binding '{binding}'");
                }
            }
            else
            {
                globals.Assign(expr.Name, value);
            }

            return value;
        }

        public object? VisitBinaryExpr(Expr.Binary expr)
        {
            object? left = Evaluate(expr.Left);
            object? right = Evaluate(expr.Right);

            // Using 'dynamic' here to avoid excessive complexity, having to support all permutations of
            // comparisons (int16 to int32, int32 to int64, etc etc). Since we validate the numerability of the
            // values first, these should be "safe" in that sense. Performance might not be great but let's live
            // with that until we rewrite the whole Perlang interpreter as an on-demand, statically typed but
            // dynamically compiled language.
            dynamic? leftNumber = left;
            dynamic? rightNumber = right;

            switch (expr.Operator.Type)
            {
                //
                // Comparison operators
                //

                case GREATER:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber > rightNumber;
                case GREATER_EQUAL:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber >= rightNumber;
                case LESS:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber < rightNumber;
                case LESS_EQUAL:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber <= rightNumber;
                case BANG_EQUAL:
                    return !IsEqual(left, right);
                case EQUAL_EQUAL:
                    return IsEqual(left, right);

                //
                // Arithmetic operators
                //

                case MINUS:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber - rightNumber;
                case MINUS_EQUAL:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber - rightNumber;
                case PLUS:
                    if (left is string s1 && right is string s2)
                    {
                        return s1 + s2;
                    }

                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber + rightNumber;
                case PLUS_EQUAL:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber + rightNumber;
                case SLASH:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber / rightNumber;
                case STAR:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber * rightNumber;
                case STAR_STAR:
                    CheckNumberOperands(expr.Operator, left, right);

                    if (leftNumber is float || leftNumber is double || rightNumber is float || rightNumber is double)
                    {
                        return Math.Pow(leftNumber, rightNumber);
                    }
                    else if (rightNumber < 0)
                    {
                        return Math.Pow(leftNumber, rightNumber);
                    }
                    else
                    {
                        // Both values are some form of integers. The BigInteger implementation is more likely to yield
                        // a result that is useful for us in this case.
                        return BigInteger.Pow(leftNumber, rightNumber);
                    }

                case PERCENT:
                    CheckNumberOperands(expr.Operator, left, right);
                    return leftNumber % rightNumber;

                default:
                    throw new RuntimeError(expr.Operator, $"Internal error: Unsupported operator {expr.Operator.Type} in binary expression.");
            }
        }

        public object? VisitCallExpr(Expr.Call expr)
        {
            object? callee = Evaluate(expr.Callee);

            var arguments = new List<object>();

            foreach (Expr argument in expr.Arguments)
            {
                arguments.Add(Evaluate(argument)!);
            }

            switch (callee)
            {
                case ICallable callable:
                    if (arguments.Count != callable.Arity())
                    {
                        throw new RuntimeError(
                            expr.Paren,
                            "Expected " + callable.Arity() + " argument(s) but got " + arguments.Count + "."
                        );
                    }

                    try
                    {
                        return callable.Call(this, arguments);
                    }
                    catch (Exception e)
                    {
                        if (expr.Callee is Expr.Identifier identifier)
                        {
                            throw new RuntimeError(identifier.Name, $"{identifier.Name.Lexeme}: {e.Message}");
                        }
                        else
                        {
                            throw new RuntimeError(expr.Paren, e.Message);
                        }
                    }

                case TargetAndMethodContainer container:
                    if (expr.Callee is Expr.Get)
                    {
                        return container.Method.Invoke(container.Target, arguments.ToArray());
                    }
                    else
                    {
                        throw new RuntimeError(expr.Paren, $"Internal error: Expected Get expression, not {expr.Callee}.");
                    }

                default:
                    throw new RuntimeError(expr.Paren, $"Can only call functions, classes and native methods, not {callee}.");
            }
        }
    }
}
