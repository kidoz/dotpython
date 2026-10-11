using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SiteBuiltinsExecutionTests
{
    /// <summary>
    /// `help` renders the runtime's own reflection the way `pydoc`'s plain text does:
    /// a function's signature and docstring, a bound method's receiver note, a class
    /// with the methods it defines, a module's classes and functions, and `pydoc`'s
    /// own text for a name it cannot resolve. A class's inherited members and a native
    /// callable's argument list are the shapes the runtime does not carry.
    /// </summary>
    [Fact]
    public void TheHelperDocumentsWhatTheRuntimeKnows()
    {
        var output = Run(
            """"
            def greet(name, punct='!'):
                """Say hello.

                More detail here.
                """
                return name + punct

            def full(a, b=1, /, c=2, *args, d, **kw):
                """Full signature."""

            class Greeter:
                """A greeter."""

                def greet(self, name, punct='!'):
                    """Say hello."""
                    return name + punct

            print("repr", repr(help))
            print("types", type(help).__name__, type(help).__module__)
            print("doc", help.__doc__ == type(help).__doc__)
            help(greet)
            help(full)
            help(Greeter().greet)
            help(Greeter)
            help(Greeter())
            help("json")
            help("nope-nope")
            help(5)
            """"
        );
        Assert.Equal(
            Lines(
                "repr Type help() for interactive help, or help(object) for help about object.",
                "types _Helper _sitebuiltins",
                "doc True",
                "Help on function greet in module __main__:",
                "",
                "greet(name, punct='!')",
                "    Say hello.",
                "",
                "    More detail here.",
                "",
                "Help on function full in module __main__:",
                "",
                "full(a, b=1, /, c=2, *args, d, **kw)",
                "    Full signature.",
                "",
                "Help on method greet in module __main__:",
                "",
                "greet(name, punct='!') method of __main__.Greeter instance",
                "    Say hello.",
                "",
                "Help on class Greeter in module __main__:",
                "",
                "class Greeter(builtins.object)",
                " |  A greeter.",
                " |",
                " |  Methods defined here:",
                " |",
                " |  greet(self, name, punct='!')",
                " |      Say hello.",
                "Help on Greeter in module __main__ object:",
                "",
                "class Greeter(builtins.object)",
                " |  A greeter.",
                " |",
                " |  Methods defined here:",
                " |",
                " |  greet(self, name, punct='!')",
                " |      Say hello.",
                "Help on module json:",
                "",
                "NAME",
                "    json",
                "",
                "CLASSES",
                "    JSONDecodeError",
                "",
                "FUNCTIONS",
                "    dumps(...)",
                "    loads(...)",
                "No Python documentation found for 'nope-nope'.",
                "Use help() to get the interactive help utility.",
                "Use help(str) for help on the str class.",
                "",
                "No Python documentation found for 5.",
                "Use help() to get the interactive help utility.",
                "Use help(str) for help on the str class.",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The three printers are `_sitebuiltins._Printer` instances over the runtime's own
    /// texts: a text of at most `MAXLINES` lines is its own repr, and calling the printer
    /// writes the text out.
    /// </summary>
    [Fact]
    public void ThePrintersCarryTheRuntimesOwnTexts()
    {
        var output = Run(
            """
            print("types", type(copyright).__name__, type(credits).__name__, type(license).__name__,
                  copyright.__class__ is license.__class__, copyright.__class__.__module__)
            print("doc", repr(copyright.__doc__))
            print("repr", repr(license).splitlines()[0], len(repr(copyright).splitlines()),
                  len(repr(credits).splitlines()), len(repr(license).splitlines()))
            credits()
            """
        );
        Assert.Equal(
            Lines(
                "types _Printer _Printer _Printer True _sitebuiltins",
                "doc 'interactive prompt objects for printing the license text, a list of\\ncontributors and the copyright notice.'",
                "repr MIT License 17 6 21",
                "Thanks to CWI, CNRI, BeOpen, Zope Corporation, the Python Software",
                "Foundation, and a cast of thousands for supporting Python development.",
                "See www.python.org for more information.",
                "",
                "The DotPython managed runtime is built by Aleksandr Pavlov and the",
                "DotPython contributors."
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
