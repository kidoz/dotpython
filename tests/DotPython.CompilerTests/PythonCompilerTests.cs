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
    public void Compile_EmitsCollectionElementsInSourceOrder()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("values = [1, 2]; empty = (); single = (3,)")
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var collectionInstructions = result.Code.Instructions.Where(instruction =>
            instruction.OpCode is PythonOpCode.BuildList or PythonOpCode.BuildTuple
        );
        Assert.Equal(
            [
                (PythonOpCode.BuildList, 2),
                (PythonOpCode.BuildTuple, 0),
                (PythonOpCode.BuildTuple, 1),
            ],
            collectionInstructions.Select(instruction => (instruction.OpCode, instruction.Operand))
        );
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

    [Fact]
    public void Compile_EmitsPatchedControlFlowAndShortCircuitJumps()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "while value < 3:\n"
                    + "    if value != 1 and enabled or fallback:\n"
                    + "        print(value)\n"
                    + "    value = value + 1\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.JumpIfFalse
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.JumpIfFalseOrPop
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.JumpIfTrueOrPop
        );
        Assert.All(
            result.Code.Instructions.Where(instruction =>
                instruction.OpCode
                    is PythonOpCode.Jump
                        or PythonOpCode.JumpIfFalse
                        or PythonOpCode.JumpIfFalseOrPop
                        or PythonOpCode.JumpIfTrueOrPop
            ),
            instruction => Assert.InRange(instruction.Operand, 0, result.Code.Instructions.Count)
        );
    }

    [Fact]
    public void Compile_PreservesOperandsForChainedComparisons()
    {
        var parseResult = PythonParser.Parse(new SourceText("print(1 < 2 < 3)"));

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        var opCodes = result.Code.Instructions.Select(instruction => instruction.OpCode);
        Assert.Contains(PythonOpCode.CopyTop, opCodes);
        Assert.Contains(PythonOpCode.RotateThree, opCodes);
        Assert.Contains(PythonOpCode.RotateTwo, opCodes);
        Assert.Equal(2, opCodes.Count(opCode => opCode == PythonOpCode.CompareLessThan));
    }

    [Fact]
    public void Compile_EmitsNestedFunctionCodeWithBoundLocalsAndGlobals()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "factor = 2\n"
                    + "def calculate(value):\n"
                    + "    result = value * factor\n"
                    + "    return result\n"
                    + "print(calculate(21))\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.MakeFunction
        );
        var functionConstant = Assert.Single(
            result.Code.Constants,
            constant => constant.Type == PythonConstantType.CodeObject
        );
        var functionCode = Assert.IsType<PythonCodeObject>(functionConstant.Value);
        Assert.Equal("calculate", functionCode.Name);
        Assert.Equal(1, functionCode.ArgumentCount);
        Assert.Equal(["value", "result"], functionCode.VariableNames);
        Assert.Equal(["factor"], functionCode.Names);
        Assert.Contains(
            functionCode.Instructions,
            instruction => instruction.OpCode == PythonOpCode.LoadLocal
        );
        Assert.Contains(
            functionCode.Instructions,
            instruction => instruction.OpCode == PythonOpCode.StoreLocal
        );
        Assert.Contains(
            functionCode.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ReturnValue
        );
    }

    [Theory]
    [InlineData("return")]
    [InlineData("return None")]
    [InlineData("return (None)")]
    public void Compile_UsesReturnNoneForEquivalentNoneReturns(string returnStatement)
    {
        var parseResult = PythonParser.Parse(
            new SourceText($"def return_none(): {returnStatement}\n")
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var function = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    result.Code.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );
        Assert.Equal(PythonOpCode.ReturnNone, function.Instructions[0].OpCode);
        Assert.DoesNotContain(
            function.Instructions,
            instruction =>
                instruction.OpCode is PythonOpCode.LoadConstant or PythonOpCode.ReturnValue
        );
        Assert.Empty(function.Constants);
    }

    [Fact]
    public void Compile_EmitsClosureMetadataAndCellOperations()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "def outer(value):\n"
                    + "    def inner():\n"
                    + "        return value\n"
                    + "    return inner()\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        var outer = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    result.Code.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );
        var inner = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(outer.Constants, constant => constant.Type == PythonConstantType.CodeObject)
                .Value
        );
        Assert.Equal(["value"], outer.CellVariableNames);
        Assert.Empty(outer.FreeVariableNames);
        Assert.Empty(inner.CellVariableNames);
        Assert.Equal(["value"], inner.FreeVariableNames);
        Assert.Contains(
            inner.Instructions,
            instruction => instruction.OpCode == PythonOpCode.LoadCell
        );
    }

    [Fact]
    public void Compile_StoresNestedRecursiveFunctionInItsCapturedCell()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "def outer():\n"
                    + "    def inner(value):\n"
                    + "        if value <= 1: return 1\n"
                    + "        return value * inner(value - 1)\n"
                    + "    return inner(5)\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        var outer = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    result.Code.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );
        var inner = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(outer.Constants, constant => constant.Type == PythonConstantType.CodeObject)
                .Value
        );
        Assert.Equal(["inner"], outer.CellVariableNames);
        Assert.Equal(["inner"], inner.FreeVariableNames);
        Assert.Contains(
            outer.Instructions,
            instruction => instruction.OpCode == PythonOpCode.StoreCell
        );
    }
}
