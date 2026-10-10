using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class WarningsExecutionTests
{
    [Fact]
    public void WarningsSurface()
    {
        var output = Run(
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
        Assert.Equal(
            Lines(
                "all ['warn', 'warn_explicit', 'showwarning', 'formatwarning', 'filterwarnings', 'simplefilter', 'resetwarnings', 'catch_warnings', 'deprecated']",
                "surface ['WarningMessage', 'catch_warnings', 'deprecated', 'defaultaction', 'filters', 'filterwarnings', 'formatwarning', 'onceregistry', 'resetwarnings', 'showwarning', 'simplefilter', 'warn', 'warn_explicit']",
                "builtins False False",
                "categories ['Warning', 'UserWarning', 'DeprecationWarning', 'PendingDeprecationWarning', 'SyntaxWarning', 'RuntimeWarning', 'FutureWarning', 'ImportWarning', 'UnicodeWarning', 'BytesWarning', 'ResourceWarning', 'EncodingWarning']",
                "hierarchy True True True",
                "filters [('default', None, <class 'DeprecationWarning'>, '__main__', 0), ('ignore', None, <class 'DeprecationWarning'>, None, 0), ('ignore', None, <class 'PendingDeprecationWarning'>, None, 0), ('ignore', None, <class 'ImportWarning'>, None, 0), ('ignore', None, <class 'ResourceWarning'>, None, 0)]",
                "defaultaction default {} 1",
                "format 'f.py:3: UserWarning: msg\\n'",
                "format-line 'f.py:3: UserWarning: msg\\n  x = 1\\n'",
                "format-bare 'f.py:3: UserWarning: msg\\n  x = 1\\n'",
                "format-missing 'nowhere.py:99: UserWarning: msg\\n'",
                "quiet-log None",
                "inside-filters False 5",
                "copy 5 6",
                "restored 5",
                "--- filterwarnings",
                "f1 ('ignore', re.compile('match.*', re.IGNORECASE), <class 'Warning'>, None, 0)",
                "f2 ('error', None, <class 'FutureWarning'>, re.compile('pkg.sub'), 0)",
                "f3 ('always', None, <class 'RuntimeWarning'>, None, 7) 8",
                "dup 1",
                "simple ('ignore', re.compile('match.*', re.IGNORECASE), <class 'Warning'>, None, 0)",
                "--- errors",
                "action ValueError invalid action: 'nope'",
                "message TypeError message must be a string",
                "category TypeError category must be a Warning subclass",
                "module TypeError module must be a string",
                "lineno-type TypeError lineno must be an int",
                "lineno-neg ValueError lineno must be an int >= 0",
                "simple-lineno-neg ValueError lineno must be an int >= 0",
                "--- warn_explicit",
                "logged [('a.py', 1, 'UserWarning', 'alpha'), ('b.py', 2, 'RuntimeWarning', 'beta'), ('b.py', 3, 'RuntimeWarning', 'instance'), ('<file>', 62, 'UserWarning', 'script-warning'), ('<file>', 63, 'UserWarning', 'again')]",
                "entry <WarningMessage {message : UserWarning('alpha'), category : 'UserWarning', filename : 'a.py', lineno : 1, line : None}> {message : UserWarning('alpha'), category : 'UserWarning', filename : 'a.py', lineno : 1, line : None}",
                "attrs alpha <class 'UserWarning'> a.py 1 None None None",
                "--- actions",
                "raised UserWarning boom",
                "once 2",
                "default 3",
                "module 2",
                "registry-count 1 True",
                "registry-after-mutation 2 {'version': 28}",
                "--- matching",
                "ignore-count 4",
                "category-filtered 1",
                "--- showwarning",
                "hook-was-bypassed 1 []",
                "record-with-default-hook 1",
                "--- resetwarnings",
                "reset 9",
                "--- deprecated",
                "deprecated-type type",
                "wrapped 1 old function use new",
                "deprecated-warning [('DeprecationWarning', 'use new')]",
                "deprecated-none quiet 1 0"
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
