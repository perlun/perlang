using System.Linq;
using Perlang.Interpreter;
using Xunit;
using static Perlang.Tests.Integration.EvalHelper;

namespace Perlang.Tests.Integration.Stdlib
{
    public class Base64DecodeTests
    {
        [Fact]
        public void Base64_decode_is_defined()
        {
            Assert.IsAssignableFrom<TargetAndMethodContainer>(Eval("Base64.decode"));
        }

        [Fact]
        public void Base64_decode_with_no_arguments_throws_the_expected_exception()
        {
            var result = EvalWithTypeValidationErrorCatch("Base64.decode()");
            var exception = result.TypeValidationErrors.First();

            Assert.Single(result.TypeValidationErrors);
            Assert.Contains("Method 'decode' has 1 parameter(s) but was called with 0 argument(s)", exception.Message);
        }

        [Fact]
        public void Base64_decode_with_a_padded_string_argument_returns_the_expected_result()
        {
            Assert.Equal("hej hej", Eval("Base64.decode(\"aGVqIGhlag==\")"));
        }

        [Fact]
        public void Base64_decode_with_an_non_padded_string_argument_returns_the_expected_result()
        {
            // This used to fail at one point, which is why we added a test for it.
            Assert.Equal("hej hej", Eval("Base64.decode(\"aGVqIGhlag\")"));
        }

        [Fact]
        public void Base64_decode_with_a_numeric_argument_throws_the_expected_exception()
        {
            var result = EvalWithTypeValidationErrorCatch("Base64.decode(123.45)");
            var runtimeError = result.TypeValidationErrors.First();

            Assert.Single(result.TypeValidationErrors);

            Assert.Equal("Cannot pass System.Double argument as System.String parameter to decode()",
                runtimeError.Message);
        }
    }
}
