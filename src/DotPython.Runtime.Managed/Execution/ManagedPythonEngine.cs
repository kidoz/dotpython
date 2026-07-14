using System.Runtime.CompilerServices;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Runtime.Managed.Execution;

public sealed class ManagedPythonEngine
{
    private readonly object _executionGate = new();
    private readonly PythonGlobalNamespace _globals = new();
    private readonly ConditionalWeakTable<PythonCodeObject, PreparedPythonCode> _preparedCodes =
        new();

    public ManagedExecutionResult Execute(
        string code,
        string fileName,
        TextWriter output,
        ManagedExecutionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(output);

        return Execute(new SourceText(code, fileName), output, options, cancellationToken);
    }

    internal PythonValue Invoke(
        string functionName,
        IReadOnlyList<PythonValue> arguments,
        TextWriter output,
        ManagedExecutionOptions options,
        CancellationToken cancellationToken
    ) =>
        InvokeCore(
            functionName,
            arguments,
            output,
            options,
            enableReturnLocalContinuation: true,
            cancellationToken
        );

    internal PythonValue InvokeWithoutReturnLocalContinuation(
        string functionName,
        IReadOnlyList<PythonValue> arguments,
        TextWriter output,
        ManagedExecutionOptions options,
        CancellationToken cancellationToken
    ) =>
        InvokeCore(
            functionName,
            arguments,
            output,
            options,
            enableReturnLocalContinuation: false,
            cancellationToken
        );

    private PythonValue InvokeCore(
        string functionName,
        IReadOnlyList<PythonValue> arguments,
        TextWriter output,
        ManagedExecutionOptions options,
        bool enableReturnLocalContinuation,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InstructionLimit);

        lock (_executionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualMachine = new PythonVirtualMachine(
                _globals,
                output,
                options.InstructionLimit,
                enableReturnLocalContinuation,
                cancellationToken
            );
            return virtualMachine.Invoke(functionName, arguments);
        }
    }

    internal PythonValue InvokeProfiled(
        string functionName,
        IReadOnlyList<PythonValue> arguments,
        TextWriter output,
        ManagedExecutionOptions options,
        PythonExecutionProfile profile,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InstructionLimit);

        lock (_executionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualMachine = new PythonVirtualMachine(
                _globals,
                output,
                options.InstructionLimit,
                enableReturnLocalContinuation: false,
                cancellationToken
            );
            return virtualMachine.InvokeProfiled(functionName, arguments, profile);
        }
    }

    internal bool HasFunction(string functionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        lock (_executionGate)
        {
            return _globals.TryGetValue(functionName, out var value)
                && value is PythonFunctionValue;
        }
    }

    public ManagedExecutionResult Execute(
        SourceText source,
        TextWriter output,
        ManagedExecutionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);

        options ??= new ManagedExecutionOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InstructionLimit);

        lock (_executionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parseResult = PythonParser.Parse(source);
            if (!parseResult.Success)
            {
                return new ManagedExecutionResult(source, [.. parseResult.Diagnostics]);
            }

            var compilation = PythonCompiler.Compile(
                parseResult.Module,
                source.FilePath ?? "<module>"
            );
            if (!compilation.Success)
            {
                return new ManagedExecutionResult(source, [.. compilation.Diagnostics]);
            }

            return ExecuteCodeObject(source, compilation.Code, output, options, cancellationToken);
        }
    }

    public ManagedExecutionResult Execute(
        DotPythonModuleArtifact artifact,
        TextWriter output,
        ManagedExecutionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(output);

        options ??= new ManagedExecutionOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InstructionLimit);

        lock (_executionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new SourceText(
                string.Empty,
                artifact.Manifest.ModuleName + DotPythonModuleArtifactFormat.FileExtension
            );
            return ExecuteCodeObject(source, artifact.Code, output, options, cancellationToken);
        }
    }

    private ManagedExecutionResult ExecuteCodeObject(
        SourceText source,
        PythonCodeObject code,
        TextWriter output,
        ManagedExecutionOptions options,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var virtualMachine = new PythonVirtualMachine(
                _globals,
                output,
                options.InstructionLimit,
                enableReturnLocalContinuation: true,
                cancellationToken
            );
            virtualMachine.Execute(PrepareCode(code));
            return new ManagedExecutionResult(source, []);
        }
        catch (PythonRuntimeException fault)
        {
            return new ManagedExecutionResult(
                source,
                [new Diagnostic(fault.Code, fault.Message, DiagnosticSeverity.Error, fault.Span)]
            );
        }
    }

    internal PreparedPythonCode PrepareCode(PythonCodeObject code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _preparedCodes.GetValue(code, PreparedPythonCode.Create);
    }
}
