namespace DotPython;

/// <summary>Describes the compatibility guarantees of a DotPython runtime.</summary>
public sealed record PythonCompatibilityProfile
{
    /// <summary>Gets the Python language version targeted by the runtime.</summary>
    public required Version LanguageVersion { get; init; }

    /// <summary>Gets the stable implementation identifier.</summary>
    public required string Implementation { get; init; }

    /// <summary>Gets whether CPython extension binaries can be loaded.</summary>
    public required bool SupportsCpythonAbi { get; init; }
}
