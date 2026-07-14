using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Language.Ast;
using DotPython.ParserGenerator;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class CompilerBenchmarks
{
    private PythonModule _module = null!;

    [Params(1, 25)]
    public int FunctionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var parseResult = PythonParser.Parse(BenchmarkPrograms.CreateFrontEndSource(FunctionCount));
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The compiler benchmark source did not parse.");
        }

        _module = parseResult.Module;
    }

    [Benchmark]
    public PythonCompilationResult Compile() =>
        PythonCompiler.Compile(_module, "compiler-benchmark.py");
}
