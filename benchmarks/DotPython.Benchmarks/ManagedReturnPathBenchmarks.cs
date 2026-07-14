using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class ManagedReturnPathBenchmarks
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];
    private ManagedPythonEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateManagedReturnPathSource();
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException(
                "The managed-return benchmark source did not parse."
            );
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException(
                "The managed-return benchmark source did not compile."
            );
        }

        _engine = new ManagedPythonEngine();
        var initialization = _engine.Execute(
            DotPythonModuleArtifact.Create("managed_return_path", compilation.Code),
            TextWriter.Null
        );
        if (!initialization.Success)
        {
            throw new InvalidOperationException(
                "The managed-return benchmark program did not initialize."
            );
        }

        VerifyResult(Invoke("call_bare_return"));
        VerifyResult(Invoke("call_explicit_none"));
        VerifyCallCacheState(compilation.Code, "call_bare_return");
        VerifyCallCacheState(compilation.Code, "call_explicit_none");
    }

    [Benchmark(Baseline = true)]
    public object BareReturn() => Invoke("call_bare_return");

    [Benchmark]
    public object ExplicitNoneReturn() => Invoke("call_explicit_none");

    private PythonValue Invoke(string functionName) =>
        _engine.Invoke(
            functionName,
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private void VerifyCallCacheState(PythonCodeObject code, string functionName)
    {
        var functionCodeIndex = code
            .Constants.Select((constant, index) => (constant, index))
            .Single(item =>
                item.constant.Type == PythonConstantType.CodeObject
                && item.constant.Value is PythonCodeObject function
                && function.Name == functionName
            )
            .index;
        var function = _engine.PrepareCode(code).GetFunctionCode(functionCodeIndex);
        var instructionIndex = function
            .Definition.Instructions.Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.OpCode == PythonOpCode.Call)
            .index;
        if (
            function.GetCallCacheState(instructionIndex)
            != AdaptiveCallCacheState.ManagedFunctionEmptyFrame
        )
        {
            throw new InvalidOperationException(
                "The managed-return benchmark call site was not specialized."
            );
        }
    }

    private static void VerifyResult(PythonValue result)
    {
        if (result is not PythonNoneValue)
        {
            throw new InvalidOperationException(
                "The managed-return benchmark produced an unexpected result."
            );
        }
    }
}
