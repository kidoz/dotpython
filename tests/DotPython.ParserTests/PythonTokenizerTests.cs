using DotPython.Language.Syntax;
using DotPython.Language.Text;
using Xunit;

namespace DotPython.ParserTests;

public sealed class PythonTokenizerTests
{
    [Fact]
    public void Tokenize_RecognizesNamesNumbersAndOperators()
    {
        const string code = "if café := .5 + 0xCA_FE ** 2j: ...";

        var result = Tokenize(code);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                SyntaxTokenKind.Identifier,
                SyntaxTokenKind.Identifier,
                SyntaxTokenKind.ColonEqual,
                SyntaxTokenKind.FloatLiteral,
                SyntaxTokenKind.Plus,
                SyntaxTokenKind.IntegerLiteral,
                SyntaxTokenKind.DoubleStar,
                SyntaxTokenKind.ImaginaryLiteral,
                SyntaxTokenKind.Colon,
                SyntaxTokenKind.Ellipsis,
                SyntaxTokenKind.NewLine,
                SyntaxTokenKind.EndOfFile,
            ],
            result.Tokens.Select(token => token.Kind)
        );
        Assert.Equal("if", result.Tokens[0].Text);
        Assert.Equal("café", result.Tokens[1].Text);
        Assert.Equal("", result.Tokens[^2].Text);
    }

    [Fact]
    public void Tokenize_PrefersLongestExactOperator()
    {
        const string code =
            "== != <= >= << >> ** // := -> += -= *= /= %= &= |= ^= <<= >>= **= //= @=";

        var result = Tokenize(code);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                SyntaxTokenKind.EqualEqual,
                SyntaxTokenKind.NotEqual,
                SyntaxTokenKind.LessThanOrEqual,
                SyntaxTokenKind.GreaterThanOrEqual,
                SyntaxTokenKind.LeftShift,
                SyntaxTokenKind.RightShift,
                SyntaxTokenKind.DoubleStar,
                SyntaxTokenKind.DoubleSlash,
                SyntaxTokenKind.ColonEqual,
                SyntaxTokenKind.Arrow,
                SyntaxTokenKind.PlusEqual,
                SyntaxTokenKind.MinusEqual,
                SyntaxTokenKind.StarEqual,
                SyntaxTokenKind.SlashEqual,
                SyntaxTokenKind.PercentEqual,
                SyntaxTokenKind.AmpersandEqual,
                SyntaxTokenKind.VerticalBarEqual,
                SyntaxTokenKind.CaretEqual,
                SyntaxTokenKind.LeftShiftEqual,
                SyntaxTokenKind.RightShiftEqual,
                SyntaxTokenKind.DoubleStarEqual,
                SyntaxTokenKind.DoubleSlashEqual,
                SyntaxTokenKind.AtEqual,
                SyntaxTokenKind.NewLine,
                SyntaxTokenKind.EndOfFile,
            ],
            result.Tokens.Select(token => token.Kind)
        );
    }

    [Fact]
    public void Tokenize_EmitsIndentAndDedentAroundLogicalLines()
    {
        const string code = "if ready:\n\tvalue = 1\nvalue = 2";

        var result = Tokenize(code);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Tokens.Count(token => token.Kind == SyntaxTokenKind.Indent));
        Assert.Equal(1, result.Tokens.Count(token => token.Kind == SyntaxTokenKind.Dedent));
        Assert.Equal(
            "\t",
            result.Tokens.Single(token => token.Kind == SyntaxTokenKind.Indent).Text
        );

        var dedent = result.Tokens.Single(token => token.Kind == SyntaxTokenKind.Dedent);
        Assert.Equal(0, dedent.Span.Length);
        Assert.Equal(code.LastIndexOf("value", StringComparison.Ordinal), dedent.Span.Start);
    }

    [Fact]
    public void Tokenize_DistinguishesNonSignificantPhysicalNewLines()
    {
        const string code = "# heading\n\nvalue = (\n    1\n)\n";

        var result = Tokenize(code);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            4,
            result.Tokens.Count(token => token.Kind == SyntaxTokenKind.NonSignificantNewLine)
        );
        Assert.Equal(1, result.Tokens.Count(token => token.Kind == SyntaxTokenKind.NewLine));
        Assert.DoesNotContain(result.Tokens, token => token.Kind == SyntaxTokenKind.Indent);
    }

    [Fact]
    public void Tokenize_DeletesExplicitLineJoiningPair()
    {
        const string code = "value = 1 + \\\r\n    2";

        var result = Tokenize(code);

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(
            result.Tokens,
            token => token.Kind == SyntaxTokenKind.NonSignificantNewLine
        );
        Assert.DoesNotContain(result.Tokens, token => token.Kind == SyntaxTokenKind.Indent);
        Assert.Equal(2, result.Tokens.Count(token => token.Kind == SyntaxTokenKind.IntegerLiteral));
    }

    [Fact]
    public void Tokenize_RecognizesPython314StringPrefixesAndTripleQuotes()
    {
        const string code = """
            'a' r"b" b'c' f"{value["key"]}" t'item {x}' '''multi
            line'''
            """;

        var result = Tokenize(code);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                SyntaxTokenKind.StringLiteral,
                SyntaxTokenKind.StringLiteral,
                SyntaxTokenKind.BytesLiteral,
                SyntaxTokenKind.FormattedStringLiteral,
                SyntaxTokenKind.TemplateStringLiteral,
                SyntaxTokenKind.StringLiteral,
            ],
            result
                .Tokens.Where(token =>
                    token.Kind
                        is SyntaxTokenKind.StringLiteral
                            or SyntaxTokenKind.BytesLiteral
                            or SyntaxTokenKind.FormattedStringLiteral
                            or SyntaxTokenKind.TemplateStringLiteral
                )
                .Select(token => token.Kind)
        );
    }

    [Fact]
    public void Tokenize_ReportsMalformedNumbersWithoutStoppingRecovery()
    {
        const string code = "1__2 0b102 1e+ valid";

        var result = Tokenize(code);

        Assert.Equal(3, result.Diagnostics.Count(diagnostic => diagnostic.Code == "DPY1006"));
        Assert.Contains(result.Tokens, token => token.Text == "valid");
        Assert.Equal(SyntaxTokenKind.EndOfFile, result.Tokens[^1].Kind);
    }

    [Fact]
    public void Tokenize_ReportsAmbiguousTabsAndSpaces()
    {
        const string code = "if ready:\n\tpass\n        pass\n";

        var result = Tokenize(code);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY1003", diagnostic.Code);
        Assert.Equal(8, diagnostic.Span.Length);
    }

    [Fact]
    public void Tokenize_ReportsUnmatchedDedentAndContinues()
    {
        const string code = "if first:\n    if second:\n        pass\n  pass\n";

        var result = Tokenize(code);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DPY1002");
        Assert.Equal(2, result.Tokens.Count(token => token.Kind == SyntaxTokenKind.Dedent));
        Assert.Equal(SyntaxTokenKind.EndOfFile, result.Tokens[^1].Kind);
    }

    [Theory]
    [InlineData("'unterminated", "DPY1007")]
    [InlineData("(value", "DPY1005")]
    [InlineData("value ]", "DPY1004")]
    [InlineData("value $ other", "DPY1001")]
    [InlineData("\0", "DPY1001")]
    public void Tokenize_ProducesStructuredDiagnosticsForInvalidInput(
        string code,
        string expectedCode
    )
    {
        var result = Tokenize(code);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
        Assert.Equal(SyntaxTokenKind.EndOfFile, result.Tokens[^1].Kind);
    }

    [Fact]
    public void Tokenize_EmptySourceContainsOnlyEndMarker()
    {
        var result = Tokenize(string.Empty);

        Assert.Empty(result.Diagnostics);
        var token = Assert.Single(result.Tokens);
        Assert.Equal(SyntaxTokenKind.EndOfFile, token.Kind);
        Assert.Equal(new TextSpan(0, 0), token.Span);
    }

    private static TokenizationResult Tokenize(string code) =>
        PythonTokenizer.Tokenize(new SourceText(code, "test.py"));
}
