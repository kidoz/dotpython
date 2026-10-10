using Xunit;

namespace DotPython.DifferentialTests;

public sealed class WarningsCompatibilityTests
{
    [Fact]
    public Task WarningsSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import warnings

            print("all", warnings.__all__)
            print("surface", [name for name in ("WarningMessage", "catch_warnings", "deprecated", "defaultaction", "filters", "filterwarnings", "formatwarning", "onceregistry", "resetwarnings", "showwarning", "simplefilter", "warn", "warn_explicit") if hasattr(warnings, name)])
            print("builtins", hasattr(warnings, "UserWarning"), hasattr(warnings, "Warning"))
            print("categories", [c.__name__ for c in (Warning, UserWarning, DeprecationWarning, PendingDeprecationWarning, SyntaxWarning, RuntimeWarning, FutureWarning, ImportWarning, UnicodeWarning, BytesWarning, ResourceWarning, EncodingWarning)])
            print("hierarchy", issubclass(UserWarning, Warning), issubclass(Warning, Exception), issubclass(EncodingWarning, Warning))
            print("filters", warnings.filters)
            print("defaultaction", warnings.defaultaction, warnings.onceregistry, warnings._filters_version)
            print("format", repr(warnings.formatwarning("msg", UserWarning, "f.py", 3)))
            print("format-line", repr(warnings.formatwarning("msg", UserWarning, "f.py", 3, line="    x = 1\n")))
            print("format-bare", repr(warnings.formatwarning("msg", UserWarning, "f.py", 3, line="x = 1")))
            print("format-missing", repr(warnings.formatwarning("msg", UserWarning, "nowhere.py", 99)))

            with warnings.catch_warnings() as quiet:
                print("quiet-log", quiet)
                print("inside-filters", warnings.filters is warnings._get_filters(), len(warnings._get_filters()))
                warnings.simplefilter("ignore")
                print("copy", len(warnings.filters), len(warnings._get_filters()))
            print("restored", len(warnings.filters))

            print("--- filterwarnings")
            warnings.filterwarnings("ignore", message="match.*")
            print("f1", warnings.filters[0])
            warnings.filterwarnings("error", category=FutureWarning, module="pkg.sub")
            print("f2", warnings.filters[0])
            warnings.filterwarnings("always", category=RuntimeWarning, lineno=7, append=True)
            print("f3", warnings.filters[-1], len(warnings.filters))
            warnings.filterwarnings("ignore", message="match.*")
            print("dup", len([f for f in warnings.filters if f[1] is not None]))

            with warnings.catch_warnings():
                warnings.simplefilter("ignore")
                print("simple", warnings.filters[0])

            print("--- errors")
            for label, thunk in [
                ("action", lambda: warnings.simplefilter("nope")),
                ("message", lambda: warnings.filterwarnings("ignore", message=42)),
                ("category", lambda: warnings.filterwarnings("ignore", category=int)),
                ("module", lambda: warnings.filterwarnings("ignore", module=42)),
                ("lineno-type", lambda: warnings.filterwarnings("ignore", lineno="x")),
                ("lineno-neg", lambda: warnings.filterwarnings("ignore", lineno=-1)),
                ("simple-lineno-neg", lambda: warnings.simplefilter("ignore", lineno=-1)),
            ]:
                try:
                    thunk()
                    print(label, "ok")
                except Exception as error:
                    print(label, type(error).__name__, error)

            print("--- warn_explicit")
            def where(entry):
                name = entry.filename
                return "<file>" if name.startswith("<") or "/" in name else name

            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("always")
                warnings.warn_explicit("alpha", UserWarning, "a.py", 1)
                warnings.warn_explicit("beta", RuntimeWarning, "b.py", 2)
                warnings.warn_explicit(RuntimeWarning("instance"), RuntimeWarning, "b.py", 3)
                warnings.warn("script-warning", UserWarning)
                warnings.warn("again", UserWarning)
            print("logged", [(where(e), e.lineno, e.category.__name__, str(e.message)) for e in log])
            entry = log[0]
            print("entry", repr(entry), str(entry))
            print("attrs", entry.message, entry.category, entry.filename, entry.lineno, entry.file, entry.line, entry.source)

            print("--- actions")
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("error")
                try:
                    warnings.warn_explicit("boom", UserWarning, "mod.py", 3)
                except UserWarning as error:
                    print("raised", type(error).__name__, error)
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("once")
                warnings.warn_explicit("same", UserWarning, "a.py", 1)
                warnings.warn_explicit("same", UserWarning, "a.py", 2)
                warnings.warn_explicit("other", UserWarning, "b.py", 9)
                print("once", len(log))
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("default")
                warnings.warn_explicit("dflt", UserWarning, "a.py", 1)
                warnings.warn_explicit("dflt", UserWarning, "a.py", 1)
                warnings.warn_explicit("dflt", UserWarning, "a.py", 2)
                print("default", len(log))
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("module")
                warnings.warn_explicit("mod", UserWarning, "a.py", 1)
                warnings.warn_explicit("mod", UserWarning, "a.py", 2)
                print("module", len(log))
            registry = {}
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("default")
                warnings.warn_explicit("reg", UserWarning, "a.py", 1, registry=registry)
                warnings.warn_explicit("reg", UserWarning, "a.py", 1, registry=registry)
                print("registry-count", len(log), registry.get(("reg", UserWarning, 1)))
                warnings.filterwarnings("always")
                warnings.warn_explicit("reg", UserWarning, "a.py", 1, registry=registry)
                print("registry-after-mutation", len(log), registry)

            print("--- matching")
            with warnings.catch_warnings(record=True) as log:
                warnings.filters.insert(0, ("error", None, UserWarning, "a.py", 0))
                try:
                    warnings.warn_explicit("x", UserWarning, "a.py", 1)
                except UserWarning:
                    print("module-literal matched")
                try:
                    warnings.warn_explicit("x", UserWarning, "b.py", 1)
                except UserWarning:
                    print("unexpected")
                except Exception:
                    pass
                warnings.filters[0] = ("ignore", None, UserWarning, "b.py", 2)
                warnings.warn_explicit("y", UserWarning, "b.py", 2)
                warnings.warn_explicit("y", UserWarning, "b.py", 3)
                print("ignore-count", len(log))
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("ignore", UserWarning)
                try:
                    warnings.warn_explicit("z", RuntimeWarning, "a.py", 1)
                finally:
                    print("category-filtered", len(log))

            print("--- showwarning")
            seen = []
            def hook(message, category, filename, lineno, file=None, line=None):
                seen.append((category.__name__, str(message), where(type("E", (), {"filename": filename})()), lineno, line))
            warnings.showwarning = hook
            with warnings.catch_warnings(record=True) as log:
                warnings.warn_explicit("hooked", UserWarning, "h.py", 4)
            print("hook-was-bypassed", len(log), seen)
            warnings.showwarning = warnings._showwarning_orig
            with warnings.catch_warnings(record=True) as log:
                warnings.warn_explicit("hooked2", UserWarning, "h.py", 5)
            print("record-with-default-hook", len(log))

            print("--- resetwarnings")
            with warnings.catch_warnings():
                warnings.simplefilter("ignore")
                warnings.resetwarnings()
                print("reset", len(warnings.filters))

            print("--- deprecated")
            print("deprecated-type", type(warnings.deprecated).__name__)

            def old():
                return 1

            new = warnings.deprecated("use new")(old)
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("always")
                print("wrapped", new(), new.__name__, type(new).__name__, new.__deprecated__)
            print("deprecated-warning", [(e.category.__name__, str(e.message)) for e in log])
            with warnings.catch_warnings(record=True) as log:
                warnings.simplefilter("always")
                silent = warnings.deprecated("quiet", category=None)(old)
            print("deprecated-none", silent.__deprecated__, silent(), len(log))
            """
        );
}
