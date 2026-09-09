using DotPython.Cli;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CommandDedentationTests
{
    [Theory]
    [InlineData("    print(42)", "42\n")]
    [InlineData("\n  \n    if True:\n        print(42)\n \t\n    print(43)\n ", "42\n43\n")]
    [InlineData("\tif True:\n\t\tprint(42)\n\tprint(43)", "42\n43\n")]
    [InlineData(" \tif True:\n \t    print(42)\n \tprint(43)", "42\n43\n")]
    [InlineData("    print(42)\r\n    print(43)\r\n", "42\n43\n")]
    [InlineData("    value = '''x\n   \n    y'''\n    print(repr(value))", "'x\\n\\ny'\n")]
    [InlineData("value = '''x\n   \ny'''\nprint(repr(value))", "'x\\n   \\ny'\n")]
    [InlineData("", "")]
    [InlineData(" \t\n  \n\t", "")]
    public void Command_DedentsCommonMargin(string source, string expectedOutput)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", source],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(expectedOutput, output.ToString().ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("    print(42)")]
    [InlineData("\n  \n    if True:\n        print(42)\n \t\n    print(43)\n ")]
    [InlineData("\tif True:\n\t\tprint(42)\n\tprint(43)")]
    [InlineData(" \tif True:\n \t    print(42)\n \tprint(43)")]
    [InlineData("    print(42)\r\n    print(43)\r\n")]
    [InlineData("    value = '''x\n   \n    y'''\n    print(repr(value))")]
    [InlineData("value = '''x\n   \ny'''\nprint(repr(value))")]
    public async Task Command_DedentationMatchesPython(string source)
    {
        await CompatibilityOracle.AssertMatchesAsync(source);
    }

    [Theory]
    [InlineData("    print(42)\n\tprint(43)")]
    [InlineData("    print(42)\r\n\r\n    print(43)")]
    [InlineData("    value = '''x\n\t y'''\n    print(repr(value))")]
    public void Command_RejectsIndentationWithoutCommonMargin(string source)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", source],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.NotEqual(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData(false, "    print(42)\n", 1, "")]
    [InlineData(true, "    print(42)\n", 1, "")]
    [InlineData(false, "value = '''x\n   \ny'''\nprint(repr(value))", 0, "'x\\n   \\ny'\n")]
    [InlineData(true, "value = '''x\n   \ny'''\nprint(repr(value))", 0, "'x\\n   \\ny'\n")]
    public void ScriptAndStandardInput_PreserveIndentation(
        bool useScript,
        string source,
        int expectedExitCode,
        string expectedOutput
    )
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dotpython-dedent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "script.py");
            File.WriteAllText(path, source);
            using var input = new StringReader(source);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = DotPythonCommand.Run(
                [useScript ? path : "-"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(expectedExitCode, exitCode);
            Assert.Equal(expectedOutput, output.ToString().ReplaceLineEndings("\n"));
            if (expectedExitCode == 0)
            {
                Assert.Equal(string.Empty, error.ToString());
            }
            else
            {
                Assert.NotEqual(string.Empty, error.ToString());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
