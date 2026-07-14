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

    [Fact]
    public void Parse_BuildsIfElifElseAndWhileSuites()
    {
        var result = Parse(
            "while value < 3:\n"
                + "    if value == 1:\n"
                + "        print(value)\n"
                + "    elif value == 2: print(value)\n"
                + "    else:\n"
                + "        print(0)\n"
                + "    value = value + 1\n"
                + "else:\n"
                + "    print('done')\n"
        );

        Assert.Empty(result.Diagnostics);
        var loop = Assert.IsType<PythonWhileStatement>(Assert.Single(result.Module.Statements));
        Assert.Equal(2, loop.Body.Count);
        Assert.Single(loop.ElseBody);

        var conditional = Assert.IsType<PythonIfStatement>(loop.Body[0]);
        Assert.Equal(2, conditional.Clauses.Count);
        Assert.All(conditional.Clauses, clause => Assert.Single(clause.Body));
        Assert.Single(conditional.ElseBody);
    }

    [Fact]
    public void Parse_AppliesBooleanAndComparisonPrecedence()
    {
        var result = Parse("value = not first < middle < last and fallback or final");

        Assert.Empty(result.Diagnostics);
        var assignment = Assert.IsType<PythonAssignmentStatement>(
            Assert.Single(result.Module.Statements)
        );
        var disjunction = Assert.IsType<PythonBinaryExpression>(assignment.Value);
        Assert.Equal(PythonBinaryOperator.Or, disjunction.Operator);
        var conjunction = Assert.IsType<PythonBinaryExpression>(disjunction.Left);
        Assert.Equal(PythonBinaryOperator.And, conjunction.Operator);
        var inversion = Assert.IsType<PythonUnaryExpression>(conjunction.Left);
        Assert.Equal(PythonUnaryOperator.Not, inversion.Operator);
        var comparison = Assert.IsType<PythonComparisonExpression>(inversion.Operand);
        Assert.Equal(2, comparison.Comparisons.Count);
    }

    [Fact]
    public void Parse_BuildsFunctionParametersAndReturns()
    {
        var result = Parse(
            "def calculate(left, right):\n"
                + "    result = left + right\n"
                + "    if result > 0:\n"
                + "        return result\n"
                + "    return\n"
        );

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<PythonFunctionDefinitionStatement>(
            Assert.Single(result.Module.Statements)
        );
        Assert.Equal("calculate", function.Name.Name);
        Assert.Equal(["left", "right"], function.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(3, function.Body.Count);
        Assert.IsType<PythonReturnStatement>(function.Body[^1]);
        var conditional = Assert.IsType<PythonIfStatement>(function.Body[1]);
        Assert.IsType<PythonReturnStatement>(Assert.Single(conditional.Clauses[0].Body));
    }

    [Fact]
    public void Parse_AllowsFunctionAndReturnOnOneLogicalLine()
    {
        var result = Parse("def identity(value): return value");

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<PythonFunctionDefinitionStatement>(
            Assert.Single(result.Module.Statements)
        );
        Assert.IsType<PythonReturnStatement>(Assert.Single(function.Body));
    }

    [Theory]
    [InlineData("value =", "DPY2001")]
    [InlineData("value 42", "DPY2003")]
    [InlineData("for value in values:\n    print(value)\n", "DPY2004")]
    [InlineData("None = 42", "DPY2005")]
    [InlineData("pass", "DPY2004")]
    [InlineData("return 42", "DPY2008")]
    [InlineData("def duplicate(value, value): return value", "DPY2009")]
    [InlineData("def invalid(return): return 42", "DPY2010")]
    [InlineData("print($)", "DPY1001")]
    public void Parse_ReportsStructuredDiagnosticsAndReachesEnd(string code, string expectedCode)
    {
        var result = Parse(code);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static PythonParseResult Parse(string code) =>
        PythonParser.Parse(new SourceText(code, "test.py"));
}
