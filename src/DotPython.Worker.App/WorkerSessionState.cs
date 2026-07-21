using DotPython.Runtime.Managed.Execution;
using DotPython.Runtime.Native;

namespace DotPython.Worker.App;

internal sealed class WorkerSessionState : IAsyncDisposable
{
    private readonly NativeExecutionLane _nativeLane = new();
    private readonly Dictionary<long, StableAbiFixtureModule> _nativeModules = [];
    private readonly List<long> _nativeOrder = [];
    private long _nextObjectId;
    private int _disposed;

    internal WorkerSessionState(ManagedPythonEngine engine)
    {
        Engine = engine;
    }

    internal ManagedPythonEngine Engine { get; }

    internal Task<LoadedStableAbiModule> LoadStableAbiModuleAsync(
        StableAbiFixtureConfiguration configuration,
        CancellationToken cancellationToken
    ) =>
        _nativeLane.InvokeAsync(
            () =>
            {
                var module = StableAbiFixtureLoader.Load(configuration);
                var objectId = checked(++_nextObjectId);
                _nativeModules.Add(objectId, module);
                _nativeOrder.Add(objectId);
                return new LoadedStableAbiModule(
                    objectId,
                    module.ModuleName,
                    module.ManifestVersion,
                    module.ArtifactSha256 ?? configuration.FixtureSha256,
                    module.NativeEntrySha256 ?? configuration.FixtureSha256,
                    module.MultiPhase,
                    module.ReadyValue
                );
            },
            cancellationToken
        );

    internal Task<long> InvokeStableAbiModuleAsync(
        long objectId,
        string method,
        long? argument,
        CancellationToken cancellationToken
    ) =>
        _nativeLane.InvokeAsync(
            () => GetModule(objectId).InvokeLong(method, argument),
            cancellationToken
        );

    internal Task<long> CompareAnyverAsync(
        long objectId,
        string left,
        string right,
        string ecosystem,
        CancellationToken cancellationToken
    ) =>
        _nativeLane.InvokeAsync(
            () => GetModule(objectId).CompareAnyver(left, right, ecosystem),
            cancellationToken
        );

    internal Task<IReadOnlyList<string>> SortAnyverAsync(
        long objectId,
        IReadOnlyList<string> versions,
        string ecosystem,
        CancellationToken cancellationToken
    ) =>
        _nativeLane.InvokeAsync(
            () => GetModule(objectId).SortAnyver(versions, ecosystem),
            cancellationToken
        );

    internal Task<StableAbiAnyverVersionInfo> DescribeAnyverVersionAsync(
        long objectId,
        string version,
        string ecosystem,
        CancellationToken cancellationToken
    ) =>
        _nativeLane.InvokeAsync(
            () => GetModule(objectId).DescribeAnyverVersion(version, ecosystem),
            cancellationToken
        );

    internal Task<bool> ReleaseStableAbiModuleAsync(
        long objectId,
        CancellationToken cancellationToken
    ) =>
        _nativeLane.InvokeAsync(
            () =>
            {
                if (!_nativeModules.Remove(objectId, out var module))
                {
                    return false;
                }

                _nativeOrder.Remove(objectId);
                module.Dispose();
                return true;
            },
            cancellationToken
        );

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = await _nativeLane
            .InvokeAsync(
                () =>
                {
                    for (var index = _nativeOrder.Count - 1; index >= 0; index--)
                    {
                        _nativeModules[_nativeOrder[index]].Dispose();
                    }

                    _nativeModules.Clear();
                    _nativeOrder.Clear();
                    return true;
                },
                CancellationToken.None
            )
            .ConfigureAwait(false);
        await _nativeLane.DisposeAsync().ConfigureAwait(false);
    }

    private StableAbiFixtureModule GetModule(long objectId)
    {
        if (!_nativeModules.TryGetValue(objectId, out var module))
        {
            throw new StableAbiLoadException(
                "DPY8106",
                StableAbiLoadPhase.Invocation,
                "The native module handle is not active.",
                artifactPath: null,
                artifactSha256: null,
                missingSymbol: null
            );
        }

        return module;
    }
}

internal sealed record LoadedStableAbiModule(
    long ObjectId,
    string ModuleName,
    string ManifestVersion,
    string ArtifactSha256,
    string NativeEntrySha256,
    bool MultiPhase,
    long ReadyValue
);
