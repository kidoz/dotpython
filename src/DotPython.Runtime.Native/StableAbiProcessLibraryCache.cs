namespace DotPython.Runtime.Native;

internal static class StableAbiProcessLibraryCache
{
    private static readonly object Gate = new();
    private static string? _bridgeIdentity;
    private static string? _moduleIdentity;
    private static nint _bridge;
    private static nint _module;

    internal static (nint Bridge, nint Module) Load(
        string bridgePath,
        string bridgeHash,
        string modulePath,
        string moduleHash
    )
    {
        var bridgeIdentity = $"{bridgePath}\n{bridgeHash}";
        var moduleIdentity = $"{modulePath}\n{moduleHash}";
        lock (Gate)
        {
            if (_bridge != 0 || _module != 0)
            {
                if (
                    !string.Equals(_bridgeIdentity, bridgeIdentity, StringComparison.Ordinal)
                    || !string.Equals(_moduleIdentity, moduleIdentity, StringComparison.Ordinal)
                )
                {
                    throw new StableAbiLoadException(
                        "DPY8004",
                        StableAbiLoadPhase.Policy,
                        "A worker process can pin only one Stable-ABI bridge and native entry identity.",
                        modulePath,
                        moduleHash,
                        missingSymbol: null
                    );
                }

                return (_bridge, _module);
            }

            var bridge = StableAbiModuleLoader.LoadBridgeLibrary(
                bridgePath,
                bridgeHash,
                StableAbiLoadPhase.BridgeLoad
            );
            try
            {
                var module = StableAbiModuleLoader.LoadLibrary(
                    modulePath,
                    moduleHash,
                    StableAbiLoadPhase.ModuleLoad
                );
                _bridgeIdentity = bridgeIdentity;
                _moduleIdentity = moduleIdentity;
                _bridge = bridge;
                _module = module;
                return (bridge, module);
            }
            catch
            {
                NativeLibraryGlobalLoader.Free(bridge);
                throw;
            }
        }
    }
}
