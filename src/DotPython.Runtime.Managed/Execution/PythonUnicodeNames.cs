using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>CPython 3.14 character names from the pinned Unicode 16 data resource.</summary>
internal static class PythonUnicodeNames
{
    private const string ResourceName = "DotPython.UnicodeNames16";
    private static readonly Lazy<NameTable> Names = new(Load);

    internal static string? GetName(int codePoint, bool includeAliasesAndSequences = false) =>
        codePoint is < 0 or > 0x10ffff
        || !includeAliasesAndSequences && codePoint is >= 0xf0000 and <= 0xffffd
            ? null
            : Names.Value.GetName(codePoint);

    private static NameTable Load()
    {
        // This fixed, embedded asset needs no ambient file access. Loading does
        // not invoke execution callbacks: cancellation cannot poison the shared
        // Lazy. The encoder checks current work before and after using names.
        using var stream =
            typeof(PythonUnicodeNames).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Unicode name resource is missing.");
        if (stream.Length is < 16 or > 4_000_000)
            throw new InvalidDataException("The Unicode name resource has an invalid size.");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return new NameTable(data);
    }

    private sealed class NameTable
    {
        private const int HeaderSize = 16;
        private const int NameRecordSize = 12;
        private const int RangeRecordSize = 16;
        private readonly byte[] _data;
        private readonly int _nameCount;
        private readonly int _rangeCount;
        private readonly int _rangesStart;
        private readonly int _namesStart;

        internal NameTable(byte[] data)
        {
            _data = data;
            if (!data.AsSpan(0, 8).SequenceEqual("DPYUN16\0"u8))
                throw new InvalidDataException("The Unicode name resource has an invalid version.");
            _nameCount = ReadInt(8);
            _rangeCount = ReadInt(12);
            if (_nameCount is < 0 or > 200_000 || _rangeCount is < 0 or > 1_000)
                throw new InvalidDataException("The Unicode name resource has invalid counts.");
            _rangesStart = HeaderSize + _nameCount * NameRecordSize;
            _namesStart = _rangesStart + _rangeCount * RangeRecordSize;
            if (_namesStart > data.Length)
                throw new InvalidDataException("The Unicode name resource is truncated.");
        }

        internal string? GetName(int codePoint)
        {
            var low = 0;
            var high = _nameCount - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var record = HeaderSize + middle * NameRecordSize;
                var candidate = ReadInt(record);
                if (candidate < codePoint)
                    low = middle + 1;
                else if (candidate > codePoint)
                    high = middle - 1;
                else
                    return ReadName(record + 4);
            }
            low = 0;
            high = _rangeCount - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var record = _rangesStart + middle * RangeRecordSize;
                if (ReadInt(record + 4) < codePoint)
                    low = middle + 1;
                else if (ReadInt(record) > codePoint)
                    high = middle - 1;
                else
                    return ReadName(record + 8)
                        + codePoint.ToString("X4", CultureInfo.InvariantCulture);
            }
            return null;
        }

        private int ReadInt(int offset) =>
            BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, 4));

        private string ReadName(int record)
        {
            var offset = ReadInt(record);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(record + 4, 2));
            if (offset < _namesStart || offset > _data.Length - length || length == 0)
                throw new InvalidDataException("The Unicode name resource has an invalid name.");
            return Encoding.ASCII.GetString(_data, offset, length);
        }
    }
}
