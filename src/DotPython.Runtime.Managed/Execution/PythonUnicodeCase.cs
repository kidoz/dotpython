// Case mapping follows CPython 3.14.7 Objects/unicodectype.c and the Unicode 16.0.0
// special casing it pins:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The Unicode 16.0.0 case mappings behind <c>str.upper</c>, <c>lower</c>, <c>title</c>,
/// <c>capitalize</c>, <c>swapcase</c> and <c>casefold</c>, read from the pinned resource.
/// </summary>
/// <remarks>
/// The runtime's own string casing cannot be used: it follows the platform's Unicode version,
/// which expands differently (no `ß` → `SS`) and moves ahead of the 16.0.0 the language
/// target pins. The tables here are generated from the checked-in UCD files and qualified
/// against CPython for every code point.
/// </remarks>
internal static class PythonUnicodeCase
{
    private const string ResourceName = "DotPython.UnicodeCase16";
    private static readonly Lazy<CaseTable> Mappings = new(Load);

    internal static string ToUpper(string value) => Map(value, Kind.Upper, titlecase: false);

    internal static string ToLower(string value) => Map(value, Kind.Lower, titlecase: false);

    /// <summary>`str.title`: the first cased character of each word is titlecased, the rest lowered.</summary>
    internal static string ToTitle(string value) => Map(value, Kind.Title, titlecase: true);

    internal static string ToCaseFold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var code in Codes(value))
            Mappings.Value.Append(builder, code, Kind.Fold);
        return builder.ToString();
    }

    /// <summary>
    /// The code points of a string, kept as the runtime traverses them: an unpaired surrogate
    /// stays a code point of its own rather than becoming a replacement character, and a long
    /// scan charges the instruction budget.
    /// </summary>
    private static int[] Codes(string value) =>
        [.. PythonTextTraversal.Enumerate(value).Select(character => character.Value)];

    /// <summary>`str.capitalize`: the first character is titlecased and the rest lowered.</summary>
    internal static string Capitalize(string value)
    {
        if (value.Length == 0)
            return value;
        var codes = Codes(value);
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < codes.Length; index++)
        {
            if (index == 0)
                Mappings.Value.Append(builder, codes[index], Kind.Title);
            else
                AppendLower(builder, codes, index);
        }
        return builder.ToString();
    }

    /// <summary>
    /// `str.swapcase`: a lowercase character becomes uppercase and an uppercase one becomes
    /// lowercase, while anything uncased — including a titlecase letter — is left alone.
    /// </summary>
    internal static string SwapCase(string value)
    {
        var builder = new StringBuilder(value.Length);
        var codes = Codes(value);
        for (var index = 0; index < codes.Length; index++)
        {
            var code = codes[index];
            if (IsLower(code))
                Mappings.Value.Append(builder, code, Kind.Upper);
            else if (IsUpper(code))
                AppendLower(builder, codes, index);
            else
                Mappings.Value.Append(builder, code, Kind.None);
        }
        return builder.ToString();
    }

    private static string Map(string value, Kind kind, bool titlecase)
    {
        var builder = new StringBuilder(value.Length);
        var codes = Codes(value);
        var atWordStart = true;
        for (var index = 0; index < codes.Length; index++)
        {
            var code = codes[index];
            if (!titlecase)
            {
                if (kind == Kind.Lower)
                    AppendLower(builder, codes, index);
                else
                    Mappings.Value.Append(builder, code, kind);
                continue;
            }

            var isCased = IsCased(code);
            if (atWordStart)
                Mappings.Value.Append(builder, code, Kind.Title);
            else
                AppendLower(builder, codes, index);
            atWordStart = !isCased;
        }
        return builder.ToString();
    }

    /// <summary>
    /// Lowers one character, applying Final_Sigma: a capital sigma takes the final form only
    /// when a cased character precedes it and none follows, ignoring case-ignorable ones.
    /// </summary>
    private static void AppendLower(StringBuilder builder, int[] codes, int index)
    {
        if (codes[index] == 0x3A3 && IsFinalSigma(codes, index))
        {
            builder.Append('ς');
            return;
        }
        Mappings.Value.Append(builder, codes[index], Kind.Lower);
    }

    /// <summary>
    /// Whether `str.swapcase` treats the character as lowercase: the `Ll` category plus the
    /// `Other_Lowercase` additions, which have an uppercase mapping but no lowercase one.
    /// </summary>
    internal static bool IsLower(int codePoint) => Mappings.Value.IsLower(codePoint);

    /// <summary>The `Lu` category plus `Other_Uppercase`, the counterpart of <see cref="IsLower"/>.</summary>
    internal static bool IsUpper(int codePoint) => Mappings.Value.IsUpper(codePoint);

    private static bool IsFinalSigma(int[] codes, int index)
    {
        var preceded = false;
        for (var previous = index - 1; previous >= 0; previous--)
        {
            var code = codes[previous];
            if (IsCaseIgnorable(code))
                continue;
            preceded = IsCased(code);
            break;
        }
        if (!preceded)
            return false;

        for (var next = index + 1; next < codes.Length; next++)
        {
            var code = codes[next];
            if (IsCaseIgnorable(code))
                continue;
            return !IsCased(code);
        }
        return true;
    }

    /// <summary>
    /// The Unicode `Cased` property, read from the pinned data. The platform's own categories
    /// cannot answer it: `Other_Lowercase` characters such as the modifier letters are not
    /// `Ll`, and a mapping table does not carry them either.
    /// </summary>
    internal static bool IsCased(int codePoint) => Mappings.Value.IsCased(codePoint);

    /// <summary>The Unicode `Case_Ignorable` property, which the sigma rule skips over.</summary>
    private static bool IsCaseIgnorable(int codePoint) => Mappings.Value.IsCaseIgnorable(codePoint);

    private enum Kind
    {
        /// <summary>No mapping at all: the character is appended unchanged.</summary>
        None,
        Upper,
        Lower,
        Title,
        Fold,
    }

    private static CaseTable Load()
    {
        using var stream =
            typeof(PythonUnicodeCase).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Unicode case resource is missing.");
        if (stream.Length is < 32 or > 4_000_000)
            throw new InvalidDataException("The Unicode case resource has an invalid size.");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return new CaseTable(data);
    }

    private sealed class CaseTable
    {
        private const int HeaderSize = 46;
        private const int RecordSize = 8;
        private const int RangeSize = 8;

        private readonly byte[] _data;
        private readonly int[] _counts = new int[4];
        private readonly int[] _starts = new int[4];
        private readonly int[][] _rangeSets;
        private readonly int _payloadStart;

        internal CaseTable(byte[] data)
        {
            _data = data;
            if (!data.AsSpan(0, 10).SequenceEqual("DPYCASE16\0"u8))
                throw new InvalidDataException("The Unicode case resource has an invalid version.");
            var payloadLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(26));
            var start = HeaderSize;
            for (var kind = 0; kind < 4; kind++)
            {
                _counts[kind] = (int)
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10 + kind * 4));
                _starts[kind] = start;
                start += _counts[kind] * RecordSize;
            }
            _payloadStart = start;
            var at = _payloadStart + payloadLength * 4;
            // The property sets follow the mapping payload in the order the writer emits them:
            // Cased, Case_Ignorable, Lowercase, Uppercase.
            _rangeSets = new int[4][];
            for (var set = 0; set < 4; set++)
            {
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30 + set * 4));
                _rangeSets[set] = new int[count * 2];
                for (var index = 0; index < count; index++)
                {
                    _rangeSets[set][index * 2] = (int)
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    _rangeSets[set][index * 2 + 1] = (int)
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
                    at += RangeSize;
                }
            }
            if (at != data.Length)
                throw new InvalidDataException("The Unicode case resource is truncated.");
        }

        internal bool IsCased(int codePoint) => InRanges(_rangeSets[0], codePoint);

        internal bool IsCaseIgnorable(int codePoint) => InRanges(_rangeSets[1], codePoint);

        internal bool IsLower(int codePoint) => InRanges(_rangeSets[2], codePoint);

        internal bool IsUpper(int codePoint) => InRanges(_rangeSets[3], codePoint);

        private static bool InRanges(int[] ranges, int codePoint)
        {
            var low = 0;
            var high = ranges.Length / 2 - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (codePoint < ranges[middle * 2])
                    high = middle - 1;
                else if (codePoint > ranges[middle * 2 + 1])
                    low = middle + 1;
                else
                    return true;
            }
            return false;
        }

        /// <summary>The mapping for a code point, or null when it maps to itself.</summary>
        internal int[]? Lookup(int codePoint, Kind kind)
        {
            var index = kind switch
            {
                Kind.Upper => 0,
                Kind.Lower => 1,
                Kind.Title => 2,
                _ => 3,
            };
            if (kind == Kind.None)
                return null;
            var low = 0;
            var high = _counts[index] - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var at = _starts[index] + middle * RecordSize;
                var candidate = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at));
                if (candidate == codePoint)
                {
                    var raw = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at + 4));
                    if ((raw & 0x80000000) == 0)
                        return [(int)raw];
                    var length = (int)((raw >> 24) & 0x7f);
                    var offset = (int)(raw & 0x00FFFFFF);
                    var mapped = new int[length];
                    for (var position = 0; position < length; position++)
                        mapped[position] = (int)
                            BinaryPrimitives.ReadUInt32LittleEndian(
                                _data.AsSpan(_payloadStart + (offset + position) * 4)
                            );
                    return mapped;
                }
                if (candidate < codePoint)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return null;
        }

        internal void Append(StringBuilder builder, int codePoint, Kind kind)
        {
            var mapped = Lookup(codePoint, kind);
            if (mapped is null)
            {
                AppendCodePoint(builder, codePoint);
                return;
            }
            foreach (var item in mapped)
                AppendCodePoint(builder, item);
        }

        /// <summary>`ConvertFromUtf32` refuses a surrogate, which a string may still hold.</summary>
        private static void AppendCodePoint(StringBuilder builder, int codePoint)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF)
                builder.Append((char)codePoint);
            else
                builder.Append(char.ConvertFromUtf32(codePoint));
        }
    }
}
