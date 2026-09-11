using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.Lint;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.LintTests;

public sealed class PythonLinterTests
{
    [Theory]
    [InlineData("try:\n    pass\nexcept:\n    pass\n", "DPYL001", "except")]
    [InlineData("def f(x=[]): pass\n", "DPYL002", "[]")]
    [InlineData("def f(*, x={}): pass\n", "DPYL002", "{}")]
    [InlineData("def f(x=({1})): pass\n", "DPYL002", "({1})")]
    [InlineData("f = lambda x=[]: x\n", "DPYL002", "[]")]
    [InlineData("text = f'{(lambda x=[]: x)()}'\n", "DPYL002", "[]")]
    [InlineData("text = t'{(lambda x=[]: x)()}'\n", "DPYL002", "[]")]
    [InlineData("text = f\"{f'{(lambda x=[]: x)()}'}\"\n", "DPYL002", "[]")]
    [InlineData("assert (False, 'message')\n", "DPYL003", "(False, 'message')")]
    [InlineData("assert ((False,))\n", "DPYL003", "((False,))")]
    [InlineData("items = []; assert (*items, False)\n", "DPYL003", "(*items, False)")]
    public void Analyze_ReportsRuleAndExactSourceSpan(string text, string code, string fragment)
    {
        var source = new SourceText(text, "example.py");
        var result = Analyze(source);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(fragment, source.GetText(diagnostic.Span));
        Assert.Same(source, result.Source);
        Assert.False(result.IsClean);
    }

    [Theory]
    [InlineData("try:\n    pass\nexcept Exception:\n    pass\n")]
    [InlineData("try:\n    pass\nexcept* Exception:\n    pass\n")]
    [InlineData("def f(x=None, y=(), z=1): pass\n")]
    [InlineData("def f(x=list()): pass\n")]
    [InlineData("assert False, 'message'\n")]
    [InlineData("assert (False)\n")]
    [InlineData("assert ()\n")]
    [InlineData("items = []; assert (*items,)\n")]
    [InlineData("# def f(x=[]): pass\nx = 'assert (False,)'\n")]
    public void Analyze_DoesNotReportCounterexamples(string text)
    {
        Assert.Empty(Analyze(new SourceText(text)).Diagnostics);
    }

    [Fact]
    public void Analyze_VisitsNestedBodiesAndLambdaDefaultsInExpressions()
    {
        const string text = """
            class C:
                def outer(self):
                    if True:
                        def inner(x=[]): pass
                    else:
                        assert (False,)
                    try:
                        pass
                    except:
                        pass
                    finally:
                        f = [lambda y={}: y for x in [1]]
                    match self:
                        case _:
                            assert (False,)
            """;
        var result = Analyze(new SourceText(text));

        Assert.Equal(
            ["DPYL002", "DPYL003", "DPYL001", "DPYL002", "DPYL003"],
            result.Diagnostics.Select(d => d.Code)
        );
        Assert.Equal(result.Diagnostics.OrderBy(d => d.Span.Start), result.Diagnostics);
    }

    [Theory]
    [InlineData("assert (False,) # dotpython: ignore[DPYL003]\n", 0)]
    [InlineData("assert (False,) #dotpython: ignore[DPYL001, DPYL003] -- intentional\n", 0)]
    [InlineData("assert (False,) # dotpython: ignore[DPYL001]\n", 1)]
    [InlineData("assert (False,) # dotpython: ignore[DPYL003\n", 1)]
    [InlineData("# dotpython: ignore[DPYL003]\nassert (False,)\n", 1)]
    [InlineData("assert (False, '# dotpython: ignore[DPYL003]')\n", 1)]
    [InlineData("assert (False, r'# dotpython: ignore[DPYL003]')\n", 1)]
    [InlineData("assert (False, f'# dotpython: ignore[DPYL003]')\n", 1)]
    [InlineData("assert (False, '''# dotpython: ignore[DPYL003]''')\n", 1)]
    public void Analyze_UsesOnlyRealSameLineSuppressionComments(string text, int count)
    {
        var result = Analyze(new SourceText(text));

        Assert.Equal(count, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, d => Assert.Equal("DPYL003", d.Code));
    }

    [Fact]
    public void Analyze_PreservesParserErrorsAndSkipsLintOnPartialTrees()
    {
        var result = Analyze(
            new SourceText("assert (False,)\ndef : # dotpython: ignore[DPY2001]\n")
        );

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Code.StartsWith("DPYL", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Analyze_SelectionAndIgnoreAreExactAndIgnoreWins()
    {
        var source = new SourceText("def f(x=[]):\n    assert (False,)\n");
        var result = Analyze(
            source,
            new PythonLintOptions { Select = ["DPYL002", "DPYL003"], Ignore = ["DPYL003"] }
        );

        Assert.Equal("DPYL002", Assert.Single(result.Diagnostics).Code);
        Assert.True(Analyze(source, new PythonLintOptions { Select = [] }).IsClean);
        Assert.Throws<ArgumentException>(() =>
            Analyze(source, new PythonLintOptions { Select = ["DPYL"] })
        );
        Assert.Throws<ArgumentException>(() =>
            Analyze(source, new PythonLintOptions { Ignore = ["unknown"] })
        );
    }

    [Fact]
    public void Analyze_PreservesCrLfAndUtf16Positions()
    {
        var source = new SourceText("# привет 😀\r\nx = '😀'; assert (False,)\r\n");
        var diagnostic = Assert.Single(Analyze(source).Diagnostics);

        Assert.Equal(new LinePosition(1, 17), source.GetLinePosition(diagnostic.Span.Start));
        Assert.Equal("(False,)", source.GetText(diagnostic.Span));
    }

    [Fact]
    public void Analyze_ObservesCancellationBeforeParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PythonLinter.Analyze(
                new SourceText("assert (False,)"),
                cancellationToken: cancellation.Token
            )
        );
    }

    [Fact]
    public void Parse_PreservesCommentSpansWithoutChangingGrammarTokens()
    {
        var source = new SourceText("# first\r\nx = '# string' # second\r\n");
        var result = PythonParser.Parse(source);

        Assert.True(result.Success);
        Assert.Equal(["# first", "# second"], result.Comments.Select(source.GetText));
    }

    private static PythonLintResult Analyze(SourceText source, PythonLintOptions? options = null) =>
        PythonLinter.Analyze(source, options, TestContext.Current.CancellationToken);
}
