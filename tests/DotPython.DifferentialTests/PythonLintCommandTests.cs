using System.Text.Json;
using DotPython.Cli;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class PythonLintCommandTests
{
    [Fact]
    public void Lint_UndefinedNamesUseHostGlobalsAndJsonLocations()
    {
        const string source = "print(host, missing)\n";
        var result = Run(
            ["lint", "--known-globals", "host", "--output-format", "json", "-"],
            source
        );
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostic = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("DPYL005", diagnostic.GetProperty("code").GetString());
        Assert.Equal(
            "Name 'missing' has no visible definition.",
            diagnostic.GetProperty("message").GetString()
        );
        Assert.Equal(13, diagnostic.GetProperty("column").GetInt32());
        Assert.Equal(20, diagnostic.GetProperty("endColumn").GetInt32());
        Assert.Equal(0, Run(["lint", "--known-globals", "host, missing", "-"], source).ExitCode);
        Assert.Equal(0, Run(["lint", "--ignore", "DPYL005", "-"], source).ExitCode);
        Assert.Equal(
            1,
            Run(
                ["lint", "--known-globals", "host,missing", "--known-globals", "host", "-"],
                source
            ).ExitCode
        );
        Assert.Equal(1, Run(["lint", "--known-globals", "", "-"], source).ExitCode);
    }

    [Fact]
    public void Lint_UnusedImportsAreEnabledByDefaultWithoutImportingPackages()
    {
        var result = Run(
            ["lint", "--output-format", "json", "-"],
            "import package_that_does_not_exist as unused\n"
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostic = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("DPYL004", diagnostic.GetProperty("code").GetString());
        Assert.Equal(8, diagnostic.GetProperty("column").GetInt32());
        Assert.Equal(
            0,
            Run(
                ["lint", "--ignore", "DPYL004", "-"],
                "import package_that_does_not_exist\n"
            ).ExitCode
        );
    }

    [Fact]
    public void Lint_StdinWritesJsonWithFilenameAndPositions()
    {
        var result = Run(
            ["lint", "--output-format", "json", "--stdin-filename", "buffer.py", "-"],
            "assert (False,)\n"
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostic = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("buffer.py", diagnostic.GetProperty("file").GetString());
        Assert.Equal("DPYL003", diagnostic.GetProperty("code").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(1, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(8, diagnostic.GetProperty("column").GetInt32());
        Assert.Equal(16, diagnostic.GetProperty("endColumn").GetInt32());
    }

    [Fact]
    public void Lint_CleanJsonIsEmptyArrayAndDoesNotExecuteSource()
    {
        var result = Run(
            ["lint", "--output-format", "json", "-"],
            "raise RuntimeError('must not execute')\n"
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Empty(json.RootElement.EnumerateArray());
    }

    [Theory]
    [InlineData("--known-globals", "host.value")]
    [InlineData("--known-globals", "host,,other")]
    [InlineData("--known-globals", "class")]
    [InlineData("--bad", "x")]
    [InlineData("--output-format", "xml")]
    [InlineData("--select", "DPYL999")]
    [InlineData("--ignore", "DPYL")]
    public void Lint_InvalidOptionsReturnUsageError(string option, string value)
    {
        var result = Run(["lint", option, value, "-"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public void Lint_RequiresInputsAndOptionValues()
    {
        Assert.Equal(2, Run(["lint"]).ExitCode);
        Assert.Equal(2, Run(["lint", "--select"]).ExitCode);
        Assert.Equal(2, Run(["lint", "-", "example.py"]).ExitCode);
        Assert.Equal(
            2,
            Run([
                "lint",
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.py"),
            ]).ExitCode
        );
        Assert.Equal(0, Run(["lint", "--help"]).ExitCode);
    }

    [Fact]
    public void Lint_SelectionDoesNotSuppressSyntaxErrors()
    {
        var result = Run(["lint", "--select", "DPYL002", "--ignore", "DPYL002", "-"], "def :\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("DPY2", result.Output, StringComparison.Ordinal);
        Assert.Equal(0, Run(["lint", "--ignore", "DPYL003", "-"], "assert (False,)\n").ExitCode);
    }

    [Fact]
    public void Lint_DirectoryDiscoveryIsSortedDeduplicatedAndSkipsGeneratedDirectories()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "dotpython-lint-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        Directory.CreateDirectory(Path.Combine(directory, ".venv"));
        try
        {
            var first = Path.Combine(directory, "a.py");
            File.WriteAllText(first, "assert (False,)\n");
            File.WriteAllText(Path.Combine(directory, "nested", "b.py"), "assert (False,)\n");
            File.WriteAllText(Path.Combine(directory, "z.pyi"), "def f(x=[]): ...\n");
            File.WriteAllText(Path.Combine(directory, ".venv", "skip.py"), "assert (False,)\n");
            File.WriteAllText(Path.Combine(directory, "skip.txt"), "assert (False,)\n");

            var result = Run(["lint", directory, first]);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Error);
            var lines = result.Output.Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            );
            Assert.Equal(3, lines.Length);
            Assert.StartsWith(first, lines[0], StringComparison.Ordinal);
            Assert.Contains("b.py:", lines[1], StringComparison.Ordinal);
            Assert.Contains("z.pyi:", lines[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Lint_ReportsCancellationOnErrorStream()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = DotPythonCommand.Run(
            ["lint", "-"],
            TextReader.Null,
            output,
            error,
            cancellation.Token
        );

        Assert.Equal(130, code);
        Assert.Empty(output.ToString());
        Assert.Contains("cancelled", error.ToString(), StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Error) Run(
        string[] arguments,
        string text = ""
    )
    {
        using var input = new StringReader(text);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = DotPythonCommand.Run(
            arguments,
            input,
            output,
            error,
            TestContext.Current.CancellationToken
        );
        return (code, output.ToString(), error.ToString());
    }
}
