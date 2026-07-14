using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class ManagedCallDispatchBenchmarks
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];
    private ManagedPythonEngine _engine = null!;

    [Params(0, 1)]
    public int ArgumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateManagedCallDispatchSource(ArgumentCount);
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

        _engine = new ManagedPythonEngine();
        var initialization = _engine.Execute(
            DotPythonModuleArtifact.Create("managed_call_dispatch", compilation.Code),
            TextWriter.Null
        );
        if (!initialization.Success)
        {
            throw new InvalidOperationException(
                "The managed-call benchmark program did not initialize."
            );
        }

        VerifyResult(Invoke("inline_loop"));
        VerifyResult(Invoke("call_loop"));
    }

    [Benchmark(Baseline = true)]
    public object InlineLoop() => Invoke("inline_loop");

    [Benchmark]
    public object ManagedFunctionCalls() => Invoke("call_loop");

    private PythonValue Invoke(string functionName) =>
        _engine.Invoke(
            functionName,
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

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
