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
    public void ExecutionProfile_CountsSequentialPairsWithinEachLogicalFrame()
    {
        var code = Compile(
            "def callee(): return None\n"
                + "def invoke():\n"
                + "    current = 0\n"
                + "    value = None\n"
                + "    while current != 10:\n"
                + "        value = callee()\n"
                + "        current = current + 1\n"
                + "    return value\n"
        );
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("execution_profile", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var profile = new PythonExecutionProfile();

        var result = engine.InvokeProfiled(
            "invoke",
            Array.Empty<PythonValue>(),
            TextWriter.Null,
            new ManagedExecutionOptions(),
            profile,
            TestContext.Current.CancellationToken
        );

        Assert.True(initialization.Success);
        Assert.IsType<PythonNoneValue>(result);
        Assert.Equal(10, profile.GetInstructionCount(PythonOpCode.Call));
        Assert.Equal(10, profile.GetPairCount(PythonOpCode.LoadName, PythonOpCode.Call));
        Assert.Equal(10, profile.GetPairCount(PythonOpCode.Call, PythonOpCode.StoreLocal));
        Assert.Equal(10, profile.GetInstructionCount(PythonOpCode.ReturnNone));
        Assert.Equal(1, profile.GetInstructionCount(PythonOpCode.ReturnLocal));
        Assert.Equal(0, profile.GetPairCount(PythonOpCode.LoadConstant, PythonOpCode.ReturnValue));
        Assert.Equal(0, profile.GetPairCount(PythonOpCode.LoadLocal, PythonOpCode.ReturnValue));
        Assert.Contains(
            new PythonInstructionPairCount(PythonOpCode.Call, PythonOpCode.StoreLocal, 10),
            profile.GetPairs()
        );
    }

    [Fact]
    public void ReturnLocalContinuation_PreservesInstructionAccountingAndFailureCleanup()
    {
        var code = Compile(
            "def identity(value): return value\n"
                + "def invoke():\n"
                + "    result = identity(42)\n"
                + "    return result\n"
        );
        var artifact = DotPythonModuleArtifact.Create("return_local_continuation", code);
        var optimizedEngine = new ManagedPythonEngine();
        var controlEngine = new ManagedPythonEngine();

        Assert.True(
            optimizedEngine
                .Execute(
                    artifact,
                    TextWriter.Null,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.True(
            controlEngine
                .Execute(
                    artifact,
                    TextWriter.Null,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );

        var limitedOptions = new ManagedExecutionOptions { InstructionLimit = 5 };
        var optimizedFault = Assert.Throws<PythonRuntimeException>(() =>
            optimizedEngine.Invoke(
                "invoke",
                Array.Empty<PythonValue>(),
                TextWriter.Null,
                limitedOptions,
                TestContext.Current.CancellationToken
            )
        );
        var controlFault = Assert.Throws<PythonRuntimeException>(() =>
            controlEngine.InvokeWithoutReturnLocalContinuation(
                "invoke",
                Array.Empty<PythonValue>(),
                TextWriter.Null,
                limitedOptions,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("DPY4001", optimizedFault.Code);
        Assert.Equal(controlFault.Code, optimizedFault.Code);
        Assert.Equal(controlFault.Span, optimizedFault.Span);
        AssertWholeNumber(42, Invoke(optimizedEngine, "invoke", Array.Empty<PythonValue>()));
        AssertWholeNumber(
            42,
            controlEngine.InvokeWithoutReturnLocalContinuation(
                "invoke",
                Array.Empty<PythonValue>(),
                TextWriter.Null,
                new ManagedExecutionOptions(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void ReturnLocalContinuation_LeavesCapturedStoresOnTheGeneralPath()
    {
        var code = Compile(
            "def identity(value): return value\n"
                + "def outer():\n"
                + "    result = 0\n"
                + "    def capture(): return result\n"
                + "    result = identity(42)\n"
                + "    return capture()\n"
        );
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("return_cell_control", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(initialization.Success);
        AssertWholeNumber(42, Invoke(engine, "outer", Array.Empty<PythonValue>()));
        AssertWholeNumber(
            42,
            engine.InvokeWithoutReturnLocalContinuation(
                "outer",
                Array.Empty<PythonValue>(),
                TextWriter.Null,
                new ManagedExecutionOptions(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void ReturnLocalContinuation_PreservesCancellationBeforeTheConsumedStore()
    {
        var code = Compile(
            "def callee():\n"
                + "    print()\n"
                + "    return 42\n"
                + "def invoke():\n"
                + "    result = callee()\n"
                + "    return result\n"
        );
        var artifact = DotPythonModuleArtifact.Create("return_local_cancellation", code);
        var optimizedEngine = new ManagedPythonEngine();
        var controlEngine = new ManagedPythonEngine();

        Assert.True(
            optimizedEngine
                .Execute(
                    artifact,
                    TextWriter.Null,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.True(
            controlEngine
                .Execute(
                    artifact,
                    TextWriter.Null,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );

        using var optimizedCancellation = new CancellationTokenSource();
        using var controlCancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            optimizedEngine.Invoke(
                "invoke",
                Array.Empty<PythonValue>(),
                new CancelOnWriteTextWriter(optimizedCancellation),
                new ManagedExecutionOptions(),
                optimizedCancellation.Token
            )
        );
        Assert.Throws<OperationCanceledException>(() =>
            controlEngine.InvokeWithoutReturnLocalContinuation(
                "invoke",
                Array.Empty<PythonValue>(),
                new CancelOnWriteTextWriter(controlCancellation),
                new ManagedExecutionOptions(),
                controlCancellation.Token
            )
        );

        AssertWholeNumber(42, Invoke(optimizedEngine, "invoke", Array.Empty<PythonValue>()));
        AssertWholeNumber(
            42,
            controlEngine.InvokeWithoutReturnLocalContinuation(
                "invoke",
                Array.Empty<PythonValue>(),
                TextWriter.Null,
                new ManagedExecutionOptions(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void ReturnLocal_PreservesInstructionAccountingAndFailureCleanup()
    {
        var code = Compile(
            "def identity(value): return value\n"
                + "def fail():\n"
                + "    return value\n"
                + "    value = 42\n"
        );
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("return_local", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var arguments = new PythonValue[] { PythonWholeNumberValue.Create(42) };

        Assert.True(initialization.Success);
        AssertWholeNumber(
            42,
            engine.Invoke(
                "identity",
                arguments,
                TextWriter.Null,
                new ManagedExecutionOptions { InstructionLimit = 1 },
                TestContext.Current.CancellationToken
            )
        );
        var fault = Assert.Throws<PythonRuntimeException>(() =>
            Invoke(engine, "fail", Array.Empty<PythonValue>())
        );
        Assert.Equal("DPY4008", fault.Code);
        AssertWholeNumber(42, Invoke(engine, "identity", arguments));
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
    public void ManagedCallCache_SpecializesStableTargetsAndDeoptimizesBeforeReplacementCalls()
    {
        var code = Compile(
            "def identity(value): return value\n"
                + "def replacement(value): return value + 1\n"
                + "def no_arguments(): return 7\n"
                + "def invoke(value): return identity(value)\n"
        );
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("managed_call_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "invoke");
        var callIndex = GetInstructionIndex(function, PythonOpCode.Call);

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertWholeNumber(41, Invoke(engine, "invoke", PythonWholeNumberValue.Create(41)));
        }

        Assert.True(initialization.Success);
        Assert.Equal(AdaptiveCallCacheState.ManagedFunction, function.GetCallCacheState(callIndex));

        var replace = engine.Execute(
            "identity = replacement",
            "replace-call-target.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(replace.Success);
        AssertWholeNumber(42, Invoke(engine, "invoke", PythonWholeNumberValue.Create(41)));
        Assert.Equal(AdaptiveCallCacheState.Adaptive, function.GetCallCacheState(callIndex));

        for (var invocation = 1; invocation < 8; invocation++)
        {
            AssertWholeNumber(42, Invoke(engine, "invoke", PythonWholeNumberValue.Create(41)));
        }

        Assert.Equal(AdaptiveCallCacheState.ManagedFunction, function.GetCallCacheState(callIndex));

        var changeArity = engine.Execute(
            "identity = no_arguments",
            "change-call-arity.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(changeArity.Success);
        var exception = Assert.Throws<PythonRuntimeException>(() =>
            Invoke(engine, "invoke", PythonWholeNumberValue.Create(41))
        );
        Assert.Equal("DPY4009", exception.Code);
        Assert.Equal(AdaptiveCallCacheState.Adaptive, function.GetCallCacheState(callIndex));
    }

    [Fact]
    public void ManagedCallCache_SaturatesGenericForBuiltinTargetsWithoutChangingSemantics()
    {
        var code = Compile("def invoke(): return print()\n");
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("builtin_call_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "invoke");
        var callIndex = GetInstructionIndex(function, PythonOpCode.Call);

        for (var invocation = 0; invocation < 8; invocation++)
        {
            Assert.IsType<PythonNoneValue>(Invoke(engine, "invoke", Array.Empty<PythonValue>()));
        }

        Assert.True(initialization.Success);
        Assert.Equal(AdaptiveCallCacheState.Generic, function.GetCallCacheState(callIndex));

        var shadow = engine.Execute(
            "def managed(): return 42\nprint = managed",
            "shadow-call-target.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(shadow.Success);
        AssertWholeNumber(42, Invoke(engine, "invoke", Array.Empty<PythonValue>()));
        Assert.Equal(AdaptiveCallCacheState.Generic, function.GetCallCacheState(callIndex));
    }

    [Fact]
    public void ManagedCallCache_UsesEmptyFramesOnlyForEligibleZeroArgumentFunctions()
    {
        var code = Compile(
            "def answer(): return 42\n"
                + "def with_local():\n"
                + "    value = 42\n"
                + "    return value\n"
                + "def invoke_answer(): return answer()\n"
                + "def invoke_local(): return with_local()\n"
        );
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("empty_frame_call_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var answerInvoker = GetPreparedFunction(engine, code, "invoke_answer");
        var localInvoker = GetPreparedFunction(engine, code, "invoke_local");
        var answerCallIndex = GetInstructionIndex(answerInvoker, PythonOpCode.Call);
        var localCallIndex = GetInstructionIndex(localInvoker, PythonOpCode.Call);

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertWholeNumber(42, Invoke(engine, "invoke_answer", Array.Empty<PythonValue>()));
            AssertWholeNumber(42, Invoke(engine, "invoke_local", Array.Empty<PythonValue>()));
        }

        Assert.True(initialization.Success);
        Assert.Equal(
            AdaptiveCallCacheState.ManagedFunctionEmptyFrame,
            answerInvoker.GetCallCacheState(answerCallIndex)
        );
        Assert.Equal(
            AdaptiveCallCacheState.ManagedFunction,
            localInvoker.GetCallCacheState(localCallIndex)
        );
    }

    [Fact]
    public void CallLocal_ReusesCallSpecializationAndPreservesFailureCleanup()
    {
        var code = Compile(
            "def no_arguments(): return 42\n"
                + "def needs_argument(value): return value\n"
                + "def call_managed():\n"
                + "    target = no_arguments\n"
                + "    return target()\n"
                + "def call_builtin():\n"
                + "    target = print\n"
                + "    return target()\n"
                + "def call_invalid():\n"
                + "    target = needs_argument\n"
                + "    return target()\n"
                + "def call_unbound():\n"
                + "    return target()\n"
                + "    target = no_arguments\n"
        );
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("call_local", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "call_managed");
        var callIndex = GetInstructionIndex(function, PythonOpCode.CallLocal);

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertWholeNumber(42, Invoke(engine, "call_managed", Array.Empty<PythonValue>()));
        }

        Assert.True(initialization.Success);
        Assert.Equal(
            AdaptiveCallCacheState.ManagedFunctionEmptyFrame,
            function.GetCallCacheState(callIndex)
        );
        Assert.IsType<PythonNoneValue>(Invoke(engine, "call_builtin", Array.Empty<PythonValue>()));
        var arityFailure = Assert.Throws<PythonRuntimeException>(() =>
            Invoke(engine, "call_invalid", Array.Empty<PythonValue>())
        );
        Assert.Equal("DPY4009", arityFailure.Code);
        var unboundFailure = Assert.Throws<PythonRuntimeException>(() =>
            Invoke(engine, "call_unbound", Array.Empty<PythonValue>())
        );
        Assert.Equal("DPY4008", unboundFailure.Code);
        AssertWholeNumber(42, Invoke(engine, "call_managed", Array.Empty<PythonValue>()));
    }

    [Fact]
    public void ManagedCallCache_KeepsClosureFunctionsOnTheGeneralFramePath()
    {
        var code = Compile(
            "def outer(value):\n"
                + "    def inner(): return value\n"
                + "    return inner\n"
                + "target = outer(42)\n"
                + "def invoke(): return target()\n"
        );
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("closure_call_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "invoke");
        var callIndex = GetInstructionIndex(function, PythonOpCode.Call);

        for (var invocation = 0; invocation < 8; invocation++)
        {
            AssertWholeNumber(42, Invoke(engine, "invoke", Array.Empty<PythonValue>()));
        }

        Assert.True(initialization.Success);
        Assert.Equal(AdaptiveCallCacheState.ManagedFunction, function.GetCallCacheState(callIndex));
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

    private sealed class CancelOnWriteTextWriter(CancellationTokenSource cancellation)
        : StringWriter
    {
        public override void WriteLine(string? value)
        {
            cancellation.Cancel();
            base.WriteLine(value);
        }
    }
}
