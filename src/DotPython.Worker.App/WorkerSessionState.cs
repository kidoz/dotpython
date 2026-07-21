using DotPython.Runtime.Managed.Execution;
using DotPython.Runtime.Native;

namespace DotPython.Worker.App;

internal sealed class WorkerSessionState : IAsyncDisposable
{
    private readonly NativeExecutionLane _nativeLane = new();
    private readonly Dictionary<long, StableAbiFixtureModule> _nativeModules = [];
    private readonly List<long> _nativeOrder = [];
    private readonly StableAbiFixtureConfiguration? _stableAbiFixture;
    private readonly StableAbiSymbolManifest? _stableAbiManifest;
    private long _boundNativeObjectId;
    private long _nextObjectId;
    private int _disposed;

    internal WorkerSessionState(
        IReadOnlyList<string> packageRoots,
        StableAbiFixtureConfiguration? stableAbiFixture
    )
    {
        ArgumentNullException.ThrowIfNull(packageRoots);
        _stableAbiFixture = stableAbiFixture;
        _stableAbiManifest = stableAbiFixture is null
            ? null
            : StableAbiSymbolManifest.Load(stableAbiFixture.ManifestPath);
        if (stableAbiFixture is not null && _stableAbiManifest is not null)
        {
            ValidateQualifiedPackage(packageRoots, stableAbiFixture, _stableAbiManifest);
        }
        Engine =
            packageRoots.Count == 0
                ? new ManagedPythonEngine()
                : new ManagedPythonEngine(
                    new ManagedModuleDiscoveryOptions
                    {
                        SearchPaths = packageRoots,
                        NativeExtensionResolver = ResolveNativeExtension,
                    }
                );
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

    internal T InvokeNative<T>(Func<T> operation) =>
        _nativeLane.InvokeAsync(operation, CancellationToken.None).GetAwaiter().GetResult();

    private Action<PythonGlobalNamespace>? ResolveNativeExtension(string name, string path)
    {
        if (
            _stableAbiFixture is null
            || _stableAbiManifest is null
            || !string.Equals(name, _stableAbiManifest.ModuleName, StringComparison.Ordinal)
        )
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(_stableAbiFixture.FixturePath),
                comparison
            )
        )
        {
            throw new InvalidDataException(
                $"Qualified native module '{name}' resolved to an artifact other than its configured path."
            );
        }

        return InitializeNativeExtension;
    }

    private void InitializeNativeExtension(PythonGlobalNamespace globals)
    {
        if (_stableAbiFixture is null || _boundNativeObjectId != 0)
        {
            throw new PythonRuntimeException(
                "DPY4029",
                "The qualified native module cannot be initialized in this session.",
                default,
                "ImportError"
            );
        }

        try
        {
            var loaded = LoadStableAbiModuleAsync(_stableAbiFixture, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            _boundNativeObjectId = loaded.ObjectId;
            var exports = InvokeNative(() =>
            {
                var module = GetModule(loaded.ObjectId);
                return module
                    .GetAttributeNames()
                    .Where(name =>
                        _stableAbiManifest!.AllowedMethods.Contains(name, StringComparer.Ordinal)
                    )
                    .Select(name => new KeyValuePair<string, PythonValue>(
                        name,
                        QualifiedStableAbiObjectProtocol.ToManaged(this, module.GetAttribute(name))
                    ))
                    .ToArray();
            });
            foreach (var export in exports)
            {
                globals.SetValue(export.Key, export.Value);
            }
        }
        catch (StableAbiLoadException exception)
        {
            throw new PythonRuntimeException(
                exception.Code,
                exception.Message,
                default,
                "ImportError"
            );
        }
    }

    private static void ValidateQualifiedPackage(
        IReadOnlyList<string> packageRoots,
        StableAbiFixtureConfiguration configuration,
        StableAbiSymbolManifest manifest
    )
    {
        if (
            manifest.NativeEntry is null
            || manifest.PackageInitializer is null
            || manifest.PackageInitializerSha256 is null
            || manifest.PackageMetadata is null
            || manifest.PackageMetadataSha256 is null
        )
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var packageRoot = packageRoots.FirstOrDefault(root =>
            string.Equals(
                Path.GetFullPath(Path.Combine(root, manifest.NativeEntry)),
                Path.GetFullPath(configuration.FixturePath),
                comparison
            )
        );
        if (packageRoot is null)
        {
            return;
        }

        ValidatePackageFile(
            packageRoot,
            manifest.PackageInitializer,
            manifest.PackageInitializerSha256
        );
        ValidatePackageFile(packageRoot, manifest.PackageMetadata, manifest.PackageMetadataSha256);
    }

    private static void ValidatePackageFile(
        string packageRoot,
        string relativePath,
        string expectedSha256
    )
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison) || !File.Exists(path))
        {
            throw PackagePolicyFailure(path, "A qualified package file is missing.");
        }

        var file = new FileInfo(path);
        if (
            file.LinkTarget is not null
            || (file.Attributes & FileAttributes.ReparsePoint) != 0
            || !string.Equals(
                StableAbiFixtureLoader.ComputeSha256(path),
                expectedSha256,
                StringComparison.Ordinal
            )
        )
        {
            throw PackagePolicyFailure(
                path,
                "A qualified package file failed identity validation."
            );
        }
    }

    private static StableAbiLoadException PackagePolicyFailure(string path, string message) =>
        new(
            "DPY8001",
            StableAbiLoadPhase.Policy,
            message,
            path,
            artifactSha256: null,
            missingSymbol: null
        );
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
