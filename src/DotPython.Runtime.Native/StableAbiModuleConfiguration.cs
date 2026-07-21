namespace DotPython.Runtime.Native;

internal sealed record StableAbiModuleConfiguration(
    string BridgePath,
    string ModulePath,
    string ManifestPath,
    string BridgeSha256,
    string ModuleSha256,
    string ManifestSha256
);
