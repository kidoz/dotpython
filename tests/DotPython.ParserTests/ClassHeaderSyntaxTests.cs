using DotPython.Language.Ast;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.ParserTests;

public sealed class ClassHeaderSyntaxTests
{
    [Fact]
    public void ParseClassHeaderPreservesPositionalAndKeywordArgumentGroups()
    {
        var result = PythonParser.Parse(
            new SourceText("class Example(Base, flag=1, *bases, metaclass=Meta, **options): pass")
        );
        Assert.Empty(result.Diagnostics);
        var definition = Assert.IsType<PythonClassDefinitionStatement>(
            Assert.Single(result.Module.Statements)
        );
        Assert.Equal("Base", Assert.IsType<PythonNameExpression>(definition.Bases[0]).Name);
        Assert.IsType<PythonStarredExpression>(definition.Bases[1]);
        Assert.NotNull(definition.KeywordArguments);
        Assert.Equal(
            new string?[] { "flag", "metaclass", null },
            definition.KeywordArguments.Select(keyword => keyword.Name)
        );
    }

    [Theory]
    [InlineData("class C(metaclass=Meta, metaclass=Other): pass")]
    [InlineData("class C(flag=1, Base): pass")]
    [InlineData("class C(**options, *bases): pass")]
    [InlineData("class C(**options, Base): pass")]
    [InlineData("class C(metaclass=): pass")]
    [InlineData("class C(**): pass")]
    [InlineData("class C(x for x in []): pass")]
    [InlineData("class C(flag=x:=1): pass")]
    [InlineData("class C(*x:=()): pass")]
    [InlineData("class C(**x:={}): pass")]
    [InlineData("call(flag=x:=1)")]
    [InlineData("call(*x:=())")]
    [InlineData("call(**x:={})")]
    public void ParseRejectsMalformedClassArguments(string source)
    {
        var result = PythonParser.Parse(new SourceText(source));
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void SharedCallParserRejectsStarredArgumentAfterKeywordUnpacking()
    {
        var result = PythonParser.Parse(new SourceText("call(**options, *values)"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DPY2017");
    }

    [Theory]
    [InlineData("class C(base:=object): pass")]
    [InlineData("class C(flag=(value:=1)): pass")]
    [InlineData("class C(*(bases:=())): pass")]
    [InlineData("class C(**(options:={})): pass")]
    [InlineData("class C((x for x in [])): pass")]
    [InlineData("call(x for x in [])")]
    public void ArgumentParenthesisRestrictionsPreserveValidForms(string source)
    {
        var result = PythonParser.Parse(new SourceText(source));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ClassDefinitionRetainsFiveArgumentConstructorAndDeconstruction()
    {
        IReadOnlyList<PythonExpression> decorators = [];
        IReadOnlyList<PythonExpression> bases = [];
        IReadOnlyList<PythonStatement> body = [];
        var name = new PythonNameExpression("Example", new TextSpan(0, 7));
        var span = new TextSpan(0, 12);
        var definition = new PythonClassDefinitionStatement(decorators, name, bases, body, span);
        var (actualDecorators, actualName, actualBases, actualBody, actualSpan) = definition;
        Assert.Same(decorators, actualDecorators);
        Assert.Same(name, actualName);
        Assert.Same(bases, actualBases);
        Assert.Same(body, actualBody);
        Assert.Equal(span, actualSpan);
        Assert.Null(definition.KeywordArguments);
        Assert.NotNull(
            typeof(PythonClassDefinitionStatement).GetConstructor([
                typeof(IReadOnlyList<PythonExpression>),
                typeof(PythonNameExpression),
                typeof(IReadOnlyList<PythonExpression>),
                typeof(IReadOnlyList<PythonStatement>),
                typeof(TextSpan),
            ])
        );
    }
}
