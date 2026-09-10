using Xunit;

namespace DotPython.DifferentialTests;

public sealed class MissingFunctionArgumentCompatibilityTests
{
    [Fact]
    public async Task MissingArgumentsReportAllRequiredNamesAndPrioritizePositionalArguments()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            def one(required): pass
            def two(first, second): pass
            def three(first, second, third): pass
            def four(first, second, third, fourth): pass
            def defaults(first, second, optional=3): pass
            def mixed(first, second, *, key, other): pass
            def keyword_only(*, first, second, third=3): pass
            def variadic(first, *values, key, other, final): pass
            try: one()
            except TypeError as error: print(error)
            try: two()
            except TypeError as error: print(error)
            try: three()
            except TypeError as error: print(error)
            try: four()
            except TypeError as error: print(error)
            try: defaults()
            except TypeError as error: print(error)
            try: defaults(second=2)
            except TypeError as error: print(error)
            try: mixed()
            except TypeError as error: print(error)
            try: mixed(1, 2)
            except TypeError as error: print(error)
            try: keyword_only(second=2)
            except TypeError as error: print(error)
            try: keyword_only()
            except TypeError as error: print(error)
            try: variadic(1, 2, 3)
            except TypeError as error: print(error)
            """
        );
    }

    [Fact]
    public async Task MissingArgumentsUseLexicalFunctionNamesAcrossBindingAndNestedScopes()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class Original:
                def method(self, required): pass
                @staticmethod
                def static(required): pass
                @classmethod
                def class_method(cls, required): pass
            class Child(Original): pass
            alias = Original.method
            for call in (Original.method, Original().method, Child().method,
                         Original.static, Original.class_method, alias):
                try: call()
                except TypeError as error: print(error)
            def unrelated(self, required): pass
            Original.assigned = unrelated
            try: Original().assigned()
            except TypeError as error: print(error)
            def factory():
                def nested(first, second): pass
                class Nested:
                    def method(self, required): pass
                return nested, Nested
            nested, Nested = factory()
            print(nested.__qualname__, Nested.method.__qualname__)
            try: nested()
            except TypeError as error: print(error)
            try: Nested().method()
            except TypeError as error: print(error)
            class Outer:
                class Inner:
                    def method(self, required): pass
            try: Outer.Inner().method()
            except TypeError as error: print(error)
            def exporter():
                global exported
                def exported(required): pass
            exporter()
            print(exported.__qualname__)
            try: exported()
            except TypeError as error: print(error)
            """
        );
    }
}
