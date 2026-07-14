using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class RuntimeBenchmarks
{
    private DotPythonModuleArtifact _artifact = null!;
    private ManagedPythonEngine _artifactEngine = null!;
    private SourceText _source = null!;
    private ManagedPythonEngine _sourceEngine = null!;

    [Params(10, 1_000)]
    public int Iterations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _source = BenchmarkPrograms.CreateRuntimeSource(Iterations);
        var parseResult = PythonParser.Parse(_source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The runtime benchmark source did not parse.");
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, _source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException("The runtime benchmark source did not compile.");
        }

        _artifact = DotPythonModuleArtifact.Create("runtime_benchmark", compilation.Code);
        _artifactEngine = new ManagedPythonEngine();
        _sourceEngine = new ManagedPythonEngine();

        var verification = new ManagedPythonEngine().Execute(_artifact, TextWriter.Null);
        if (!verification.Success)
        {
            throw new InvalidOperationException("The runtime benchmark program did not execute.");
        }
    }

    [Benchmark(Baseline = true)]
    public ManagedExecutionResult ExecuteCompiledArtifact() =>
        _artifactEngine.Execute(_artifact, TextWriter.Null);

    [Benchmark]
    public ManagedExecutionResult ExecuteSource() =>
        _sourceEngine.Execute(_source, TextWriter.Null);
}
