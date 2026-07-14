namespace DotPython.Contracts;

internal static class ContractNameValidator
{
    internal static bool IsIdentifier(string value, bool allowVerbatimPrefix = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var start = allowVerbatimPrefix && value[0] == '@' ? 1 : 0;
        if (start == value.Length || !IsIdentifierStart(value[start]))
        {
            return false;
        }

        return value[(start + 1)..].All(IsIdentifierPart);
    }

    internal static bool IsQualifiedName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Split('.').All(segment => IsIdentifier(segment));

    internal static bool IsClrTypeName(string value)
    {
        var elementName = value.EndsWith("[]", StringComparison.Ordinal) ? value[..^2] : value;
        return IsQualifiedName(elementName);
    }

    private static bool IsIdentifierStart(char value) =>
        value is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || value is >= '0' and <= '9';
}
