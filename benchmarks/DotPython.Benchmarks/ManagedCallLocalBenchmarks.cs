using BenchmarkDotNet.Attributes;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

[MemoryDiagnoser]
public class ManagedCallLocalBenchmarks
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];
    private ManagedPythonEngine _controlEngine = null!;
    private ManagedPythonEngine _optimizedEngine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var source = BenchmarkPrograms.CreateManagedFramePathSource(argumentCount: 0);
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The call-local benchmark source did not parse.");
        }

        var controlCompilation = PythonCompiler.CompileWithoutCallLocal(
            parseResult.Module,
            source.FilePath!
        );
        var optimizedCompilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!controlCompilation.Success || !optimizedCompilation.Success)
        {
            throw new InvalidOperationException("The call-local benchmark source did not compile.");
        }

        _controlEngine = CreateEngine("managed_call_local_control", controlCompilation.Code);
        _optimizedEngine = CreateEngine("managed_call_local_optimized", optimizedCompilation.Code);

        VerifyResult(InvokeControl());
        VerifyResult(InvokeOptimized());
        VerifyPreparedState(_controlEngine, controlCompilation.Code, expectCallLocal: false);
        VerifyPreparedState(_optimizedEngine, optimizedCompilation.Code, expectCallLocal: true);
    }

    [Benchmark(Baseline = true)]
    public object DispatchLoadAndCall() => InvokeControl();

    [Benchmark]
    public object DispatchCallLocal() => InvokeOptimized();

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

    private static ManagedPythonEngine CreateEngine(string moduleName, PythonCodeObject code)
    {
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create(moduleName, code),
            TextWriter.Null
        );
        if (!initialization.Success)
        {
            throw new InvalidOperationException("The call-local benchmark did not initialize.");
        }

        return engine;
    }

    private static void VerifyPreparedState(
        ManagedPythonEngine engine,
        PythonCodeObject code,
        bool expectCallLocal
    )
    {
        var prepared = engine.PrepareCode(code);
        var callLoop = GetFunctionCode(prepared, code, "call_loop");
        var opCodes = callLoop
            .Definition.Instructions.Select(instruction => instruction.OpCode)
            .ToArray();
        var hasCallLocal = opCodes.Contains(PythonOpCode.CallLocal);
        var hasControlPair = opCodes
            .Zip(opCodes.Skip(1))
            .Any(pair => pair.First == PythonOpCode.LoadLocal && pair.Second == PythonOpCode.Call);
        if (hasCallLocal != expectCallLocal || hasControlPair == expectCallLocal)
        {
            throw new InvalidOperationException(
                "The call-local benchmark does not contain the expected opcode sequence."
            );
        }

        var expectedOpCode = expectCallLocal ? PythonOpCode.CallLocal : PythonOpCode.Call;
        var callIndex = callLoop
            .Definition.Instructions.Select((instruction, index) => (instruction, index))
            .Single(item => item.instruction.OpCode == expectedOpCode)
            .index;
        if (
            callLoop.GetCallCacheState(callIndex)
            != AdaptiveCallCacheState.ManagedFunctionEmptyFrame
        )
        {
            throw new InvalidOperationException(
                "The call-local benchmark call site was not specialized."
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
                "The call-local benchmark produced an unexpected result."
            );
        }
    }
}
