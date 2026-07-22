using DotPython.Contracts;

namespace DotPython.Hosting;

internal class DotPythonModuleProvider : IDotPythonModuleProvider
{
    private int _activeOperationCount;
    private readonly object _gate = new();
    private readonly List<ModuleEntry> _loadOrder = [];
    private readonly Dictionary<string, int> _maximumInitializationAttempts = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, ModuleEntry> _modules = new(StringComparer.Ordinal);
    private readonly IDotPythonModuleRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _disposeTask;
    private TaskCompletionSource<object?>? _operationsDrained;
    private bool _disposed;

    internal DotPythonModuleProvider(IDotPythonModuleRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    internal void ConfigureInitialization(
        PythonModuleDefinition definition,
        int maximumInitializationAttempts
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (
            maximumInitializationAttempts
            is < 1
                or > DotPythonModuleHostingOptions.MaximumSupportedInitializationAttempts
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInitializationAttempts),
                maximumInitializationAttempts,
                $"The maximum initialization attempt count must be between 1 and {DotPythonModuleHostingOptions.MaximumSupportedInitializationAttempts}."
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var moduleName = definition.Contract.ModuleName;
            if (
                _maximumInitializationAttempts.TryGetValue(moduleName, out var configuredAttempts)
                && configuredAttempts != maximumInitializationAttempts
            )
            {
                throw new InvalidOperationException(
                    $"Module '{moduleName}' is already configured for {configuredAttempts} initialization attempt(s)."
                );
            }

            _maximumInitializationAttempts[moduleName] = maximumInitializationAttempts;
        }
    }

    public async ValueTask WarmUpAsync(
        PythonModuleDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        var loadTask = BeginOperation(definition, cancellationToken);
        try
        {
            _ = await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    public async ValueTask InvokeAsync(
        PythonModuleDefinition definition,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var loadTask = BeginOperation(definition, cancellationToken);
        try
        {
            var module = await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            await module.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    public async ValueTask<TResult> InvokeAsync<TResult>(
        PythonModuleDefinition definition,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var loadTask = BeginOperation(definition, cancellationToken);
        try
        {
            var module = await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await module
                .InvokeAsync<TResult>(invocation, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            var operationsDrained =
                _activeOperationCount == 0
                    ? Task.CompletedTask
                    : (
                        _operationsDrained ??= new(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )
                    ).Task;
            _disposeTask = DisposeModulesAsync(
                _loadOrder.AsEnumerable().Reverse().ToArray(),
                operationsDrained
            );
            return new ValueTask(_disposeTask);
        }
    }

    private Task<IDotPythonModule> BeginOperation(
        PythonModuleDefinition definition,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var moduleName = definition.Contract.ModuleName;
            Task<IDotPythonModule> loadTask;
            if (_modules.TryGetValue(moduleName, out var existing))
            {
                if (!DefinitionsEqual(existing.Definition, definition))
                {
                    throw new DotPythonException(
                        "DPY6002",
                        $"Module '{moduleName}' is already registered with a different definition.",
                        DotPythonFailurePhase.ModuleLoad,
                        moduleName
                    );
                }

                loadTask = existing.LoadTask;
            }
            else
            {
                var maximumAttempts = _maximumInitializationAttempts.GetValueOrDefault(
                    moduleName,
                    1
                );
                _maximumInitializationAttempts[moduleName] = maximumAttempts;
                loadTask = LoadModuleAsync(definition, maximumAttempts);
                var entry = new ModuleEntry(definition, loadTask);
                _modules.Add(moduleName, entry);
                _loadOrder.Add(entry);
            }

            _activeOperationCount++;
            return loadTask;
        }
    }

    private void EndOperation()
    {
        lock (_gate)
        {
            _activeOperationCount--;
            if (_activeOperationCount == 0)
            {
                _operationsDrained?.TrySetResult(null);
            }
        }
    }

    private async Task<IDotPythonModule> LoadModuleAsync(
        PythonModuleDefinition definition,
        int maximumAttempts
    )
    {
        var moduleName = definition.Contract.ModuleName;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            DotPythonHostingTelemetry.RecordInitializationAttempt(definition, attempt);
            try
            {
                return await _runtime
                    .LoadModuleAsync(definition, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (DotPythonException exception)
            {
                DotPythonHostingTelemetry.RecordInitializationFailure(
                    definition,
                    attempt,
                    exception
                );
                if (attempt == maximumAttempts || _shutdown.IsCancellationRequested)
                {
                    throw;
                }
            }
            catch (Exception exception)
            {
                DotPythonHostingTelemetry.RecordInitializationFailure(
                    definition,
                    attempt,
                    exception
                );
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Module '{moduleName}' initialization ended without a result."
        );
    }

    private async Task DisposeModulesAsync(
        IReadOnlyList<ModuleEntry> entries,
        Task operationsDrained
    )
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await operationsDrained.ConfigureAwait(false);
        foreach (var entry in entries)
        {
            IDotPythonModule module;
            try
            {
                module = await entry.LoadTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                continue;
            }
            catch (DotPythonException)
            {
                continue;
            }

            await module.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private static bool DefinitionsEqual(
        PythonModuleDefinition left,
        PythonModuleDefinition right
    ) =>
        string.Equals(left.ArtifactFingerprint, right.ArtifactFingerprint, StringComparison.Ordinal)
        && ContractsEqual(left.Contract, right.Contract);

    private static bool ContractsEqual(PythonModuleContract left, PythonModuleContract right) =>
        left.FormatVersion == right.FormatVersion
        && string.Equals(left.ModuleName, right.ModuleName, StringComparison.Ordinal)
        && string.Equals(left.ClrNamespace, right.ClrNamespace, StringComparison.Ordinal)
        && string.Equals(left.ClrTypeName, right.ClrTypeName, StringComparison.Ordinal)
        && left.StatePolicy == right.StatePolicy
        && left.Functions.Count == right.Functions.Count
        && left.Functions.Zip(right.Functions).All(pair => FunctionsEqual(pair.First, pair.Second));

    private static bool FunctionsEqual(PythonFunctionContract left, PythonFunctionContract right) =>
        string.Equals(left.PythonName, right.PythonName, StringComparison.Ordinal)
        && string.Equals(left.ClrName, right.ClrName, StringComparison.Ordinal)
        && left.CallShape == right.CallShape
        && left.Parameters.Count == right.Parameters.Count
        && left.Parameters.Zip(right.Parameters)
            .All(pair => ParametersEqual(pair.First, pair.Second))
        && TypesEqual(left.ReturnType, right.ReturnType);

    private static bool ParametersEqual(
        PythonParameterContract left,
        PythonParameterContract right
    ) =>
        string.Equals(left.PythonName, right.PythonName, StringComparison.Ordinal)
        && string.Equals(left.ClrName, right.ClrName, StringComparison.Ordinal)
        && left.Kind == right.Kind
        && left.HasDefault == right.HasDefault
        && TypesEqual(left.Type, right.Type);

    private static bool TypesEqual(PythonTypeContract left, PythonTypeContract right) =>
        string.Equals(left.PythonName, right.PythonName, StringComparison.Ordinal)
        && string.Equals(left.ClrTypeName, right.ClrTypeName, StringComparison.Ordinal)
        && left.IsNullable == right.IsNullable
        && left.IsValueType == right.IsValueType
        && left.IsClsCompliant == right.IsClsCompliant
        && left.TypeArguments.Count == right.TypeArguments.Count
        && left.TypeArguments.Zip(right.TypeArguments)
            .All(pair => TypesEqual(pair.First, pair.Second));

    private sealed record ModuleEntry(
        PythonModuleDefinition Definition,
        Task<IDotPythonModule> LoadTask
    );
}

internal sealed class PerRuntimePythonModuleProvider(IDotPythonModuleRuntime runtime)
    : DotPythonModuleProvider(runtime);

internal sealed class PerSessionPythonModuleProvider(IDotPythonModuleRuntime runtime)
    : DotPythonModuleProvider(runtime);
