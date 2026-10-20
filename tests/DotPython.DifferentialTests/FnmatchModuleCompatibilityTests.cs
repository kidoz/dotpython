using Xunit;

namespace DotPython.DifferentialTests;

public sealed class FnmatchModuleCompatibilityTests
{
    /// <summary>
    /// The `fnmatch` module: `translate`'s exact regular expressions for stars, classes,
    /// ranges, set operations and the degenerate bracket forms, the matching functions
    /// over text and bytes, `filter`/`filterfalse`, the path-like normcase rule, and
    /// CPython's own refusal wording for mixed types and bad calls.
    /// </summary>
    [Fact]
    public Task TheFnmatchModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            """"
        );
}
