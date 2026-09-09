using DotPython.Language.Ast;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.ParserTests;

public sealed class Python314ExceptionSyntaxTests
{
    [Theory]
    [InlineData("(value): int")]
    [InlineData("((value)): int = 1")]
    [InlineData("(obj.field): int")]
    [InlineData("(values[0]): int = 1")]
    public void Parse_ParenthesizedAnnotationTargets(string source)
    {
        var result = PythonParser.Parse(new SourceText(source));
        Assert.Empty(result.Diagnostics);
        var statement = Assert.IsType<PythonAnnotatedAssignmentStatement>(
            Assert.Single(result.Module.Statements)
        );
        Assert.IsType<PythonParenthesizedExpression>(statement.Target);
    }

    [Theory]
    [InlineData("(a, b): int")]
    [InlineData("(a + b): int")]
    public void Parse_RejectsInvalidParenthesizedAnnotationTargets(string source)
    {
        var result = PythonParser.Parse(new SourceText(source));
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData("except ValueError, TypeError:", false, 2)]
    [InlineData("except ValueError,:", false, 1)]
    [InlineData("except* ValueError, TypeError,:", true, 2)]
    [InlineData("except (ValueError), TypeError:", false, 2)]
    public void Parse_UnparenthesizedExceptionTypes(string header, bool isStar, int typeCount)
    {
        var result = PythonParser.Parse(new SourceText($"try:\n    pass\n{header}\n    pass\n"));

        Assert.Empty(result.Diagnostics);
        var statement = Assert.IsType<PythonTryStatement>(Assert.Single(result.Module.Statements));
        var handler = Assert.Single(statement.Handlers);
        Assert.Equal(isStar, handler.IsStar);
        Assert.Equal(typeCount, Assert.IsType<PythonTupleExpression>(handler.Type).Elements.Count);
    }

    [Theory]
    [InlineData("except ValueError, TypeError as error:")]
    [InlineData("except ValueError, as error:")]
    [InlineData("except* ValueError, TypeError as error:")]
    public void Parse_RejectsUnparenthesizedExceptionBinding(string header)
    {
        var result = PythonParser.Parse(new SourceText($"try:\n    pass\n{header}\n    pass\n"));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DPY2028");
    }

    [Theory]
    [InlineData("except (ValueError, TypeError) as error:")]
    [InlineData("except* (ValueError, TypeError) as error:")]
    public void Parse_PreservesParenthesizedExceptionBinding(string header)
    {
        var result = PythonParser.Parse(new SourceText($"try:\n    pass\n{header}\n    pass\n"));

        Assert.Empty(result.Diagnostics);
        var statement = Assert.IsType<PythonTryStatement>(Assert.Single(result.Module.Statements));
        Assert.Equal("error", Assert.Single(statement.Handlers).Target?.Name);
    }
}
