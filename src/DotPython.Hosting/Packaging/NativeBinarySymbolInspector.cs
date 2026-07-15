using System.Buffers.Binary;
using System.Text;

namespace DotPython.Hosting.Packaging;

internal static class NativeBinarySymbolInspector
{
    private const uint ElfSectionDynamicSymbols = 11;
    private const uint ElfSectionSymbols = 2;
    private const uint MachSymbolTableCommand = 2;

    internal static NativeBinaryInspection Inspect(ReadOnlySpan<byte> bytes)
    {
        if (
            bytes.Length >= 4
            && bytes[0] == 0x7f
            && bytes[1] == (byte)'E'
            && bytes[2] == (byte)'L'
            && bytes[3] == (byte)'F'
        )
        {
            return new NativeBinaryInspection(
                PythonNativeBinaryFormat.Elf,
                TryReadElfSymbols(bytes, out var symbols),
                symbols
            );
        }

        if (IsMachO(bytes))
        {
            return new NativeBinaryInspection(
                PythonNativeBinaryFormat.MachO,
                TryReadMachOSymbols(bytes, out var symbols),
                symbols
            );
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
        {
            return new NativeBinaryInspection(
                PythonNativeBinaryFormat.PortableExecutable,
                TryReadPortableExecutableSymbols(bytes, out var symbols),
                symbols
            );
        }

        return new NativeBinaryInspection(PythonNativeBinaryFormat.Unknown, false, []);
    }

    private static bool TryReadElfSymbols(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<string> symbols
    )
    {
        symbols = [];
        if (bytes.Length < 52 || bytes[5] is not (1 or 2))
        {
            return false;
        }

        var is64Bit = bytes[4] == 2;
        if (!is64Bit && bytes[4] != 1)
        {
            return false;
        }

        var littleEndian = bytes[5] == 1;
        if (
            !TryReadUnsigned(
                bytes,
                is64Bit ? 40UL : 32UL,
                is64Bit ? 8 : 4,
                littleEndian,
                out var sectionTableOffset
            )
            || !TryReadUnsigned(
                bytes,
                is64Bit ? 58UL : 46UL,
                2,
                littleEndian,
                out var sectionEntrySize
            )
            || !TryReadUnsigned(bytes, is64Bit ? 60UL : 48UL, 2, littleEndian, out var sectionCount)
            || sectionEntrySize < (is64Bit ? 64UL : 40UL)
            || sectionCount == 0
            || sectionCount > 65_535
        )
        {
            return false;
        }

        var result = new SortedSet<string>(StringComparer.Ordinal);
        for (ulong sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            if (
                !TryAdd(sectionTableOffset, sectionIndex * sectionEntrySize, out var sectionOffset)
                || !TryReadUnsigned(bytes, sectionOffset + 4, 4, littleEndian, out var sectionType)
                || sectionType is not (ElfSectionDynamicSymbols or ElfSectionSymbols)
            )
            {
                continue;
            }

            if (
                !TryReadUnsigned(
                    bytes,
                    sectionOffset + (is64Bit ? 24UL : 16UL),
                    is64Bit ? 8 : 4,
                    littleEndian,
                    out var symbolTableOffset
                )
                || !TryReadUnsigned(
                    bytes,
                    sectionOffset + (is64Bit ? 32UL : 20UL),
                    is64Bit ? 8 : 4,
                    littleEndian,
                    out var symbolTableSize
                )
                || !TryReadUnsigned(
                    bytes,
                    sectionOffset + (is64Bit ? 40UL : 24UL),
                    4,
                    littleEndian,
                    out var stringSectionIndex
                )
                || !TryReadUnsigned(
                    bytes,
                    sectionOffset + (is64Bit ? 56UL : 36UL),
                    is64Bit ? 8 : 4,
                    littleEndian,
                    out var symbolEntrySize
                )
                || symbolEntrySize < (is64Bit ? 24UL : 16UL)
                || stringSectionIndex >= sectionCount
                || !TryAdd(
                    sectionTableOffset,
                    stringSectionIndex * sectionEntrySize,
                    out var stringSectionOffset
                )
                || !TryReadUnsigned(
                    bytes,
                    stringSectionOffset + (is64Bit ? 24UL : 16UL),
                    is64Bit ? 8 : 4,
                    littleEndian,
                    out var stringTableOffset
                )
                || !TryReadUnsigned(
                    bytes,
                    stringSectionOffset + (is64Bit ? 32UL : 20UL),
                    is64Bit ? 8 : 4,
                    littleEndian,
                    out var stringTableSize
                )
                || !ContainsRange(bytes, symbolTableOffset, symbolTableSize)
                || !ContainsRange(bytes, stringTableOffset, stringTableSize)
            )
            {
                return false;
            }

            var symbolCount = symbolTableSize / symbolEntrySize;
            if (symbolCount > 1_000_000)
            {
                return false;
            }

            for (ulong symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
            {
                var symbolOffset = symbolTableOffset + (symbolIndex * symbolEntrySize);
                if (
                    !TryReadUnsigned(bytes, symbolOffset, 4, littleEndian, out var nameOffset)
                    || !TryReadUnsigned(
                        bytes,
                        symbolOffset + (is64Bit ? 6UL : 14UL),
                        2,
                        littleEndian,
                        out var sectionNumber
                    )
                    || sectionNumber != 0
                    || nameOffset == 0
                    || nameOffset >= stringTableSize
                    || !TryReadNullTerminatedAscii(
                        bytes,
                        stringTableOffset + nameOffset,
                        stringTableOffset + stringTableSize,
                        out var symbol
                    )
                )
                {
                    continue;
                }

                result.Add(symbol);
            }
        }

        symbols = [.. result];
        return true;
    }

    private static bool IsMachO(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        var bigEndianMagic = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return bigEndianMagic
            is 0xfeedface
                or 0xfeedfacf
                or 0xcefaedfe
                or 0xcffaedfe
                or 0xcafebabe
                or 0xcafebabf;
    }

    private static bool TryReadMachOSymbols(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<string> symbols
    )
    {
        symbols = [];
        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (magic is 0xcafebabe or 0xcafebabf)
        {
            return TryReadFatMachOSymbols(bytes, magic == 0xcafebabf, out symbols);
        }

        var littleEndian = magic is 0xcefaedfe or 0xcffaedfe;
        var is64Bit = magic is 0xfeedfacf or 0xcffaedfe;
        var headerSize = is64Bit ? 32UL : 28UL;
        if (
            bytes.Length < (int)headerSize
            || !TryReadUnsigned(bytes, 16, 4, littleEndian, out var commandCount)
            || commandCount > 65_535
        )
        {
            return false;
        }

        var commandOffset = headerSize;
        for (ulong commandIndex = 0; commandIndex < commandCount; commandIndex++)
        {
            if (
                !TryReadUnsigned(bytes, commandOffset, 4, littleEndian, out var command)
                || !TryReadUnsigned(bytes, commandOffset + 4, 4, littleEndian, out var commandSize)
                || commandSize < 8
                || !ContainsRange(bytes, commandOffset, commandSize)
            )
            {
                return false;
            }

            if (command == MachSymbolTableCommand)
            {
                if (
                    commandSize < 24
                    || !TryReadUnsigned(
                        bytes,
                        commandOffset + 8,
                        4,
                        littleEndian,
                        out var symbolTableOffset
                    )
                    || !TryReadUnsigned(
                        bytes,
                        commandOffset + 12,
                        4,
                        littleEndian,
                        out var symbolCount
                    )
                    || !TryReadUnsigned(
                        bytes,
                        commandOffset + 16,
                        4,
                        littleEndian,
                        out var stringTableOffset
                    )
                    || !TryReadUnsigned(
                        bytes,
                        commandOffset + 20,
                        4,
                        littleEndian,
                        out var stringTableSize
                    )
                )
                {
                    return false;
                }

                return TryReadMachOSymbolTable(
                    bytes,
                    littleEndian,
                    is64Bit,
                    symbolTableOffset,
                    symbolCount,
                    stringTableOffset,
                    stringTableSize,
                    out symbols
                );
            }

            commandOffset += commandSize;
        }

        return false;
    }

    private static bool TryReadFatMachOSymbols(
        ReadOnlySpan<byte> bytes,
        bool is64Bit,
        out IReadOnlyList<string> symbols
    )
    {
        symbols = [];
        if (!TryReadUnsigned(bytes, 4, 4, littleEndian: false, out var architectureCount))
        {
            return false;
        }

        var entrySize = is64Bit ? 32UL : 20UL;
        var result = new SortedSet<string>(StringComparer.Ordinal);
        for (ulong index = 0; index < architectureCount; index++)
        {
            var entryOffset = 8UL + (index * entrySize);
            if (
                !TryReadUnsigned(
                    bytes,
                    entryOffset + 8,
                    is64Bit ? 8 : 4,
                    littleEndian: false,
                    out var sliceOffset
                )
                || !TryReadUnsigned(
                    bytes,
                    entryOffset + (is64Bit ? 16UL : 12UL),
                    is64Bit ? 8 : 4,
                    littleEndian: false,
                    out var sliceSize
                )
                || !ContainsRange(bytes, sliceOffset, sliceSize)
                || sliceSize > int.MaxValue
                || !TryReadMachOSymbols(
                    bytes.Slice((int)sliceOffset, (int)sliceSize),
                    out var sliceSymbols
                )
            )
            {
                return false;
            }

            result.UnionWith(sliceSymbols);
        }

        symbols = [.. result];
        return true;
    }

    private static bool TryReadMachOSymbolTable(
        ReadOnlySpan<byte> bytes,
        bool littleEndian,
        bool is64Bit,
        ulong symbolTableOffset,
        ulong symbolCount,
        ulong stringTableOffset,
        ulong stringTableSize,
        out IReadOnlyList<string> symbols
    )
    {
        symbols = [];
        var entrySize = is64Bit ? 16UL : 12UL;
        if (
            symbolCount > 1_000_000
            || !ContainsRange(bytes, symbolTableOffset, symbolCount * entrySize)
            || !ContainsRange(bytes, stringTableOffset, stringTableSize)
        )
        {
            return false;
        }

        var result = new SortedSet<string>(StringComparer.Ordinal);
        for (ulong index = 0; index < symbolCount; index++)
        {
            var symbolOffset = symbolTableOffset + (index * entrySize);
            if (
                !TryReadUnsigned(bytes, symbolOffset, 4, littleEndian, out var nameOffset)
                || nameOffset == 0
                || nameOffset >= stringTableSize
                || !TryReadByte(bytes, symbolOffset + 4, out var type)
                || (type & 0x01) == 0
                || (type & 0x0e) != 0
                || !TryReadNullTerminatedAscii(
                    bytes,
                    stringTableOffset + nameOffset,
                    stringTableOffset + stringTableSize,
                    out var symbol
                )
            )
            {
                continue;
            }

            if (
                symbol.Length > 1
                && symbol[0] == '_'
                && (symbol.AsSpan(1).StartsWith("Py") || symbol.AsSpan(1).StartsWith("_Py"))
            )
            {
                symbol = symbol[1..];
            }

            result.Add(symbol);
        }

        symbols = [.. result];
        return true;
    }

    private static bool TryReadPortableExecutableSymbols(
        ReadOnlySpan<byte> bytes,
        out IReadOnlyList<string> symbols
    )
    {
        symbols = [];
        if (
            !TryReadUnsigned(bytes, 0x3c, 4, littleEndian: true, out var peOffset)
            || !ContainsRange(bytes, peOffset, 24)
            || !TryReadUnsigned(bytes, peOffset, 4, littleEndian: true, out var signature)
            || signature != 0x00004550
            || !TryReadUnsigned(bytes, peOffset + 6, 2, true, out var sectionCount)
            || !TryReadUnsigned(bytes, peOffset + 20, 2, true, out var optionalHeaderSize)
        )
        {
            return false;
        }

        var optionalHeaderOffset = peOffset + 24;
        if (
            !TryReadUnsigned(bytes, optionalHeaderOffset, 2, true, out var optionalMagic)
            || optionalMagic is not (0x10b or 0x20b)
        )
        {
            return false;
        }

        var is64Bit = optionalMagic == 0x20b;
        var dataDirectoryOffset = optionalHeaderOffset + (is64Bit ? 112UL : 96UL);
        if (
            !TryReadUnsigned(bytes, dataDirectoryOffset + 8, 4, true, out var importRva)
            || !TryReadUnsigned(bytes, dataDirectoryOffset + 12, 4, true, out var importSize)
            || importRva == 0
            || importSize == 0
        )
        {
            return false;
        }

        var sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
        if (
            !TryMapPortableExecutableRva(
                bytes,
                sectionTableOffset,
                sectionCount,
                importRva,
                out var importOffset
            )
        )
        {
            return false;
        }

        var result = new SortedSet<string>(StringComparer.Ordinal);
        for (ulong descriptorIndex = 0; descriptorIndex < 65_536; descriptorIndex++)
        {
            var descriptorOffset = importOffset + (descriptorIndex * 20);
            if (!ContainsRange(bytes, descriptorOffset, 20))
            {
                return false;
            }

            if (
                !TryReadUnsigned(bytes, descriptorOffset, 4, true, out var originalThunkRva)
                || !TryReadUnsigned(bytes, descriptorOffset + 12, 4, true, out var nameRva)
                || !TryReadUnsigned(bytes, descriptorOffset + 16, 4, true, out var firstThunkRva)
            )
            {
                return false;
            }

            if (originalThunkRva == 0 && nameRva == 0 && firstThunkRva == 0)
            {
                symbols = [.. result];
                return true;
            }

            var thunkRva = originalThunkRva == 0 ? firstThunkRva : originalThunkRva;
            if (
                !TryMapPortableExecutableRva(
                    bytes,
                    sectionTableOffset,
                    sectionCount,
                    thunkRva,
                    out var thunkOffset
                )
            )
            {
                return false;
            }

            var thunkSize = is64Bit ? 8UL : 4UL;
            var ordinalMask = is64Bit ? 0x8000000000000000UL : 0x80000000UL;
            for (ulong thunkIndex = 0; thunkIndex < 1_000_000; thunkIndex++)
            {
                if (
                    !TryReadUnsigned(
                        bytes,
                        thunkOffset + (thunkIndex * thunkSize),
                        (int)thunkSize,
                        true,
                        out var thunkValue
                    )
                )
                {
                    return false;
                }

                if (thunkValue == 0)
                {
                    break;
                }

                if ((thunkValue & ordinalMask) != 0)
                {
                    result.Add($"#{thunkValue & 0xffff}");
                    continue;
                }

                if (
                    !TryMapPortableExecutableRva(
                        bytes,
                        sectionTableOffset,
                        sectionCount,
                        thunkValue,
                        out var importNameOffset
                    )
                    || !TryReadNullTerminatedAscii(
                        bytes,
                        importNameOffset + 2,
                        (ulong)bytes.Length,
                        out var symbol
                    )
                )
                {
                    return false;
                }

                result.Add(symbol);
            }
        }

        return false;
    }

    private static bool TryMapPortableExecutableRva(
        ReadOnlySpan<byte> bytes,
        ulong sectionTableOffset,
        ulong sectionCount,
        ulong rva,
        out ulong fileOffset
    )
    {
        fileOffset = 0;
        for (ulong index = 0; index < sectionCount; index++)
        {
            var sectionOffset = sectionTableOffset + (index * 40);
            if (
                !TryReadUnsigned(bytes, sectionOffset + 8, 4, true, out var virtualSize)
                || !TryReadUnsigned(bytes, sectionOffset + 12, 4, true, out var virtualAddress)
                || !TryReadUnsigned(bytes, sectionOffset + 16, 4, true, out var rawSize)
                || !TryReadUnsigned(bytes, sectionOffset + 20, 4, true, out var rawOffset)
            )
            {
                return false;
            }

            var mappedSize = Math.Max(virtualSize, rawSize);
            if (rva < virtualAddress || rva - virtualAddress >= mappedSize)
            {
                continue;
            }

            fileOffset = rawOffset + (rva - virtualAddress);
            return fileOffset < (ulong)bytes.Length;
        }

        return false;
    }

    private static bool TryReadUnsigned(
        ReadOnlySpan<byte> bytes,
        ulong offset,
        int size,
        bool littleEndian,
        out ulong value
    )
    {
        value = 0;
        if (!ContainsRange(bytes, offset, (ulong)size))
        {
            return false;
        }

        var slice = bytes.Slice((int)offset, size);
        value = size switch
        {
            2 => littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(slice)
                : BinaryPrimitives.ReadUInt16BigEndian(slice),
            4 => littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
                : BinaryPrimitives.ReadUInt32BigEndian(slice),
            8 => littleEndian
                ? BinaryPrimitives.ReadUInt64LittleEndian(slice)
                : BinaryPrimitives.ReadUInt64BigEndian(slice),
            _ => 0,
        };
        return size is 2 or 4 or 8;
    }

    private static bool TryReadByte(ReadOnlySpan<byte> bytes, ulong offset, out byte value)
    {
        value = 0;
        if (offset >= (ulong)bytes.Length)
        {
            return false;
        }

        value = bytes[(int)offset];
        return true;
    }

    private static bool TryReadNullTerminatedAscii(
        ReadOnlySpan<byte> bytes,
        ulong offset,
        ulong limit,
        out string value
    )
    {
        value = string.Empty;
        if (offset >= limit || limit > (ulong)bytes.Length || offset > int.MaxValue)
        {
            return false;
        }

        var maximumLength = (int)Math.Min(limit - offset, 16_384UL);
        var source = bytes.Slice((int)offset, maximumLength);
        var terminator = source.IndexOf((byte)0);
        if (terminator <= 0)
        {
            return false;
        }

        foreach (var item in source[..terminator])
        {
            if (item is < 0x20 or > 0x7e)
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(source[..terminator]);
        return true;
    }

    private static bool ContainsRange(ReadOnlySpan<byte> bytes, ulong offset, ulong length) =>
        offset <= (ulong)bytes.Length && length <= (ulong)bytes.Length - offset;

    private static bool TryAdd(ulong left, ulong right, out ulong result)
    {
        result = left + right;
        return result >= left;
    }
}

internal sealed record NativeBinaryInspection(
    PythonNativeBinaryFormat Format,
    bool SymbolsAvailable,
    IReadOnlyList<string> RequiredSymbols
);
