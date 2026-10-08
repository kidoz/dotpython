using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class NamedEscapeExecutionTests
{
    [Fact]
    public void NamedEscapeResolvesItsCharacter()
    {
        var output = Run(
            """
            print("\N{SNOWMAN}")
            print(repr("\N{GRINNING FACE}"), len("\N{GRINNING FACE}"))
            print(repr("\N{LF}"), repr("\N{NUL}"))
            print(repr("\N{CJK UNIFIED IDEOGRAPH-4E00}"))
            print(repr("\N{TANGUT IDEOGRAPH-17000}"))
            """
        );

        Assert.Equal(
            string.Join(Environment.NewLine, "☃", "'😀' 1", "'\\n' '\\x00'", "'一'", "'𗀀'", ""),
            output
        );
    }

    [Fact]
    public void NamedEscapeMatchingIgnoresCase()
    {
        var output = Run(
            """
            print("\N{snowman}", "\N{SnowMan}", "\N{cjk unified ideograph-4e00}")
            """
        );

        Assert.Equal($"☃ ☃ 一{Environment.NewLine}", output);
    }

    [Theory]
    [InlineData("SNOW_MAN")]
    [InlineData("SNOW-MAN")]
    [InlineData("SNOW MAN")]
    [InlineData(" snowman ")]
    [InlineData("SNOWMEN")]
    [InlineData("KEYCAP NUMBER SIGN")]
    public void NamesThatAreNotExactMatchesAreRejected(string name)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            $"value = \"\\N{{{name}}}\"\n",
            "named_escape_rejection.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY3003", diagnostic.Code);
        Assert.Contains(
            "unknown Unicode character name",
            diagnostic.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void NamedEscapeWorksInFormattedAndTemplateStrings()
    {
        var output = Run(
            """
            x = 1
            print(f"a\N{SNOWMAN}b{x}")
            print(f"\N{SNOWMAN}{{literal}}")
            print(t"a\N{SNOWMAN}b")
            print(r"\N{SNOWMAN}")
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "a☃b1",
                "☃{literal}",
                "Template(strings=('a☃b',), interpolations=())",
                "\\N{SNOWMAN}",
                ""
            ),
            output
        );
    }

    [Theory]
    [InlineData("value = \"\\N\"", 0, 1)]
    [InlineData("value = \"\\N{\"", 0, 2)]
    [InlineData("value = \"\\N{}\"", 0, 2)]
    [InlineData("value = \"\\N{SNOWMAN\"", 0, 9)]
    [InlineData("value = \"ab\\N{NOPE}cd\"", 2, 9)]
    public void MalformedNamedEscapesReportTheConsumedRange(string source, int start, int end)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "named_escape_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY3003", diagnostic.Code);
        Assert.Contains(
            $"'unicodeescape' codec can't decode bytes in position {start}-{end}:",
            diagnostic.Message,
            StringComparison.Ordinal
        );
        Assert.True(
            diagnostic.Message.Contains("unknown Unicode character name", StringComparison.Ordinal)
                || diagnostic.Message.Contains(
                    "malformed \\N character escape",
                    StringComparison.Ordinal
                )
        );
    }

    [Fact]
    public void AnUnterminatedFormattedNamedEscapeReportsInsteadOfThrowing()
    {
        // The formatted-string call sites have no constant decoder around them, so a
        // bad named escape must report rather than escape the compiler.
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "value = f\"\\N{SNOWMAN\"\n",
            "named_escape_unterminated_fstring.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "named_escape_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
