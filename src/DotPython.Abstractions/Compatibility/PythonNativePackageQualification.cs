namespace DotPython;

/// <summary>Identifies one native package artifact qualified for a runtime capability.</summary>
public sealed record PythonNativePackageQualification
{
    /// <summary>Gets the normalized distribution name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the exact package version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the immutable artifact file name used by qualification tests.</summary>
    public required string ArtifactFileName { get; init; }

    /// <summary>Gets the lowercase hexadecimal SHA-256 digest of the artifact.</summary>
    public required string Sha256 { get; init; }
}
