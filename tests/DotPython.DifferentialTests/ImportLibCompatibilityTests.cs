using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ImportLibCompatibilityTests
{
    [Fact]
    public Task ImportLibAndSysModulesMatchCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
