namespace DotPython.Runtime.Native;

internal static class StableAbiAnyverLibraryCache
{
    private static readonly object Gate = new();
    private static string? _bridgeIdentity;
    private static string? _fixtureIdentity;
    private static nint _bridge;
    private static nint _fixture;

    internal static (nint Bridge, nint Fixture) Load(
        string bridgePath,
        string bridgeHash,
        string fixturePath,
        string fixtureHash
    )
    {
        var bridgeIdentity = $"{bridgePath}\n{bridgeHash}";
        var fixtureIdentity = $"{fixturePath}\n{fixtureHash}";
        lock (Gate)
        {
            if (_bridge != 0 || _fixture != 0)
            {
                if (
                    !string.Equals(_bridgeIdentity, bridgeIdentity, StringComparison.Ordinal)
                    || !string.Equals(_fixtureIdentity, fixtureIdentity, StringComparison.Ordinal)
                )
                {
                    throw new StableAbiLoadException(
                        "DPY8004",
                        StableAbiLoadPhase.Policy,
                        "A worker process can pin only one Anyver bridge and native entry identity.",
                        fixturePath,
                        fixtureHash,
                        missingSymbol: null
                    );
                }

                return (_bridge, _fixture);
            }

            var bridge = StableAbiFixtureLoader.LoadBridgeLibrary(
                bridgePath,
                bridgeHash,
                StableAbiLoadPhase.BridgeLoad
            );
            try
            {
                var fixture = StableAbiFixtureLoader.LoadLibrary(
                    fixturePath,
                    fixtureHash,
                    StableAbiLoadPhase.FixtureLoad
                );
                _bridgeIdentity = bridgeIdentity;
                _fixtureIdentity = fixtureIdentity;
                _bridge = bridge;
                _fixture = fixture;
                return (bridge, fixture);
            }
            catch
            {
                NativeLibraryGlobalLoader.Free(bridge);
                throw;
            }
        }
    }
}
