using Xunit;

namespace DotPython.DifferentialTests;

public sealed class GlobModuleCompatibilityTests
{
    /// <summary>
    /// The `glob` module over a fixture tree beneath the search root: wildcards, classes,
    /// the literal fast path, hidden names (with and without `include_hidden`), the
    /// recursive `**` walk with symlinked directories and a broken link, `root_dir`, the
    /// `escape`/`has_magic`/`translate` surface and CPython's refusal wording. Every case
    /// catches what it exercises, so the reference exits 0.
    /// </summary>
    [Fact]
    public async Task TheGlobModuleMatchesCpython()
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
            await CompatibilityOracle.AssertMatchesAsync(
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
                    """"
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
}
