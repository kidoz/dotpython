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
    public void Compile_EmitsPackageImportAndAttributeBytecode()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "import package.helper\n"
                    + "import package.values as values\n"
                    + "from package import answer as result\n"
                    + "print(package.helper.answer(), values, result)"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("package.helper", result.Code.Names);
        Assert.Contains("package.values", result.Code.Names);
        Assert.Equal(
            6,
            result.Code.Instructions.Count(instruction =>
                instruction.OpCode == PythonOpCode.ImportName
            )
        );
        Assert.Single(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ImportFrom
        );
        Assert.Equal(
            2,
            result.Code.Instructions.Count(instruction =>
                instruction.OpCode == PythonOpCode.LoadAttribute
            )
        );
    }

    [Fact]
    public void Compile_EmitsDictionarySubscriptionMutationAndIterationBytecode()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "values = [1]\n"
                    + "values[0] = 2\n"
                    + "mapping = {'value': values[0]}\n"
                    + "for key in mapping: print(mapping[key])\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            result.Code.Instructions,
            instruction =>
                instruction.OpCode == PythonOpCode.BuildDictionary && instruction.Operand == 1
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.StoreSubscript
        );
        Assert.Equal(
            2,
            result.Code.Instructions.Count(instruction =>
                instruction.OpCode == PythonOpCode.LoadSubscript
            )
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.GetIterator
        );
        var forIter = Assert.Single(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ForIter
        );
        Assert.InRange(forIter.Operand, 0, result.Code.Instructions.Count);
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
            instruction => instruction.OpCode == PythonOpCode.ReturnLocal
        );
    }

    [Fact]
    public void Compile_EmitsClassBodyMethodsAndAttributeStores()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "class Counter:\n"
                    + "    kind = 'counter'\n"
                    + "    def __init__(self, value):\n"
                    + "        self.value = value\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.MakeClass
        );
        var classCode = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    result.Code.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );
        Assert.Equal("Counter", classCode.Name);
        Assert.Contains(
            classCode.Instructions,
            instruction => instruction.OpCode == PythonOpCode.MakeFunction
        );
        var initializerCode = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    classCode.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );
        Assert.Contains(
            initializerCode.Instructions,
            instruction => instruction.OpCode == PythonOpCode.StoreAttribute
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

    [Theory]
    [InlineData("value")]
    [InlineData("(value)")]
    public void Compile_UsesReturnLocalForUncapturedLocalNames(string expression)
    {
        var parseResult = PythonParser.Parse(
            new SourceText($"def identity(value): return {expression}\n")
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
        var returnLocal = Assert.Single(
            function.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ReturnLocal
        );
        Assert.Equal(0, returnLocal.Operand);
        Assert.DoesNotContain(
            function.Instructions,
            instruction => instruction.OpCode is PythonOpCode.LoadLocal or PythonOpCode.ReturnValue
        );
    }

    [Theory]
    [InlineData("target")]
    [InlineData("(target)")]
    public void Compile_UsesCallLocalForZeroArgumentUncapturedLocalTargets(string expression)
    {
        var parseResult = PythonParser.Parse(
            new SourceText($"def invoke(target): return {expression}()\n")
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
        var callLocal = Assert.Single(
            function.Instructions,
            instruction => instruction.OpCode == PythonOpCode.CallLocal
        );
        Assert.Equal(0, callLocal.Operand);
        Assert.DoesNotContain(
            function.Instructions,
            instruction => instruction.OpCode is PythonOpCode.LoadLocal or PythonOpCode.Call
        );
    }

    [Fact]
    public void Compile_LeavesArgumentedAndCapturedCallsOnGeneralPath()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "def invoke(target, value): return target(value)\n"
                    + "def outer(target):\n"
                    + "    def inner(): return target()\n"
                    + "    return inner\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var functions = result
            .Code.Constants.Where(constant => constant.Type == PythonConstantType.CodeObject)
            .Select(constant => Assert.IsType<PythonCodeObject>(constant.Value))
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        var invoke = functions["invoke"];
        var outer = functions["outer"];
        var inner = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(outer.Constants, constant => constant.Type == PythonConstantType.CodeObject)
                .Value
        );

        Assert.Contains(
            invoke.Instructions,
            instruction => instruction.OpCode == PythonOpCode.Call
        );
        Assert.DoesNotContain(
            invoke.Instructions,
            instruction => instruction.OpCode == PythonOpCode.CallLocal
        );
        Assert.Contains(
            inner.Instructions,
            instruction => instruction.OpCode == PythonOpCode.LoadCell
        );
        Assert.Contains(inner.Instructions, instruction => instruction.OpCode == PythonOpCode.Call);
        Assert.DoesNotContain(
            inner.Instructions,
            instruction => instruction.OpCode == PythonOpCode.CallLocal
        );
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
        Assert.DoesNotContain(
            inner.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ReturnLocal
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

    [Fact]
    public void Compile_EmitsManagedExceptionAndFinallyControlFlow()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "try:\n"
                    + "    raise ValueError('bad')\n"
                    + "except ValueError as error:\n"
                    + "    print(error)\n"
                    + "else:\n"
                    + "    print('clean')\n"
                    + "finally:\n"
                    + "    print('done')\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.SetupExcept
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.SetupFinally
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.MatchException
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ClearException
        );
        Assert.Contains(
            result.Code.Instructions,
            instruction => instruction is { OpCode: PythonOpCode.Raise, Operand: 1 }
        );
        Assert.All(
            result.Code.Instructions.Where(instruction =>
                instruction.OpCode is PythonOpCode.SetupExcept or PythonOpCode.SetupFinally
            ),
            instruction => Assert.InRange(instruction.Operand, 0, result.Code.Instructions.Count)
        );
    }

    [Fact]
    public void Compile_EmitsIteratorCleanupAndSkipsTheElseClauseForBreak()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("for value in [1]:\n    break\nelse:\n    print('else')\n")
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                PythonOpCode.LoadConstant,
                PythonOpCode.BuildList,
                PythonOpCode.GetIterator,
                PythonOpCode.ForIter,
                PythonOpCode.StoreName,
                PythonOpCode.PopTop,
                PythonOpCode.Jump,
                PythonOpCode.Jump,
                PythonOpCode.LoadName,
                PythonOpCode.LoadConstant,
                PythonOpCode.Call,
                PythonOpCode.PopTop,
                PythonOpCode.ReturnNone,
            ],
            result.Code.Instructions.Select(instruction => instruction.OpCode)
        );
        var breakJump = result.Code.Instructions[6];
        Assert.Equal(12, breakJump.Operand);
        var loopJump = result.Code.Instructions[7];
        Assert.Equal(3, loopJump.Operand);
        Assert.Equal(8, result.Code.Instructions[3].Operand);
    }

    [Fact]
    public void Compile_UnwindsProtectedBlocksBeforeLoopControlJumps()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "while True:\n"
                    + "    try:\n"
                    + "        break\n"
                    + "    finally:\n"
                    + "        print('cleanup')\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var instructions = result.Code.Instructions;
        var breakIndex = instructions
            .Select((instruction, index) => (instruction, index))
            .First(entry =>
                entry.instruction.OpCode == PythonOpCode.PopExceptionBlock
                && instructions[entry.index + 1].OpCode == PythonOpCode.LoadName
            )
            .index;
        Assert.Equal(
            [
                PythonOpCode.PopExceptionBlock,
                PythonOpCode.LoadName,
                PythonOpCode.LoadConstant,
                PythonOpCode.Call,
                PythonOpCode.PopTop,
                PythonOpCode.Jump,
            ],
            instructions.Skip(breakIndex).Take(6).Select(instruction => instruction.OpCode)
        );
    }

    [Theory]
    [InlineData("break", "DPY3104")]
    [InlineData("continue", "DPY3105")]
    [InlineData("def act():\n    break\n", "DPY3104")]
    [InlineData("for value in [1]:\n    def act():\n        continue\n", "DPY3105")]
    [InlineData("while True:\n    try:\n        pass\n    finally:\n        break\n", "DPY3106")]
    [InlineData(
        "for value in [1]:\n    try:\n        pass\n    finally:\n        continue\n",
        "DPY3106"
    )]
    public void Compile_ReportsLoopControlDiagnostics(string code, string expectedCode)
    {
        var parseResult = PythonParser.Parse(new SourceText(code));

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Compile_EmitsDefaultTuplesAndKeywordCallBytecode()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("def act(first, second=2, third=3): return first\nact(1, third=9)\n")
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var buildTupleIndex = result
            .Code.Instructions.ToList()
            .FindIndex(instruction =>
                instruction is { OpCode: PythonOpCode.BuildTuple, Operand: 2 }
            );
        Assert.True(buildTupleIndex >= 0);
        Assert.Equal(
            PythonOpCode.MakeFunctionWithDefaults,
            result.Code.Instructions[buildTupleIndex + 1].OpCode
        );
        var keywordCall = Assert.Single(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.CallKeyword
        );
        Assert.Equal(2, keywordCall.Operand);
        Assert.Contains(
            result.Code.Constants,
            constant =>
                constant.Type == PythonConstantType.TextValue && Equals(constant.Value, "third")
        );
        Assert.DoesNotContain(
            result.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.Call
        );
    }

    [Fact]
    public void Compile_BindsDeclaredGlobalAndNonlocalStores()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "count = 0\n"
                    + "def bump():\n"
                    + "    global count\n"
                    + "    count = count + 1\n"
                    + "def outer():\n"
                    + "    value = 0\n"
                    + "    def inner():\n"
                    + "        nonlocal value\n"
                    + "        value = value + 1\n"
                    + "    inner()\n"
                    + "    return value\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var functions = result
            .Code.Constants.Where(constant => constant.Type == PythonConstantType.CodeObject)
            .Select(constant => Assert.IsType<PythonCodeObject>(constant.Value))
            .ToList();
        var bump = Assert.Single(functions, function => function.Name == "bump");
        Assert.Empty(bump.VariableNames);
        Assert.Contains(
            bump.Instructions,
            instruction => instruction.OpCode == PythonOpCode.StoreName
        );
        var outer = Assert.Single(functions, function => function.Name == "outer");
        Assert.Equal(["value"], outer.CellVariableNames);
        var inner = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(outer.Constants, constant => constant.Type == PythonConstantType.CodeObject)
                .Value
        );
        Assert.Equal(["value"], inner.FreeVariableNames);
        Assert.Contains(
            inner.Instructions,
            instruction => instruction.OpCode == PythonOpCode.StoreCell
        );
    }

    [Theory]
    [InlineData("nonlocal value\n", "DPY3107")]
    [InlineData("def act(value):\n    global value\n", "DPY3108")]
    [InlineData("def act(value):\n    nonlocal value\n", "DPY3108")]
    [InlineData("def act():\n    global value\n    nonlocal value\n", "DPY3109")]
    [InlineData("def act():\n    nonlocal value\n    value = 1\n", "DPY3110")]
    public void Compile_ReportsScopeDeclarationDiagnostics(string code, string expectedCode)
    {
        var parseResult = PythonParser.Parse(new SourceText(code));

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Compile_EmitsMembershipIdentitySliceAndInPlaceBytecode()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "found = 2 in values\nsame = value is None\nother = value is not None\n"
                    + "missing = 3 not in values\npart = values[1:5:2]\ntotal += 1\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        foreach (
            var expected in (PythonOpCode[])
                [
                    PythonOpCode.CompareIn,
                    PythonOpCode.CompareIs,
                    PythonOpCode.CompareIsNot,
                    PythonOpCode.CompareNotIn,
                    PythonOpCode.BuildSlice,
                    PythonOpCode.InPlaceAdd,
                ]
        )
        {
            Assert.Contains(
                result.Code.Instructions,
                instruction => instruction.OpCode == expected
            );
        }
    }

    [Fact]
    public void Compile_EmitsSingleEvaluationAugmentedSubscriptBytecode()
    {
        var parseResult = PythonParser.Parse(new SourceText("counts['key'] += 1\n"));

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var opCodes = result.Code.Instructions.Select(instruction => instruction.OpCode).ToList();
        var copyIndex = opCodes.IndexOf(PythonOpCode.CopyTopTwo);
        Assert.True(copyIndex >= 0);
        Assert.Equal(
            [
                PythonOpCode.CopyTopTwo,
                PythonOpCode.LoadSubscript,
                PythonOpCode.LoadConstant,
                PythonOpCode.InPlaceAdd,
                PythonOpCode.RotateThree,
                PythonOpCode.StoreSubscript,
            ],
            opCodes.Skip(copyIndex).Take(6)
        );
    }

    [Fact]
    public void Compile_EmitsUnpackSequenceForTupleTargets()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("a, b = b, a\nfor key, value in pairs:\n    print(key, value)\n")
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            2,
            result.Code.Instructions.Count(instruction =>
                instruction is { OpCode: PythonOpCode.UnpackSequence, Operand: 2 }
            )
        );
        var buildTuple = result.Code.Instructions[2];
        Assert.Equal(PythonOpCode.BuildTuple, buildTuple.OpCode);
        Assert.Equal(2, buildTuple.Operand);
    }

    [Fact]
    public void Compile_LowersComprehensionsToIsolatedFunctionScopes()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("squares = [x * x for x in values]\nlookup = {k: v for k, v in pairs}\n")
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var comprehensions = result
            .Code.Constants.Where(constant => constant.Type == PythonConstantType.CodeObject)
            .Select(constant => Assert.IsType<PythonCodeObject>(constant.Value))
            .ToList();
        Assert.Equal(2, comprehensions.Count);

        var listComprehension = Assert.Single(comprehensions, code => code.Name == "<listcomp>");
        Assert.Equal(1, listComprehension.ArgumentCount);
        Assert.Equal(".0", listComprehension.VariableNames[0]);
        Assert.Contains(
            listComprehension.Instructions,
            instruction => instruction is { OpCode: PythonOpCode.ListAppend, Operand: 1 }
        );
        Assert.Contains(
            listComprehension.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ReturnValue
        );

        var dictionaryComprehension = Assert.Single(
            comprehensions,
            code => code.Name == "<dictcomp>"
        );
        Assert.Contains(
            dictionaryComprehension.Instructions,
            instruction => instruction is { OpCode: PythonOpCode.DictionaryAdd, Operand: 1 }
        );
        Assert.Contains(
            dictionaryComprehension.Instructions,
            instruction => instruction.OpCode == PythonOpCode.UnpackSequence
        );

        Assert.DoesNotContain("x", result.Code.Names);
        Assert.Equal(
            2,
            result.Code.Instructions.Count(instruction =>
                instruction is { OpCode: PythonOpCode.Call, Operand: 1 }
            )
        );
    }

    [Fact]
    public void Compile_AllowsLoopControlForLoopsInsideAFinallyClause()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "try:\n"
                    + "    pass\n"
                    + "finally:\n"
                    + "    for value in [1, 2]:\n"
                    + "        if value == 2:\n"
                    + "            break\n"
                    + "        continue\n"
            )
        );

        var result = PythonCompiler.Compile(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
    }
}
