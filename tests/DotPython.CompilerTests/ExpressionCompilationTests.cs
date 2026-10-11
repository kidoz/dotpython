using DotPython.Compiler;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Ast;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

/// <summary>
/// `PythonCompiler.CompileExpression`: the one expression `eval` compiles, whose value the
/// code object returns — the shape `compile(source, filename, 'eval')` produces.
/// </summary>
public sealed class ExpressionCompilationTests
{
    [Fact]
    public void TheExpressionIsTheCodeObjectsReturnValue()
    {
        var result = CompileExpression("1 + 2");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("<module>", result.Code.Name);
        Assert.Equal(0, result.Code.ArgumentCount);
        Assert.False(result.Code.IsSuspendable);
        Assert.Equal(PythonOpCode.ReturnValue, result.Code.Instructions[^1].OpCode);
    }

    [Fact]
    public void ANamedExpressionKeepsItsNames()
    {
        var result = CompileExpression("value + 1");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["value"], result.Code.Names);
    }

    [Fact]
    public void AComprehensionCompilesItsOwnCodeObject()
    {
        var result = CompileExpression("[item * 2 for item in values]");

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            result.Code.Constants,
            constant => constant.Type == PythonConstantType.CodeObject
        );
    }

    [Fact]
    public void AMalformedExpressionIsADiagnostic()
    {
        // The parser reports it, and the compiler that reads the recovered statement
        // passes the same diagnostics on.
        var parse = PythonParser.Parse(new SourceText("1 +", "<eval>"));

        Assert.NotEmpty(parse.Diagnostics);
    }

    private static PythonCompilationResult CompileExpression(string source)
    {
        var module = PythonParser.Parse(new SourceText(source, "<eval>")).Module;
        var statement = Assert.IsType<PythonExpressionStatement>(Assert.Single(module.Statements));
        return PythonCompiler.CompileExpression(module, statement.Expression);
    }
}
