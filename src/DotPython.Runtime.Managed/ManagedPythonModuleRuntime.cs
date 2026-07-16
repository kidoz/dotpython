using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using DotPython.Compiler.Artifacts;
using DotPython.Contracts;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Runtime.Managed;

/// <summary>Activates compiled DotPython modules on the self-hosted managed virtual machine.</summary>
public sealed class ManagedPythonModuleRuntime : IDotPythonModuleRuntime
{
    private readonly ManagedExecutionOptions _executionOptions;
    private readonly Dictionary<string, ManagedModuleState> _modules = new(StringComparer.Ordinal);
    private readonly TextWriter _output;
    private readonly ManagedRuntimeScheduler _scheduler;

    /// <summary>Initializes a managed module runtime.</summary>
    public ManagedPythonModuleRuntime(
        ManagedExecutionOptions? executionOptions = null,
        TextWriter? output = null
    )
    {
        _executionOptions = executionOptions ?? new ManagedExecutionOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_executionOptions.InstructionLimit);
        _output = output ?? TextWriter.Null;
        _scheduler = new ManagedRuntimeScheduler(finalize: _modules.Clear);
    }

    /// <inheritdoc />
    public ValueTask<IDotPythonModule> LoadModuleAsync(
        PythonModuleDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        return AwaitModuleAsync(
            _scheduler.InvokeAsync(token => LoadModule(definition, token), cancellationToken)
        );
    }

    private static async ValueTask<IDotPythonModule> AwaitModuleAsync(
        ValueTask<ManagedPythonModule> module
    ) => await module.ConfigureAwait(false);

    private ManagedPythonModule LoadModule(
        PythonModuleDefinition definition,
        CancellationToken cancellationToken
    )
    {
        DotPythonModuleArtifact artifact;
        try
        {
            artifact = DotPythonModuleArtifactSerializer.Deserialize(definition.Artifact.Span);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            throw Failure(
                "DPY6001",
                "The compiled module artifact is invalid.",
                DotPythonFailurePhase.ModuleLoad,
                definition.Contract.ModuleName,
                innerException: exception
            );
        }

        ValidateDefinition(definition.Contract, artifact);
        cancellationToken.ThrowIfCancellationRequested();
        if (_modules.TryGetValue(definition.Contract.ModuleName, out var existing))
        {
            if (
                !string.Equals(
                    existing.ArtifactFingerprint,
                    definition.ArtifactFingerprint,
                    StringComparison.Ordinal
                ) || !ContractsEqual(existing.Contract, definition.Contract)
            )
            {
                throw Failure(
                    "DPY6002",
                    $"Module '{definition.Contract.ModuleName}' is already loaded with a different definition.",
                    DotPythonFailurePhase.ModuleLoad,
                    definition.Contract.ModuleName
                );
            }

            return CreateModuleHandle(existing);
        }

        var engine = new ManagedPythonEngine();
        var result = engine.Execute(artifact, _output, _executionOptions, cancellationToken);
        if (!result.Success)
        {
            var diagnostic = result.Diagnostics[0];
            throw Failure(
                diagnostic.Code,
                diagnostic.Message,
                DotPythonFailurePhase.ModuleLoad,
                definition.Contract.ModuleName
            );
        }

        if (definition.Contract.Functions.Any(function => !engine.HasFunction(function.PythonName)))
        {
            throw ExportMismatch(definition.Contract.ModuleName);
        }

        var state = new ManagedModuleState(
            definition.Contract,
            definition.ArtifactFingerprint,
            engine
        );
        _modules.Add(definition.Contract.ModuleName, state);
        return CreateModuleHandle(state);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();

    private ValueTask<TResult> InvokeAsync<TResult>(
        ManagedModuleState state,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken
    ) =>
        _scheduler.InvokeAsync(
            token => Invoke<TResult>(state, invocation, token),
            cancellationToken
        );

    private TResult Invoke<TResult>(
        ManagedModuleState state,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken
    )
    {
        var result = InvokeCore(state, invocation, cancellationToken, out var function);
        if (string.Equals(function.ReturnType.ClrTypeName, "System.Void", StringComparison.Ordinal))
        {
            throw Failure(
                "DPY6007",
                $"Function '{function.PythonName}' does not declare a CLR result.",
                DotPythonFailurePhase.Conversion,
                state.Contract.ModuleName,
                function.PythonName
            );
        }

        return PythonValueConverter.ToClr<TResult>(
            result,
            function.ReturnType,
            state.Contract.ModuleName,
            function.PythonName
        );
    }

    private ValueTask InvokeAsync(
        ManagedModuleState state,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken
    ) =>
        AwaitVoidInvocationAsync(
            _scheduler.InvokeAsync(
                token =>
                {
                    Invoke(state, invocation, token);
                    return true;
                },
                cancellationToken
            )
        );

    private static async ValueTask AwaitVoidInvocationAsync(ValueTask<bool> invocation) =>
        await invocation.ConfigureAwait(false);

    private void Invoke(
        ManagedModuleState state,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken
    )
    {
        var result = InvokeCore(state, invocation, cancellationToken, out var function);
        if (
            !string.Equals(function.ReturnType.ClrTypeName, "System.Void", StringComparison.Ordinal)
            || result is not PythonNoneValue
        )
        {
            throw Failure(
                "DPY6007",
                $"Function '{function.PythonName}' does not have a void-compatible result.",
                DotPythonFailurePhase.Conversion,
                state.Contract.ModuleName,
                function.PythonName
            );
        }
    }

    private PythonValue InvokeCore(
        ManagedModuleState state,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken,
        out PythonFunctionContract function
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        function =
            state.Contract.Functions.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.PythonName,
                    invocation.FunctionName,
                    StringComparison.Ordinal
                )
            )
            ?? throw Failure(
                "DPY6005",
                $"Function '{invocation.FunctionName}' is not exported by module '{state.Contract.ModuleName}'.",
                DotPythonFailurePhase.Invocation,
                state.Contract.ModuleName,
                invocation.FunctionName
            );

        if (invocation.Arguments.Count != function.Parameters.Count)
        {
            throw Failure(
                "DPY6006",
                $"Function '{function.PythonName}' expects {function.Parameters.Count} positional argument(s), but received {invocation.Arguments.Count}.",
                DotPythonFailurePhase.Invocation,
                state.Contract.ModuleName,
                function.PythonName
            );
        }

        var arguments = new PythonValue[function.Parameters.Count];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = PythonValueConverter.FromClr(
                invocation.Arguments[index],
                function.Parameters[index].Type,
                state.Contract.ModuleName,
                function.PythonName
            );
        }

        try
        {
            return state.Engine.Invoke(
                function.PythonName,
                arguments,
                _output,
                _executionOptions,
                cancellationToken
            );
        }
        catch (PythonRuntimeException exception)
        {
            throw Failure(
                exception.Code,
                exception.Message,
                DotPythonFailurePhase.Invocation,
                state.Contract.ModuleName,
                function.PythonName,
                exception
            );
        }
    }

    private static void ValidateDefinition(
        PythonModuleContract contract,
        DotPythonModuleArtifact artifact
    )
    {
        if (contract.FormatVersion != DotPythonContractFormat.CurrentVersion)
        {
            throw Failure(
                "DPY6002",
                $"Contract format {contract.FormatVersion} is not supported.",
                DotPythonFailurePhase.ModuleLoad,
                contract.ModuleName
            );
        }

        if (
            !string.Equals(
                contract.ModuleName,
                artifact.Manifest.ModuleName,
                StringComparison.Ordinal
            )
        )
        {
            throw Failure(
                "DPY6002",
                "The artifact module name does not match its export contract.",
                DotPythonFailurePhase.ModuleLoad,
                contract.ModuleName
            );
        }

        if (contract.StatePolicy != PythonModuleStatePolicy.PerRuntime)
        {
            throw Failure(
                "DPY6003",
                "The managed module runtime currently supports only per-runtime module state.",
                DotPythonFailurePhase.ModuleLoad,
                contract.ModuleName
            );
        }

        if (
            contract.Functions.Any(function =>
                function.CallShape != PythonCallShape.Synchronous
                || function.Parameters.Any(parameter => parameter.HasDefault)
            )
        )
        {
            throw Failure(
                "DPY6003",
                "The managed module runtime currently supports synchronous functions with explicit positional arguments.",
                DotPythonFailurePhase.ModuleLoad,
                contract.ModuleName
            );
        }

        if (artifact.Manifest.Exports.Count != contract.Functions.Count)
        {
            throw ExportMismatch(contract.ModuleName);
        }

        foreach (var function in contract.Functions)
        {
            var export = artifact.Manifest.Exports.FirstOrDefault(candidate =>
                string.Equals(candidate.PythonName, function.PythonName, StringComparison.Ordinal)
            );
            if (
                export is null
                || export.Kind != DotPythonExportKind.Function
                || !string.Equals(export.ContractName, function.ClrName, StringComparison.Ordinal)
            )
            {
                throw ExportMismatch(contract.ModuleName);
            }
        }
    }

    private static DotPythonException ExportMismatch(string moduleName) =>
        Failure(
            "DPY6004",
            "The artifact exports do not match the static module contract.",
            DotPythonFailurePhase.ModuleLoad,
            moduleName
        );

    private static DotPythonException Failure(
        string code,
        string message,
        DotPythonFailurePhase phase,
        string? moduleName,
        string? functionName = null,
        Exception? innerException = null
    ) => new(code, message, phase, moduleName, functionName, innerException);

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned module handle takes ownership of the scheduler resource lease."
    )]
    private ManagedPythonModule CreateModuleHandle(ManagedModuleState state)
    {
        var resource = _scheduler.RegisterResource(state.ReleaseHandle);
        state.AcquireHandle();
        return new ManagedPythonModule(this, state, resource);
    }

    private static bool ContractsEqual(PythonModuleContract left, PythonModuleContract right) =>
        string.Equals(left.ClrNamespace, right.ClrNamespace, StringComparison.Ordinal)
        && string.Equals(left.ClrTypeName, right.ClrTypeName, StringComparison.Ordinal)
        && left.FormatVersion == right.FormatVersion
        && left.StatePolicy == right.StatePolicy
        && left.Functions.Count == right.Functions.Count
        && left.Functions.Zip(right.Functions).All(pair => FunctionsEqual(pair.First, pair.Second));

    private static bool FunctionsEqual(PythonFunctionContract left, PythonFunctionContract right) =>
        string.Equals(left.PythonName, right.PythonName, StringComparison.Ordinal)
        && string.Equals(left.ClrName, right.ClrName, StringComparison.Ordinal)
        && left.CallShape == right.CallShape
        && left.Parameters.Count == right.Parameters.Count
        && left.Parameters.Zip(right.Parameters)
            .All(pair =>
                string.Equals(
                    pair.First.PythonName,
                    pair.Second.PythonName,
                    StringComparison.Ordinal
                )
                && string.Equals(pair.First.ClrName, pair.Second.ClrName, StringComparison.Ordinal)
                && pair.First.Kind == pair.Second.Kind
                && pair.First.HasDefault == pair.Second.HasDefault
                && TypesEqual(pair.First.Type, pair.Second.Type)
            )
        && TypesEqual(left.ReturnType, right.ReturnType);

    private static bool TypesEqual(PythonTypeContract left, PythonTypeContract right) =>
        string.Equals(left.PythonName, right.PythonName, StringComparison.Ordinal)
        && string.Equals(left.ClrTypeName, right.ClrTypeName, StringComparison.Ordinal)
        && left.IsNullable == right.IsNullable
        && left.IsValueType == right.IsValueType
        && left.IsClsCompliant == right.IsClsCompliant
        && left.TypeArguments.Count == right.TypeArguments.Count
        && left.TypeArguments.Zip(right.TypeArguments)
            .All(pair => TypesEqual(pair.First, pair.Second));

    private sealed class ManagedModuleState(
        PythonModuleContract contract,
        string artifactFingerprint,
        ManagedPythonEngine engine
    )
    {
        internal PythonModuleContract Contract { get; } = contract;

        internal string ArtifactFingerprint { get; } = artifactFingerprint;

        internal ManagedPythonEngine Engine { get; } = engine;

        internal int OpenHandleCount { get; private set; }

        internal void AcquireHandle() => OpenHandleCount++;

        internal void ReleaseHandle() => OpenHandleCount--;
    }

    private sealed class ManagedPythonModule(
        ManagedPythonModuleRuntime owner,
        ManagedModuleState state,
        ManagedRuntimeScheduler.SchedulerOwnedResource resource
    ) : IDotPythonModule
    {
        private int _disposed;

        public PythonModuleContract Contract => state.Contract;

        public ValueTask InvokeAsync(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return owner.InvokeAsync(state, invocation, cancellationToken);
        }

        public ValueTask<TResult> InvokeAsync<TResult>(
            PythonFunctionInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return owner.InvokeAsync<TResult>(state, invocation, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref _disposed, 1) == 0
                ? resource.DisposeAsync()
                : ValueTask.CompletedTask;
        }
    }
}
