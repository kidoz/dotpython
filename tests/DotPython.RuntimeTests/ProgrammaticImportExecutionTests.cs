using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ProgrammaticImportExecutionTests
{
    [Fact]
    public void ImportBuiltinMatchesTheImportStatement()
    {
        var output = Run(
            """
            import collections.abc, math, string.templatelib
            print("basic", __import__("math").__name__, __import__("math") is math)
            print("fromlist", __import__("math", fromlist=["sqrt"]).__name__,
                  __import__("math", fromlist=["sqrt"]).sqrt(9))
            print("dotted", __import__("collections.abc").__name__,
                  __import__("collections.abc", fromlist=["Mapping"]).__name__)
            print("dotted-attribute", __import__("collections.abc").abc.__name__)
            print("identity", __import__("collections.abc", fromlist=["Mapping"]) is collections.abc,
                  __import__("collections.abc") is __import__("collections.abc"))
            print("keywords", __import__(name="math").__name__,
                  __import__("math", None, None, (), 0).__name__,
                  __import__("math", fromlist=("sqrt",)).__name__)
            print("falsy-fromlist", __import__("string", fromlist=None) is __import__("string"),
                  __import__("string", fromlist="") is __import__("string"),
                  __import__("string", fromlist=[]) is __import__("string"))
            print("submodule", __import__("string.templatelib").__name__,
                  __import__("string.templatelib", fromlist=["Template"]).__name__,
                  __import__("string.templatelib", fromlist=["Template"]) is string.templatelib)
            print("fromlist-sub", __import__("string", fromlist=["templatelib"]).__name__,
                  __import__("string", fromlist=["nope"]).__name__,
                  __import__("string", fromlist=["*"]).__name__)
            def report(label, thunk):
                try:
                    thunk()
                except Exception as error:
                    print("error", label, type(error).__name__, error)
            report("empty", lambda: __import__(""))
            report("missing", lambda: __import__("nowhere"))
            report("missing-dotted", lambda: __import__("string.nowhere"))
            report("missing-name", lambda: __import__())
            report("int-name", lambda: __import__(1))
            report("none-name", lambda: __import__(None))
            report("too-many", lambda: __import__("math", 1, 2, 3, 4, 5))
            report("fromlist-item", lambda: __import__("string", fromlist=[1]))
            report("negative-level", lambda: __import__("math", level=-1))
            report("bad-level", lambda: __import__("math", level="x"))
            report("relative-nothing", lambda: __import__("string", level=1))
            report("relative-no-package", lambda: __import__("string", {"__name__": "mod"}, None, (), 1))
            report("relative-missing", lambda: __import__("nowhere", {"__package__": "string"}, None, (), 1))
            print("relative", __import__("templatelib", {"__package__": "string"}, None, (), 1).__name__,
                  __import__("templatelib", {"__package__": "string"}, None, (), 1).Template.__name__)
            """
        );
        Assert.Equal(
            Lines(
                "basic math True",
                "fromlist math 3.0",
                "dotted collections collections.abc",
                "dotted-attribute collections.abc",
                "identity True True",
                "keywords math math math",
                "falsy-fromlist True True True",
                "submodule string string.templatelib True",
                "fromlist-sub string string string",
                "error empty ValueError Empty module name",
                "error missing ModuleNotFoundError No module named 'nowhere'",
                "error missing-dotted ModuleNotFoundError No module named 'string.nowhere'",
                "error missing-name TypeError __import__() missing required argument 'name' (pos 1)",
                "error int-name TypeError module name must be a string",
                "error none-name TypeError module name must be a string",
                "error too-many TypeError __import__() takes at most 5 arguments (6 given)",
                "error fromlist-item TypeError Item in ``from list'' must be str, not int",
                "error negative-level ValueError level must be >= 0",
                "error bad-level TypeError 'str' object cannot be interpreted as an integer",
                "error relative-nothing KeyError \"'__name__' not in globals\"",
                "error relative-no-package ImportError attempted relative import with no known parent package",
                "error relative-missing ModuleNotFoundError No module named 'string.nowhere'",
                "relative string.templatelib Template"
            ),
            output
        );
    }

    /// <summary>
    /// A module the runtime has to execute, and a package with a submodule that imports
    /// its neighbour relatively: the import runs to its end before the builtin returns.
    /// </summary>
    [Fact]
    public void ImportRunsTheModuleBodyItHasToExecute()
    {
        var sources = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["plain"] = new SourceText("print('plain inits')\nvalue = 'plain'\n", "plain.py"),
            ["pkg"] = new SourceText(string.Empty, "pkg.py"),
            ["pkg.inner"] = new SourceText("value = 42\n", "pkg/inner.py"),
            ["pkg.relative"] = new SourceText(
                "from . import inner\nname = 'rel:' + str(inner.value)\n",
                "pkg/relative.py"
            ),
        };

        using var output = new StringWriter();
        var result = new ManagedPythonEngine(sources).Execute(
            """
            plain = __import__("plain")
            print("source", plain.__name__, plain.value)
            print("package", __import__("pkg.inner").__name__,
                  __import__("pkg.inner", fromlist=["value"]).value)
            print("relative", __import__("relative", {"__package__": "pkg"}, None, (), 1).name)
            """,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        Assert.Equal(
            Lines("plain inits", "source plain plain", "package pkg 42", "relative rel:42"),
            output.ToString()
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
