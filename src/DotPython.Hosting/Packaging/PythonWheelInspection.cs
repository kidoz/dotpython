namespace DotPython.Hosting.Packaging;

/// <summary>Contains deterministic, read-only wheel classification results.</summary>
public sealed record PythonWheelInspection
{
    private readonly int _formatVersion = 1;

    /// <summary>Gets the inspection schema version.</summary>
    public int FormatVersion => _formatVersion;

    /// <summary>Gets the artifact filename without a machine-local directory.</summary>
    public required string FileName { get; init; }

    /// <summary>Gets the lowercase hexadecimal SHA-256 hash of the complete artifact.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the distribution component parsed from the filename.</summary>
    public required string Distribution { get; init; }

    /// <summary>Gets the version component parsed from the filename.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the optional build tag.</summary>
    public string? BuildTag { get; init; }

    /// <summary>Gets the classified compatibility contract.</summary>
    public required PythonWheelCompatibilityKind Compatibility { get; init; }

    /// <summary>Gets a concise reason for the classification.</summary>
    public required string CompatibilitySummary { get; init; }

    /// <summary>Gets whether any expanded tag selects a free-threaded CPython ABI.</summary>
    public required bool IsFreeThreaded { get; init; }

    /// <summary>Gets expanded compatibility tags in ordinal order.</summary>
    public required IReadOnlyList<PythonWheelTag> Tags { get; init; }

    /// <summary>Gets distinct expanded platform descriptions in ordinal order.</summary>
    public required IReadOnlyList<PythonWheelPlatform> Platforms { get; init; }

    /// <summary>Gets native binaries found in the archive in ordinal path order.</summary>
    public required IReadOnlyList<PythonWheelNativeBinary> NativeBinaries { get; init; }

    /// <summary>Gets deterministic inspection findings.</summary>
    public required IReadOnlyList<PythonWheelDiagnostic> Diagnostics { get; init; }

    /// <summary>Gets whether no structural error was reported.</summary>
    public bool IsValid =>
        Diagnostics.All(diagnostic => diagnostic.Severity != PythonWheelDiagnosticSeverity.Error);
}
