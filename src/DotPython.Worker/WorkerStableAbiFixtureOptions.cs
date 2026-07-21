namespace DotPython.Worker;

public sealed record WorkerStableAbiFixtureOptions
{
    public required string BridgePath { get; init; }

    public required string FixturePath { get; init; }

    public required string ManifestPath { get; init; }

    public required string BridgeSha256 { get; init; }

    public required string FixtureSha256 { get; init; }

    public required string ManifestSha256 { get; init; }

    internal void Validate()
    {
        ValidateFile(BridgePath, nameof(BridgePath));
        ValidateFile(FixturePath, nameof(FixturePath));
        ValidateFile(ManifestPath, nameof(ManifestPath));
        ValidateHash(BridgeSha256, nameof(BridgeSha256));
        ValidateHash(FixtureSha256, nameof(FixtureSha256));
        ValidateHash(ManifestSha256, nameof(ManifestSha256));
    }

    private static void ValidateFile(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new ArgumentException(
                "An existing absolute native artifact path is required.",
                parameterName
            );
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                "Native artifact paths cannot be links or reparse points.",
                parameterName
            );
        }
    }

    private static void ValidateHash(string hash, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, parameterName);
        if (
            hash.Length != 64
            || hash.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')
            )
        )
        {
            throw new ArgumentException("A lowercase SHA-256 value is required.", parameterName);
        }
    }
}
