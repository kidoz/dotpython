using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class FnmatchModuleExecutionTests
{
    /// <summary>
    /// The runtime's `fnmatch`: CPython 3.14's shell-pattern translation — the atomic
    /// groups behind interior stars, the range and set-operation rewriting, the empty
    /// class cases — and the four matching functions over the runtime's own `re`, with
    /// byte-for-character matching for bytes patterns and CPython's mixed-type messages.
    /// The module publishes only its own names; CPython's namespace also leaks its
    /// imports, which the pinned local answer records.
    /// </summary>
    [Fact]
    public void TheFnmatchModuleAnswersAsCpythonDoes()
    {
        var output = Run(
            """"
            import fnmatch as f
            import pathlib
            def show(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            show("all", lambda: (f.__all__))
            show("translate", lambda: ([f.translate(p) for p in ("*", "?", "a*b", "a**b", "*.py", "[a-c]*", "[!a-c]*", "[]]", "[!]]", "[]", "[!]", "[", "[a-", "[a-b-c]", "[-a]", "[a-]", "\\*", "a\\b", "[\\]", "[[]", "[^a]", "[&]", "[&&]", "[a-c-e]", "[--0]", "[!-]", "[-]b", "a b", "")]))
            show("match", lambda: (f.fnmatch("A.PY", "*.py"), f.fnmatchcase("A.PY", "*.py"), f.fnmatch("a/b", "*"), f.fnmatch("", "*"), f.fnmatch(".x", "*"), f.fnmatch("abc", "a*c"), f.fnmatch("ac", "a*c"), f.fnmatch("ab", "a*c"), f.fnmatch("", "?"), f.fnmatch("a", "??")))
            show("classes", lambda: ([f.fnmatch(n, p) for n, p in [("a", "[abc]"), ("d", "[!abc]"), ("-", "[-a]"), ("b", "[a-c]"), ("]", "[]]"), ("[", "[[]"), ("^", "[\\^]"), ("&", "[&]"), ("a", "[^a]"), ("b", "[^a]")]]))
            show("stars", lambda: ([f.fnmatch(n, p) for n, p in [("abc", "*a*b*"), ("ab", "*a*b*"), ("aXbXc", "*a*b*c"), ("", "**"), ("a", "*?*"), ("a", "?*?")]]))
            show("bytes", lambda: (f.fnmatch(b"A.PY", b"*.py"), f.fnmatch(b"a\xe9", b"a?"), f.fnmatchcase(b"abc", b"a*c"), f.filter([b"a.py", b"b.txt", b"A.PY"], b"*.py")))
            show("filter", lambda: (f.filter(["a.py", "b.txt", "A.PY", "c.pyc"], "*.py"), f.filterfalse(["a.py", "b.txt", "A.PY"], "*.py"), f.filter((x for x in ["a.py", "b.txt"]), "*.py"), f.filter([], "*")))
            show("pathlike", lambda: (f.fnmatch(pathlib.Path("a.py"), "*.py"), f.filter([pathlib.Path("a.py")], "*.py")))
            show("translate-bytes", lambda: f.translate(b"*.py"))
            show("translate-int", lambda: f.translate(5))
            show("mixed1", lambda: f.fnmatch("a", b"*"))
            show("mixed2", lambda: f.fnmatch(b"a", "*"))
            show("mixed3", lambda: f.filter(["a.py"], b"*.py"))
            show("case-int", lambda: f.fnmatchcase(5, "*"))
            show("pat-int", lambda: f.fnmatchcase("a", 5))
            show("norm-int", lambda: f.fnmatch(5, "*"))
            show("filter-int", lambda: f.filter(["a"], 5))
            show("arity1", lambda: f.filter([], "*", 3))
            show("arity2", lambda: f.fnmatchcase("a", "*", 3))
            show("arity3", lambda: f.fnmatchcase("a"))
            show("arity4", lambda: f.fnmatchcase("a", nope=1))
            show("kw", lambda: (f.fnmatchcase(name="a.py", pat="*.py"), f.translate(pat="*.py"), f.filter(names=["a.py"], pat="*")))
            show("local-names", lambda: sorted(n for n in dir(f) if not n.startswith("_")))
            """"
        );
        Assert.Equal(
            Lines(
                "all ['filter', 'filterfalse', 'fnmatch', 'fnmatchcase', 'translate']",
                "translate ['(?s:.*)\\\\z', '(?s:.)\\\\z', '(?s:a.*b)\\\\z', '(?s:a.*b)\\\\z', '(?s:.*\\\\.py)\\\\z', '(?s:[a-c].*)\\\\z', '(?s:[^a-c].*)\\\\z', '(?s:[]])\\\\z', '(?s:[^]])\\\\z', '(?s:\\\\[\\\\])\\\\z', '(?s:\\\\[!\\\\])\\\\z', '(?s:\\\\[)\\\\z', '(?s:\\\\[a\\\\-)\\\\z', '(?s:[a-b\\\\-c])\\\\z', '(?s:[\\\\-a])\\\\z', '(?s:[a\\\\-])\\\\z', '(?s:\\\\\\\\.*)\\\\z', '(?s:a\\\\\\\\b)\\\\z', '(?s:[\\\\\\\\])\\\\z', '(?s:[\\\\[])\\\\z', '(?s:[\\\\^a])\\\\z', '(?s:[\\\\&])\\\\z', '(?s:[\\\\&\\\\&])\\\\z', '(?s:[a-c\\\\-e])\\\\z', '(?s:[\\\\--0])\\\\z', '(?s:[^\\\\-])\\\\z', '(?s:[\\\\-]b)\\\\z', '(?s:a\\\\ b)\\\\z', '(?s:)\\\\z']",
                "match (False, False, True, True, True, True, True, False, False, False)",
                "classes [True, True, True, True, True, True, True, True, True, False]",
                "stars [True, True, True, True, True, False]",
                "bytes (False, True, True, [b'a.py'])",
                "filter (['a.py'], ['b.txt', 'A.PY'], ['a.py'], [])",
                "pathlike TypeError expected string or bytes-like object, got 'PosixPath'",
                "translate-bytes TypeError decoding to str: need a bytes-like object, int found",
                "translate-int TypeError object of type 'int' has no len()",
                "mixed1 TypeError cannot use a bytes pattern on a string-like object",
                "mixed2 TypeError cannot use a string pattern on a bytes-like object",
                "mixed3 TypeError cannot use a bytes pattern on a string-like object",
                "case-int TypeError expected string or bytes-like object, got 'int'",
                "pat-int TypeError object of type 'int' has no len()",
                "norm-int TypeError expected str, bytes or os.PathLike object, not int",
                "filter-int TypeError expected str, bytes or os.PathLike object, not int",
                "arity1 TypeError filter() takes 2 positional arguments but 3 were given",
                "arity2 TypeError fnmatchcase() takes 2 positional arguments but 3 were given",
                "arity3 TypeError fnmatchcase() missing 1 required positional argument: 'pat'",
                "arity4 TypeError fnmatchcase() got an unexpected keyword argument 'nope'",
                "kw (True, '(?s:.*\\\\.py)\\\\z', ['a.py'])",
                "local-names ['filter', 'filterfalse', 'fnmatch', 'fnmatchcase', 'translate']"
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
