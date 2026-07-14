namespace DotPython.Contracts;

/// <summary>Describes one parameter in an exported Python function.</summary>
public sealed class PythonParameterContract
{
    /// <summary>Initializes an exported function parameter.</summary>
    public PythonParameterContract(
        string pythonName,
        string clrName,
        PythonParameterKind kind,
        bool hasDefault,
        PythonTypeContract type
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clrName);
        if (!ContractNameValidator.IsIdentifier(pythonName))
        {
            throw new ArgumentException(
                "The Python parameter name must be a portable identifier.",
                nameof(pythonName)
            );
        }

        if (!ContractNameValidator.IsIdentifier(clrName, allowVerbatimPrefix: true))
        {
            throw new ArgumentException(
                "The CLR parameter name must be a portable identifier.",
                nameof(clrName)
            );
        }

        if (!Enum.IsDefined(kind) || kind == PythonParameterKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(type);
        PythonName = pythonName;
        ClrName = clrName;
        Kind = kind;
        HasDefault = hasDefault;
        Type = type;
    }

    /// <summary>Gets the parameter name in Python.</summary>
    public string PythonName { get; }

    /// <summary>Gets the deterministic C# parameter name.</summary>
    public string ClrName { get; }

    /// <summary>Gets how the Python parameter can be supplied.</summary>
    public PythonParameterKind Kind { get; }

    /// <summary>Gets whether the stub declares a default value.</summary>
    public bool HasDefault { get; }

    /// <summary>Gets the parameter's mapped type.</summary>
    public PythonTypeContract Type { get; }
}
