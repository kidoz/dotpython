namespace DotPython.Runtime.Native;

internal sealed record StableAbiFixtureConfiguration(
    string BridgePath,
    string FixturePath,
    string ManifestPath,
    string BridgeSha256,
    string FixtureSha256,
    string ManifestSha256
);
