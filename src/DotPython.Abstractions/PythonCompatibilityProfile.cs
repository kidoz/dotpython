namespace DotPython;

/// <summary>Describes the compatibility guarantees of a DotPython runtime.</summary>
public sealed record PythonCompatibilityProfile
{
    /// <summary>Gets the Python language version targeted by the runtime.</summary>
    public required Version LanguageVersion { get; init; }

    /// <summary>
    /// Gets the Python language versions whose compiled module artifacts the runtime accepts.
    /// </summary>
    public required IReadOnlyList<Version> SupportedArtifactLanguageVersions { get; init; }

    /// <summary>Gets the stable implementation identifier.</summary>
    public required string Implementation { get; init; }

    /// <summary>
    /// Gets the executable native-extension capabilities exposed by this runtime provider.
    /// An empty list means native wheels may be recognized by tooling but cannot be executed.
    /// </summary>
    public required IReadOnlyList<PythonNativeExtensionCapability> NativeExtensionCapabilities { get; init; }
}
