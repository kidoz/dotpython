using DotPython.Protocol;

namespace DotPython.Worker;

public sealed class WorkerProcessPool : IAsyncDisposable
{
    private readonly WorkerProcessOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkerProcessClient? _client;
    private long _generation;
    private int _disposed;

    public WorkerProcessPool(WorkerProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options with
        {
            StableAbiModules = Array.AsReadOnly(options.StableAbiModules.ToArray()),
        };
        Key = new WorkerPoolKey(
            options.ProviderId,
            options.RuntimeId,
            options.Architecture,
            options.EnvironmentHash
        );
    }

    public WorkerPoolKey Key { get; }

    public WorkerIdentity? ActiveIdentity => _client?.Handshake.Identity;

    public WorkerProcessState State => _client?.State ?? WorkerProcessState.Stopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkerSession> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            var client =
                _client ?? throw new InvalidOperationException("The worker did not start.");
            if (client.RequestCount >= _options.Policy.MaxRequestsPerWorker)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                _client = await WorkerProcessClient
                    .StartAsync(_options, checked(++_generation), cancellationToken)
                    .ConfigureAwait(false);
                client = _client;
            }

            return await client.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecycleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }

            _client = await WorkerProcessClient
                .StartAsync(_options, checked(++_generation), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal Task InjectTestFaultAsync(
        WorkerTestFault fault,
        CancellationToken cancellationToken = default
    ) =>
        (
            _client ?? throw new InvalidOperationException("The worker is not running.")
        ).InjectTestFaultAsync(fault, cancellationToken);

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (
            _client is not null
            && _client.State is not (WorkerProcessState.Stopped or WorkerProcessState.Faulted)
        )
        {
            return;
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _client = await WorkerProcessClient
            .StartAsync(_options, checked(++_generation), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

public readonly record struct WorkerPoolKey(
    string ProviderId,
    string RuntimeId,
    string Architecture,
    string EnvironmentHash
);
