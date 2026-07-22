using DotPython.Contracts;

namespace DotPython.Hosting;

/// <summary>
/// Owns typed per-session module state inside a <see cref="DotPythonHost"/>. This is not a security
/// boundary.
/// </summary>
public sealed class DotPythonModuleSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Action<DotPythonModuleSession> _onDisposed;
    private readonly DotPythonModuleProvider _provider;
    private Task? _disposeTask;

    internal DotPythonModuleSession(
        IDotPythonModuleRuntime runtime,
        Action<DotPythonModuleSession> onDisposed
    )
    {
        _provider = new DotPythonModuleProvider(runtime);
        _onDisposed = onDisposed;
    }

    /// <summary>Creates a typed client whose module state belongs to this session.</summary>
    public TService GetModule<TService>(PythonModuleRegistration<TService> registration)
        where TService : class => GetModule(registration, static _ => { });

    /// <summary>Creates a configured typed client whose module state belongs to this session.</summary>
    public TService GetModule<TService>(
        PythonModuleRegistration<TService> registration,
        Action<DotPythonModuleHostingOptions> configure
    )
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(configure);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (registration.StatePolicy != PythonModuleStatePolicy.PerSession)
            {
                throw new InvalidOperationException(
                    "Only per-session modules can be resolved from a DotPythonModuleSession."
                );
            }

            DotPythonHost.ConfigureProvider(_provider, registration.Definition, configure);
            return registration.CreateClient(_provider);
        }
    }

    /// <summary>Loads and validates a per-session module before its first invocation.</summary>
    public ValueTask WarmUpAsync<TService>(
        PythonModuleRegistration<TService> registration,
        CancellationToken cancellationToken = default
    )
        where TService : class => WarmUpAsync(registration, static _ => { }, cancellationToken);

    /// <summary>Loads and validates a configured per-session module.</summary>
    public ValueTask WarmUpAsync<TService>(
        PythonModuleRegistration<TService> registration,
        Action<DotPythonModuleHostingOptions> configure,
        CancellationToken cancellationToken = default
    )
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(configure);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (registration.StatePolicy != PythonModuleStatePolicy.PerSession)
            {
                throw new InvalidOperationException(
                    "Only per-session modules can be warmed within a DotPythonModuleSession."
                );
            }

            DotPythonHost.ConfigureProvider(_provider, registration.Definition, configure);
            return _provider.WarmUpAsync(registration.Definition, cancellationToken);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _onDisposed(this);
        }
    }

    internal ValueTask DisposeFromHostAsync() => DisposeAsync();
}
