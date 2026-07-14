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

public enum OrderedComparisonOperation
{
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
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

    [ParamsAllValues]
    public OrderedComparisonOperation Operation { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateComparisonSpecializationSource(
            OperandFamily,
            Operation
        );
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
            InvokeComparison(
                _genericEngine,
                new PythonTextValue("Dot"),
                new PythonTextValue("Python")
            );
            InvokeComparison(_specializedEngine, CreateLeftOperand(), CreateRightOperand());
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

    private static void InvokeComparison(
        ManagedPythonEngine engine,
        PythonValue left,
        PythonValue right
    ) =>
        engine.Invoke(
            "compare",
            [left, right],
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private PythonValue CreateLeftOperand() => CreateOperand(useAscendingLeft: true);

    private PythonValue CreateRightOperand() => CreateOperand(useAscendingLeft: false);

    private PythonValue CreateOperand(bool useAscendingLeft)
    {
        var useSmallerValue = Operation
            is OrderedComparisonOperation.LessThan
                or OrderedComparisonOperation.LessThanOrEqual
            ? useAscendingLeft
            : !useAscendingLeft;
        return OperandFamily switch
        {
            ComparisonOperandFamily.WholeNumber => PythonWholeNumberValue.Create(
                useSmallerValue ? 10000 : 11000
            ),
            ComparisonOperandFamily.FloatingPoint => new PythonFloatingPointValue(
                useSmallerValue ? 10000 : 11000
            ),
            _ => throw new InvalidOperationException("The operand family is unsupported."),
        };
    }

    private static void VerifyResult(PythonValue result)
    {
        if (result is not PythonTruthValue { Value: true })
        {
            throw new InvalidOperationException(
                "The comparison benchmark produced an unexpected result."
            );
        }
    }

    private void VerifyCacheState(
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
                && function.Name == "compare"
            )
            .index;
        var function = engine.PrepareCode(code).GetFunctionCode(functionCodeIndex);
        var instructionIndex = function
            .Definition.Instructions.Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.OpCode == GetOpCode())
            .index;
        if (function.GetOrderedComparisonCacheState(instructionIndex) != expectedState)
        {
            throw new InvalidOperationException(
                "The comparison benchmark cache did not reach its expected state."
            );
        }
    }

    private PythonOpCode GetOpCode() =>
        Operation switch
        {
            OrderedComparisonOperation.LessThan => PythonOpCode.CompareLessThan,
            OrderedComparisonOperation.LessThanOrEqual => PythonOpCode.CompareLessThanOrEqual,
            OrderedComparisonOperation.GreaterThan => PythonOpCode.CompareGreaterThan,
            OrderedComparisonOperation.GreaterThanOrEqual => PythonOpCode.CompareGreaterThanOrEqual,
            _ => throw new InvalidOperationException("The comparison operation is unsupported."),
        };
}
