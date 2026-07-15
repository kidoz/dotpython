using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class ManagedReturnLocalBenchmarks
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];
    private ManagedPythonEngine _controlEngine = null!;
    private ManagedPythonEngine _optimizedEngine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateManagedFramePathSource(argumentCount: 1);
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The return-local benchmark source did not parse.");
        }

        var controlCompilation = PythonCompiler.CompileWithoutReturnLocal(
            parseResult.Module,
            source.FilePath!
        );
        var optimizedCompilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!controlCompilation.Success || !optimizedCompilation.Success)
        {
            throw new InvalidOperationException(
                "The return-local benchmark source did not compile."
            );
        }

        _controlEngine = new ManagedPythonEngine();
        _optimizedEngine = new ManagedPythonEngine();
        var controlInitialization = _controlEngine.Execute(
            DotPythonModuleArtifact.Create("managed_return_local_control", controlCompilation.Code),
            TextWriter.Null
        );
        var optimizedInitialization = _optimizedEngine.Execute(
            DotPythonModuleArtifact.Create(
                "managed_return_local_optimized",
                optimizedCompilation.Code
            ),
            TextWriter.Null
        );
        if (!controlInitialization.Success || !optimizedInitialization.Success)
        {
            throw new InvalidOperationException(
                "The return-local benchmark program did not initialize."
            );
        }

        VerifyResult(InvokeControl());
        VerifyResult(InvokeOptimized());
        VerifyPreparedState(_controlEngine, controlCompilation.Code, expectReturnLocal: false);
        VerifyPreparedState(_optimizedEngine, optimizedCompilation.Code, expectReturnLocal: true);
    }

    [Benchmark(Baseline = true)]
    public object DispatchLoadAndReturn() => InvokeControl();

    [Benchmark]
    public object DispatchReturnLocal() => InvokeOptimized();

    private PythonValue InvokeControl() =>
        _controlEngine.Invoke(
            "run_calls",
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private PythonValue InvokeOptimized() =>
        _optimizedEngine.Invoke(
            "run_calls",
            NoArguments,
            TextWriter.Null,
            ExecutionOptions,
            CancellationToken.None
        );

    private static void VerifyPreparedState(
        ManagedPythonEngine engine,
        PythonCodeObject code,
        bool expectReturnLocal
    )
    {
        var prepared = engine.PrepareCode(code);
        var callee = GetFunctionCode(prepared, code, "callee");
        var opCodes = callee
            .Definition.Instructions.Select(instruction => instruction.OpCode)
            .ToArray();
        var hasReturnLocal = opCodes.Contains(PythonOpCode.ReturnLocal);
        var hasControlPair = opCodes
            .Zip(opCodes.Skip(1))
            .Any(pair =>
                pair.First == PythonOpCode.LoadLocal && pair.Second == PythonOpCode.ReturnValue
            );
        if (hasReturnLocal != expectReturnLocal || hasControlPair == expectReturnLocal)
        {
            throw new InvalidOperationException(
                "The return-local benchmark does not contain the expected opcode pair."
            );
        }

        var callLoop = GetFunctionCode(prepared, code, "call_loop");
        var callIndex = callLoop
            .Definition.Instructions.Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.OpCode == PythonOpCode.Call)
            .index;
        if (callLoop.GetCallCacheState(callIndex) != AdaptiveCallCacheState.ManagedFunction)
        {
            throw new InvalidOperationException(
                "The return-local benchmark call site was not specialized."
            );
        }
    }

    private static PreparedPythonCode GetFunctionCode(
        PreparedPythonCode prepared,
        PythonCodeObject code,
        string name
    )
    {
        var index = code
            .Constants.Select((constant, index) => (constant, index))
            .Single(item =>
                item.constant.Type == PythonConstantType.CodeObject
                && item.constant.Value is PythonCodeObject function
                && function.Name == name
            )
            .index;
        return prepared.GetFunctionCode(index);
    }

    private static void VerifyResult(PythonValue result)
    {
        if (result is not PythonNoneValue)
        {
            throw new InvalidOperationException(
                "The return-local benchmark produced an unexpected result."
            );
        }
    }
}
