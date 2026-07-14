using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

public enum ComparisonOperandFamily
{
    WholeNumber,
    FloatingPoint,
}

[MemoryDiagnoser]
public class ComparisonSpecializationBenchmarks
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];
    private ManagedPythonEngine _genericEngine = null!;
    private ManagedPythonEngine _specializedEngine = null!;

    [ParamsAllValues]
    public ComparisonOperandFamily OperandFamily { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateComparisonSpecializationSource(OperandFamily);
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The comparison benchmark source did not parse.");
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException("The comparison benchmark source did not compile.");
        }

        var artifact = DotPythonModuleArtifact.Create(
            "comparison_specialization",
            compilation.Code
        );
        _genericEngine = CreateInitializedEngine(artifact);
        _specializedEngine = CreateInitializedEngine(artifact);

        for (var observation = 0; observation < 8; observation++)
        {
            InvokeLessThan(
                _genericEngine,
                new PythonTextValue("Dot"),
                new PythonTextValue("Python")
            );
            InvokeLessThan(_specializedEngine, CreateLeftOperand(), CreateRightOperand());
        }

        VerifyCacheState(_genericEngine, compilation.Code, AdaptiveNumericCacheState.Generic);
        VerifyCacheState(
            _specializedEngine,
            compilation.Code,
            OperandFamily == ComparisonOperandFamily.WholeNumber
                ? AdaptiveNumericCacheState.WholeNumber
                : AdaptiveNumericCacheState.FloatingPoint
        );
        VerifyResult(InvokeComparisonLoop(_genericEngine));
        VerifyResult(InvokeComparisonLoop(_specializedEngine));
    }

    [Benchmark(Baseline = true)]
    public object GenericPath() => InvokeComparisonLoop(_genericEngine);

    [Benchmark]
    public object SpecializedPath() => InvokeComparisonLoop(_specializedEngine);

    private static ManagedPythonEngine CreateInitializedEngine(DotPythonModuleArtifact artifact)
    {
        var engine = new ManagedPythonEngine();
        var result = engine.Execute(artifact, TextWriter.Null);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "The comparison benchmark program did not initialize."
            );
        }

        return engine;
    }

    private static PythonValue InvokeComparisonLoop(ManagedPythonEngine engine) =>
        engine.Invoke(
            "compare_values",
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private static void InvokeLessThan(
        ManagedPythonEngine engine,
        PythonValue left,
        PythonValue right
    ) =>
        engine.Invoke(
            "less_than",
            [left, right],
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private PythonValue CreateLeftOperand() =>
        OperandFamily switch
        {
            ComparisonOperandFamily.WholeNumber => PythonWholeNumberValue.Create(10000),
            ComparisonOperandFamily.FloatingPoint => new PythonFloatingPointValue(10000),
            _ => throw new ArgumentOutOfRangeException(nameof(OperandFamily)),
        };

    private PythonValue CreateRightOperand() =>
        OperandFamily switch
        {
            ComparisonOperandFamily.WholeNumber => PythonWholeNumberValue.Create(11000),
            ComparisonOperandFamily.FloatingPoint => new PythonFloatingPointValue(11000),
            _ => throw new ArgumentOutOfRangeException(nameof(OperandFamily)),
        };

    private static void VerifyResult(PythonValue result)
    {
        if (result is not PythonTruthValue { Value: true })
        {
            throw new InvalidOperationException(
                "The comparison benchmark produced an unexpected result."
            );
        }
    }

    private static void VerifyCacheState(
        ManagedPythonEngine engine,
        PythonCodeObject code,
        AdaptiveNumericCacheState expectedState
    )
    {
        var functionCodeIndex = code
            .Constants.Select((constant, index) => (constant, index))
            .Single(item =>
                item.constant.Type == PythonConstantType.CodeObject
                && item.constant.Value is PythonCodeObject function
                && function.Name == "less_than"
            )
            .index;
        var function = engine.PrepareCode(code).GetFunctionCode(functionCodeIndex);
        var instructionIndex = function
            .Definition.Instructions.Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.OpCode == PythonOpCode.CompareLessThan)
            .index;
        if (function.GetLessThanCacheState(instructionIndex) != expectedState)
        {
            throw new InvalidOperationException(
                "The comparison benchmark cache did not reach its expected state."
            );
        }
    }
}
