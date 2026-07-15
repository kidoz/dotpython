using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using DotPython.Hosting.Packaging;
using Xunit;

namespace DotPython.PackageCompatibilityTests;

public sealed class PythonWheelInspectorTests
{
    [Fact]
    public void Inspect_ClassifiesPurePythonAndExpandsCompressedTags()
    {
        using var wheel = WheelFixture.Create(
            "sample-1.0-py2.py3-none-any.whl",
            "sample",
            "1.0",
            ["py2-none-any", "py3-none-any"]
        );

        var result = PythonWheelInspector.Inspect(wheel.Path);

        Assert.True(result.IsValid);
        Assert.Equal(PythonWheelCompatibilityKind.PurePython, result.Compatibility);
        Assert.Equal(["py2-none-any", "py3-none-any"], result.Tags.Select(tag => tag.Value));
        var platform = Assert.Single(result.Platforms);
        Assert.Equal(PythonWheelPlatformFamily.Any, platform.Family);
        Assert.Equal("any", platform.Architecture);
        Assert.Empty(result.NativeBinaries);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(wheel.Sha256, result.Sha256);
    }

    [Fact]
    public void Inspect_ClassifiesAnyverShapedStableAbiWithoutClaimingExecutionSupport()
    {
        using var wheel = WheelFixture.Create(
            "anyver-1.1.0-cp311-abi3-macosx_11_0_arm64.whl",
            "anyver",
            "1.1.0",
            ["cp311-abi3-macosx_11_0_arm64"],
            new WheelEntry("anyver/_anyver.abi3.so", [0, 1, 2, 3])
        );

        var result = PythonWheelInspector.Inspect(wheel.Path);

        Assert.True(result.IsValid);
        Assert.Equal(PythonWheelCompatibilityKind.CpythonStableAbi, result.Compatibility);
        Assert.False(result.IsFreeThreaded);
        var platform = Assert.Single(result.Platforms);
        Assert.Equal(PythonWheelPlatformFamily.MacOS, platform.Family);
        Assert.Equal("arm64", platform.Architecture);
        Assert.Equal(new Version(11, 0), platform.MinimumVersion);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code == PythonWheelDiagnosticCode.NativeRuntimeUnsupported
                && diagnostic.Severity == PythonWheelDiagnosticSeverity.Warning
        );
        Assert.False(
            DotPython.Runtime.Managed.ManagedRuntimeDescriptor.Compatibility.SupportsCpythonAbi
        );
    }

    [Fact]
    public void Inspect_ClassifiesFreeThreadedCpythonWheelAndMuslBaseline()
    {
        using var wheel = WheelFixture.Create(
            "numpy-2.5.1-cp314-cp314t-musllinux_1_2_aarch64.whl",
            "numpy",
            "2.5.1",
            ["cp314-cp314t-musllinux_1_2_aarch64"],
            new WheelEntry("numpy/_core.so", [0, 1, 2, 3])
        );

        var result = PythonWheelInspector.Inspect(wheel.Path);

        Assert.True(result.IsValid);
        Assert.Equal(PythonWheelCompatibilityKind.CpythonVersionSpecific, result.Compatibility);
        Assert.True(result.IsFreeThreaded);
        var platform = Assert.Single(result.Platforms);
        Assert.Equal(PythonWheelPlatformFamily.Musllinux, platform.Family);
        Assert.Equal("musl", platform.Libc);
        Assert.Equal("aarch64", platform.Architecture);
        Assert.Equal(new Version(1, 2), platform.MinimumVersion);
    }

    [Fact]
    public void Inspect_PrefersHpyUniversalClassificationFromNativeModuleName()
    {
        using var wheel = WheelFixture.Create(
            "sample-1.0-py3-none-manylinux_2_17_x86_64.whl",
            "sample",
            "1.0",
            ["py3-none-manylinux_2_17_x86_64"],
            new WheelEntry("sample/native.hpy0.so", [0, 1, 2, 3])
        );

        var result = PythonWheelInspector.Inspect(wheel.Path);

        Assert.True(result.IsValid);
        Assert.Equal(PythonWheelCompatibilityKind.HpyUniversal, result.Compatibility);
        Assert.True(Assert.Single(result.NativeBinaries).IsHpyUniversal);
        var platform = Assert.Single(result.Platforms);
        Assert.Equal(PythonWheelPlatformFamily.Manylinux, platform.Family);
        Assert.Equal("glibc", platform.Libc);
        Assert.Equal(new Version(2, 17), platform.MinimumVersion);
    }

    [Fact]
    public void Inspect_RecordsRequiredSymbolsFromElfDynamicSymbolTable()
    {
        using var wheel = WheelFixture.Create(
            "native-1.0-cp311-abi3-manylinux_2_17_x86_64.whl",
            "native",
            "1.0",
            ["cp311-abi3-manylinux_2_17_x86_64"],
            new WheelEntry("native/_native.abi3.so", CreateElfWithUndefinedSymbols())
        );

        var result = PythonWheelInspector.Inspect(wheel.Path);

        var binary = Assert.Single(result.NativeBinaries);
        Assert.Equal(PythonNativeBinaryFormat.Elf, binary.Format);
        Assert.Equal(["PyExc_ValueError", "PyLong_FromLong"], binary.RequiredSymbols);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == PythonWheelDiagnosticCode.NativeSymbolsUnavailable
        );
    }

    [Fact]
    public void Inspect_RecordsRequiredSymbolsFromMachODynamicSymbolTable()
    {
        using var wheel = WheelFixture.Create(
            "native-1.0-cp311-abi3-macosx_11_0_arm64.whl",
            "native",
            "1.0",
            ["cp311-abi3-macosx_11_0_arm64"],
            new WheelEntry("native/_native.abi3.so", CreateMachOWithUndefinedSymbols())
        );

        var binary = Assert.Single(PythonWheelInspector.Inspect(wheel.Path).NativeBinaries);

        Assert.Equal(PythonNativeBinaryFormat.MachO, binary.Format);
        Assert.Equal(["PyLong_FromLong", "_Py_NewRef"], binary.RequiredSymbols);
    }

    [Fact]
    public void Inspect_RecordsRequiredSymbolsFromPeImportTable()
    {
        using var wheel = WheelFixture.Create(
            "native-1.0-cp311-abi3-win_amd64.whl",
            "native",
            "1.0",
            ["cp311-abi3-win_amd64"],
            new WheelEntry("native/_native.pyd", CreatePeWithImportedSymbol())
        );

        var binary = Assert.Single(PythonWheelInspector.Inspect(wheel.Path).NativeBinaries);

        Assert.Equal(PythonNativeBinaryFormat.PortableExecutable, binary.Format);
        Assert.Equal(["PyLong_FromLong"], binary.RequiredSymbols);
    }

    [Fact]
    public void Inspect_RejectsMetadataMismatchAndDraftVariantFilename()
    {
        using var mismatch = WheelFixture.Create(
            "sample-1.0-py3-none-any.whl",
            "other",
            "1.0",
            ["py3-none-any"]
        );
        using var variant = WheelFixture.Create(
            "sample-1.0-cp314-cp314-macosx_14_0_arm64-arm64_v8.whl",
            "sample",
            "1.0",
            ["cp314-cp314-macosx_14_0_arm64"]
        );

        var mismatchResult = PythonWheelInspector.Inspect(mismatch.Path);
        var variantResult = PythonWheelInspector.Inspect(variant.Path);

        Assert.False(mismatchResult.IsValid);
        Assert.Equal(PythonWheelCompatibilityKind.Invalid, mismatchResult.Compatibility);
        Assert.Contains(
            mismatchResult.Diagnostics,
            diagnostic => diagnostic.Code == PythonWheelDiagnosticCode.MetadataMismatch
        );
        Assert.False(variantResult.IsValid);
        Assert.Contains(
            variantResult.Diagnostics,
            diagnostic => diagnostic.Code == PythonWheelDiagnosticCode.DraftVariantUnsupported
        );
    }

    [Fact]
    public void Inspect_RejectsMalformedZipAndBoundedEntryCount()
    {
        using var malformed = WheelFixture.CreateRaw("broken-1.0-py3-none-any.whl", [1, 2, 3]);
        using var wheel = WheelFixture.Create(
            "sample-1.0-py3-none-any.whl",
            "sample",
            "1.0",
            ["py3-none-any"]
        );

        var malformedResult = PythonWheelInspector.Inspect(malformed.Path);
        var boundedResult = PythonWheelInspector.Inspect(
            wheel.Path,
            new PythonWheelInspectionOptions { MaximumEntryCount = 2 }
        );
        var metadataBoundedResult = PythonWheelInspector.Inspect(
            wheel.Path,
            new PythonWheelInspectionOptions { MaximumMetadataBytes = 16 }
        );

        Assert.False(malformedResult.IsValid);
        Assert.Contains(
            malformedResult.Diagnostics,
            diagnostic => diagnostic.Code == PythonWheelDiagnosticCode.MalformedArchive
        );
        Assert.False(boundedResult.IsValid);
        Assert.Contains(
            boundedResult.Diagnostics,
            diagnostic => diagnostic.Code == PythonWheelDiagnosticCode.InspectionLimitExceeded
        );
        Assert.False(metadataBoundedResult.IsValid);
        Assert.Contains(
            metadataBoundedResult.Diagnostics,
            diagnostic => diagnostic.Code == PythonWheelDiagnosticCode.InspectionLimitExceeded
        );
    }

    [Fact]
    public void Inspect_RejectsUnsafeArchivePath()
    {
        using var wheel = WheelFixture.Create(
            "sample-1.0-py3-none-any.whl",
            "sample",
            "1.0",
            ["py3-none-any"],
            new WheelEntry("../escape.py", [1])
        );

        var result = PythonWheelInspector.Inspect(wheel.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == PythonWheelDiagnosticCode.UnsafeArchivePath
        );
    }

    [Fact]
    public void InspectionJson_IsDeterministicAndOmitsMachineLocalPath()
    {
        using var wheel = WheelFixture.Create(
            "sample-1.0-py3-none-any.whl",
            "sample",
            "1.0",
            ["py3-none-any"]
        );
        var inspection = PythonWheelInspector.Inspect(wheel.Path);

        var first = PythonWheelInspectionJson.Serialize(inspection);
        var second = PythonWheelInspectionJson.Serialize(inspection);

        Assert.Equal(first, second);
        Assert.Contains(
            "\"fileName\": \"sample-1.0-py3-none-any.whl\"",
            first,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(Path.GetDirectoryName(wheel.Path)!, first, StringComparison.Ordinal);
    }

    private static byte[] CreateElfWithUndefinedSymbols()
    {
        const int headerSize = 64;
        const int sectionSize = 64;
        const int sectionCount = 3;
        const int symbolOffset = headerSize + (sectionSize * sectionCount);
        const int symbolEntrySize = 24;
        const int symbolCount = 3;
        const int stringOffset = symbolOffset + (symbolEntrySize * symbolCount);
        var strings = "\0PyLong_FromLong\0PyExc_ValueError\0"u8.ToArray();
        var result = new byte[stringOffset + strings.Length];
        result[0] = 0x7f;
        result[1] = (byte)'E';
        result[2] = (byte)'L';
        result[3] = (byte)'F';
        result[4] = 2;
        result[5] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(40), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(58), sectionSize);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(60), sectionCount);

        var dynamicSymbols = result.AsSpan(headerSize + sectionSize, sectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicSymbols[4..], 11);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicSymbols[24..], symbolOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            dynamicSymbols[32..],
            symbolEntrySize * symbolCount
        );
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicSymbols[40..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicSymbols[56..], symbolEntrySize);

        var stringTable = result.AsSpan(headerSize + (sectionSize * 2), sectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(stringTable[4..], 3);
        BinaryPrimitives.WriteUInt64LittleEndian(stringTable[24..], stringOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(stringTable[32..], (ulong)strings.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(symbolOffset + symbolEntrySize), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(symbolOffset + (symbolEntrySize * 2)),
            17
        );
        strings.CopyTo(result.AsSpan(stringOffset));
        return result;
    }

    private static byte[] CreateMachOWithUndefinedSymbols()
    {
        const int loadCommandOffset = 32;
        const int symbolOffset = 56;
        const int symbolEntrySize = 16;
        const int stringOffset = symbolOffset + (symbolEntrySize * 2);
        var strings = "\0_PyLong_FromLong\0__Py_NewRef\0"u8.ToArray();
        var result = new byte[stringOffset + strings.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), 24);

        var symbolTable = result.AsSpan(loadCommandOffset, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(symbolTable, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(symbolTable[4..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(symbolTable[8..], symbolOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(symbolTable[12..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(symbolTable[16..], stringOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(symbolTable[20..], (uint)strings.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(symbolOffset), 1);
        result[symbolOffset + 4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(symbolOffset + symbolEntrySize), 18);
        result[symbolOffset + symbolEntrySize + 4] = 1;
        strings.CopyTo(result.AsSpan(stringOffset));
        return result;
    }

    private static byte[] CreatePeWithImportedSymbol()
    {
        const int peOffset = 0x80;
        const int optionalHeaderOffset = peOffset + 24;
        const int sectionTableOffset = optionalHeaderOffset + 0xf0;
        const int rawSectionOffset = 0x200;
        var result = new byte[0x600];
        result[0] = (byte)'M';
        result[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x3c), peOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(peOffset), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(peOffset + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(peOffset + 20), 0xf0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(optionalHeaderOffset), 0x20b);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(optionalHeaderOffset + 120), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(optionalHeaderOffset + 124), 40);

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 8), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 16), 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(sectionTableOffset + 20),
            rawSectionOffset
        );

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(rawSectionOffset), 0x1040);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(rawSectionOffset + 12), 0x1080);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(rawSectionOffset + 0x40), 0x1060);
        "PyLong_FromLong\0"u8.CopyTo(result.AsSpan(rawSectionOffset + 0x62));
        "python3.dll\0"u8.CopyTo(result.AsSpan(rawSectionOffset + 0x80));
        return result;
    }

    private sealed record WheelEntry(string Path, byte[] Content);

    private sealed class WheelFixture : IDisposable
    {
        private readonly string _directory;

        private WheelFixture(string directory, string path)
        {
            _directory = directory;
            Path = path;
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }

        internal string Path { get; }

        internal string Sha256 { get; }

        internal static WheelFixture Create(
            string fileName,
            string distribution,
            string version,
            IReadOnlyList<string> tags,
            params WheelEntry[] entries
        )
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dotpython-wheel-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, fileName);
            using (var stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var metadataRoot = $"{distribution}-{version}.dist-info";
                WriteEntry(
                    archive,
                    $"{metadataRoot}/WHEEL",
                    "Wheel-Version: 1.0\nRoot-Is-Purelib: true\n"
                        + string.Join(string.Empty, tags.Select(tag => $"Tag: {tag}\n"))
                );
                WriteEntry(
                    archive,
                    $"{metadataRoot}/METADATA",
                    $"Metadata-Version: 2.4\nName: {distribution}\nVersion: {version}\n"
                );
                WriteEntry(archive, $"{metadataRoot}/RECORD", string.Empty);
                foreach (var entry in entries)
                {
                    var item = archive.CreateEntry(entry.Path, CompressionLevel.NoCompression);
                    using var target = item.Open();
                    target.Write(entry.Content);
                }
            }

            return new WheelFixture(directory, path);
        }

        internal static WheelFixture CreateRaw(string fileName, byte[] content)
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dotpython-wheel-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, fileName);
            File.WriteAllBytes(path, content);
            return new WheelFixture(directory, path);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        private static void WriteEntry(ZipArchive archive, string path, string content)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
