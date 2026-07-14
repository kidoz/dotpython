using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class ManagedCallSpecializationBenchmarks
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];
    private ManagedPythonEngine _genericEngine = null!;
    private ManagedPythonEngine _specializedEngine = null!;

    [Params(0, 1)]
    public int ArgumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateManagedCallSpecializationSource(ArgumentCount);
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The managed-call benchmark source did not parse.");
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException(
                "The managed-call benchmark source did not compile."
            );
        }

        var artifact = DotPythonModuleArtifact.Create(
            "managed_call_specialization",
            compilation.Code
        );
        _genericEngine = CreateInitializedEngine(artifact);
        _specializedEngine = CreateInitializedEngine(artifact);

        VerifyResult(InvokeCallLoop(_genericEngine));
        SetManagedTarget(_genericEngine);
        SetManagedTarget(_specializedEngine);
        VerifyResult(InvokeCallLoop(_genericEngine));
        VerifyResult(InvokeCallLoop(_specializedEngine));

        VerifyCacheState(_genericEngine, compilation.Code, AdaptiveCallCacheState.Generic);
        VerifyCacheState(
            _specializedEngine,
            compilation.Code,
            ArgumentCount == 0
                ? AdaptiveCallCacheState.ManagedFunctionEmptyFrame
                : AdaptiveCallCacheState.ManagedFunction
        );
    }

    [Benchmark(Baseline = true)]
    public object GenericPath() => InvokeCallLoop(_genericEngine);

    [Benchmark]
    public object SpecializedPath() => InvokeCallLoop(_specializedEngine);

    private static ManagedPythonEngine CreateInitializedEngine(DotPythonModuleArtifact artifact)
    {
        var engine = new ManagedPythonEngine();
        var result = engine.Execute(artifact, TextWriter.Null);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "The managed-call benchmark program did not initialize."
            );
        }

        return engine;
    }

    private static void SetManagedTarget(ManagedPythonEngine engine)
    {
        var result = engine.Execute(
            "target = callee",
            "managed-call-specialization-setup.py",
            TextWriter.Null
        );
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "The managed-call benchmark target could not be configured."
            );
        }
    }

    private static PythonValue InvokeCallLoop(ManagedPythonEngine engine) =>
        engine.Invoke(
            "call_loop",
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private static void VerifyCacheState(
        ManagedPythonEngine engine,
        PythonCodeObject code,
        AdaptiveCallCacheState expectedState
    )
    {
        var functionCodeIndex = code
            .Constants.Select((constant, index) => (constant, index))
            .Single(item =>
                item.constant.Type == PythonConstantType.CodeObject
                && item.constant.Value is PythonCodeObject function
                && function.Name == "call_loop"
            )
            .index;
        var function = engine.PrepareCode(code).GetFunctionCode(functionCodeIndex);
        var instructionIndex = function
            .Definition.Instructions.Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.OpCode == PythonOpCode.Call)
            .index;
        if (function.GetCallCacheState(instructionIndex) != expectedState)
        {
            throw new InvalidOperationException(
                "The managed-call benchmark cache did not reach its expected state."
            );
        }
    }

    private static void VerifyResult(PythonValue result)
    {
        if (result is not PythonNoneValue)
        {
            throw new InvalidOperationException(
                "The managed-call benchmark produced an unexpected result."
            );
        }
    }
}
