namespace DotPython.Hosting.Packaging;

/// <summary>Describes a deterministic wheel-inspection finding.</summary>
/// <param name="Code">The stable symbolic finding code.</param>
/// <param name="Severity">The finding severity.</param>
/// <param name="Message">The human-readable explanation.</param>
public sealed record PythonWheelDiagnostic(
    PythonWheelDiagnosticCode Code,
    PythonWheelDiagnosticSeverity Severity,
    string Message
);

/// <summary>Identifies wheel-inspection findings without using compiler diagnostic ranges.</summary>
public enum PythonWheelDiagnosticCode
{
    /// <summary>The filename does not follow the wheel filename convention.</summary>
    InvalidFileName,

    /// <summary>The archive is missing required wheel metadata.</summary>
    MissingMetadata,

    /// <summary>The archive contains duplicate wheel metadata or duplicate paths.</summary>
    DuplicateEntry,

    /// <summary>The archive contains a path that is not safe to extract.</summary>
    UnsafeArchivePath,

    /// <summary>The archive exceeds an inspection safety limit.</summary>
    InspectionLimitExceeded,

    /// <summary>The ZIP archive or metadata is malformed.</summary>
    MalformedArchive,

    /// <summary>The filename and embedded metadata disagree.</summary>
    MetadataMismatch,

    /// <summary>The wheel format major version is not supported.</summary>
    UnsupportedWheelVersion,

    /// <summary>The artifact uses the draft wheel-variant filename shape.</summary>
    DraftVariantUnsupported,

    /// <summary>A native binary format or symbol table could not be inspected.</summary>
    NativeSymbolsUnavailable,

    /// <summary>The managed runtime cannot execute the advertised binary contract.</summary>
    NativeRuntimeUnsupported,
}

/// <summary>Identifies the severity of a wheel-inspection finding.</summary>
public enum PythonWheelDiagnosticSeverity
{
    /// <summary>Informational context that does not invalidate the artifact.</summary>
    Information,

    /// <summary>A compatibility limitation or non-fatal inspection issue.</summary>
    Warning,

    /// <summary>A structural error that invalidates the artifact.</summary>
    Error,
}
