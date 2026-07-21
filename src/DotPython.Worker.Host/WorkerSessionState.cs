using DotPython.Runtime.Managed.Execution;
using DotPython.Runtime.Native;

namespace DotPython.Worker.Host;

internal sealed class WorkerSessionState : IAsyncDisposable
{
    private readonly NativeExecutionLane _nativeLane = new();
    private readonly Dictionary<long, StableAbiModule> _nativeModules = [];
    private readonly List<long> _nativeOrder = [];
    private readonly Dictionary<string, StableAbiModuleCatalogEntry> _stableAbiModules;
    private readonly Dictionary<string, long> _boundNativeObjectIds = new(StringComparer.Ordinal);
    private long _nextObjectId;
    private int _disposed;

    internal WorkerSessionState(
        IReadOnlyList<string> packageRoots,
        IReadOnlyList<StableAbiModuleCatalogEntry> stableAbiModules
    )
    {
        ArgumentNullException.ThrowIfNull(packageRoots);
        ArgumentNullException.ThrowIfNull(stableAbiModules);
        _stableAbiModules = stableAbiModules.ToDictionary(
            entry => entry.Manifest.ModuleName,
            StringComparer.Ordinal
        );
        foreach (var entry in stableAbiModules)
        {
            ValidateQualifiedPackage(packageRoots, entry.Configuration, entry.Manifest);
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

    private Task<long> LoadStableAbiModuleAsync(
        StableAbiModuleConfiguration configuration,
        CancellationToken cancellationToken
    ) =>
        _nativeLane.InvokeAsync(
            () =>
            {
                var module = StableAbiModuleLoader.Load(configuration);
                var objectId = checked(++_nextObjectId);
                _nativeModules.Add(objectId, module);
                _nativeOrder.Add(objectId);
                return objectId;
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

    private StableAbiModule GetModule(long objectId)
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
        if (!_stableAbiModules.TryGetValue(name, out var entry))
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(entry.Configuration.ModulePath),
                comparison
            )
        )
        {
            throw new InvalidDataException(
                $"Qualified native module '{name}' resolved to an artifact other than its configured path."
            );
        }

        return globals => InitializeNativeExtension(entry, globals);
    }

    private void InitializeNativeExtension(
        StableAbiModuleCatalogEntry entry,
        PythonGlobalNamespace globals
    )
    {
        if (_boundNativeObjectIds.ContainsKey(entry.Manifest.ModuleName))
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
            var objectId = LoadStableAbiModuleAsync(
                    entry.Configuration,
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult();
            _boundNativeObjectIds.Add(entry.Manifest.ModuleName, objectId);
            var exports = InvokeNative(() =>
            {
                var module = GetModule(objectId);
                return module
                    .GetAttributeNames()
                    .Where(name =>
                        entry.Manifest.AllowedMethods.Contains(name, StringComparer.Ordinal)
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
        StableAbiModuleConfiguration configuration,
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
                Path.GetFullPath(configuration.ModulePath),
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
                StableAbiModuleLoader.ComputeSha256(path),
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
