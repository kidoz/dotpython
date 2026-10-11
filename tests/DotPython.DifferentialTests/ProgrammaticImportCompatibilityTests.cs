using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ProgrammaticImportCompatibilityTests
{
    [Fact]
    public Task ImportBuiltinMatchesTheImportStatement() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
