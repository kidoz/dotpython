using System.Globalization;
using System.Text;

namespace DotPython.Language.Text;

/// <summary>
/// The identifier character rules the tokenizer scans with. Shared with the managed
/// runtime so declaration-time checks such as `__slots__` accept exactly the names
/// that parse as identifiers.
/// </summary>
internal static class PythonIdentifier
{
    internal static bool IsStart(Rune rune)
    {
        if (rune.Value == '_')
        {
            return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category
                is UnicodeCategory.UppercaseLetter
                    or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.OtherLetter
                    or UnicodeCategory.LetterNumber
            || rune.Value is 0x1885 or 0x1886 or 0x2118 or 0x212E or 0x309B or 0x309C;
    }

    internal static bool IsContinue(Rune rune)
    {
        if (IsStart(rune))
        {
            return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category
                is UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.DecimalDigitNumber
                    or UnicodeCategory.ConnectorPunctuation
            || rune.Value is 0x00B7 or 0x0387 or 0x19DA
            || rune.Value is >= 0x1369 and <= 0x1371;
    }

    /// <summary>Whether the text is a valid identifier in full.</summary>
    internal static bool IsIdentifier(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var offset = 0;
        var first = true;
        while (offset < value.Length)
        {
            if (!Rune.TryGetRuneAt(value, offset, out var rune))
            {
                return false;
            }

            if (first ? !IsStart(rune) : !IsContinue(rune))
            {
                return false;
            }

            first = false;
            offset += rune.Utf16SequenceLength;
        }

        return true;
    }
}
