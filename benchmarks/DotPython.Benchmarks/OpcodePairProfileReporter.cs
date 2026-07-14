using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Benchmarks;

internal static class OpcodePairProfileReporter
{
    private static readonly ManagedExecutionOptions ExecutionOptions = new();
    private static readonly PythonValue[] NoArguments = [];

    internal static void Write(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        WriteScenario(output, "managed-frame-0", 0);
        WriteScenario(output, "managed-frame-1", 1);
    }

    private static void WriteScenario(TextWriter output, string name, int argumentCount)
    {
        var source = BenchmarkPrograms.CreateManagedFramePathSource(argumentCount);
        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw new InvalidOperationException("The opcode-profile source did not parse.");
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath!);
        if (!compilation.Success)
        {
            throw new InvalidOperationException("The opcode-profile source did not compile.");
        }

        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create(name, compilation.Code),
            TextWriter.Null
        );
        if (!initialization.Success)
        {
            throw new InvalidOperationException("The opcode-profile program did not initialize.");
        }

        VerifyResult(Invoke(engine, "run_calls"));
        var profile = new PythonExecutionProfile();
        VerifyResult(
            engine.InvokeProfiled(
                "run_calls",
                NoArguments,
                TextWriter.Null,
                ExecutionOptions,
                profile,
                CancellationToken.None
            )
        );

        output.WriteLine(name);
        output.WriteLine("count\tfirst\tsecond");
        foreach (var pair in profile.GetPairs().Take(12))
        {
            output.WriteLine($"{pair.Count}\t{pair.First}\t{pair.Second}");
        }
    }

    private static PythonValue Invoke(ManagedPythonEngine engine, string functionName) =>
        engine.Invoke(
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
                "The opcode-profile program produced an unexpected result."
            );
        }
    }
}
