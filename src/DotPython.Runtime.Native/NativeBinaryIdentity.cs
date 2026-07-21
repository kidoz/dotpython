using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DotPython.Runtime.Native;

internal enum NativeBinaryFormat
{
    Elf,
    MachO,
    PortableExecutable,
}

internal sealed record NativeBinaryIdentity(NativeBinaryFormat Format, Architecture Architecture)
{
    internal static NativeBinaryIdentity Read(string path)
    {
        Span<byte> header = stackalloc byte[4096];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.SequentialScan
        );
        var length = stream.Read(header);
        var bytes = header[..length];
        if (
            bytes.Length >= 20
            && bytes[0] == 0x7f
            && bytes[1] == (byte)'E'
            && bytes[2] == (byte)'L'
            && bytes[3] == (byte)'F'
        )
        {
            var littleEndian = bytes[5] == 1;
            if (!littleEndian && bytes[5] != 2)
            {
                throw Unsupported(path, "ELF endianness is invalid.");
            }

            var machine = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..])
                : BinaryPrimitives.ReadUInt16BigEndian(bytes[18..]);
            return new NativeBinaryIdentity(NativeBinaryFormat.Elf, MapElf(machine, path));
        }

        if (bytes.Length >= 8)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (magic is 0xfeedface or 0xfeedfacf)
            {
                var cpu = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
                return new NativeBinaryIdentity(NativeBinaryFormat.MachO, MapMachO(cpu, path));
            }
        }

        if (bytes.Length >= 64 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
        {
            var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x3c..]);
            if (
                peOffset > int.MaxValue - 6
                || peOffset + 6 > bytes.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)peOffset..]) != 0x00004550
            )
            {
                throw Unsupported(path, "PE header is invalid or outside the bounded preflight.");
            }

            var machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes[((int)peOffset + 4)..]);
            return new NativeBinaryIdentity(
                NativeBinaryFormat.PortableExecutable,
                MapPortableExecutable(machine, path)
            );
        }

        throw Unsupported(path, "Native binary format is not ELF, Mach-O, or PE.");
    }

    internal void ValidateCurrentPlatform(string path)
    {
        var expectedFormat =
            OperatingSystem.IsWindows() ? NativeBinaryFormat.PortableExecutable
            : OperatingSystem.IsMacOS() ? NativeBinaryFormat.MachO
            : NativeBinaryFormat.Elf;
        if (Format != expectedFormat || Architecture != RuntimeInformation.ProcessArchitecture)
        {
            throw new StableAbiLoadException(
                "DPY8002",
                StableAbiLoadPhase.Architecture,
                $"Native artifact '{Path.GetFileName(path)}' targets {Format}/{Architecture}; "
                    + $"the worker requires {expectedFormat}/{RuntimeInformation.ProcessArchitecture}.",
                path,
                artifactSha256: null,
                missingSymbol: null
            );
        }
    }

    private static Architecture MapElf(ushort machine, string path) =>
        machine switch
        {
            62 => Architecture.X64,
            183 => Architecture.Arm64,
            _ => throw Unsupported(path, $"ELF machine {machine} is unsupported."),
        };

    private static Architecture MapMachO(uint cpu, string path) =>
        cpu switch
        {
            0x01000007 => Architecture.X64,
            0x0100000c => Architecture.Arm64,
            _ => throw Unsupported(path, $"Mach-O CPU {cpu} is unsupported."),
        };

    private static Architecture MapPortableExecutable(ushort machine, string path) =>
        machine switch
        {
            0x8664 => Architecture.X64,
            0xaa64 => Architecture.Arm64,
            _ => throw Unsupported(path, $"PE machine {machine} is unsupported."),
        };

    private static StableAbiLoadException Unsupported(string path, string message) =>
        new(
            "DPY8002",
            StableAbiLoadPhase.Architecture,
            message,
            path,
            artifactSha256: null,
            missingSymbol: null
        );
}
