using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class ManagedFramePathBenchmarks
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
                "The managed-frame benchmark source did not parse."
            );
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException(
                "The managed-frame benchmark source did not compile."
            );
        }

        _engine = new ManagedPythonEngine();
        var initialization = _engine.Execute(
            DotPythonModuleArtifact.Create("managed_frame_path", compilation.Code),
            TextWriter.Null
        );
        if (!initialization.Success)
        {
            throw new InvalidOperationException(
                "The managed-frame benchmark program did not initialize."
            );
        }

        VerifyResult(Invoke("run_inline"));
        VerifyResult(Invoke("run_calls"));
        VerifyCallCacheState(compilation.Code);
    }

    [Benchmark(Baseline = true)]
    public object InlinePath() => Invoke("run_inline");

    [Benchmark]
    public object SpecializedFramePath() => Invoke("run_calls");

    private PythonValue Invoke(string functionName) =>
        _engine.Invoke(
            functionName,
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
                "The managed-frame benchmark call site was not specialized."
            );
        }
    }

    private static void VerifyResult(PythonValue result)
    {
        if (result is not PythonNoneValue)
        {
            throw new InvalidOperationException(
                "The managed-frame benchmark produced an unexpected result."
            );
        }
    }
}
