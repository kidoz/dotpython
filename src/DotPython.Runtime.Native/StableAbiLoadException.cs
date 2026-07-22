namespace DotPython.Runtime.Native;

public enum StableAbiLoadPhase
{
    Policy,
    Manifest,
    Architecture,
    BridgeLoad,
    ModuleLoad,
    SymbolResolution,
    ModuleInitialization,
    Invocation,
    Cleanup,
}

public sealed class StableAbiLoadException : Exception
{
    public StableAbiLoadException()
        : this(
            "DPY8004",
            StableAbiLoadPhase.ModuleLoad,
            "A Stable-ABI load failure occurred.",
            null,
            null,
            null
        ) { }

    public StableAbiLoadException(string message)
        : this("DPY8004", StableAbiLoadPhase.ModuleLoad, message, null, null, null) { }

    public StableAbiLoadException(string message, Exception innerException)
        : this("DPY8004", StableAbiLoadPhase.ModuleLoad, message, null, null, null, innerException)
    { }

    internal StableAbiLoadException(
        string code,
        StableAbiLoadPhase phase,
        string message,
        string? artifactPath,
        string? artifactSha256,
        string? missingSymbol,
        Exception? innerException = null,
        string? pythonErrorType = null
    )
        : base(message, innerException)
    {
        Code = code;
        Phase = phase;
        ArtifactPath = artifactPath;
        ArtifactSha256 = artifactSha256;
        MissingSymbol = missingSymbol;
        PythonErrorType = pythonErrorType;
    }

    public string Code { get; }

    /// <summary>
    /// The Python exception type name reported by the native module for invocation failures
    /// (for example "ValueError"), when one was captured.
    /// </summary>
    public string? PythonErrorType { get; }

    public StableAbiLoadPhase Phase { get; }

    public string? ArtifactPath { get; }

    public string? ArtifactSha256 { get; }

    public string? MissingSymbol { get; }
}
