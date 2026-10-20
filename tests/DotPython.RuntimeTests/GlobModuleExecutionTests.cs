using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class GlobModuleExecutionTests
{
    /// <summary>
    /// The runtime's `glob` over a fixture tree beneath the search root: wildcards in
    /// file and directory segments, character classes, the literal fast path, complete
    /// weeks of hidden-name behavior, the recursive `**` walk (symlinked directories
    /// followed, broken links kept by `lexists`), `root_dir` as text and a path, the C
    /// locale of `escape`/`has_magic`/`translate`, and CPython's argument-parser and type
    /// wording. The recorded gaps — `iglob` answering a list iterator, bytes pathnames
    /// and `dir_fd` — are pinned at the end.
    /// </summary>
    [Fact]
    public void TheGlobModuleMatchesCpythonBeneathTheSearchRoot()
    {
        var name = "glob-fixture-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Directory.GetCurrentDirectory(), name);
        if (!TryCreateFixture(directory))
        {
            Assert.Skip("symbolic links are not available");
            return;
        }
        try
        {
            var output = Run(
                "base = '"
                    + name
                    + "'\nwith_links = True\n"
                    + """"
                    import glob
                    import pathlib
                    def show(label, thunk):
                        try:
                            print(label, repr(thunk()))
                        except Exception as error:
                            print(label, type(error).__name__, error)
                    show("star", lambda: (sorted(glob.glob("*", root_dir=base))))
                    show("py", lambda: (sorted(glob.glob("*.py", root_dir=base))))
                    show("q", lambda: (sorted(glob.glob("?", root_dir=base))))
                    show("cls", lambda: (sorted(glob.glob("[fa]*", root_dir=base))))
                    show("neg", lambda: (sorted(glob.glob("[!a]*", root_dir=base))))
                    show("dironly", lambda: (sorted(glob.glob("*/", root_dir=base))))
                    show("nested", lambda: (sorted(glob.glob("a/*", root_dir=base))))
                    show("nested2", lambda: (sorted(glob.glob("*/b/*", root_dir=base))))
                    show("literal", lambda: (glob.glob("f1.txt", root_dir=base), glob.glob("nope.txt", root_dir=base)))
                    show("literal-dir", lambda: (glob.glob("a/", root_dir=base), glob.glob("a", root_dir=base), glob.glob("nope/", root_dir=base)))
                    show("recursive", lambda: (sorted(glob.glob("**", recursive=True, root_dir=base))))
                    show("recursive-py", lambda: (sorted(glob.glob("**/*.py", recursive=True, root_dir=base))))
                    show("recursive-a", lambda: (sorted(glob.glob("a/**", recursive=True, root_dir=base))))
                    show("recursive-mid", lambda: (sorted(glob.glob("**/b/*.txt", recursive=True, root_dir=base))))
                    show("starstar-noflag", lambda: (sorted(glob.glob("**", root_dir=base))))
                    show("hidden-off", lambda: (sorted(glob.glob(".*", root_dir=base)), sorted(glob.glob("*hidden*", root_dir=base))))
                    show("hidden-on", lambda: (sorted(glob.glob(".*", include_hidden=True, root_dir=base)), sorted(glob.glob("*hidden*", include_hidden=True, root_dir=base))))
                    show("hidden-rec", lambda: (sorted(glob.glob("**/*.py", recursive=True, include_hidden=True, root_dir=base))))
                    show("hidden-seg", lambda: (sorted(glob.glob("*/*.py", include_hidden=True, root_dir=base))))
                    show("empty", lambda: (sorted(glob.glob("empty/*", root_dir=base))))
                    show("dir-wild", lambda: (sorted(glob.glob("*/g.py", root_dir=base))))
                    show("dir-cls", lambda: (sorted(glob.glob("[a-z]*/g.py", root_dir=base))))
                    show("rec-dir-slash", lambda: (sorted(glob.glob("**/", recursive=True, root_dir=base))))
                    show("multi-slash", lambda: (sorted(glob.glob("a//*.py", root_dir=base))))
                    show("dot-slash", lambda: (sorted(glob.glob("./*.py", root_dir=base))))
                    show("dot", lambda: (glob.glob(".", root_dir=base), glob.glob("nope", root_dir=base)))
                    show("root-path", lambda: (sorted(glob.glob("*.py", root_dir=pathlib.Path(base)))))
                    show("escape", lambda: (glob.escape("br[ack].txt"), glob.escape("*.py"), glob.escape("a/b?[c]")))
                    show("escape-glob", lambda: (glob.glob(glob.escape("br[ack].txt"), root_dir=base)))
                    show("escape-space", lambda: (glob.glob(glob.escape("sp ace.txt"), root_dir=base)))
                    show("has-magic", lambda: (glob.has_magic("*"), glob.has_magic("a"), glob.has_magic("[x"), glob.has_magic("?"), glob.has_magic("a]")))
                    show("has-magic-bytes", lambda: (glob.has_magic(b"*"), glob.has_magic(b"a")))
                    show("escape-bytes", lambda: (glob.escape(b"br[ack].txt"), glob.escape(b"*")))
                    show("translate", lambda: (glob.translate("*.py"), glob.translate("a/*/b", recursive=True), glob.translate("**/*.py", recursive=True), glob.translate("**/*.py", recursive=True, include_hidden=True), glob.translate("a?/[*]"), glob.translate("a/b", seps="/"), glob.translate("a.b", seps="./"), glob.translate(""), glob.translate("a/"), glob.translate("a/**", recursive=True), glob.translate("a/**/**/b", recursive=True)))
                    show("magic-check", lambda: (glob.magic_check.pattern))
                    if with_links:
                        show("broken", glob.glob("broken", root_dir=base))
                        show("linkdir", sorted(glob.glob("linka/*", root_dir=base)))
                        show("link-star", sorted(glob.glob("*", root_dir=base)))
                        show("link-rec", sorted(glob.glob("**/*.txt", recursive=True, root_dir=base)))
                    show("pathname-int", lambda: glob.glob(5))
                    show("pathname-none", lambda: glob.glob(None))
                    show("root-int", lambda: glob.glob("*", root_dir=5))
                    show("arity-pos", lambda: glob.glob("*", "."))
                    show("arity-missing", lambda: glob.glob())
                    show("arity-kw", lambda: glob.glob("*", nope=1))
                    show("arity-dup", lambda: glob.glob("*", pathname="x"))
                    show("translate-extra", lambda: glob.translate("*", False))
                    show("translate-int", lambda: glob.translate(5))
                    show("translate-bytes", lambda: glob.translate(b"*"))
                    show("translate-badkw", lambda: glob.translate("*", nope=1))
                    show("escape-missing", lambda: glob.escape())
                    show("escape-extra", lambda: glob.escape("a", "b"))
                    show("hasmagic-extra", lambda: glob.has_magic("a", "b"))
                    show("glob-type", lambda: (type(glob.glob("*", root_dir=base)).__name__))
                    show("all", lambda: (glob.__all__))
                    show("local-iglob", lambda: type(glob.iglob("*", root_dir=base)).__name__)
                    show("local-bytes", lambda: glob.glob(b"*.py", root_dir=base))
                    show("local-dirfd", lambda: glob.glob("*", root_dir=base, dir_fd=0))
                    show("local-names", lambda: sorted(n for n in dir(glob) if not n.startswith("_")))
                    """"
            );
            Assert.Equal(
                Lines(
                    "star ['a', 'aa', 'br[ack].txt', 'broken', 'empty', 'f1.txt', 'f2.py', 'link.txt', 'linka', 'sp ace.txt']",
                    "py ['f2.py']",
                    "q ['a']",
                    "cls ['a', 'aa', 'f1.txt', 'f2.py']",
                    "neg ['br[ack].txt', 'broken', 'empty', 'f1.txt', 'f2.py', 'link.txt', 'linka', 'sp ace.txt']",
                    "dironly ['a/', 'aa/', 'empty/', 'linka/']",
                    "nested ['a/b', 'a/g.py']",
                    "nested2 ['a/b/h.txt', 'linka/b/h.txt']",
                    "literal (['f1.txt'], [])",
                    "literal-dir (['a/'], ['a'], [])",
                    "recursive ['a', 'a/b', 'a/b/h.txt', 'a/g.py', 'aa', 'br[ack].txt', 'broken', 'empty', 'f1.txt', 'f2.py', 'link.txt', 'linka', 'linka/b', 'linka/b/h.txt', 'linka/g.py', 'sp ace.txt']",
                    "recursive-py ['a/g.py', 'f2.py', 'linka/g.py']",
                    "recursive-a ['a/', 'a/b', 'a/b/h.txt', 'a/g.py']",
                    "recursive-mid ['a/b/h.txt', 'linka/b/h.txt']",
                    "starstar-noflag ['a', 'aa', 'br[ack].txt', 'broken', 'empty', 'f1.txt', 'f2.py', 'link.txt', 'linka', 'sp ace.txt']",
                    "hidden-off (['.hid', '.hidden.py'], [])",
                    "hidden-on (['.hid', '.hidden.py'], ['.hidden.py'])",
                    "hidden-rec ['.hid/inside.py', '.hidden.py', 'a/g.py', 'f2.py', 'linka/g.py']",
                    "hidden-seg ['.hid/inside.py', 'a/g.py', 'linka/g.py']",
                    "empty []",
                    "dir-wild ['a/g.py', 'linka/g.py']",
                    "dir-cls ['a/g.py', 'linka/g.py']",
                    "rec-dir-slash ['a/', 'a/b/', 'aa/', 'empty/', 'linka/', 'linka/b/']",
                    "multi-slash ['a/g.py']",
                    "dot-slash ['./f2.py']",
                    "dot (['.'], [])",
                    "root-path ['f2.py']",
                    "escape ('br[[]ack].txt', '[*].py', 'a/b[?][[]c]')",
                    "escape-glob ['br[ack].txt']",
                    "escape-space ['sp ace.txt']",
                    "has-magic (True, False, True, True, False)",
                    "has-magic-bytes (True, False)",
                    "escape-bytes (b'br[[]ack].txt', b'[*]')",
                    "translate ('(?s:(?!\\\\.)[^/]*\\\\.py)\\\\z', '(?s:a/[^/.][^/]*/b)\\\\z', '(?s:(?:[^/.][^/]*/)*(?!\\\\.)[^/]*\\\\.py)\\\\z', '(?s:(?:.+/)?[^/]*\\\\.py)\\\\z', '(?s:a[^/]/[*])\\\\z', '(?s:a/b)\\\\z', '(?s:a[\\\\./]b)\\\\z', '(?s:)\\\\z', '(?s:a/)\\\\z', '(?s:a/(?:[^/.][^/]*/)*(?:[^/.][^/]*)?)\\\\z', '(?s:a/(?:[^/.][^/]*/)*b)\\\\z')",
                    "magic-check '([*?[])'",
                    "broken TypeError 'list' object is not callable",
                    "linkdir TypeError 'list' object is not callable",
                    "link-star TypeError 'list' object is not callable",
                    "link-rec TypeError 'list' object is not callable",
                    "pathname-int TypeError 'int' object is not subscriptable",
                    "pathname-none TypeError 'NoneType' object is not subscriptable",
                    "root-int TypeError expected str, bytes or os.PathLike object, not int",
                    "arity-pos TypeError glob() takes 1 positional argument but 2 were given",
                    "arity-missing TypeError glob() missing 1 required positional argument: 'pathname'",
                    "arity-kw TypeError glob() got an unexpected keyword argument 'nope'",
                    "arity-dup TypeError glob() got multiple values for argument 'pathname'",
                    "translate-extra TypeError translate() takes 1 positional argument but 2 were given",
                    "translate-int TypeError expected string or bytes-like object, got 'int'",
                    "translate-bytes TypeError cannot use a string pattern on a bytes-like object",
                    "translate-badkw TypeError translate() got an unexpected keyword argument 'nope'",
                    "escape-missing TypeError escape() missing 1 required positional argument: 'pathname'",
                    "escape-extra TypeError escape() takes 1 positional argument but 2 were given",
                    "hasmagic-extra TypeError has_magic() takes 1 positional argument but 2 were given",
                    "glob-type 'list'",
                    "all ['glob', 'iglob', 'escape', 'translate']",
                    "local-iglob 'list_iterator'",
                    "local-bytes NotImplementedError bytes pathnames are not supported in this runtime",
                    "local-dirfd NotImplementedError dir_fd is not supported in this runtime",
                    "local-names ['escape', 'glob', 'has_magic', 'iglob', 'magic_check', 'translate']"
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool TryCreateFixture(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "a", "b"));
        Directory.CreateDirectory(Path.Combine(directory, "aa"));
        Directory.CreateDirectory(Path.Combine(directory, "empty"));
        Directory.CreateDirectory(Path.Combine(directory, ".hid"));
        File.WriteAllText(Path.Combine(directory, "f1.txt"), "x");
        File.WriteAllText(Path.Combine(directory, "f2.py"), "x");
        File.WriteAllText(Path.Combine(directory, "sp ace.txt"), "x");
        File.WriteAllText(Path.Combine(directory, "br[ack].txt"), "x");
        File.WriteAllText(Path.Combine(directory, ".hidden.py"), "x");
        File.WriteAllText(Path.Combine(directory, "a", "g.py"), "x");
        File.WriteAllText(Path.Combine(directory, "a", "b", "h.txt"), "x");
        File.WriteAllText(Path.Combine(directory, ".hid", "inside.py"), "x");
        try
        {
            File.CreateSymbolicLink(Path.Combine(directory, "link.txt"), "f1.txt");
            Directory.CreateSymbolicLink(Path.Combine(directory, "linka"), "a");
            File.CreateSymbolicLink(Path.Combine(directory, "broken"), "missing");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
