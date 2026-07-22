using System.Diagnostics.CodeAnalysis;
using DotPython.Contracts;
using DotPython.Runtime.Managed;

namespace DotPython.Hosting;

/// <summary>
/// Owns a managed DotPython runtime and typed module clients for small applications.
/// </summary>
public sealed class DotPythonHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly DotPythonModuleProvider _provider;
    private readonly IDotPythonModuleRuntime _runtime;
    private readonly List<DotPythonModuleSession> _sessions = [];
    private Task? _disposeTask;
    private bool _disposed;

    internal DotPythonHost(IDotPythonModuleRuntime runtime)
    {
        _runtime = runtime;
        _provider = new DotPythonModuleProvider(runtime);
    }

    /// <summary>Creates a host that owns a managed DotPython runtime.</summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned host takes ownership of the managed runtime."
    )]
    public static DotPythonHost CreateManaged() => new(new ManagedPythonModuleRuntime());

    /// <summary>
    /// Creates a host that takes ownership of a backend-independent module runtime.
    /// </summary>
    public static DotPythonHost Create(IDotPythonModuleRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new DotPythonHost(runtime);
    }

    /// <summary>Creates a typed client for a per-runtime module.</summary>
    public TService GetModule<TService>(PythonModuleRegistration<TService> registration)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.StatePolicy != PythonModuleStatePolicy.PerRuntime)
        {
            throw new InvalidOperationException(
                "Per-session modules must be resolved from a DotPythonModuleSession."
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return registration.CreateClient(_provider);
        }
    }

    /// <summary>Loads and validates a per-runtime module before its first invocation.</summary>
    public ValueTask WarmUpAsync<TService>(
        PythonModuleRegistration<TService> registration,
        CancellationToken cancellationToken = default
    )
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.StatePolicy != PythonModuleStatePolicy.PerRuntime)
        {
            throw new InvalidOperationException(
                "Per-session modules must be warmed within a DotPythonModuleSession."
            );
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _provider.WarmUpAsync(registration.Definition, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a logical state scope for per-session modules. This is not a security boundary.
    /// </summary>
    public DotPythonModuleSession CreateSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = new DotPythonModuleSession(_runtime, RemoveSession);
            _sessions.Add(session);
            return session;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _disposeTask = DisposeCoreAsync(_sessions.AsEnumerable().Reverse().ToArray());
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(IReadOnlyList<DotPythonModuleSession> sessions)
    {
        try
        {
            foreach (var session in sessions)
            {
                await session.DisposeFromHostAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await _provider.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void RemoveSession(DotPythonModuleSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }
    }
}
