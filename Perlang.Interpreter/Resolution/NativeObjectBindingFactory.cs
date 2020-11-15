using System;

namespace Perlang.Interpreter.Resolution
{
    internal class NativeObjectBindingFactory : IBindingFactory
    {
        private readonly Type type;

        public NativeObjectBindingFactory(Type type)
        {
            this.type = type ?? throw new ArgumentException("type cannot be null");
        }

        // This refers to an instance of a native class, i.e. an "object".
        public string ObjectType => "object";

        public Binding CreateBinding(int distance, Expr referringExpr)
        {
            return new NativeObjectBinding(referringExpr, type);
        }
    }
}
