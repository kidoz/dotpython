using DotPython.Protocol;

namespace DotPython.Worker;

public sealed class WorkerSession : IAsyncDisposable
{
    private readonly WorkerProcessClient _client;
    private readonly WorkerGenerationScope _scope;
    private int _disposed;

    internal WorkerSession(WorkerProcessClient client, WorkerGenerationScope scope, Guid sessionId)
    {
        _client = client;
        _scope = scope;
        SessionId = sessionId;
    }

    public Guid SessionId { get; }

    public WorkerIdentity WorkerIdentity => _scope.Identity;

    public Task<WorkerExecuteResponse> ExecuteAsync(
        string code,
        string fileName = "<worker>",
        long instructionLimit = 1_000_000,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _client.ExecuteAsync(SessionId, code, fileName, instructionLimit, cancellationToken);
    }

    public void ValidateHandle(WorkerObjectHandle handle)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _client.ValidateHandle(handle, SessionId);
    }

    public async Task<WorkerStableAbiModule> LoadStableAbiFixtureAsync(
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var response = await _client
            .LoadStableAbiModuleAsync(SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (response.SessionId != SessionId || response.ObjectId <= 0)
        {
            throw new InvalidDataException(
                "The worker returned an invalid native module identity."
            );
        }

        var identity = _scope.Identity;
        return new WorkerStableAbiModule(
            _client,
            new WorkerObjectHandle(
                identity.ProviderId,
                identity.WorkerId,
                identity.Generation,
                SessionId,
                response.ObjectId
            ),
            response.ModuleName,
            response.ManifestVersion,
            response.ArtifactSha256,
            response.MultiPhase,
            response.ReadyValue
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _client.CloseSessionAsync(SessionId).ConfigureAwait(false);
        }
    }
}
