using System.Numerics;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PreparedPythonCodeTests
{
    public static TheoryData<string, int> OrderedComparisonCases =>
        new()
        {
            { "<", (int)PythonOpCode.CompareLessThan },
            { "<=", (int)PythonOpCode.CompareLessThanOrEqual },
            { ">", (int)PythonOpCode.CompareGreaterThan },
            { ">=", (int)PythonOpCode.CompareGreaterThanOrEqual },
        };

    [Fact]
    public void PrepareCode_ReusesPreparedCodeAndImmutableConstants()
    {
        var code = Compile("first = 42\nsecond = 42\n");
        var engine = new ManagedPythonEngine();

        var first = engine.PrepareCode(code);
        var second = engine.PrepareCode(code);

        Assert.Same(first, second);
        Assert.Same(first.GetConstant(0), second.GetConstant(0));
    }

    [Fact]
    public void PrepareCode_PreparesNestedFunctionConstants()
    {
        var code = Compile("def answer(): return 42\nresult = answer()\n");
        var prepared = PreparedPythonCode.Create(code);
        var functionCodeIndex = Assert
            .Single(
                code.Constants.Select((constant, index) => (constant, index)),
                item =>
                    item.constant.Type == DotPython.Compiler.Bytecode.PythonConstantType.CodeObject
            )
            .index;

        var function = prepared.GetFunctionCode(functionCodeIndex);

        Assert.Equal("answer", function.Definition.Name);
        Assert.Same(function.GetConstant(0), function.GetConstant(0));
    }

    [Fact]
    public void TruthValues_AreCanonicalSingletons()
    {
        Assert.Same(PythonTruthValue.True, PythonTruthValue.FromBoolean(true));
        Assert.Same(PythonTruthValue.False, PythonTruthValue.FromBoolean(false));
    }

    [Fact]
    public void GlobalLoadCache_ReusesSlotValuesAndInvalidatesOnNamespaceShapeChanges()
    {
        var code = Compile("global_value = 41\ndef get_value(): return global_value\n");
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("global_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "get_value");
        var loadIndex = GetLoadNameInstructionIndex(function);

        for (var invocation = 0; invocation < 8; invocation++)
        {
            Assert.Equal(new BigInteger(41), Invoke(engine, "get_value").Value);
        }

        Assert.True(initialization.Success);
        Assert.Equal(GlobalLoadCacheState.Global, function.GetGlobalLoadCacheState(loadIndex));

        var reassignment = engine.Execute(
            "global_value = 42",
            "reassignment.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(reassignment.Success);
        Assert.Equal(new BigInteger(42), Invoke(engine, "get_value").Value);
        Assert.Equal(GlobalLoadCacheState.Global, function.GetGlobalLoadCacheState(loadIndex));

        var shapeChange = engine.Execute(
            "other_value = 1",
            "shape-change.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(shapeChange.Success);
        Assert.Equal(new BigInteger(42), Invoke(engine, "get_value").Value);
        Assert.Equal(GlobalLoadCacheState.Adaptive, function.GetGlobalLoadCacheState(loadIndex));
    }

    [Fact]
    public void GlobalLoadCache_SpecializesBuiltinsAndAllowsGlobalShadowing()
    {
        var code = Compile("def get_builtin(): return print\n");
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("builtin_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "get_builtin");
        var loadIndex = GetLoadNameInstructionIndex(function);

        for (var invocation = 0; invocation < 16; invocation++)
        {
            Assert.IsType<PythonBuiltinFunctionValue>(
                engine.Invoke(
                    "get_builtin",
                    [],
                    TextWriter.Null,
                    new ManagedExecutionOptions(),
                    TestContext.Current.CancellationToken
                )
            );
        }

        Assert.True(initialization.Success);
        Assert.Equal(GlobalLoadCacheState.Builtin, function.GetGlobalLoadCacheState(loadIndex));

        var shadow = engine.Execute(
            "print = 42",
            "shadow.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(shadow.Success);
        Assert.Equal(new BigInteger(42), Invoke(engine, "get_builtin").Value);
        Assert.Equal(GlobalLoadCacheState.Adaptive, function.GetGlobalLoadCacheState(loadIndex));
    }

    [Fact]
    public void BinaryAddCache_SpecializesWholeNumbersAndRecoversAfterDeoptimization()
    {
        var (engine, function, instructionIndex) = PrepareBinaryAddFunction();

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertWholeNumber(
                3,
                Invoke(
                    engine,
                    "add",
                    PythonWholeNumberValue.Create(1),
                    PythonWholeNumberValue.Create(2)
                )
            );
        }

        Assert.Equal(
            AdaptiveNumericCacheState.WholeNumber,
            function.GetBinaryAddCacheState(instructionIndex)
        );

        var largeValue = BigInteger.One << 200;
        AssertWholeNumber(
            largeValue + 1,
            Invoke(
                engine,
                "add",
                PythonWholeNumberValue.Create(largeValue),
                PythonWholeNumberValue.Create(1)
            )
        );

        AssertWholeNumber(
            3,
            Invoke(engine, "add", PythonTruthValue.True, PythonWholeNumberValue.Create(2))
        );
        Assert.Equal(
            AdaptiveNumericCacheState.Adaptive,
            function.GetBinaryAddCacheState(instructionIndex)
        );

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertWholeNumber(
                5,
                Invoke(
                    engine,
                    "add",
                    PythonWholeNumberValue.Create(2),
                    PythonWholeNumberValue.Create(3)
                )
            );
        }

        Assert.Equal(
            AdaptiveNumericCacheState.WholeNumber,
            function.GetBinaryAddCacheState(instructionIndex)
        );
    }

    [Fact]
    public void BinaryAddCache_SpecializesFloatingPointAndSaturatesGenericForText()
    {
        var (engine, function, instructionIndex) = PrepareBinaryAddFunction();

        for (var invocation = 0; invocation < 8; invocation++)
        {
            var result = Assert.IsType<PythonFloatingPointValue>(
                Invoke(
                    engine,
                    "add",
                    new PythonFloatingPointValue(1.25),
                    new PythonFloatingPointValue(2.5)
                )
            );
            Assert.Equal(3.75, result.Value);
        }

        Assert.Equal(
            AdaptiveNumericCacheState.FloatingPoint,
            function.GetBinaryAddCacheState(instructionIndex)
        );

        for (var invocation = 0; invocation < 8; invocation++)
        {
            var text = Assert.IsType<PythonTextValue>(
                Invoke(engine, "add", new PythonTextValue("Dot"), new PythonTextValue("Python"))
            );
            Assert.Equal("DotPython", text.Value);
        }

        Assert.Equal(
            AdaptiveNumericCacheState.Generic,
            function.GetBinaryAddCacheState(instructionIndex)
        );
    }

    [Theory]
    [MemberData(nameof(OrderedComparisonCases))]
    public void OrderedComparisonCache_SpecializesNumericOperandsAndDeoptimizesForMixedTypes(
        string sourceOperator,
        int opCodeValue
    )
    {
        var opCode = (PythonOpCode)opCodeValue;
        var expectedAscending = EvaluateOrderedComparison(opCode, -1);
        var (engine, function, instructionIndex) = PrepareOrderedComparisonFunction(
            sourceOperator,
            opCode
        );

        AssertTruth(
            false,
            Invoke(
                engine,
                "compare",
                new PythonFloatingPointValue(double.NaN),
                new PythonFloatingPointValue(1)
            )
        );

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertTruth(
                expectedAscending,
                Invoke(
                    engine,
                    "compare",
                    PythonWholeNumberValue.Create(10),
                    PythonWholeNumberValue.Create(20)
                )
            );
        }

        Assert.Equal(
            AdaptiveNumericCacheState.WholeNumber,
            function.GetOrderedComparisonCacheState(instructionIndex)
        );
        AssertTruth(
            EvaluateOrderedComparison(opCode, 0),
            Invoke(
                engine,
                "compare",
                PythonWholeNumberValue.Create(10),
                PythonWholeNumberValue.Create(10)
            )
        );

        var largeValue = BigInteger.One << 200;
        AssertTruth(
            expectedAscending,
            Invoke(
                engine,
                "compare",
                PythonWholeNumberValue.Create(largeValue),
                PythonWholeNumberValue.Create(largeValue + 1)
            )
        );
        AssertTruth(
            expectedAscending,
            Invoke(engine, "compare", PythonTruthValue.False, PythonTruthValue.True)
        );
        Assert.Equal(
            AdaptiveNumericCacheState.Adaptive,
            function.GetOrderedComparisonCacheState(instructionIndex)
        );

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertTruth(
                expectedAscending,
                Invoke(
                    engine,
                    "compare",
                    new PythonFloatingPointValue(1.25),
                    new PythonFloatingPointValue(2.5)
                )
            );
        }

        Assert.Equal(
            AdaptiveNumericCacheState.FloatingPoint,
            function.GetOrderedComparisonCacheState(instructionIndex)
        );
        AssertTruth(
            EvaluateOrderedComparison(opCode, 0),
            Invoke(
                engine,
                "compare",
                new PythonFloatingPointValue(2.5),
                new PythonFloatingPointValue(2.5)
            )
        );
        AssertTruth(
            false,
            Invoke(
                engine,
                "compare",
                new PythonFloatingPointValue(double.NaN),
                new PythonFloatingPointValue(1)
            )
        );
        AssertTruth(
            expectedAscending,
            Invoke(
                engine,
                "compare",
                PythonWholeNumberValue.Create(1),
                new PythonFloatingPointValue(1.5)
            )
        );
        Assert.Equal(
            AdaptiveNumericCacheState.Adaptive,
            function.GetOrderedComparisonCacheState(instructionIndex)
        );
    }

    [Theory]
    [MemberData(nameof(OrderedComparisonCases))]
    public void OrderedComparisonCache_SaturatesGenericForTextOperands(
        string sourceOperator,
        int opCodeValue
    )
    {
        var opCode = (PythonOpCode)opCodeValue;
        var (engine, function, instructionIndex) = PrepareOrderedComparisonFunction(
            sourceOperator,
            opCode
        );

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertTruth(
                EvaluateOrderedComparison(opCode, -1),
                Invoke(engine, "compare", new PythonTextValue("Dot"), new PythonTextValue("Python"))
            );
        }

        Assert.Equal(
            AdaptiveNumericCacheState.Generic,
            function.GetOrderedComparisonCacheState(instructionIndex)
        );
        AssertTruth(
            EvaluateOrderedComparison(opCode, 1),
            Invoke(engine, "compare", new PythonTextValue("Python"), new PythonTextValue("Dot"))
        );
        Assert.Equal(
            AdaptiveNumericCacheState.Generic,
            function.GetOrderedComparisonCacheState(instructionIndex)
        );
    }

    private static PythonWholeNumberValue Invoke(ManagedPythonEngine engine, string functionName) =>
        Assert.IsType<PythonWholeNumberValue>(
            engine.Invoke(
                functionName,
                [],
                TextWriter.Null,
                new ManagedExecutionOptions(),
                TestContext.Current.CancellationToken
            )
        );

    private static PythonValue Invoke(
        ManagedPythonEngine engine,
        string functionName,
        params PythonValue[] arguments
    ) =>
        engine.Invoke(
            functionName,
            arguments,
            TextWriter.Null,
            new ManagedExecutionOptions(),
            TestContext.Current.CancellationToken
        );

    private static void AssertWholeNumber(BigInteger expected, PythonValue value) =>
        Assert.Equal(expected, Assert.IsType<PythonWholeNumberValue>(value).Value);

    private static void AssertTruth(bool expected, PythonValue value) =>
        Assert.Equal(expected, Assert.IsType<PythonTruthValue>(value).Value);

    private static (
        ManagedPythonEngine Engine,
        PreparedPythonCode Function,
        int InstructionIndex
    ) PrepareBinaryAddFunction()
    {
        var code = Compile("def add(left, right): return left + right\n");
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("binary_add_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "add");

        Assert.True(initialization.Success);
        return (engine, function, GetInstructionIndex(function, PythonOpCode.BinaryAdd));
    }

    private static (
        ManagedPythonEngine Engine,
        PreparedPythonCode Function,
        int InstructionIndex
    ) PrepareOrderedComparisonFunction(string sourceOperator, PythonOpCode opCode)
    {
        var code = Compile($"def compare(left, right): return left {sourceOperator} right\n");
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("ordered_comparison_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "compare");

        Assert.True(initialization.Success);
        return (engine, function, GetInstructionIndex(function, opCode));
    }

    private static bool EvaluateOrderedComparison(PythonOpCode opCode, int comparison) =>
        opCode switch
        {
            PythonOpCode.CompareLessThan => comparison < 0,
            PythonOpCode.CompareLessThanOrEqual => comparison <= 0,
            PythonOpCode.CompareGreaterThan => comparison > 0,
            PythonOpCode.CompareGreaterThanOrEqual => comparison >= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };

    private static PreparedPythonCode GetPreparedFunction(
        ManagedPythonEngine engine,
        PythonCodeObject code,
        string functionName
    )
    {
        var functionCodeIndex = Assert
            .Single(
                code.Constants.Select((constant, index) => (constant, index)),
                item =>
                    item.constant.Type == PythonConstantType.CodeObject
                    && item.constant.Value is PythonCodeObject function
                    && function.Name == functionName
            )
            .index;
        return engine.PrepareCode(code).GetFunctionCode(functionCodeIndex);
    }

    private static int GetLoadNameInstructionIndex(PreparedPythonCode code) =>
        GetInstructionIndex(code, PythonOpCode.LoadName);

    private static int GetInstructionIndex(PreparedPythonCode code, PythonOpCode opCode) =>
        Assert
            .Single(
                code.Definition.Instructions.Select((instruction, index) => (instruction, index)),
                item => item.instruction.OpCode == opCode
            )
            .index;

    private static PythonCodeObject Compile(string code)
    {
        var source = new SourceText(code, "prepared-code-test.py");
        var parse = PythonParser.Parse(source);
        var compilation = PythonCompiler.Compile(parse.Module, source.FilePath!);

        Assert.Empty(parse.Diagnostics);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Code;
    }
}
