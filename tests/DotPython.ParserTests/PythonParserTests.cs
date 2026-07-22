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
        Assert.Equal("value", Assert.IsType<PythonNameExpression>(assignment.Target).Name);
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
    public void Parse_DistinguishesListTupleAndParenthesizedDisplays()
    {
        var result = Parse("values = [1, (2,), (3), (), [4, 5,],]");

        Assert.Empty(result.Diagnostics);
        var assignment = Assert.IsType<PythonAssignmentStatement>(
            Assert.Single(result.Module.Statements)
        );
        var list = Assert.IsType<PythonListExpression>(assignment.Value);
        Assert.Equal(5, list.Elements.Count);
        Assert.Single(Assert.IsType<PythonTupleExpression>(list.Elements[1]).Elements);
        Assert.IsType<PythonParenthesizedExpression>(list.Elements[2]);
        Assert.Empty(Assert.IsType<PythonTupleExpression>(list.Elements[3]).Elements);
        Assert.Equal(2, Assert.IsType<PythonListExpression>(list.Elements[4]).Elements.Count);
    }

    [Fact]
    public void Parse_BuildsDictionarySubscriptionsAssignmentsAndForElse()
    {
        var result = Parse(
            "mapping = {'first': [1, 2], 'second': []}\n"
                + "mapping['second'] = mapping['first']\n"
                + "for key in mapping:\n"
                + "    print(mapping[key])\n"
                + "else:\n"
                + "    print('done')\n"
        );

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Module.Statements.Count);
        var dictionaryAssignment = Assert.IsType<PythonAssignmentStatement>(
            result.Module.Statements[0]
        );
        Assert.Equal(
            2,
            Assert.IsType<PythonDictionaryExpression>(dictionaryAssignment.Value).Items.Count
        );
        var mutation = Assert.IsType<PythonAssignmentStatement>(result.Module.Statements[1]);
        Assert.IsType<PythonSubscriptionExpression>(mutation.Target);
        Assert.IsType<PythonSubscriptionExpression>(mutation.Value);
        var loop = Assert.IsType<PythonForStatement>(result.Module.Statements[2]);
        Assert.Equal("key", loop.Target.Name);
        Assert.Single(loop.Body);
        Assert.Single(loop.ElseBody);
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
    public void Parse_BuildsRaiseTryExceptElseAndFinally()
    {
        var result = Parse(
            "try:\n"
                + "    raise ValueError('bad') from None\n"
                + "except (TypeError, ValueError) as error:\n"
                + "    print(error)\n"
                + "except:\n"
                + "    raise\n"
                + "else:\n"
                + "    print('clean')\n"
                + "finally:\n"
                + "    print('done')\n"
        );

        Assert.Empty(result.Diagnostics);
        var statement = Assert.IsType<PythonTryStatement>(Assert.Single(result.Module.Statements));
        var raised = Assert.IsType<PythonRaiseStatement>(Assert.Single(statement.Body));
        Assert.NotNull(raised.Exception);
        Assert.IsType<PythonConstantExpression>(raised.Cause);
        Assert.Equal(2, statement.Handlers.Count);
        Assert.IsType<PythonTupleExpression>(statement.Handlers[0].Type);
        Assert.Equal("error", statement.Handlers[0].Target?.Name);
        Assert.IsType<PythonRaiseStatement>(Assert.Single(statement.Handlers[1].Body));
        Assert.Single(statement.ElseBody);
        Assert.Single(statement.FinallyBody);
    }

    [Fact]
    public void Parse_RejectsAHandlerAfterBareExcept()
    {
        var result = Parse(
            "try:\n"
                + "    raise ValueError()\n"
                + "except:\n"
                + "    print('all')\n"
                + "except ValueError:\n"
                + "    print('value')\n"
        );

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DPY2013");
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
    public void Parse_BuildsImportAliasesFromImportsAndAttributes()
    {
        var result = Parse(
            "import helper as module, values\n"
                + "from helper import answer as result, identity\n"
                + "print(module.answer(), result, identity)"
        );

        Assert.Empty(result.Diagnostics);
        var import = Assert.IsType<PythonImportStatement>(result.Module.Statements[0]);
        Assert.Equal(["helper", "values"], import.Imports.Select(item => item.Name));
        Assert.Equal("module", import.Imports[0].Alias);
        var fromImport = Assert.IsType<PythonFromImportStatement>(result.Module.Statements[1]);
        Assert.Equal("helper", fromImport.ModuleName);
        Assert.Equal(["answer", "identity"], fromImport.Imports.Select(item => item.Name));
        Assert.Equal("result", fromImport.Imports[0].Alias);
        var call = Assert.IsType<PythonCallExpression>(
            Assert.IsType<PythonExpressionStatement>(result.Module.Statements[2]).Expression
        );
        var moduleCall = Assert.IsType<PythonCallExpression>(call.Arguments[0]);
        var attribute = Assert.IsType<PythonAttributeExpression>(moduleCall.Target);
        Assert.Equal("answer", attribute.AttributeName);
    }

    [Fact]
    public void Parse_BuildsDottedRelativeAndParenthesizedImports()
    {
        var result = Parse(
            "import package.tools as tools, package.values\n"
                + "from . import local\n"
                + "from ...shared.values import (answer as result, identity,)\n"
        );

        Assert.Empty(result.Diagnostics);
        var import = Assert.IsType<PythonImportStatement>(result.Module.Statements[0]);
        Assert.Equal(["package.tools", "package.values"], import.Imports.Select(item => item.Name));
        Assert.Equal("tools", import.Imports[0].Alias);
        var currentPackage = Assert.IsType<PythonFromImportStatement>(result.Module.Statements[1]);
        Assert.Equal(".", currentPackage.ModuleName);
        Assert.Equal("local", Assert.Single(currentPackage.Imports).Name);
        var parentPackage = Assert.IsType<PythonFromImportStatement>(result.Module.Statements[2]);
        Assert.Equal("...shared.values", parentPackage.ModuleName);
        Assert.Equal(["answer", "identity"], parentPackage.Imports.Select(item => item.Name));
        Assert.Equal("result", parentPackage.Imports[0].Alias);
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

    [Fact]
    public void Parse_BuildsSimpleControlFlowStatements()
    {
        var result = Parse("break; continue; pass");

        Assert.Empty(result.Diagnostics);
        Assert.IsType<PythonBreakStatement>(result.Module.Statements[0]);
        Assert.IsType<PythonContinueStatement>(result.Module.Statements[1]);
        Assert.IsType<PythonPassStatement>(result.Module.Statements[2]);
    }

    [Fact]
    public void Parse_BuildsParameterDefaultsAndKeywordArguments()
    {
        var result = Parse("def act(first, second=2):\n    return first\nact(1, second=9)\n");

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<PythonFunctionDefinitionStatement>(
            result.Module.Statements[0]
        );
        Assert.Null(function.Parameters[0].Default);
        var defaultValue = Assert.IsType<PythonConstantExpression>(function.Parameters[1].Default);
        Assert.Equal("2", defaultValue.TokenText);

        var call = Assert.IsType<PythonCallExpression>(
            Assert.IsType<PythonExpressionStatement>(result.Module.Statements[1]).Expression
        );
        Assert.Single(call.Arguments);
        var keywordArgument = Assert.Single(call.KeywordArguments);
        Assert.Equal("second", keywordArgument.Name);
        Assert.IsType<PythonConstantExpression>(keywordArgument.Value);
    }

    [Fact]
    public void Parse_BuildsGlobalAndNonlocalDeclarations()
    {
        var result = Parse("def act():\n    global first, second\n    nonlocal third\n");

        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<PythonFunctionDefinitionStatement>(
            Assert.Single(result.Module.Statements)
        );
        var globalStatement = Assert.IsType<PythonGlobalStatement>(function.Body[0]);
        Assert.Equal(["first", "second"], globalStatement.Names.Select(name => name.Name));
        var nonlocalStatement = Assert.IsType<PythonNonlocalStatement>(function.Body[1]);
        Assert.Equal("third", Assert.Single(nonlocalStatement.Names).Name);
    }

    [Fact]
    public void Parse_BuildsSliceMembershipAndAugmentedAssignmentNodes()
    {
        var result = Parse(
            "part = letters[1:8:2]\ncount += value in items\nflag = value is not None\n"
        );

        Assert.Empty(result.Diagnostics);
        var assignment = Assert.IsType<PythonAssignmentStatement>(result.Module.Statements[0]);
        var subscription = Assert.IsType<PythonSubscriptionExpression>(assignment.Value);
        var slice = Assert.IsType<PythonSliceExpression>(subscription.Index);
        Assert.NotNull(slice.Start);
        Assert.NotNull(slice.Stop);
        Assert.NotNull(slice.Step);

        var augmented = Assert.IsType<PythonAugmentedAssignmentStatement>(
            result.Module.Statements[1]
        );
        Assert.Equal(PythonBinaryOperator.Add, augmented.Operator);
        var membership = Assert.IsType<PythonComparisonExpression>(augmented.Value);
        Assert.Equal(PythonComparisonOperator.In, Assert.Single(membership.Comparisons).Operator);

        var identity = Assert.IsType<PythonComparisonExpression>(
            Assert.IsType<PythonAssignmentStatement>(result.Module.Statements[2]).Value
        );
        Assert.Equal(PythonComparisonOperator.IsNot, Assert.Single(identity.Comparisons).Operator);
    }

    [Fact]
    public void Parse_BuildsOpenEndedSlicesAndNotIn()
    {
        var result = Parse("print(letters[:2], letters[3:], letters[:], value not in items)\n");

        Assert.Empty(result.Diagnostics);
        var call = Assert.IsType<PythonCallExpression>(
            Assert
                .IsType<PythonExpressionStatement>(Assert.Single(result.Module.Statements))
                .Expression
        );
        var first = Assert.IsType<PythonSliceExpression>(
            Assert.IsType<PythonSubscriptionExpression>(call.Arguments[0]).Index
        );
        Assert.Null(first.Start);
        Assert.NotNull(first.Stop);
        var full = Assert.IsType<PythonSliceExpression>(
            Assert.IsType<PythonSubscriptionExpression>(call.Arguments[2]).Index
        );
        Assert.Null(full.Start);
        Assert.Null(full.Stop);
        Assert.Null(full.Step);
        var membership = Assert.IsType<PythonComparisonExpression>(call.Arguments[3]);
        Assert.Equal(
            PythonComparisonOperator.NotIn,
            Assert.Single(membership.Comparisons).Operator
        );
    }

    [Theory]
    [InlineData("value =", "DPY2001")]
    [InlineData("value 42", "DPY2003")]
    [InlineData("None = 42", "DPY2005")]
    [InlineData("return 42", "DPY2008")]
    [InlineData("def duplicate(value, value): return value", "DPY2009")]
    [InlineData("def invalid(return): return 42", "DPY2010")]
    [InlineData("import", "DPY2001")]
    [InlineData("from helper answer", "DPY2001")]
    [InlineData("print($)", "DPY1001")]
    [InlineData("def act(first=1, second): return first", "DPY2016")]
    [InlineData("act(first=1, 2)", "DPY2017")]
    [InlineData("act(first=1, first=2)", "DPY2018")]
    [InlineData("global for", "DPY2010")]
    [InlineData("nonlocal", "DPY2001")]
    public void Parse_ReportsStructuredDiagnosticsAndReachesEnd(string code, string expectedCode)
    {
        var result = Parse(code);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static PythonParseResult Parse(string code) =>
        PythonParser.Parse(new SourceText(code, "test.py"));
}
