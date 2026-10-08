using System.Buffers.Binary;
using System.Globalization;

namespace DotPython.Compiler;

/// <summary>
/// Name-to-codepoint lookup for the `\N{NAME}` string escape, read from the pinned
/// Unicode 16 reverse index generated beside the runtime's forward name table.
/// </summary>
/// <remarks>
/// The index is a deliberately different projection from the runtime's namereplace
/// table: it carries ordinary names and aliases at their real codepoints, restores the
/// Tangut ranges the ordinary table omits, and leaves out named sequences, which
/// `unicodedata.lookup` resolves but the escape decoder does not.
/// </remarks>
internal static class PythonUnicodeNameIndex
{
    private const string ResourceName = "DotPython.UnicodeNameIndex16";
    private static readonly Lazy<IndexTable> Index = new(Load);

    /// <summary>
    /// Resolves a name as `\N{NAME}` does: exact and case-insensitive, with no folding
    /// of underscores, hyphens or spaces, plus the algorithmic `PREFIX-XXXX` forms.
    /// </summary>
    internal static bool TryGetCodePoint(string name, out int codePoint) =>
        Index.Value.TryGetCodePoint(name, out codePoint);

    private static IndexTable Load()
    {
        using var stream =
            typeof(PythonUnicodeNameIndex).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Unicode name index resource is missing.");
        if (stream.Length is < 16 or > 4_000_000)
            throw new InvalidDataException("The Unicode name index resource has an invalid size.");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return new IndexTable(data);
    }

    private sealed class IndexTable
    {
        private const int HeaderSize = 16;
        private const int NameRecordSize = 12;
        private const int RangeRecordSize = 16;
        private readonly byte[] _data;
        private readonly int _nameCount;
        private readonly int _rangeCount;
        private readonly int _rangesStart;
        private readonly int _namesStart;

        internal IndexTable(byte[] data)
        {
            _data = data;
            if (!data.AsSpan(0, 8).SequenceEqual("DPYNIX16"u8))
                throw new InvalidDataException("The Unicode name index has an invalid version.");
            _nameCount = ReadInt(8);
            _rangeCount = ReadInt(12);
            if (_nameCount is < 0 or > 200_000 || _rangeCount is < 0 or > 1_000)
                throw new InvalidDataException("The Unicode name index has invalid counts.");
            _rangesStart = HeaderSize + _nameCount * NameRecordSize;
            _namesStart = _rangesStart + _rangeCount * RangeRecordSize;
            if (_namesStart > data.Length)
                throw new InvalidDataException("The Unicode name index is truncated.");
        }

        internal bool TryGetCodePoint(string name, out int codePoint)
        {
            var upper = name.ToUpperInvariant();
            var low = 0;
            var high = _nameCount - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var record = HeaderSize + middle * NameRecordSize;
                var comparison = string.CompareOrdinal(ReadName(record + 4), upper);
                if (comparison < 0)
                    low = middle + 1;
                else if (comparison > 0)
                    high = middle - 1;
                else
                {
                    codePoint = ReadInt(record);
                    return true;
                }
            }

            for (var index = 0; index < _rangeCount; index++)
            {
                var record = _rangesStart + index * RangeRecordSize;
                var prefix = ReadName(record + 8);
                if (
                    !upper.StartsWith(prefix, StringComparison.Ordinal)
                    || !TryParseHexSuffix(upper, prefix.Length, out var candidate)
                    || candidate < ReadInt(record)
                    || candidate > ReadInt(record + 4)
                )
                {
                    continue;
                }

                codePoint = candidate;
                return true;
            }

            codePoint = 0;
            return false;
        }

        /// <summary>Parses the `XXXX` in an algorithmic name such as `CJK UNIFIED IDEOGRAPH-4E00`.</summary>
        private static bool TryParseHexSuffix(string name, int start, out int value)
        {
            value = 0;
            var digits = name.Length - start;
            if (digits is < 4 or > 6)
            {
                return false;
            }

            for (var index = start; index < name.Length; index++)
            {
                var digit = name[index] switch
                {
                    >= '0' and <= '9' => name[index] - '0',
                    >= 'A' and <= 'F' => name[index] - 'A' + 10,
                    _ => -1,
                };
                if (digit < 0)
                {
                    return false;
                }

                value = value * 16 + digit;
            }

            return value <= 0x10FFFF;
        }

        private int ReadInt(int offset) =>
            BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, 4));

        private string ReadName(int record)
        {
            var offset = ReadInt(record);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(record + 4, 2));
            if (offset < _namesStart || offset > _data.Length - length || length == 0)
                throw new InvalidDataException("The Unicode name index has an invalid name.");
            return System.Text.Encoding.ASCII.GetString(_data, offset, length);
        }
    }
}
