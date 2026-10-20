using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class OsModuleExecutionTests
{
    /// <summary>
    /// The read-only half of `os` beneath the search root: the working directory, the
    /// process id, `listdir` and `scandir` over a fixture tree (names, joined paths,
    /// `is_file`/`is_dir`/`is_symlink` through symlinks and a broken link), the
    /// `os.path` string helpers and existence probes, and CPython's errno wording. The
    /// runtime's own refusals — writes, metadata, the process environment, the working
    /// directory and paths outside the search roots — are pinned separately.
    /// </summary>
    [Fact]
    public void TheOsModuleMatchesCpythonBeneathTheSearchRoot()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), FixtureName);
        if (!TryCreateFixture(directory))
        {
            Assert.Skip("symbolic links are not available");
            return;
        }
        try
        {
            var output = Run(
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
            Assert.Equal(
                Lines(
                    "getcwd True True",
                    "getcwdb bytes True True",
                    "getpid int True",
                    "listdir ['.hidden', 'a.txt', 'badlink', 'dirlink', 'empty', 'link', 'space name.txt', 'sub'] ['deeper', 'nested.txt'] []",
                    "listdir-empty FileNotFoundError: [Errno 2] No such file or directory: '' | errno=2 | fn=",
                    "listdir-err FileNotFoundError: [Errno 2] No such file or directory: 'os-fixture/nope' | errno=2 | fn=os-fixture/nope NotADirectoryError: [Errno 20] Not a directory: 'os-fixture/a.txt' | errno=20 | fn=os-fixture/a.txt NotADirectoryError: [Errno 20] Not a directory: 'os-fixture/link' | errno=20 | fn=os-fixture/link FileNotFoundError: [Errno 2] No such file or directory: 'os-fixture/badlink' | errno=2 | fn=os-fixture/badlink",
                    "scandir [('.hidden', 'os-fixture/.hidden', True, False, False), ('a.txt', 'os-fixture/a.txt', True, False, False), ('badlink', 'os-fixture/badlink', False, False, True), ('dirlink', 'os-fixture/dirlink', False, True, True), ('empty', 'os-fixture/empty', False, True, False), ('link', 'os-fixture/link', True, False, True), ('space name.txt', 'os-fixture/space name.txt', True, False, False), ('sub', 'os-fixture/sub', False, True, False)]",
                    "scandir-sub [('deeper', 'os-fixture/sub/deeper', False, True, False), ('nested.txt', 'os-fixture/sub/nested.txt', True, False, False)]",
                    "scandir-empty []",
                    "scandir-repr True",
                    "scandir-list []",
                    "scandir-err FileNotFoundError: [Errno 2] No such file or directory: 'os-fixture/nope' | errno=2 | fn=os-fixture/nope NotADirectoryError: [Errno 20] Not a directory: 'os-fixture/a.txt' | errno=20 | fn=os-fixture/a.txt FileNotFoundError: [Errno 2] No such file or directory: 'os-fixture/badlink' | errno=2 | fn=os-fixture/badlink FileNotFoundError: [Errno 2] No such file or directory: '' | errno=2 | fn=",
                    "consts ('/', None, ':', '.', '..', '\\n', '.', '/dev/null', 'posix')",
                    "join 'a/b' 'a/b' '/b' '/b' '' 'a/b/c'",
                    "join-arity TypeError: join() missing 1 required positional argument: 'a' | errno=None | fn=None",
                    "split ('a/b', 'c') ('a', '') ('/', '') ('', 'abc') ('//', 'a') ('', '') ('a', 'b')",
                    "dirname 'a/b' 'a' '/' '' ''",
                    "basename 'c' '' '' '' 'abc'",
                    "splitdrive ('', 'a/b') ('', '/a') ('', '')",
                    "splitext ('a/b.tar', '.gz') ('.bashrc', '') ('a', '.') ('a', '') ('dir.d/file', '') ('', '') ('..a', '') ('a.', '.b')",
                    "normcase 'A/B' ''",
                    "normpath 'a/b/d' 'a' '.' '//a/b' '.' '../a' '/' '/a' '..'",
                    "isabs True False False True",
                    "abspath True True True",
                    "probes (True, False, True, False, True, False) (True, True, False) (True, False, True, False, False) (True, False, True, False, False) (True, False, True, True, False)",
                    "commonprefix 'a' '' ''",
                    "survives ['.hidden', 'a.txt', 'badlink', 'dirlink', 'empty', 'link', 'space name.txt', 'sub']"
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The capability boundary: every filesystem mutation, metadata query, symlink
    /// operation, working-directory change and process-environment access refuses in the
    /// runtime's own words, and the fixture is unchanged afterwards.
    /// </summary>
    [Fact]
    public void TheOsModuleRefusesHostCapabilitiesOutsideTheSlice()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), FixtureName);
        if (!TryCreateFixture(directory))
        {
            Assert.Skip("symbolic links are not available");
            return;
        }
        try
        {
            var output = Run(
                """"
                import os, os.path as p
                base = "os-fixture"
                def show(label, *fns):
                    for f in fns:
                        try:
                            print(label, "ok", repr(f()))
                        except Exception as e:
                            print(label, type(e).__name__ + ": " + str(e))
                show("chdir", lambda: os.chdir(base + "/sub"))
                show("stat", lambda: os.stat(base + "/a.txt"), lambda: os.lstat(base + "/a.txt"))
                show("readlink", lambda: os.readlink(base + "/link"))
                show("symlink", lambda: os.symlink(base + "/a.txt", base + "/l2"))
                show("env", lambda: os.getenv("DPY_OS_TEST"), lambda: os.putenv("DPY_OS_TEST", "v"), lambda: os.unsetenv("DPY_OS_TEST"))
                show("environ", lambda: repr(os.environ), lambda: os.environ.get("DPY_OS_TEST"), lambda: os.environ["DPY_OS_TEST"], lambda: list(os.environ), lambda: len(os.environ), lambda: "DPY_OS_TEST" in os.environ, lambda: os.environ.keys(), lambda: type(os.environ).__name__, lambda: type(os.environ).__module__)
                show("mkdir", lambda: os.mkdir(base + "/x"), lambda: os.mkdir(base + "/x", 0o755))
                show("makedirs", lambda: os.makedirs(base + "/x/y"), lambda: os.makedirs(base + "/x/y", 0o755, True))
                show("rmdir", lambda: os.rmdir(base + "/empty"))
                show("remove", lambda: os.remove(base + "/a.txt"), lambda: os.unlink(base + "/a.txt"))
                show("rename", lambda: os.rename(base + "/a.txt", base + "/b.txt"), lambda: os.replace(base + "/a.txt", base + "/b.txt"))
                show("meta", lambda: p.getsize(base + "/a.txt"), lambda: p.getmtime(base + "/a.txt"), lambda: next(iter(os.scandir(base))).stat(), lambda: next(iter(os.scandir(base))).inode())
                show("scope", lambda: os.listdir("/"), lambda: p.exists("/etc/passwd"), lambda: os.scandir("/"))
                show("arity", lambda: p.exists(), lambda: p.split(), lambda: os.getcwd(1), lambda: os.listdir(1, 2))
                show("survives", lambda: sorted(os.listdir(base)), lambda: sorted(os.listdir(base + "/empty")))
                """"
            );
            Assert.Equal(
                Lines(
                    "chdir PermissionError: os.chdir() requires a host-process capability that this runtime slice does not provide.",
                    "stat NotImplementedError: os.stat() is not supported in this runtime slice (file metadata, ownership and symlinks are out of scope).",
                    "stat NotImplementedError: os.lstat() is not supported in this runtime slice (file metadata, ownership and symlinks are out of scope).",
                    "readlink NotImplementedError: os.readlink() is not supported in this runtime slice (file metadata, ownership and symlinks are out of scope).",
                    "symlink PermissionError: os.symlink() requires a filesystem write capability that this runtime slice does not provide.",
                    "env PermissionError: os.getenv() requires a host-process capability that this runtime slice does not provide.",
                    "env PermissionError: os.putenv() requires a host-process capability that this runtime slice does not provide.",
                    "env PermissionError: os.unsetenv() requires a host-process capability that this runtime slice does not provide.",
                    "environ PermissionError: os.environ requires a host-process capability that this runtime slice does not provide.",
                    "environ PermissionError: os.environ requires a host-process capability that this runtime slice does not provide.",
                    "environ PermissionError: os.environ requires a host-process capability that this runtime slice does not provide.",
                    "environ PermissionError: os.environ requires a host-process capability that this runtime slice does not provide.",
                    "environ PermissionError: os.environ requires a host-process capability that this runtime slice does not provide.",
                    "environ PermissionError: os.environ requires a host-process capability that this runtime slice does not provide.",
                    "environ PermissionError: os.environ requires a host-process capability that this runtime slice does not provide.",
                    "environ ok '_Environ'",
                    "environ ok 'os'",
                    "mkdir PermissionError: os.mkdir() requires a filesystem write capability that this runtime slice does not provide.",
                    "mkdir PermissionError: os.mkdir() requires a filesystem write capability that this runtime slice does not provide.",
                    "makedirs PermissionError: os.makedirs() requires a filesystem write capability that this runtime slice does not provide.",
                    "makedirs PermissionError: os.makedirs() requires a filesystem write capability that this runtime slice does not provide.",
                    "rmdir PermissionError: os.rmdir() requires a filesystem write capability that this runtime slice does not provide.",
                    "remove PermissionError: os.remove() requires a filesystem write capability that this runtime slice does not provide.",
                    "remove PermissionError: os.remove() requires a filesystem write capability that this runtime slice does not provide.",
                    "rename PermissionError: os.rename() requires a filesystem write capability that this runtime slice does not provide.",
                    "rename PermissionError: os.rename() requires a filesystem write capability that this runtime slice does not provide.",
                    "meta NotImplementedError: os.path.getsize() is not supported in this runtime slice (file metadata, ownership and symlinks are out of scope).",
                    "meta NotImplementedError: os.path.getmtime() is not supported in this runtime slice (file metadata, ownership and symlinks are out of scope).",
                    "meta NotImplementedError: os.DirEntry.stat() is not supported in this runtime slice (file metadata, ownership and symlinks are out of scope).",
                    "meta NotImplementedError: os.DirEntry.inode() is not supported in this runtime slice (file metadata, ownership and symlinks are out of scope).",
                    "scope PermissionError: os.listdir() outside the registered module search roots is not permitted in this runtime slice.",
                    "scope PermissionError: os.path.exists() outside the registered module search roots is not permitted in this runtime slice.",
                    "scope PermissionError: os.scandir() outside the registered module search roots is not permitted in this runtime slice.",
                    "arity TypeError: exists() takes 1 to 1 arguments (0 given)",
                    "arity TypeError: split() takes 1 to 1 arguments (0 given)",
                    "arity TypeError: getcwd() takes 0 to 0 arguments (1 given)",
                    "arity TypeError: listdir() takes 0 to 1 arguments (2 given)",
                    "survives ok ['.hidden', 'a.txt', 'badlink', 'dirlink', 'empty', 'link', 'space name.txt', 'sub']",
                    "survives ok []"
                ),
                output
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

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine(
            new ManagedModuleDiscoveryOptions { SearchPaths = [Directory.GetCurrentDirectory()] }
        ).Execute(
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
