using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ImportLibExecutionTests
{
    [Fact]
    public void ImportLibAndSysModulesMatchCpython()
    {
        var output = Run(
            """
            import sys, importlib, importlib.machinery, math, collections, collections.abc, string
            print("modules", type(sys.modules).__name__, "math" in sys.modules, sys.modules["math"] is math,
                  "__main__" in sys.modules, sys.modules.get("nowhere") is None)
            print("loaded", [name for name in ("sys", "builtins", "importlib", "collections.abc", "math")
                             if name in sys.modules])
            print("importlib", importlib.__all__, importlib.sys is sys)
            print("import-module", importlib.import_module("math") is math,
                  importlib.import_module("collections.abc").__name__,
                  importlib.import_module(".abc", "collections").__name__,
                  importlib.import_module(name="math").__name__,
                  importlib.import_module("math", package="collections").__name__)
            print("reload", importlib.reload(math) is math, importlib.reload(collections) is collections)
            print("invalidate", importlib.invalidate_caches())
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            report("bad-name", lambda: importlib.import_module(1))
            report("empty", lambda: importlib.import_module(""))
            report("no-package", lambda: importlib.import_module(".abc"))
            report("missing", lambda: importlib.import_module("nowhere"))
            report("missing-relative", lambda: importlib.import_module(".nowhere", "collections"))
            report("reload-bad", lambda: importlib.reload([1]))
            report("too-many", lambda: importlib.import_module("math", "collections", "extra"))
            for module in (math, collections, collections.abc, string):
                spec = module.__spec__
                print("spec", module.__name__, type(spec).__name__,
                      spec.submodule_search_locations is not None,
                      type(spec.submodule_search_locations).__name__)
            print("spec-math", repr(math.__spec__.name), repr(math.__spec__.origin),
                  repr(math.__spec__.parent), repr(math.__spec__.has_location),
                  repr(math.__spec__.cached), type(math.__spec__.submodule_search_locations).__name__)
            print("spec-parents", repr(collections.__spec__.parent), repr(string.__spec__.parent),
                  repr(collections.abc.__spec__.submodule_search_locations))
            print("spec-eq", math.__spec__ == math.__spec__, math.__spec__ == collections.__spec__,
                  math.__spec__ is math.__spec__)
            print("spec-attrs", sorted(name for name in dir(math.__spec__) if not name.startswith("_")))
            print("machinery", importlib.machinery.ModuleSpec.__name__,
                  type(math.__spec__) is importlib.machinery.ModuleSpec)
            print("path", hasattr(collections, "__path__"), hasattr(collections.abc, "__path__"),
                  hasattr(math, "__path__"), isinstance(collections.__path__, list))
            print("path-type", type(collections.__path__).__name__)
            """
        );
        Assert.Equal(
            Lines(
                "modules dict True True True True",
                "loaded ['sys', 'builtins', 'importlib', 'collections.abc', 'math']",
                "importlib ['__import__', 'import_module', 'invalidate_caches', 'reload'] True",
                "import-module True collections.abc collections.abc math math",
                "reload True True",
                "invalidate None",
                "bad-name AttributeError 'int' object has no attribute 'startswith'",
                "empty ValueError Empty module name",
                "no-package TypeError the 'package' argument is required to perform a relative import for '.abc'",
                "missing ModuleNotFoundError No module named 'nowhere'",
                "missing-relative ModuleNotFoundError No module named 'collections.nowhere'",
                "reload-bad TypeError reload() argument must be a module",
                "too-many TypeError import_module() takes from 1 to 2 positional arguments but 3 were given",
                "spec math ModuleSpec False NoneType",
                "spec collections ModuleSpec True list",
                "spec collections.abc ModuleSpec False NoneType",
                "spec string ModuleSpec True list",
                "spec-math 'math' 'built-in' '' False None NoneType",
                "spec-parents 'collections' 'string' None",
                "spec-eq True False True",
                "spec-attrs ['cached', 'has_location', 'loader', 'loader_state', 'name', 'origin', 'parent', 'submodule_search_locations']",
                "machinery ModuleSpec True",
                "path True False False True",
                "path-type list"
            ),
            output
        );
    }

    /// <summary>
    /// A module the runtime has to execute runs again on reload, keeping the namespace it
    /// has — and the running program is a module of `sys.modules` like any other.
    /// </summary>
    [Fact]
    public void ReloadRunsTheBodyAgainAndMainIsAModule()
    {
        var sources = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["subject"] = new SourceText("count = globals().get('count', 0) + 1\n", "subject.py"),
        };

        using var output = new StringWriter();
        var result = new ManagedPythonEngine(sources).Execute(
            """
            import sys, importlib
            import subject
            print("first", subject.count)
            print("again", importlib.reload(subject).count, subject.count)
            print("loaded", sys.modules["subject"] is subject)
            import __main__
            print("main", __main__.__name__, sys.modules["__main__"].__name__)
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
            Lines("first 1", "again 2 2", "loaded True", "main __main__ __main__"),
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
