using System.Numerics;
using DotPython.Compiler;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class PythonCompilerTests
{
    [Fact]
    public void Compile_EmitsDeterministicStackBytecode()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("value = 40 + 2; print(value)", "test.py")
        );

        var result = PythonCompiler.Compile(parseResult.Module, "test.py");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(DotPythonBytecodeFormat.CurrentVersion, result.Code.FormatVersion);
        Assert.Equal(["value", "print"], result.Code.Names);
        Assert.Equal(
            [
                PythonOpCode.LoadConstant,
                PythonOpCode.LoadConstant,
                PythonOpCode.BinaryAdd,
                PythonOpCode.StoreName,
                PythonOpCode.LoadName,
                PythonOpCode.LoadName,
                PythonOpCode.Call,
                PythonOpCode.PopTop,
                PythonOpCode.ReturnNone,
            ],
            result.Code.Instructions.Select(instruction => instruction.OpCode)
        );
        Assert.Equal(new BigInteger(40), result.Code.Constants[0].Value);
        Assert.Equal(new BigInteger(2), result.Code.Constants[1].Value);
    }

    [Fact]
    public void Compile_DecodesStringEscapesWithoutExecutingHostCode()
    {
        var parseResult = PythonParser.Parse(new SourceText("print('line\\nnext')"));

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        var constant = Assert.Single(result.Code.Constants);
        Assert.Equal(PythonConstantType.TextValue, constant.Type);
        Assert.Equal("line\nnext", constant.Value);
    }

    [Fact]
    public void Compile_RejectsFormattedStringsUntilComponentParsingExists()
    {
        var parseResult = PythonParser.Parse(new SourceText("print(f'{40 + 2}')"));

        var result = PythonCompiler.Compile(parseResult.Module);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY3004", diagnostic.Code);
    }

    [Fact]
    public void Compile_RejectsDecimalIntegersWithLeadingZeros()
    {
        var parseResult = PythonParser.Parse(new SourceText("print(0123)"));

        var result = PythonCompiler.Compile(parseResult.Module);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY3003", diagnostic.Code);
    }
}
