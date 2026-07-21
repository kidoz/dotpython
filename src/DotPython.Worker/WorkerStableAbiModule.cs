using DotPython.Protocol;

namespace DotPython.Worker;

public sealed class WorkerStableAbiModule : IAsyncDisposable
{
    private const int MaximumAnyverVersions = 4096;
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

    public Task<long> CompareAnyverAsync(
        string left,
        string right,
        string ecosystem = "generic",
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        ArgumentException.ThrowIfNullOrWhiteSpace(ecosystem);
        return _client.CompareAnyverAsync(Handle, left, right, ecosystem, cancellationToken);
    }

    public Task<IReadOnlyList<string>> SortAnyverAsync(
        IReadOnlyList<string> versions,
        string ecosystem = "generic",
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentException.ThrowIfNullOrWhiteSpace(ecosystem);
        if (versions.Count > MaximumAnyverVersions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(versions),
                $"At most {MaximumAnyverVersions} versions may be sorted per call."
            );
        }

        foreach (var version in versions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        return _client.SortAnyverAsync(Handle, versions, ecosystem, cancellationToken);
    }

    public Task<WorkerAnyverVersionInfo> DescribeAnyverVersionAsync(
        string version,
        string ecosystem = "auto",
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(ecosystem);
        return _client.DescribeAnyverVersionAsync(Handle, version, ecosystem, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _client.ReleaseStableAbiModuleAsync(Handle).ConfigureAwait(false);
        }
    }
}
