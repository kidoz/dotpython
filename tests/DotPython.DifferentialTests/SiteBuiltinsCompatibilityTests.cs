using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SiteBuiltinsCompatibilityTests
{
    /// <summary>
    /// The helper's shape is CPython's — `_Helper` in `_sitebuiltins`, its repr and its
    /// docstring — and so is the plain text it prints for a function, a positional-only
    /// or keyword-only signature, a bound method, and a name no module answers to. The
    /// printers' own texts are the runtime's, so only their types are compared here.
    /// </summary>
    [Fact]
    public Task TheHelperMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            def greet(name, punct='!'):
                """Say hello.

                More detail here.
                """
                return name + punct

            def full(a, b=1, /, c=2, *args, d, **kw):
                """Full signature."""

            def positional(a, b=2, /):
                """Positional-only defaults."""

            def keywords(a, *, b=3, c):
                """Keyword-only parameters."""

            class Greeter:
                """A greeter."""

                def greet(self, name, punct='!'):
                    """Say hello."""
                    return name + punct

            print("repr", repr(help))
            print("types", type(help).__name__, type(help).__module__, type(copyright).__name__,
                  type(credits).__name__, type(license).__name__, copyright.__class__ is license.__class__)
            print("doc", help.__doc__)
            help(greet)
            help(full)
            help(positional)
            help(keywords)
            help(Greeter().greet)
            help("no-such-documentation-topic")
            """"
        );
}
