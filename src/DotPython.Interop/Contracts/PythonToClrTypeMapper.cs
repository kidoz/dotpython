using System.Diagnostics.CodeAnalysis;
using DotPython.Contracts;

namespace DotPython.Interop.Contracts;

/// <summary>Maps a deliberately bounded Python annotation vocabulary to CLS-oriented CLR types.</summary>
public sealed class PythonToClrTypeMapper
{
    private static readonly Dictionary<string, ScalarMapping> Scalars = new(StringComparer.Ordinal)
    {
        ["None"] = new("System.Void", true),
        ["builtins.None"] = new("System.Void", true),
        ["bool"] = new("System.Boolean", true),
        ["builtins.bool"] = new("System.Boolean", true),
        ["int"] = new("System.Numerics.BigInteger", true),
        ["builtins.int"] = new("System.Numerics.BigInteger", true),
        ["float"] = new("System.Double", true),
        ["builtins.float"] = new("System.Double", true),
        ["str"] = new("System.String", false),
        ["builtins.str"] = new("System.String", false),
        ["bytes"] = new("System.Byte[]", false),
        ["builtins.bytes"] = new("System.Byte[]", false),
        ["decimal.Decimal"] = new("System.Decimal", true),
        ["uuid.UUID"] = new("System.Guid", true),
        ["datetime.datetime"] = new("System.DateTimeOffset", true),
        ["datetime.date"] = new("System.DateOnly", true),
        ["datetime.time"] = new("System.TimeOnly", true),
        ["datetime.timedelta"] = new("System.TimeSpan", true),
    };

    private readonly Dictionary<string, PythonExternalTypeMapping> _externalTypes;

    /// <summary>Initializes a mapper with an optional allowlist of referenced CLR contract types.</summary>
    public PythonToClrTypeMapper(
        IEnumerable<PythonExternalTypeMapping>? externalTypeMappings = null
    )
    {
        var mappings = new Dictionary<string, PythonExternalTypeMapping>(StringComparer.Ordinal);
        foreach (var mapping in externalTypeMappings ?? [])
        {
            ArgumentNullException.ThrowIfNull(mapping);
            if (!mappings.TryAdd(mapping.PythonTypeName, mapping))
            {
                throw new ArgumentException(
                    $"Duplicate external Python type mapping '{mapping.PythonTypeName}'.",
                    nameof(externalTypeMappings)
                );
            }
        }

        _externalTypes = mappings;
    }

    /// <summary>Attempts to map a canonical Python name and its already-mapped type arguments.</summary>
    public bool TryMap(
        string pythonName,
        IReadOnlyList<PythonTypeContract> typeArguments,
        bool isNullable,
        [NotNullWhen(true)] out PythonTypeContract? mappedType
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonName);
        ArgumentNullException.ThrowIfNull(typeArguments);

        if (Scalars.TryGetValue(pythonName, out var scalar) && typeArguments.Count == 0)
        {
            if (
                isNullable
                && string.Equals(scalar.ClrTypeName, "System.Void", StringComparison.Ordinal)
            )
            {
                mappedType = null;
                return false;
            }

            mappedType = new PythonTypeContract(
                pythonName,
                scalar.ClrTypeName,
                isNullable,
                scalar.IsValueType,
                isClsCompliant: true
            );
            return true;
        }

        if (IsListType(pythonName) && IsValidTypeArguments(typeArguments, 1))
        {
            mappedType = new PythonTypeContract(
                pythonName,
                "System.Collections.Generic.IReadOnlyList",
                isNullable,
                isValueType: false,
                isClsCompliant: true,
                typeArguments
            );
            return true;
        }

        if (IsDictionaryType(pythonName) && IsValidTypeArguments(typeArguments, 2))
        {
            mappedType = new PythonTypeContract(
                pythonName,
                "System.Collections.Generic.IReadOnlyDictionary",
                isNullable,
                isValueType: false,
                isClsCompliant: true,
                typeArguments
            );
            return true;
        }

        if (_externalTypes.TryGetValue(pythonName, out var external) && typeArguments.Count == 0)
        {
            mappedType = new PythonTypeContract(
                pythonName,
                external.ClrTypeName,
                isNullable,
                external.IsValueType,
                external.IsClsCompliant
            );
            return true;
        }

        mappedType = null;
        return false;
    }

    internal bool IsExternalType(string pythonName) => _externalTypes.ContainsKey(pythonName);

    internal static bool IsIntrinsicOrCollectionType(string pythonName) =>
        Scalars.ContainsKey(pythonName) || IsListType(pythonName) || IsDictionaryType(pythonName);

    private static bool IsValidTypeArguments(
        IReadOnlyList<PythonTypeContract> arguments,
        int expectedCount
    ) =>
        arguments.Count == expectedCount
        && arguments.All(argument =>
            !string.Equals(argument.ClrTypeName, "System.Void", StringComparison.Ordinal)
        );

    private static bool IsListType(string name) =>
        name
            is "list"
                or "builtins.list"
                or "typing.List"
                or "typing.Sequence"
                or "collections.abc.Sequence";

    private static bool IsDictionaryType(string name) =>
        name
            is "dict"
                or "builtins.dict"
                or "typing.Dict"
                or "typing.Mapping"
                or "collections.abc.Mapping";

    private sealed record ScalarMapping(string ClrTypeName, bool IsValueType);
}
