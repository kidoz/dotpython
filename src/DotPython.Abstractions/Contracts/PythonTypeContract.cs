using System.Collections.ObjectModel;

namespace DotPython.Contracts;

/// <summary>Describes one statically mapped Python and CLR type pair.</summary>
public sealed class PythonTypeContract
{
    /// <summary>Initializes a mapped Python type.</summary>
    public PythonTypeContract(
        string pythonName,
        string clrTypeName,
        bool isNullable,
        bool isValueType,
        bool isClsCompliant,
        IEnumerable<PythonTypeContract>? typeArguments = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clrTypeName);
        if (!ContractNameValidator.IsQualifiedName(pythonName))
        {
            throw new ArgumentException(
                "The Python type name must be a portable qualified identifier.",
                nameof(pythonName)
            );
        }

        if (!ContractNameValidator.IsClrTypeName(clrTypeName))
        {
            throw new ArgumentException(
                "The CLR type name must be a portable qualified type name.",
                nameof(clrTypeName)
            );
        }

        if (isNullable && string.Equals(clrTypeName, "System.Void", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The None return type cannot be nullable.",
                nameof(isNullable)
            );
        }

        var arguments = (typeArguments ?? []).ToList();
        if (arguments.Any(argument => argument is null))
        {
            throw new ArgumentException(
                "Type arguments cannot contain null.",
                nameof(typeArguments)
            );
        }

        PythonName = pythonName;
        ClrTypeName = clrTypeName;
        IsNullable = isNullable;
        IsValueType = isValueType;
        IsClsCompliant = isClsCompliant && arguments.All(argument => argument.IsClsCompliant);
        TypeArguments = new ReadOnlyCollection<PythonTypeContract>(arguments);
    }

    /// <summary>Gets the canonical Python annotation name.</summary>
    public string PythonName { get; }

    /// <summary>Gets the fully qualified CLR type name without generic arguments.</summary>
    public string ClrTypeName { get; }

    /// <summary>Gets whether <see langword="null"/> is allowed at the CLR boundary.</summary>
    public bool IsNullable { get; }

    /// <summary>Gets whether the mapped CLR type has value-type nullability semantics.</summary>
    public bool IsValueType { get; }

    /// <summary>Gets whether the complete mapped signature type is CLS-compliant.</summary>
    public bool IsClsCompliant { get; }

    /// <summary>Gets the mapped generic type arguments.</summary>
    public IReadOnlyList<PythonTypeContract> TypeArguments { get; }

    /// <summary>Gets a deterministic C# type spelling suitable for generated source.</summary>
    public string CSharpTypeName
    {
        get
        {
            if (string.Equals(ClrTypeName, "System.Void", StringComparison.Ordinal))
            {
                return "void";
            }

            var name =
                TypeArguments.Count == 0
                    ? ClrTypeName
                    : $"{ClrTypeName}<{string.Join(", ", TypeArguments.Select(type => type.CSharpTypeName))}>";
            if (!IsNullable)
            {
                return name;
            }

            return IsValueType ? $"System.Nullable<{name}>" : name + "?";
        }
    }
}
