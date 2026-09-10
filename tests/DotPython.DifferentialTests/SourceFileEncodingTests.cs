using System.Text;
using DotPython.Cli;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SourceFileEncodingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScriptAndImports_DecodeLatin1AndIgnoreUnimportedInvalidSources(bool import)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-cli-encoding-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var encoded = Path.Combine(directory, "encoded.py");
            File.WriteAllBytes(
                encoded,
                Encoding.Latin1.GetBytes(
                    "#!/usr/bin/python\r\n# coding: latin1\r\nprint('café')\r\n"
                )
            );
            File.WriteAllBytes(Path.Combine(directory, "unused.py"), [0xff]);
            var entry = Path.Combine(directory, "main.py");
            File.WriteAllText(entry, "import encoded");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = DotPythonCommand.Run(
                [import ? entry : encoded],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(0, exitCode);
            Assert.Equal("café\n", output.ToString().ReplaceLineEndings("\n"));
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("# coding: ascii\nprint('é')")]
    [InlineData("# coding: nonexistent\nprint(42)")]
    public void MalformedScript_FailsWithSyntaxErrorAndExitOne(string code)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-cli-bad-encoding-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "bad.py");
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(code));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = DotPythonCommand.Run(
                [path],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("SyntaxError", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public Task CommandStrings_IgnoreByteEncodingCookies() =>
        CompatibilityOracle.AssertMatchesAsync("# coding: ascii\nprint('é')");
}
