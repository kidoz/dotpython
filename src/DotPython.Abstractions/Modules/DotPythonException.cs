namespace DotPython;

/// <summary>Represents a structured failure at a public DotPython module boundary.</summary>
public class DotPythonException : Exception
{
    /// <summary>Initializes a DotPython exception with a generic failure code.</summary>
    public DotPythonException()
        : this("DPY6000", "A DotPython module operation failed.") { }

    /// <summary>Initializes a DotPython exception with a generic failure code.</summary>
    public DotPythonException(string message)
        : this("DPY6000", message) { }

    /// <summary>Initializes a DotPython exception with a generic failure code.</summary>
    public DotPythonException(string message, Exception innerException)
        : this("DPY6000", message, DotPythonFailurePhase.None, null, null, innerException) { }

    /// <summary>Initializes a structured DotPython exception.</summary>
    public DotPythonException(
        string code,
        string message,
        DotPythonFailurePhase phase = DotPythonFailurePhase.None,
        string? moduleName = null,
        string? functionName = null,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        Code = code;
        Phase = phase;
        ModuleName = moduleName;
        FunctionName = functionName;
    }

    /// <summary>Gets the stable DotPython diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the operation phase that failed.</summary>
    public DotPythonFailurePhase Phase { get; }

    /// <summary>Gets the affected module name, when known.</summary>
    public string? ModuleName { get; }

    /// <summary>Gets the affected Python function name, when known.</summary>
    public string? FunctionName { get; }
}
