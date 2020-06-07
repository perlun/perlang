using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Perlang.Interpreter.Extensions;
using Perlang.Interpreter.Resolution;
using static Perlang.TokenType;

namespace Perlang.Interpreter.Typing
{
    /// <summary>
    /// Tree-walker which validates all explicit and implicit type references in an expression tree.
    ///
    /// Each expression is populated with the "aggregate type" of its descendants, recursively. The "aggregate type"
    /// is determined by walking the tree and concluding what the final type to evaluate the expression will be. For
    /// example, adding two integers (1 + 1) will have an aggregate type of "int". Adding
    /// an integer and a float (1 + 1.0) will have an aggregate type of "float", and so forth.
    ///
    /// Adding a smaller type and a larger type (e.g. int and float) will expand the expression to the greater
    /// of the involved types. Note that this can lose some precision when expanding double or float values to
    /// the CLR decimal type, but the order of magnitude is always retained.
    ///
    /// For expression trees where no clear aggregate type can be determined, or where an aggregate type would be
    /// potentially confusing to the user, a result indicating this is returned to the caller. We try to follow the
    /// good old Python rule of "explicit is better than implicit", where relevant.
    /// </summary>
    internal static class TypeValidator
    {
        public static void Validate(IList<Stmt> statements,
            Action<TypeValidationError> typeValidationErrorCallback,
            Func<Expr, Binding> getVariableOrFunctionCallback)
        {
            bool typeResolvingFailed = false;

            var typeResolver = new TypeResolver(getVariableOrFunctionCallback,
                validationError =>
                {
                    typeValidationErrorCallback(validationError);
                    typeResolvingFailed = true;
                });

            try
            {
                typeResolver.Validate(statements);
            }
            catch (TypeValidationError e)
            {
                // Some errors are handled gracefully by the Validate() method, while others cause an exception to
                // be thrown and the rest of the validation to abort. We handle both kinds and invoke the callback
                // in either cause, doing our very best to ensure exceptions are not propagated to the caller.
                typeValidationErrorCallback(e);
                return;
            }

            if (typeResolvingFailed)
            {
                // Something went wrong already at the type resolving stage. Possible causes for this could be
                // references to undefined variables. All errors have been reported to the caller at this point.
                // Since the resolving failed, we must not continue the processing since the expression tree(s) can
                // not be guaranteed to be in a healthy state at this stage. It is quite likely that subsequent
                // exceptions would be caused because of errors which are already reported upstream.
                return;
            }

            // The whole expression tree should be walked by now; any type references still not resolved at this point
            // is a critical error that should fail the type validation. To provide as much information to the user
            // as possible, the full list errors (if any) are reported back to the caller; we don't just stop at the
            // first error encountered. (The compiler could potentially discard information except for the first n
            // errors if desired, though. The key point here is to not discard it at the wrong stage in the pipeline.)
            new TypeValidatorHelper(getVariableOrFunctionCallback, typeValidationErrorCallback)
                .ReportErrors(statements);
        }

        public static void Validate(Expr expr,
            Action<TypeValidationError> typeValidationErrorCallback,
            Func<Expr, Binding> getVariableOrFunctionCallback)
        {
            bool typeResolvingFailed = false;

            var typeResolver = new TypeResolver(getVariableOrFunctionCallback,
                validationError =>
                {
                    typeValidationErrorCallback(validationError);
                    typeResolvingFailed = true;
                });

            try
            {
                typeResolver.Validate(expr);
            }
            catch (TypeValidationError e)
            {
                // Some errors are handled gracefully by the Validate() method, while others cause an exception to
                // be thrown and the rest of the validation to abort. We handle both kinds and invoke the callback
                // in either cause, doing our very best to ensure exceptions are not propagated to the caller.
                typeValidationErrorCallback(e);
                return;
            }

            if (typeResolvingFailed)
            {
                // Something went wrong already at the type resolving stage. Possible causes for this could be
                // references to undefined variables. All errors have been reported to the caller at this point.
                // Since the resolving failed, we must not continue the processing since the expression tree(s) can
                // not be guaranteed to be in a healthy state at this stage. It is quite likely that subsequent
                // exceptions would be caused because of errors which are already reported upstream.
                return;
            }

            // The whole expression tree should be walked by now; any type references still not resolved at this point
            // is a critical error that should fail the type validation. To provide as much information to the user
            // as possible, the full list errors (if any) are reported back to the caller; we don't just stop at the
            // first error encountered. (The compiler could potentially discard information except for the first n
            // errors if desired, though. The key point here is to not discard it at the wrong stage in the pipeline.)
            new TypeValidatorHelper(getVariableOrFunctionCallback, typeValidationErrorCallback)
                .ReportErrors(expr);
        }

        /// <summary>
        /// Class responsible for resolving implicit and explicit type references.
        ///
        /// The class implements the Visitor pattern, using the mechanisms provided by the base class to reduce the
        /// amount of boilerplate code. The tree traversal must be done depth-first, since the resolving for tree nodes
        /// closer to the top are sometimes dependent on child nodes' having their type references already resolved.
        /// </summary>
        private class TypeResolver : VisitorBase
        {
            private readonly Func<Expr, Binding> getVariableOrFunctionCallback;
            private readonly Action<TypeValidationError> typeValidationErrorCallback;

            public TypeResolver(Func<Expr, Binding> getVariableOrFunctionCallback,
                Action<TypeValidationError> typeValidationErrorCallback)
            {
                this.getVariableOrFunctionCallback = getVariableOrFunctionCallback;
                this.typeValidationErrorCallback = typeValidationErrorCallback;
            }

            internal void Validate(IList<Stmt> statements)
            {
                // Walk the tree, resolving explicit and inferred type references to their corresponding CLR types.
                foreach (Stmt statement in statements)
                {
                    Validate(statement);
                }
            }

            internal void Validate(Expr expr)
            {
                expr.Accept(this);
            }

            private void Validate(Stmt stmt)
            {
                stmt.Accept(this);
            }

            //
            // Expr visitors
            //

            public override VoidObject VisitBinaryExpr(Expr.Binary expr)
            {
                // Must traverse the tree first to ensure types get resolved, since the code below relies on it.
                base.VisitBinaryExpr(expr);

                // Determine the type of this expression, to ensure that it can be used in e.g. variable initializers.
                var leftTypeReference = expr.Left.TypeReference;
                var rightTypeReference = expr.Right.TypeReference;

                if (!leftTypeReference.IsResolved || !rightTypeReference.IsResolved)
                {
                    // Something has caused implicit and explicit type resolving to fail. We ignore the expression for
                    // now since previous or subsequent steps will catch these errors.
                    return VoidObject.Void;
                }

                if (!leftTypeReference.ClrType.IsAssignableTo(typeof(IComparable)))
                {
                    throw new PerlangInterpreterException(
                        $"{leftTypeReference} is not comparable and can therefore not be used with the ${expr.Operator} operator"
                    );
                }
                else if (!rightTypeReference.ClrType.IsAssignableTo(typeof(IComparable)))
                {
                    throw new PerlangInterpreterException(
                        $"{leftTypeReference} is not comparable and can therefore not be used with the ${expr.Operator} operator"
                    );
                }

                switch (expr.Operator.Type)
                {
                    case PLUS:
                        if (leftTypeReference.ClrType == typeof(string) ||
                            rightTypeReference.ClrType == typeof(string))
                        {
                            // Special-casing of strings, to allow for string concatenation.
                            expr.TypeReference.ClrType = leftTypeReference.ClrType;

                            return VoidObject.Void;
                        }

                        // goto is indeed evil, but code duplication is an even greater evil.
                        goto STAR;

                    case MINUS:
                    case SLASH:
                    case STAR:
                        STAR:
                        TypeReference typeReference = GreaterType(leftTypeReference, rightTypeReference);

                        if (typeReference == null)
                        {
                            throw new TypeValidationError(
                                expr.Operator,
                                $"Invalid arguments to operator {expr.Operator.Type} specified"
                            );
                        }

                        expr.TypeReference.ClrType = typeReference.ClrType;

                        return VoidObject.Void;

                    case GREATER:
                    case GREATER_EQUAL:
                    case LESS:
                    case LESS_EQUAL:
                    case BANG_EQUAL:
                    case EQUAL_EQUAL:
                        expr.TypeReference.ClrType = TypeReference.Bool.ClrType;
                        return VoidObject.Void;

                    default:
                        throw new TypeValidationError(
                            expr.Operator,
                            $"Internal compiler error: {expr.Operator.Type} not valid for binary expressions"
                        );
                }
            }

            public override VoidObject VisitCallExpr(Expr.Call expr)
            {
                try
                {
                    base.VisitCallExpr(expr);
                }
                catch (NameResolutionError)
                {
                    if (expr.Callee is Expr.Variable variable)
                    {
                        throw new NameResolutionError(variable.Name,
                            $"Attempting to call undefined function '{variable.Name.Lexeme}'");
                    }
                    else
                    {
                        throw;
                    }
                }

                TypeReference typeReference = getVariableOrFunctionCallback(expr)?.TypeReference;

                if (typeReference == null)
                {
                    throw new TypeValidationError(
                        expr.Paren,
                        $"Internal compiler error: Failed to locate type reference for {expr.CalleeToString}"
                    );
                }
                else
                {
                    expr.TypeReference.ClrType = typeReference.ClrType;
                }

                return VoidObject.Void;
            }

            public override VoidObject VisitGroupingExpr(Expr.Grouping expr)
            {
                base.VisitGroupingExpr(expr);

                expr.TypeReference.ClrType = expr.Expression.TypeReference.ClrType;

                return VoidObject.Void;
            }

            public override VoidObject VisitLiteralExpr(Expr.Literal expr)
            {
                base.VisitLiteralExpr(expr);

                if (expr.Value == null)
                {
                    expr.TypeReference.ClrType = typeof(NullObject);
                }
                else
                {
                    expr.TypeReference.ClrType = expr.Value.GetType();
                }

                return VoidObject.Void;
            }

            public override VoidObject VisitUnaryPrefixExpr(Expr.UnaryPrefix expr)
            {
                base.VisitUnaryPrefixExpr(expr);

                expr.TypeReference.ClrType = expr.Right.TypeReference.ClrType;

                return VoidObject.Void;
            }

            public override VoidObject VisitUnaryPostfixExpr(Expr.UnaryPostfix expr)
            {
                base.VisitUnaryPostfixExpr(expr);

                expr.TypeReference.ClrType = expr.Left.TypeReference.ClrType;

                return VoidObject.Void;
            }

            public override VoidObject VisitVariableExpr(Expr.Variable expr)
            {
                TypeReference typeReference = getVariableOrFunctionCallback(expr)?.TypeReference;

                if (typeReference == null)
                {
                    throw new NameResolutionError(expr.Name, $"Undefined variable '{expr.Name.Lexeme}'");
                }

                if (typeReference.ExplicitTypeSpecified && !typeReference.IsResolved)
                {
                    ResolveExplicitTypes(typeReference);
                }

                expr.TypeReference.ClrType = typeReference.ClrType;

                return VoidObject.Void;
            }

            //
            // Stmt visitors
            //

            public override VoidObject VisitFunctionStmt(Stmt.Function stmt)
            {
                if (!stmt.ReturnTypeReference.ExplicitTypeSpecified)
                {
                    // TODO: Remove once https://github.com/perlun/perlang/issues/43 is fully resolved.
                    typeValidationErrorCallback(new TypeValidationError(
                        stmt.Name,
                        $"Inferred typing is not yet supported for function '{stmt.Name.Lexeme}'")
                    );
                }

                if (!stmt.ReturnTypeReference.IsResolved)
                {
                    ResolveExplicitTypes(stmt.ReturnTypeReference);
                }

                foreach (Parameter parameter in stmt.Parameters)
                {
                    if (!parameter.TypeReference.ExplicitTypeSpecified)
                    {
                        // TODO: Remove once https://github.com/perlun/perlang/issues/43 is fully resolved.
                        typeValidationErrorCallback(new TypeValidationError(
                            stmt.Name,
                            $"Inferred typing is not yet supported for function parameter '{parameter}'")
                        );
                    }

                    if (!parameter.TypeReference.IsResolved)
                    {
                        ResolveExplicitTypes(parameter.TypeReference);
                    }

                    if (!parameter.TypeReference.IsResolved)
                    {
                        typeValidationErrorCallback(new TypeValidationError(
                            stmt.Name,
                            $"Internal compiler error: Explicit type reference for '{parameter}' failed to be resolved.")
                        );
                    }
                }

                return base.VisitFunctionStmt(stmt);
            }

            public override VoidObject VisitVarStmt(Stmt.Var stmt)
            {
                base.VisitVarStmt(stmt);

                if (!stmt.TypeReference.IsResolved)
                {
                    ResolveExplicitTypes(stmt.TypeReference);
                }

                if (!stmt.TypeReference.IsResolved &&
                    !stmt.TypeReference.ExplicitTypeSpecified &&
                    stmt.HasInitializer)
                {
                    // An explicit type has not been provided. Try inferring it from the type of value provided.
                    stmt.TypeReference.ClrType = stmt.Initializer.TypeReference.ClrType;
                }

                return VoidObject.Void;
            }

            private static TypeReference GreaterType(TypeReference leftTypeReference, TypeReference rightTypeReference)
            {
                var leftMaxValue = GetMaxValue(leftTypeReference.ClrType);
                var rightMaxValue = GetMaxValue(rightTypeReference.ClrType);

                if (leftMaxValue == null || rightMaxValue == null)
                {
                    return null;
                }

                if (leftMaxValue > rightMaxValue)
                {
                    return leftTypeReference;
                }
                else
                {
                    return rightTypeReference;
                }
            }

            private static decimal? GetMaxValue(Type type)
            {
                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.Empty:
                    case TypeCode.Object:
                    case TypeCode.DBNull:
                    case TypeCode.Char:
                    case TypeCode.Boolean:
                    case TypeCode.DateTime:
                    case TypeCode.String:
                        // The types above are unsuitable for arithmetic operations. We might at some point consider
                        // relaxing this and supporting Ruby-like things, like '*' * 10 to produce '**********'. However,
                        // after having used Ruby for a long time, our conclusion is that such constructs are rarely used
                        // and even though they add a bit of elegance/syntactic sugar to the expressiveness of the
                        // language, the added cost in terms of complexity might not be worth it.
                        return null;

                    case TypeCode.SByte:
                        return SByte.MaxValue;
                    case TypeCode.Byte:
                        return Byte.MaxValue;
                    case TypeCode.Int16:
                        return Int16.MaxValue;
                    case TypeCode.UInt16:
                        return UInt16.MaxValue;
                    case TypeCode.Int32:
                        return Int32.MaxValue;
                    case TypeCode.UInt32:
                        return UInt32.MaxValue;
                    case TypeCode.Int64:
                        return Int64.MaxValue;
                    case TypeCode.UInt64:
                        return UInt64.MaxValue;
                    case TypeCode.Single:
                        return Convert.ToDecimal(Single.MaxValue);
                    case TypeCode.Double:
                        return Convert.ToDecimal(Double.MaxValue);
                    case TypeCode.Decimal:
                        return Decimal.MaxValue;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            private static void ResolveExplicitTypes(TypeReference typeReference)
            {
                if (typeReference.TypeSpecifier == null)
                {
                    // No explicit type was specified. We let the inferred type handling deal with this type
                    return;
                }

                // Initial phase: Resolve a limited set of built-in types. Only short type names are supported;
                // fully qualified type names
                string lexeme = typeReference.TypeSpecifier.Lexeme;

                switch (lexeme)
                {
                    // TODO: Replace these with a dictionary of type names in the currently imported namespaces or similar.
                    // TODO: This is not really a scalable approach. :)
                    case "int":
                    case "Int32":
                        typeReference.ClrType = typeof(int);
                        break;

                    case "string":
                    case "String":
                        typeReference.ClrType = typeof(string);
                        break;

                    case "void":
                        typeReference.ClrType = typeof(void);
                        break;

                    default:
                        // TODO: Add error handling here, based on AggregateTypeDeterminator or something.
                        break;
                }
            }
        }

        /// <summary>
        /// Helper visitor which validates the type references in the given list of statements.
        ///
        /// This visitor works under the assumption that explicit types have been resolved, and implicit types have
        /// been inferred.
        /// </summary>
        private class TypeValidatorHelper : VisitorBase
        {
            private readonly Func<Expr, Binding> getVariableOrFunctionCallback;
            private readonly Action<TypeValidationError> typeValidationErrorCallback;

            internal TypeValidatorHelper(Func<Expr, Binding> getVariableOrFunctionCallback,
                Action<TypeValidationError> typeValidationErrorCallback)
            {
                this.getVariableOrFunctionCallback = getVariableOrFunctionCallback;
                this.typeValidationErrorCallback = typeValidationErrorCallback;
            }

            internal void ReportErrors(IEnumerable<Stmt> statements)
            {
                foreach (Stmt statement in statements)
                {
                    ReportErrors(statement);
                }
            }

            internal void ReportErrors(Expr expr)
            {
                expr.Accept(this);
            }

            private void ReportErrors(Stmt stmt)
            {
                stmt.Accept(this);
            }

            //
            // Expr visitors
            //

            public override VoidObject VisitCallExpr(Expr.Call expr)
            {
                Binding binding = getVariableOrFunctionCallback(expr);

                if (binding == null)
                {
                    typeValidationErrorCallback(
                        new NameResolutionError(expr.Paren,
                            $"Attempting to call undefined function '{expr.CalleeToString}'")
                    );

                    return VoidObject.Void;
                }

                IList<Parameter> parameters;
                string functionName;

                switch (binding)
                {
                    case FunctionBinding functionBinding:
                        Stmt.Function function = functionBinding.Function;

                        if (function == null)
                        {
                            throw new NameResolutionError(expr.Paren,
                                $"Internal compiler error: function for {expr} not expected to be null");
                        }

                        parameters = function.Parameters;
                        functionName = function.Name.Lexeme;
                        break;

                    case NativeBinding nativeBinding:
                        parameters = nativeBinding.Parameters;
                        functionName = nativeBinding.FunctionName;
                        break;

                    default:
                        throw new NameResolutionError(expr.Paren,
                            $"Attempting to call invalid function {binding} using {expr}");
                }

                if (parameters.Count != expr.Arguments.Count)
                {
                    typeValidationErrorCallback(new TypeValidationError(expr.Paren,
                        $"Function '{functionName}' has {parameters.Count} parameter(s) but was called with {expr.Arguments.Count} argument(s)"));
                    return VoidObject.Void;
                }

                for (int i = 0; i < expr.Arguments.Count; i++)
                {
                    Parameter parameter = parameters[i];
                    Expr argument = expr.Arguments[i];

                    if (!argument.TypeReference.IsResolved)
                    {
                        throw new PerlangInterpreterException(
                            $"Internal compiler error: Argument '{argument}' to function {functionName} not resolved");
                    }

                    if (!CanBeCoercedInto(parameter.TypeReference, argument.TypeReference))
                    {
                        if (parameter.Name != null)
                        {
                            typeValidationErrorCallback(new TypeValidationError(argument.TypeReference.TypeSpecifier,
                                $"Cannot pass {argument.TypeReference.ClrType} argument as parameter '{parameter.Name.Lexeme}: {parameter.TypeReference.ClrType}' to {functionName}()"));
                        }
                        else
                        {
                            // Very likely refers to a native method, where method names are not available at this point.
                            typeValidationErrorCallback(new TypeValidationError(argument.TypeReference.TypeSpecifier,
                                $"Cannot pass {argument.TypeReference.ClrType} argument as {parameter.TypeReference.ClrType} parameter to {functionName}()"));
                        }
                    }
                }

                return VoidObject.Void;
            }

            //
            // Stmt visitors
            //

            public override VoidObject VisitFunctionStmt(Stmt.Function stmt)
            {
                if (!stmt.ReturnTypeReference.IsResolved)
                {
                    // Note: this is a bit duplicated; we also have a check in TypeResolver for this. I considered
                    // de-duplicating it but it feels dangerous, since we need to remember to edit this when we
                    // add support for function return type inference.
                    if (!stmt.ReturnTypeReference.ExplicitTypeSpecified)
                    {
                        // TODO: Remove once https://github.com/perlun/perlang/issues/43 is fully resolved.
                        typeValidationErrorCallback(new TypeValidationError(
                            stmt.Name,
                            $"Inferred typing is not yet supported for function '{stmt.Name.Lexeme}'")
                        );
                    }
                    else
                    {
                        typeValidationErrorCallback(new TypeValidationError(stmt.ReturnTypeReference.TypeSpecifier,
                            $"Type not found: {stmt.ReturnTypeReference.TypeSpecifier.Lexeme}"));
                    }
                }

                foreach (Parameter parameter in stmt.Parameters)
                {
                    if (parameter.TypeSpecifier == null)
                    {
                        // TODO: Remove once https://github.com/perlun/perlang/issues/43 is fully resolved.
                        typeValidationErrorCallback(new TypeValidationError(
                            stmt.Name,
                            $"Inferred typing is not yet supported for parameter '{parameter.Name.Lexeme}' to function '{stmt.Name.Lexeme}'")
                        );
                    }
                }

                return base.VisitFunctionStmt(stmt);
            }

            public override VoidObject VisitReturnStmt(Stmt.Return stmt)
            {
                bool sanityCheckFailed = false;

                if (stmt.Value != null)
                {
                    if (stmt.Value.TypeReference == null)
                    {
                        typeValidationErrorCallback(new TypeValidationError(stmt.Keyword,
                            $"Internal compiler error: return Stmt missing a TypeReference"));

                        sanityCheckFailed = true;
                    }
                    else if (!stmt.Value.TypeReference.IsResolved)
                    {
                        typeValidationErrorCallback(new TypeValidationError(stmt.Keyword,
                            $"Internal compiler error: return {stmt.Value} inference has not been attempted"));

                        sanityCheckFailed = true;
                    }
                }
                else
                {
                    // No return value - ensure that the return type of the current function is 'void'.
                }

                if (sanityCheckFailed)
                {
                    return VoidObject.Void;
                }

                // TODO: Validate that return type in stmt.TypeReference matches that of the associated function.

                return VoidObject.Void;
            }

            public override VoidObject VisitVarStmt(Stmt.Var stmt)
            {
                bool sanityCheckFailed = false;

                // Sanity check the input to ensure that we don't get NullReferenceExceptions later on
                if (stmt.TypeReference == null)
                {
                    typeValidationErrorCallback(new TypeValidationError(stmt.Name,
                        $"Internal compiler error: {stmt.Name.Lexeme} is missing a TypeReference"));

                    sanityCheckFailed = true;
                }

                if (stmt.Initializer != null)
                {
                    if (stmt.Initializer.TypeReference == null)
                    {
                        typeValidationErrorCallback(new TypeValidationError(stmt.Name,
                            $"Internal compiler error: var {stmt.Name.Lexeme} initializer {stmt.Initializer} missing a TypeReference"));

                        sanityCheckFailed = true;
                    }
                    else if (!stmt.Initializer.TypeReference.IsResolved)
                    {
                        typeValidationErrorCallback(new TypeValidationError(stmt.Name,
                            $"Internal compiler error: var {stmt.Name.Lexeme} initializer {stmt.Initializer} inference has not been attempted"));

                        sanityCheckFailed = true;
                    }
                }

                if (sanityCheckFailed)
                {
                    return VoidObject.Void;
                }

                if (stmt.TypeReference.IsResolved)
                {
                    if (!CanBeCoercedInto(stmt.TypeReference, stmt.Initializer.TypeReference))
                    {
                        typeValidationErrorCallback(new TypeValidationError(stmt.TypeReference.TypeSpecifier,
                            $"Cannot assign {stmt.Initializer.TypeReference.ClrType.Name} value to {stmt.TypeReference.ClrType.Name}"));
                    }
                }
                else
                {
                    if (stmt.Initializer == null)
                    {
                        typeValidationErrorCallback(new TypeValidationError(null,
                            $"Type inference for variable '{stmt.Name.Lexeme}' cannot be performed when initializer is not specified. " +
                            "Either provide an initializer, or specify the type explicitly."));
                    }
                    else if (!stmt.TypeReference.ExplicitTypeSpecified)
                    {
                        // FIXME: Let's see if we'll ever go into this branch. If we don't have a test that reproduces
                        // this once all tests are green, we should consider wiping it from the face off the earth.
                        typeValidationErrorCallback(new TypeValidationError(null,
                            $"Failed to infer type for variable '{stmt.Name.Lexeme}' from usage. Try specifying the type explicitly."));
                    }
                    else
                    {
                        typeValidationErrorCallback(new TypeValidationError(stmt.TypeReference.TypeSpecifier,
                            $"Type not found: {stmt.TypeReference.TypeSpecifier.Lexeme}"));
                    }
                }

                return VoidObject.Void;
            }

            /// <summary>
            /// Determines if a value of <paramref name="sourceTypeReference"/> can be coerced into
            /// <paramref name="targetTypeReference"/>.
            ///
            /// The <![CDATA[source]]> and <![CDATA[target]]> concepts are important here. Sometimes values can
            /// be coerced in one direction but not the other. For example, an int can be coerced to a long, but not
            /// the other way around (without an explicit type cast). The same goes for unsigned integer types; they
            /// can not be coerced to their signed counterpart (uint -> int), but they can be coerced to a larger
            /// signed type if available.
            /// </summary>
            /// <param name="targetTypeReference">a reference to the target type</param>
            /// <param name="sourceTypeReference">a reference to the source type</param>
            /// <returns>true if a source value can be coerced into the target type, false otherwise</returns>
            private static bool CanBeCoercedInto(TypeReference targetTypeReference,
                TypeReference sourceTypeReference)
            {
                if (targetTypeReference.ClrType == sourceTypeReference.ClrType)
                {
                    return true;
                }

                // None of the defined type coercions was successful.
                return false;
            }
        }
    }
}