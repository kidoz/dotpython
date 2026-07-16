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
    private readonly PythonModuleRegistry _modules;
    private readonly ConditionalWeakTable<PythonCodeObject, PreparedPythonCode> _preparedCodes =
        new();

    /// <summary>Creates an engine without registered source modules.</summary>
    public ManagedPythonEngine()
        : this(PythonModuleCatalog.Empty) { }

    /// <summary>Creates an engine with an immutable catalog of importable managed source modules.</summary>
    /// <param name="moduleSources">
    /// Dot-separated module names and decoded sources. Packages are entries with registered child
    /// modules. The bounded catalog is copied and module state remains private to this engine.
    /// </param>
    public ManagedPythonEngine(IReadOnlyDictionary<string, SourceText>? moduleSources)
        : this(PythonModuleCatalog.FromSources(moduleSources)) { }

    /// <summary>
    /// Creates an engine from a deterministic startup snapshot of configured module search paths.
    /// </summary>
    /// <param name="discoveryOptions">The validated managed-module discovery configuration.</param>
    public ManagedPythonEngine(ManagedModuleDiscoveryOptions discoveryOptions)
        : this(PythonModuleCatalog.Discover(discoveryOptions)) { }

    private ManagedPythonEngine(PythonModuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _modules = new PythonModuleRegistry(catalog.Modules, CompileModule);
    }

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
                _modules,
                output,
                options.InstructionLimit,
                enableReturnLocalContinuation,
                cancellationToken
            );
            try
            {
                return virtualMachine.Invoke(functionName, arguments);
            }
            catch (PythonRaisedException raised)
            {
                throw ToRuntimeFault(raised);
            }
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
                _modules,
                output,
                options.InstructionLimit,
                enableReturnLocalContinuation: false,
                cancellationToken
            );
            try
            {
                return virtualMachine.InvokeProfiled(functionName, arguments, profile);
            }
            catch (PythonRaisedException raised)
            {
                throw ToRuntimeFault(raised);
            }
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
                _modules,
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
        catch (PythonRaisedException raised)
        {
            var fault = ToRuntimeFault(raised);
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

    private PreparedPythonCode CompileModule(
        string name,
        PythonModuleDefinition definition,
        TextSpan importSpan
    )
    {
        if (definition.Artifact is not null)
        {
            return PrepareCode(definition.Artifact.Code);
        }

        var source = definition.Source;
        if (source is null)
        {
            throw new PythonRuntimeException(
                "DPY4021",
                $"Managed module '{name}' has no executable source or artifact.",
                importSpan
            );
        }

        var parseResult = PythonParser.Parse(source);
        if (!parseResult.Success)
        {
            throw ModuleCompilationFailure(name, parseResult.Diagnostics[0], importSpan);
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, source.FilePath ?? name);
        if (!compilation.Success)
        {
            throw ModuleCompilationFailure(name, compilation.Diagnostics[0], importSpan);
        }

        return PrepareCode(compilation.Code);
    }

    private static PythonRuntimeException ModuleCompilationFailure(
        string name,
        Diagnostic diagnostic,
        TextSpan importSpan
    ) =>
        new(
            "DPY4021",
            $"Managed module '{name}' could not be compiled: {diagnostic.Message}",
            importSpan
        );

    private static PythonRuntimeException ToRuntimeFault(PythonRaisedException raised)
    {
        var span = raised.Traceback.Count == 0 ? new TextSpan(0, 0) : raised.Traceback[0].Span;
        var message =
            raised.Value.Message.Length == 0
                ? raised.Value.TypeName
                : $"{raised.Value.TypeName}: {raised.Value.Message}";
        return new PythonRuntimeException("DPY4031", message, span);
    }
}
