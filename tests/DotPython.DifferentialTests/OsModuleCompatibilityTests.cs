using Xunit;

namespace DotPython.DifferentialTests;

public sealed class OsModuleCompatibilityTests
{
    /// <summary>
    /// The read-only `os` surface against CPython 3.14: `getcwd`/`getcwdb`/`getpid`,
    /// `listdir` and `scandir` over a fixture tree with symlinks and a broken link, the
    /// `os.path` string helpers, existence probes and the errno attributes of failures.
    /// The capability refusals are runtime policy, so they are pinned in the runtime
    /// tests rather than compared with the host interpreter.
    /// </summary>
    [Fact]
    public async Task TheOsModuleMatchesCpython()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), FixtureName);
        if (!TryCreateFixture(directory))
        {
            Assert.Skip("symbolic links are not available");
            return;
        }
        try
        {
            await CompatibilityOracle.AssertMatchesAsync(
                """"
                import os, os.path as p
                base = "os-fixture"
                def show(label, *fns):
                    out = []
                    for f in fns:
                        try:
                            out.append(repr(f()))
                        except Exception as e:
                            out.append(type(e).__name__ + ": " + str(e) + " | errno=" + str(getattr(e, "errno", None)) + " | fn=" + str(getattr(e, "filename", None)))
                    print(label, *out)
                print("getcwd", os.getcwd() == p.abspath("."), p.abspath("") == os.getcwd())
                print("getcwdb", type(os.getcwdb()).__name__, isinstance(os.getcwdb(), bytes), os.getcwdb() == os.getcwd().encode())
                print("getpid", type(os.getpid()).__name__, os.getpid() > 0)
                show("listdir", lambda: sorted(os.listdir(base)), lambda: sorted(os.listdir(base + "/sub")), lambda: sorted(os.listdir(base + "/empty")))
                show("listdir-empty", lambda: os.listdir(""))
                show("listdir-err", lambda: os.listdir(base + "/nope"), lambda: os.listdir(base + "/a.txt"), lambda: os.listdir(base + "/link"), lambda: os.listdir(base + "/badlink"))
                show("scandir", lambda: sorted((e.name, e.path, e.is_file(), e.is_dir(), e.is_symlink()) for e in os.scandir(base)))
                show("scandir-sub", lambda: sorted((e.name, e.path, e.is_file(), e.is_dir(), e.is_symlink()) for e in os.scandir(base + "/sub")))
                show("scandir-empty", lambda: list(os.scandir(base + "/empty")))
                show("scandir-repr", lambda: repr(next(iter(os.scandir(base)))).startswith("<DirEntry '"))
                show("scandir-list", lambda: sorted(e.name for e in list(os.scandir(base + "/empty"))))
                show("scandir-err", lambda: os.scandir(base + "/nope"), lambda: os.scandir(base + "/a.txt"), lambda: os.scandir(base + "/badlink"), lambda: os.scandir(""))
                show("consts", lambda: (os.sep, os.altsep, os.pathsep, os.curdir, os.pardir, os.linesep, os.extsep, os.devnull, os.name))
                show("join", lambda: p.join("a", "b"), lambda: p.join("a/", "b"), lambda: p.join("a", "/b"), lambda: p.join("/", "b"), lambda: p.join(""), lambda: p.join("a", "b", "c"))
                show("join-arity", lambda: p.join())
                show("split", lambda: p.split("a/b/c"), lambda: p.split("a/"), lambda: p.split("/"), lambda: p.split("abc"), lambda: p.split("//a"), lambda: p.split(""), lambda: p.split("a//b"))
                show("dirname", lambda: p.dirname("a/b/c"), lambda: p.dirname("a/"), lambda: p.dirname("/"), lambda: p.dirname(""), lambda: p.dirname("abc"))
                show("basename", lambda: p.basename("a/b/c"), lambda: p.basename("a/"), lambda: p.basename("/"), lambda: p.basename(""), lambda: p.basename("abc"))
                show("splitdrive", lambda: p.splitdrive("a/b"), lambda: p.splitdrive("/a"), lambda: p.splitdrive(""))
                show("splitext", lambda: p.splitext("a/b.tar.gz"), lambda: p.splitext(".bashrc"), lambda: p.splitext("a."), lambda: p.splitext("a"), lambda: p.splitext("dir.d/file"), lambda: p.splitext(""), lambda: p.splitext("..a"), lambda: p.splitext("a..b"))
                show("normcase", lambda: p.normcase("A/B"), lambda: p.normcase(""))
                show("normpath", lambda: p.normpath("a//b/./c/../d"), lambda: p.normpath("./a"), lambda: p.normpath(""), lambda: p.normpath("//a//b"), lambda: p.normpath("a/.."), lambda: p.normpath("../a"), lambda: p.normpath("/.."), lambda: p.normpath("///a"), lambda: p.normpath("a/../.."))
                show("isabs", lambda: p.isabs("/a"), lambda: p.isabs("a"), lambda: p.isabs(""), lambda: p.isabs("//a"))
                show("abspath", lambda: p.abspath(base + "/a.txt").endswith("/os-fixture/a.txt"), lambda: p.abspath(base + "/nope").endswith("/os-fixture/nope"), lambda: p.abspath(base + "/a.txt/..").endswith("/os-fixture"))
                show("probes", lambda: (p.exists(base + "/a.txt"), p.exists(base + "/nope"), p.exists(base + "/link"), p.exists(base + "/badlink"), p.exists(base + "/sub"), p.exists("")), lambda: (p.lexists(base + "/link"), p.lexists(base + "/badlink"), p.lexists("")), lambda: (p.isfile(base + "/a.txt"), p.isfile(base + "/sub"), p.isfile(base + "/link"), p.isfile(base + "/badlink"), p.isfile("")), lambda: (p.isdir(base + "/sub"), p.isdir(base + "/a.txt"), p.isdir(base + "/dirlink"), p.isdir(base + "/badlink"), p.isdir("")), lambda: (p.islink(base + "/link"), p.islink(base + "/a.txt"), p.islink(base + "/badlink"), p.islink(base + "/dirlink"), p.islink("")))
                show("commonprefix", lambda: p.commonprefix(["ab", "abc", "a"]), lambda: p.commonprefix([]), lambda: p.commonprefix(["a", "b"]))
                show("survives", lambda: sorted(os.listdir(base)))
                """"
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string FixtureName = "os-fixture";

    private static bool TryCreateFixture(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A fixture left behind by an earlier run.
        }
        Directory.CreateDirectory(Path.Combine(directory, "sub", "deeper"));
        Directory.CreateDirectory(Path.Combine(directory, "empty"));
        File.WriteAllText(Path.Combine(directory, "a.txt"), "abc");
        File.WriteAllText(Path.Combine(directory, ".hidden"), "x");
        File.WriteAllText(Path.Combine(directory, "space name.txt"), "y");
        File.WriteAllText(Path.Combine(directory, "sub", "nested.txt"), "z");
        File.WriteAllText(Path.Combine(directory, "sub", "deeper", "leaf.txt"), "q");
        try
        {
            File.CreateSymbolicLink(Path.Combine(directory, "link"), "a.txt");
            File.CreateSymbolicLink(Path.Combine(directory, "badlink"), "missing");
            Directory.CreateSymbolicLink(Path.Combine(directory, "dirlink"), "sub");
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
