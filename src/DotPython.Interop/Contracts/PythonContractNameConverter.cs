using System.Globalization;
using System.Text;

namespace DotPython.Interop.Contracts;

/// <summary>Converts Python export names into deterministic generated C# identifiers.</summary>
public static class PythonContractNameConverter
{
    private static readonly HashSet<string> CSharpKeywords = new(
        [
            "abstract",
            "as",
            "base",
            "bool",
            "break",
            "byte",
            "case",
            "catch",
            "char",
            "checked",
            "class",
            "const",
            "continue",
            "decimal",
            "default",
            "delegate",
            "do",
            "double",
            "else",
            "enum",
            "event",
            "explicit",
            "extern",
            "false",
            "finally",
            "fixed",
            "float",
            "for",
            "foreach",
            "goto",
            "if",
            "implicit",
            "in",
            "int",
            "interface",
            "internal",
            "is",
            "lock",
            "long",
            "namespace",
            "new",
            "null",
            "object",
            "operator",
            "out",
            "override",
            "params",
            "private",
            "protected",
            "public",
            "readonly",
            "ref",
            "return",
            "sbyte",
            "sealed",
            "short",
            "sizeof",
            "stackalloc",
            "static",
            "string",
            "struct",
            "switch",
            "this",
            "throw",
            "true",
            "try",
            "typeof",
            "uint",
            "ulong",
            "unchecked",
            "unsafe",
            "ushort",
            "using",
            "virtual",
            "void",
            "volatile",
            "while",
        ],
        StringComparer.Ordinal
    );

    /// <summary>Converts a public Python function name into an asynchronous CLR member name.</summary>
    public static string ToClrMemberName(string pythonName)
    {
        var name = ConvertSnakeCase(pythonName, upperFirst: true);
        return name.EndsWith("Async", StringComparison.Ordinal) ? name : name + "Async";
    }

    /// <summary>Converts a Python parameter name into a C# parameter identifier.</summary>
    public static string ToClrParameterName(string pythonName)
    {
        var name = ConvertSnakeCase(pythonName, upperFirst: false);
        return CSharpKeywords.Contains(name) ? "@" + name : name;
    }

    /// <summary>Gets whether a name is in the deliberately portable ASCII identifier subset.</summary>
    public static bool IsValidIdentifier(string value, bool allowVerbatimPrefix = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var start = allowVerbatimPrefix && value[0] == '@' ? 1 : 0;
        if (start == value.Length || !IsAsciiIdentifierStart(value[start]))
        {
            return false;
        }

        return value[(start + 1)..].All(IsAsciiIdentifierPart);
    }

    /// <summary>Gets whether every segment is a portable ASCII identifier.</summary>
    public static bool IsValidQualifiedName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Split('.').All(segment => IsValidIdentifier(segment));

    private static string ConvertSnakeCase(string pythonName, bool upperFirst)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonName);
        if (!IsValidIdentifier(pythonName) || pythonName[0] == '_')
        {
            throw new ArgumentException(
                "The Python name is outside the portable public identifier subset.",
                nameof(pythonName)
            );
        }

        var parts = pythonName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(pythonName.Length);
        foreach (var part in parts)
        {
            builder.Append(char.ToUpper(part[0], CultureInfo.InvariantCulture));
            builder.Append(part.AsSpan(1));
        }

        if (!upperFirst)
        {
            builder[0] = char.ToLower(builder[0], CultureInfo.InvariantCulture);
        }

        return builder.ToString();
    }

    private static bool IsAsciiIdentifierStart(char value) =>
        value is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiIdentifierPart(char value) =>
        IsAsciiIdentifierStart(value) || value is >= '0' and <= '9';
}
