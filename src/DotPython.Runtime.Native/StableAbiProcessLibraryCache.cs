namespace DotPython.Runtime.Native;

internal static class StableAbiProcessLibraryCache
{
    private static readonly object Gate = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly Dictionary<string, CachedModule> Modules = new(PathComparer);
    private static string? _bridgePath;
    private static string? _bridgeHash;
    private static nint _bridge;

    internal static (nint Bridge, nint Module) Load(
        string bridgePath,
        string bridgeHash,
        string modulePath,
        string moduleHash
    )
    {
        var normalizedBridgePath = Path.GetFullPath(bridgePath);
        var normalizedModulePath = Path.GetFullPath(modulePath);
        lock (Gate)
        {
            if (_bridge != 0)
            {
                if (
                    !PathComparer.Equals(_bridgePath, normalizedBridgePath)
                    || !string.Equals(_bridgeHash, bridgeHash, StringComparison.Ordinal)
                )
                {
                    throw new StableAbiLoadException(
                        "DPY8004",
                        StableAbiLoadPhase.Policy,
                        "A worker process can pin only one Stable-ABI bridge identity.",
                        bridgePath,
                        bridgeHash,
                        missingSymbol: null
                    );
                }
            }

            if (Modules.TryGetValue(normalizedModulePath, out var cachedModule))
            {
                if (!string.Equals(cachedModule.Hash, moduleHash, StringComparison.Ordinal))
                {
                    throw new StableAbiLoadException(
                        "DPY8004",
                        StableAbiLoadPhase.Policy,
                        "A process-pinned native path cannot be reused with another artifact identity.",
                        modulePath,
                        moduleHash,
                        missingSymbol: null
                    );
                }

                return (_bridge, cachedModule.Handle);
            }

            if (Modules.Count >= 64)
            {
                throw new StableAbiLoadException(
                    "DPY8004",
                    StableAbiLoadPhase.Policy,
                    "The process-pinned Stable-ABI module cache reached its 64-module bound.",
                    modulePath,
                    moduleHash,
                    missingSymbol: null
                );
            }

            var loadedBridge = false;
            if (_bridge == 0)
            {
                _bridge = StableAbiModuleLoader.LoadBridgeLibrary(
                    bridgePath,
                    bridgeHash,
                    StableAbiLoadPhase.BridgeLoad
                );
                _bridgePath = normalizedBridgePath;
                _bridgeHash = bridgeHash;
                loadedBridge = true;
            }

            try
            {
                var module = StableAbiModuleLoader.LoadLibrary(
                    modulePath,
                    moduleHash,
                    StableAbiLoadPhase.ModuleLoad
                );
                Modules.Add(normalizedModulePath, new CachedModule(moduleHash, module));
                return (_bridge, module);
            }
            catch
            {
                if (loadedBridge && Modules.Count == 0)
                {
                    NativeLibraryGlobalLoader.Free(_bridge);
                    _bridge = 0;
                    _bridgePath = null;
                    _bridgeHash = null;
                }

                throw;
            }
        }
    }

    private sealed record CachedModule(string Hash, nint Handle);
}
