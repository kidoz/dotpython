namespace DotPython.Interop.Contracts;

/// <summary>Maps an approved Python annotation name to a referenced CLR contract type.</summary>
public sealed record PythonExternalTypeMapping
{
    /// <summary>Initializes an external type mapping without loading or reflecting over an assembly.</summary>
    public PythonExternalTypeMapping(
        string pythonTypeName,
        string clrTypeName,
        bool isValueType = false,
        bool isClsCompliant = true
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clrTypeName);
        if (!PythonContractNameConverter.IsValidQualifiedName(pythonTypeName))
        {
            throw new ArgumentException(
                "The Python type name must be a portable qualified identifier.",
                nameof(pythonTypeName)
            );
        }

        var clrElementType = clrTypeName.EndsWith("[]", StringComparison.Ordinal)
            ? clrTypeName[..^2]
            : clrTypeName;
        if (!PythonContractNameConverter.IsValidQualifiedName(clrElementType))
        {
            throw new ArgumentException(
                "The CLR type name must be a portable qualified identifier.",
                nameof(clrTypeName)
            );
        }

        PythonTypeName = pythonTypeName;
        ClrTypeName = clrTypeName;
        IsValueType = isValueType;
        IsClsCompliant = isClsCompliant;
    }

    /// <summary>Gets the fully qualified Python annotation name.</summary>
    public string PythonTypeName { get; }

    /// <summary>Gets the fully qualified CLR metadata type name.</summary>
    public string ClrTypeName { get; }

    /// <summary>Gets whether the CLR type is a value type.</summary>
    public bool IsValueType { get; }

    /// <summary>Gets whether the CLR type is approved for a CLS-compliant public signature.</summary>
    public bool IsClsCompliant { get; }
}
