using BenchmarkDotNet.Attributes;
using DotPython.Language.Syntax;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class FrontEndBenchmarks
{
    private SourceText _source = null!;

    [Params(1, 25)]
    public int FunctionCount { get; set; }

    [GlobalSetup]
    public void Setup() => _source = BenchmarkPrograms.CreateFrontEndSource(FunctionCount);

    [Benchmark(Baseline = true)]
    public TokenizationResult Tokenize() => PythonTokenizer.Tokenize(_source);

    [Benchmark]
    public PythonParseResult Parse() => PythonParser.Parse(_source);
}
