namespace DotPython.Language;

/// <summary>Defines the Python language version targeted by the managed front end.</summary>
public static class PythonLanguageVersion
{
    /// <summary>Gets the current Python language version.</summary>
    public static Version Current { get; } = new(3, 14);

    /// <summary>
    /// Gets the Python language versions whose compiled module artifacts the runtime accepts,
    /// per the single-track policy in ADR-015.
    /// </summary>
    public static IReadOnlyList<Version> SupportedArtifactVersions { get; } = [new Version(3, 14)];

    /// <summary>Gets whether module artifacts stamped with the version are accepted.</summary>
    public static bool IsSupportedArtifactVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var normalized = new Version(version.Major, version.Minor);
        return SupportedArtifactVersions.Contains(normalized);
    }
}
