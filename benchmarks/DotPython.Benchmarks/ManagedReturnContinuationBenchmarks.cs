using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class ManagedReturnContinuationBenchmarks
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];
    private ManagedPythonEngine _engine = null!;

    [Params(0, 1)]
    public int ArgumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateManagedFramePathSource(ArgumentCount);
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException(
                "The return-continuation benchmark source did not parse."
            );
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException(
                "The return-continuation benchmark source did not compile."
            );
        }

        _engine = new ManagedPythonEngine();
        var initialization = _engine.Execute(
            DotPythonModuleArtifact.Create("managed_return_continuation", compilation.Code),
            TextWriter.Null
        );
        if (!initialization.Success)
        {
            throw new InvalidOperationException(
                "The return-continuation benchmark program did not initialize."
            );
        }

        VerifyResult(InvokeWithoutContinuation());
        VerifyResult(InvokeWithContinuation());
        VerifyCallCacheState(compilation.Code);
    }

    [Benchmark(Baseline = true)]
    public object DispatchStoreLocal() => InvokeWithoutContinuation();

    [Benchmark]
    public object ContinueToLocal() => InvokeWithContinuation();

    private PythonValue InvokeWithoutContinuation() =>
        _engine.InvokeWithoutReturnLocalContinuation(
            "run_calls",
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private PythonValue InvokeWithContinuation() =>
        _engine.Invoke(
            "run_calls",
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private void VerifyCallCacheState(PythonCodeObject code)
    {
        var functionCodeIndex = code
            .Constants.Select((constant, index) => (constant, index))
            .Single(item =>
                item.constant.Type == PythonConstantType.CodeObject
                && item.constant.Value is PythonCodeObject function
                && function.Name == "call_loop"
            )
            .index;
        var function = _engine.PrepareCode(code).GetFunctionCode(functionCodeIndex);
        var instructionIndex = function
            .Definition.Instructions.Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.OpCode == PythonOpCode.Call)
            .index;
        var expectedState =
            ArgumentCount == 0
                ? AdaptiveCallCacheState.ManagedFunctionEmptyFrame
                : AdaptiveCallCacheState.ManagedFunction;
        if (function.GetCallCacheState(instructionIndex) != expectedState)
        {
            throw new InvalidOperationException(
                "The return-continuation benchmark call site was not specialized."
            );
        }
    }

    private static void VerifyResult(PythonValue result)
    {
        if (result is not PythonNoneValue)
        {
            throw new InvalidOperationException(
                "The return-continuation benchmark produced an unexpected result."
            );
        }
    }
}
