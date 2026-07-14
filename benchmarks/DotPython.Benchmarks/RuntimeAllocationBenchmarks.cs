using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

public enum RuntimeAllocationScenario
{
    Empty,
    Constants,
    IntegerLoop,
    FunctionCalls,
    GlobalLookup,
}

[MemoryDiagnoser]
public class RuntimeAllocationBenchmarks
{
    private DotPythonModuleArtifact _artifact = null!;
    private ManagedPythonEngine _engine = null!;

    [ParamsAllValues]
    public RuntimeAllocationScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateAllocationSource(Scenario);
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The allocation benchmark source did not parse.");
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException("The allocation benchmark source did not compile.");
        }

        _artifact = DotPythonModuleArtifact.Create("allocation_benchmark", compilation.Code);
        _engine = new ManagedPythonEngine();

        var verification = new ManagedPythonEngine().Execute(_artifact, TextWriter.Null);
        if (!verification.Success)
        {
            throw new InvalidOperationException(
                "The allocation benchmark program did not execute."
            );
        }
    }

    [Benchmark]
    public ManagedExecutionResult ExecuteCompiledArtifact() =>
        _engine.Execute(_artifact, TextWriter.Null);
}
