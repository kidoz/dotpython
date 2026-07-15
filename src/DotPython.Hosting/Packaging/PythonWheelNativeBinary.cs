namespace DotPython.Hosting.Packaging;

/// <summary>Describes a native binary entry found inside a wheel archive.</summary>
public sealed record PythonWheelNativeBinary
{
    /// <summary>Gets the slash-separated archive path.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Gets the detected native container format.</summary>
    public required PythonNativeBinaryFormat Format { get; init; }

    /// <summary>Gets whether the filename advertises an HPy Universal ABI module.</summary>
    public required bool IsHpyUniversal { get; init; }

    /// <summary>Gets imported native symbol names when a supported symbol table was available.</summary>
    public required IReadOnlyList<string> RequiredSymbols { get; init; }
}
