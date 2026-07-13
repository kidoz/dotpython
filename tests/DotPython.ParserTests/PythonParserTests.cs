using DotPython.Language.Ast;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.ParserTests;

public sealed class PythonParserTests
{
    [Fact]
    public void Parse_BuildsAssignmentAndCallAstWithPrecedence()
    {
        var result = Parse("value = 40 + 2 * 3; print(value)");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Module.Statements.Count);

        var assignment = Assert.IsType<PythonAssignmentStatement>(result.Module.Statements[0]);
        Assert.Equal("value", assignment.Target.Name);
        var addition = Assert.IsType<PythonBinaryExpression>(assignment.Value);
        Assert.Equal(PythonBinaryOperator.Add, addition.Operator);
        var multiplication = Assert.IsType<PythonBinaryExpression>(addition.Right);
        Assert.Equal(PythonBinaryOperator.Multiply, multiplication.Operator);

        var expressionStatement = Assert.IsType<PythonExpressionStatement>(
            result.Module.Statements[1]
        );
        var call = Assert.IsType<PythonCallExpression>(expressionStatement.Expression);
        Assert.Equal("print", Assert.IsType<PythonNameExpression>(call.Target).Name);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void Parse_MakesPowerRightAssociativeAndTighterThanUnaryMinus()
    {
        var result = Parse("value = -2 ** 3 ** 2");

        Assert.Empty(result.Diagnostics);
        var assignment = Assert.IsType<PythonAssignmentStatement>(
            Assert.Single(result.Module.Statements)
        );
        var unary = Assert.IsType<PythonUnaryExpression>(assignment.Value);
        var outerPower = Assert.IsType<PythonBinaryExpression>(unary.Operand);
        var innerPower = Assert.IsType<PythonBinaryExpression>(outerPower.Right);

        Assert.Equal(PythonUnaryOperator.Negative, unary.Operator);
        Assert.Equal(PythonBinaryOperator.Power, outerPower.Operator);
        Assert.Equal(PythonBinaryOperator.Power, innerPower.Operator);
    }

    [Fact]
    public void Parse_AllowsPhysicalNewLinesInsideParentheses()
    {
        var result = Parse("value = (\n    40 +\n    2\n)\nprint(value)\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Module.Statements.Count);
    }

    [Theory]
    [InlineData("value =", "DPY2001")]
    [InlineData("value 42", "DPY2003")]
    [InlineData("if True:\n    print(1)\n", "DPY2004")]
    [InlineData("None = 42", "DPY2005")]
    [InlineData("pass", "DPY2004")]
    [InlineData("print($)", "DPY1001")]
    public void Parse_ReportsStructuredDiagnosticsAndReachesEnd(string code, string expectedCode)
    {
        var result = Parse(code);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static PythonParseResult Parse(string code) =>
        PythonParser.Parse(new SourceText(code, "test.py"));
}
