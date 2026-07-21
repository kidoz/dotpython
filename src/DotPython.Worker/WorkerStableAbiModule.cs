using DotPython.Protocol;

namespace DotPython.Worker;

public sealed class WorkerStableAbiModule : IAsyncDisposable
{
    private readonly WorkerProcessClient _client;
    private int _disposed;

    internal WorkerStableAbiModule(
        WorkerProcessClient client,
        WorkerObjectHandle handle,
        string moduleName,
        string manifestVersion,
        string artifactSha256,
        string nativeEntrySha256,
        bool multiPhase,
        long readyValue
    )
    {
        _client = client;
        Handle = handle;
        ModuleName = moduleName;
        ManifestVersion = manifestVersion;
        ArtifactSha256 = artifactSha256;
        NativeEntrySha256 = nativeEntrySha256;
        MultiPhase = multiPhase;
        ReadyValue = readyValue;
    }

    public WorkerObjectHandle Handle { get; }

    public string ModuleName { get; }

    public string ManifestVersion { get; }

    public string ArtifactSha256 { get; }

    public string NativeEntrySha256 { get; }

    public bool MultiPhase { get; }

    public long ReadyValue { get; }

    public Task<long> InvokeLongAsync(
        string method,
        long? argument = null,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return _client.InvokeStableAbiModuleAsync(Handle, method, argument, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _client.ReleaseStableAbiModuleAsync(Handle).ConfigureAwait(false);
        }
    }
}
