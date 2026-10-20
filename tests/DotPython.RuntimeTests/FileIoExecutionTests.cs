using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

/// <summary>
/// The file-backed `io` surface: binary reads, encodings, newline modes, the io class
/// constructors, and the read-only capability boundary that refuses write modes in the
/// runtime's own words.
/// </summary>
public sealed class FileIoExecutionTests
{
    [Fact]
    public void BinaryTextAndIoConstructorsReadBeneathTheSearchRoot()
    {
        var directory = Directory.CreateTempSubdirectory("dotpython-file-io-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "data.txt"), "alpha\nbeta\ngamma");
            File.WriteAllText(Path.Combine(directory, "crlf.txt"), "a\r\nb\rc\n");
            File.WriteAllBytes(
                Path.Combine(directory, "latin.txt"),
                [0x68, 0xe9, 0x6c, 0x6c, 0x6f, 0x0a]
            );
            File.WriteAllBytes(Path.Combine(directory, "bin.dat"), [0x00, 0x01, 0x0a, 0xff]);
            var output = Run(
                directory,
                $$"""
                import io
                base = "{{directory}}"
                b = open(base + "/bin.dat", "rb")
                print(type(b).__name__, b.mode, repr(b.read()))
                b.seek(0)
                print(repr(b.readline()), repr(b.readlines()))
                with open(base + "/data.txt") as f:
                    print(repr(f.read(3)), f.tell(), repr(f.readline()))
                print(repr(open(base + "/latin.txt", encoding="latin-1").read()))
                print(repr(open(base + "/crlf.txt", newline="").read()))
                f = open(base + "/crlf.txt")
                print([repr(line) for line in f], repr(f.newlines))
                try:
                    open(base + "/data.txt", "w")
                except PermissionError as e:
                    print("write:", e)
                f = open(base + "/data.txt")
                try:
                    f.write("x")
                except io.UnsupportedOperation as e:
                    print("unsupported:", e)
                print(repr(io.BufferedReader(io.FileIO(base + "/bin.dat")).read()))
                print(repr(io.TextIOWrapper(io.BytesIO(b"h\xe9llo\n"), encoding="latin-1").read()))
                """
            );

            Assert.Equal(
                string.Join(
                    Environment.NewLine,
                    "BufferedReader rb b'\\x00\\x01\\n\\xff'",
                    "b'\\x00\\x01\\n' [b'\\xff']",
                    "'alp' 3 'ha\\n'",
                    "'héllo\\n'",
                    "'a\\r\\nb\\rc\\n'",
                    "[\"'a\\\\n'\", \"'b\\\\n'\", \"'c\\\\n'\"] ('\\r', '\\n', '\\r\\n')",
                    "write: open() mode 'w' requires a filesystem write capability that this runtime slice does not provide.",
                    "unsupported: not writable",
                    "b'\\x00\\x01\\n\\xff'",
                    "'héllo\\n'",
                    ""
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileReadsOutsideTheSearchRootsAreRefused()
    {
        var directory = Directory.CreateTempSubdirectory("dotpython-file-io-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("dotpython-file-io-outside-").FullName;
        try
        {
            var secret = Path.Combine(outside, "secret.txt");
            File.WriteAllText(secret, "secret");
            var output = Run(
                directory,
                $$"""
                def show(label, action):
                    try: action()
                    except PermissionError as error: print(label, type(error).__name__, error)
                show("outside", lambda: open("{{secret}}"))
                show("fileio", lambda: __import__("io").FileIO("{{secret}}"))
                """
            );

            Assert.Equal(
                string.Join(
                    Environment.NewLine,
                    "outside PermissionError open() outside the registered module search roots is not permitted in this runtime slice.",
                    "fileio PermissionError open() outside the registered module search roots is not permitted in this runtime slice.",
                    ""
                ),
                output
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static string Run(string directory, string source)
    {
        var engine = new ManagedPythonEngine(
            new ManagedModuleDiscoveryOptions { SearchPaths = [directory] }
        );
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "file_io_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
