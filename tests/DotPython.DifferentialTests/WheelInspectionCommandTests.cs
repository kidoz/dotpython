using System.IO.Compression;
using DotPython.Cli;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class WheelInspectionCommandTests
{
    [Fact]
    public void WheelInspect_WritesJsonAndUsesDiagnosticExitCodes()
    {
        using var wheel = WheelFixture.Create();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["wheel", "inspect", wheel.Path],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            "\"compatibility\": \"CpythonStableAbi\"",
            output.ToString(),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "\"code\": \"NativeRuntimeUnsupported\"",
            output.ToString(),
            StringComparison.Ordinal
        );

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var usageExitCode = DotPythonCommand.Run(
            ["wheel", "inspect"],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, usageExitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("usage", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class WheelFixture : IDisposable
    {
        private readonly string _directory;

        private WheelFixture(string directory, string path)
        {
            _directory = directory;
            Path = path;
        }

        internal string Path { get; }

        internal static WheelFixture Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dotpython-wheel-cli-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(
                directory,
                "anyver-1.1.0-cp311-abi3-macosx_11_0_arm64.whl"
            );
            using (var stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(
                    archive,
                    "anyver-1.1.0.dist-info/WHEEL",
                    "Wheel-Version: 1.0\nRoot-Is-Purelib: false\n"
                        + "Tag: cp311-abi3-macosx_11_0_arm64\n"
                );
                WriteEntry(
                    archive,
                    "anyver-1.1.0.dist-info/METADATA",
                    "Metadata-Version: 2.4\nName: anyver\nVersion: 1.1.0\n"
                );
                WriteEntry(archive, "anyver-1.1.0.dist-info/RECORD", string.Empty);
                WriteEntry(archive, "anyver/_anyver.abi3.so", "not executable in this fixture");
            }

            return new WheelFixture(directory, path);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        private static void WriteEntry(ZipArchive archive, string path, string content)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
