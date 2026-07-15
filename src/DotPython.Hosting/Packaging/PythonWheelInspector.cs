using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DotPython.Hosting.Packaging;

/// <summary>Performs bounded, read-only inspection of Python wheel artifacts.</summary>
public static class PythonWheelInspector
{
    /// <summary>Inspects a wheel without extracting or executing its contents.</summary>
    /// <param name="path">The local wheel artifact path.</param>
    /// <param name="options">Optional bounded inspection policy.</param>
    /// <returns>A deterministic compatibility report.</returns>
    /// <exception cref="ArgumentException">The path is empty or options are invalid.</exception>
    /// <exception cref="FileNotFoundException">The artifact does not exist.</exception>
    /// <exception cref="IOException">The artifact cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The artifact cannot be accessed.</exception>
    public static PythonWheelInspection Inspect(
        string path,
        PythonWheelInspectionOptions? options = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= PythonWheelInspectionOptions.Default;
        ValidateOptions(options);

        var fileName = Path.GetFileName(path);
        var diagnostics = new List<PythonWheelDiagnostic>();
        var parsedName = ParseFileName(fileName, diagnostics);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The wheel artifact does not exist.", path);
        }

        if (file.Length > options.MaximumWheelBytes)
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.InspectionLimitExceeded,
                    $"Wheel size {file.Length} exceeds the {options.MaximumWheelBytes}-byte inspection limit."
                )
            );
            return CreateResult(
                fileName,
                string.Empty,
                parsedName,
                PythonWheelCompatibilityKind.Invalid,
                "The artifact exceeds the bounded inspection policy.",
                [],
                [],
                diagnostics
            );
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan
        );
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = 0;

        var nativeBinaries = new List<PythonWheelNativeBinary>();
        var metadataTags = new List<PythonWheelTag>();
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            InspectArchive(archive, options, parsedName, metadataTags, nativeBinaries, diagnostics);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MalformedArchive,
                    $"The wheel ZIP archive is malformed: {exception.Message}"
                )
            );
        }

        var effectiveTags = metadataTags.Count == 0 ? parsedName.Tags : metadataTags;
        var compatibility = Classify(parsedName, effectiveTags, nativeBinaries, diagnostics);
        var summary = GetCompatibilitySummary(compatibility, effectiveTags);
        var platforms = effectiveTags
            .Select(tag => tag.Platform)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(ParsePlatform)
            .ToArray();

        if (diagnostics.Any(item => item.Severity == PythonWheelDiagnosticSeverity.Error))
        {
            compatibility = PythonWheelCompatibilityKind.Invalid;
            summary = "The artifact is not a structurally valid wheel.";
        }

        return CreateResult(
            fileName,
            sha256,
            parsedName,
            compatibility,
            summary,
            effectiveTags,
            platforms,
            nativeBinaries,
            diagnostics
        );
    }

    private static void InspectArchive(
        ZipArchive archive,
        PythonWheelInspectionOptions options,
        WheelFileName parsedName,
        List<PythonWheelTag> metadataTags,
        List<PythonWheelNativeBinary> nativeBinaries,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        if (archive.Entries.Count > options.MaximumEntryCount)
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.InspectionLimitExceeded,
                    $"Wheel entry count {archive.Entries.Count} exceeds the {options.MaximumEntryCount}-entry inspection limit."
                )
            );
            return;
        }

        long totalLength = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var wheelMetadata = new List<ZipArchiveEntry>();
        var coreMetadata = new List<ZipArchiveEntry>();
        var recordMetadata = new List<ZipArchiveEntry>();
        var nativeEntries = new List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            if (!TryAdd(totalLength, entry.Length, out totalLength))
            {
                totalLength = long.MaxValue;
            }

            if (!paths.Add(entry.FullName))
            {
                diagnostics.Add(
                    Error(
                        PythonWheelDiagnosticCode.DuplicateEntry,
                        $"Wheel contains duplicate archive path '{entry.FullName}'."
                    )
                );
            }

            if (!IsSafeArchivePath(entry.FullName))
            {
                diagnostics.Add(
                    Error(
                        PythonWheelDiagnosticCode.UnsafeArchivePath,
                        $"Wheel contains unsafe archive path '{entry.FullName}'."
                    )
                );
            }

            if (entry.FullName.EndsWith(".dist-info/WHEEL", StringComparison.Ordinal))
            {
                wheelMetadata.Add(entry);
            }
            else if (entry.FullName.EndsWith(".dist-info/METADATA", StringComparison.Ordinal))
            {
                coreMetadata.Add(entry);
            }
            else if (entry.FullName.EndsWith(".dist-info/RECORD", StringComparison.Ordinal))
            {
                recordMetadata.Add(entry);
            }

            if (IsNativeBinaryPath(entry.FullName))
            {
                nativeEntries.Add(entry);
            }
        }

        if (totalLength > options.MaximumUncompressedBytes)
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.InspectionLimitExceeded,
                    $"Wheel advertises {totalLength} uncompressed bytes, exceeding the {options.MaximumUncompressedBytes}-byte inspection limit."
                )
            );
            return;
        }

        var wheelEntry = RequireSingleMetadata("WHEEL", wheelMetadata, diagnostics);
        var metadataEntry = RequireSingleMetadata("METADATA", coreMetadata, diagnostics);
        RequireSingleMetadata("RECORD", recordMetadata, diagnostics);
        ValidateMetadataRoot(parsedName, wheelMetadata, coreMetadata, recordMetadata, diagnostics);
        if (
            wheelEntry is not null
            && EnsureMetadataEntryWithinLimit(wheelEntry, options.MaximumMetadataBytes, diagnostics)
        )
        {
            InspectWheelMetadata(ReadTextEntry(wheelEntry), parsedName, metadataTags, diagnostics);
        }

        if (
            metadataEntry is not null
            && EnsureMetadataEntryWithinLimit(
                metadataEntry,
                options.MaximumMetadataBytes,
                diagnostics
            )
        )
        {
            InspectCoreMetadata(ReadTextEntry(metadataEntry), parsedName, diagnostics);
        }

        foreach (var nativeEntry in nativeEntries)
        {
            nativeBinaries.Add(InspectNativeBinary(nativeEntry, options, diagnostics));
        }

        nativeBinaries.Sort(
            (left, right) => StringComparer.Ordinal.Compare(left.ArchivePath, right.ArchivePath)
        );
    }

    private static PythonWheelNativeBinary InspectNativeBinary(
        ZipArchiveEntry entry,
        PythonWheelInspectionOptions options,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        if (entry.Length > options.MaximumNativeBinaryBytes)
        {
            diagnostics.Add(
                Warning(
                    PythonWheelDiagnosticCode.InspectionLimitExceeded,
                    $"Native binary '{entry.FullName}' exceeds the symbol-inspection size limit."
                )
            );
            return new PythonWheelNativeBinary
            {
                ArchivePath = entry.FullName,
                Format = PythonNativeBinaryFormat.Unknown,
                IsHpyUniversal = IsHpyUniversalPath(entry.FullName),
                RequiredSymbols = [],
            };
        }

        using var source = entry.Open();
        var bytes = new byte[(int)entry.Length];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"Native binary '{entry.FullName}' expands beyond its advertised size."
            );
        }

        var inspection = NativeBinarySymbolInspector.Inspect(bytes);
        if (!inspection.SymbolsAvailable)
        {
            diagnostics.Add(
                Warning(
                    PythonWheelDiagnosticCode.NativeSymbolsUnavailable,
                    $"Required symbols could not be read from native binary '{entry.FullName}'."
                )
            );
        }

        return new PythonWheelNativeBinary
        {
            ArchivePath = entry.FullName,
            Format = inspection.Format,
            IsHpyUniversal = IsHpyUniversalPath(entry.FullName),
            RequiredSymbols = inspection.RequiredSymbols,
        };
    }

    private static bool EnsureMetadataEntryWithinLimit(
        ZipArchiveEntry entry,
        int maximumBytes,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        if (entry.Length <= maximumBytes)
        {
            return true;
        }

        diagnostics.Add(
            Error(
                PythonWheelDiagnosticCode.InspectionLimitExceeded,
                $"Metadata entry '{entry.FullName}' exceeds the {maximumBytes}-byte inspection limit."
            )
        );
        return false;
    }

    private static void ValidateMetadataRoot(
        WheelFileName parsedName,
        IReadOnlyList<ZipArchiveEntry> wheelMetadata,
        IReadOnlyList<ZipArchiveEntry> coreMetadata,
        IReadOnlyList<ZipArchiveEntry> recordMetadata,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        if (parsedName == WheelFileName.Invalid)
        {
            return;
        }

        var expectedRoot = $"{parsedName.Distribution}-{parsedName.Version}.dist-info/";
        if (
            wheelMetadata.Any(entry => entry.FullName != $"{expectedRoot}WHEEL")
            || coreMetadata.Any(entry => entry.FullName != $"{expectedRoot}METADATA")
            || recordMetadata.Any(entry => entry.FullName != $"{expectedRoot}RECORD")
        )
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MetadataMismatch,
                    $"Required metadata entries must be stored under '{expectedRoot}'."
                )
            );
        }
    }

    private static ZipArchiveEntry? RequireSingleMetadata(
        string name,
        List<ZipArchiveEntry> entries,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        if (entries.Count == 1)
        {
            return entries[0];
        }

        diagnostics.Add(
            Error(
                entries.Count == 0
                    ? PythonWheelDiagnosticCode.MissingMetadata
                    : PythonWheelDiagnosticCode.DuplicateEntry,
                entries.Count == 0
                    ? $"Wheel is missing required .dist-info/{name} metadata."
                    : $"Wheel contains more than one .dist-info/{name} entry."
            )
        );
        return null;
    }

    private static string ReadTextEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false
        );
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Metadata entry '{entry.FullName}' is not valid UTF-8.",
                exception
            );
        }
    }

    private static void InspectWheelMetadata(
        string text,
        WheelFileName parsedName,
        List<PythonWheelTag> metadataTags,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        var fields = ParseMetadataFields(text);
        if (
            !fields.TryGetValue("Wheel-Version", out var versions)
            || versions.Count != 1
            || !System.Version.TryParse(versions[0], out var wheelVersion)
        )
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MalformedArchive,
                    "WHEEL metadata must contain one valid Wheel-Version field."
                )
            );
        }
        else if (wheelVersion.Major != 1)
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.UnsupportedWheelVersion,
                    $"Wheel-Version {wheelVersion} is not supported; the inspector supports major version 1."
                )
            );
        }

        if (
            !fields.TryGetValue("Root-Is-Purelib", out var pureLibraryValues)
            || pureLibraryValues.Count != 1
            || !bool.TryParse(pureLibraryValues[0], out _)
        )
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MalformedArchive,
                    "WHEEL metadata must contain one Boolean Root-Is-Purelib field."
                )
            );
        }

        if (!fields.TryGetValue("Tag", out var tags) || tags.Count == 0)
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MalformedArchive,
                    "WHEEL metadata must contain at least one expanded Tag field."
                )
            );
            return;
        }

        foreach (var value in tags)
        {
            if (!TryParseExpandedTag(value, out var tag))
            {
                diagnostics.Add(
                    Error(
                        PythonWheelDiagnosticCode.MalformedArchive,
                        $"WHEEL metadata contains invalid compatibility tag '{value}'."
                    )
                );
                continue;
            }

            metadataTags.Add(tag);
        }

        metadataTags.Sort((left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        var embedded = metadataTags.Select(tag => tag.Value).ToHashSet(StringComparer.Ordinal);
        var filename = parsedName.Tags.Select(tag => tag.Value).ToHashSet(StringComparer.Ordinal);
        if (embedded.Count != filename.Count || !embedded.SetEquals(filename))
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MetadataMismatch,
                    "Filename compatibility tags do not match expanded Tag fields in WHEEL metadata."
                )
            );
        }
    }

    private static void InspectCoreMetadata(
        string text,
        WheelFileName parsedName,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        var fields = ParseMetadataFields(text);
        if (
            !fields.TryGetValue("Name", out var names)
            || names.Count != 1
            || !fields.TryGetValue("Version", out var versions)
            || versions.Count != 1
        )
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MalformedArchive,
                    "METADATA must contain one Name and one Version field."
                )
            );
            return;
        }

        if (
            !string.Equals(
                NormalizeDistributionName(parsedName.Distribution),
                NormalizeDistributionName(names[0]),
                StringComparison.Ordinal
            )
            || !string.Equals(
                parsedName.Version,
                versions[0].Replace('-', '_'),
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.MetadataMismatch,
                    "Filename distribution/version does not match core METADATA."
                )
            );
        }
    }

    private static Dictionary<string, List<string>> ParseMetadataFields(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (!result.TryGetValue(name, out var values))
            {
                values = [];
                result.Add(name, values);
            }

            values.Add(value);
        }

        return result;
    }

    private static WheelFileName ParseFileName(
        string fileName,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        if (!fileName.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.InvalidFileName,
                    "Wheel filename must end with '.whl'."
                )
            );
            return WheelFileName.Invalid;
        }

        var components = fileName[..^4].Split('-');
        var hasBuild =
            components.Length is 6 or 7
            && components[2].Length != 0
            && char.IsAsciiDigit(components[2][0]);
        var hasVariant = components.Length == (hasBuild ? 7 : 6);
        if (components.Length != (hasBuild ? (hasVariant ? 7 : 6) : (hasVariant ? 6 : 5)))
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.InvalidFileName,
                    "Wheel filename must contain distribution, version, optional build, and three compatibility tag components."
                )
            );
            return WheelFileName.Invalid;
        }

        if (hasVariant)
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.DraftVariantUnsupported,
                    "Draft PEP 825 wheel variants are recognized but are not an accepted wheel format."
                )
            );
        }

        var tagOffset = hasBuild ? 3 : 2;
        if (
            components[0].Length == 0
            || components[1].Length == 0
            || (hasBuild && !char.IsAsciiDigit(components[2][0]))
        )
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.InvalidFileName,
                    "Wheel filename contains an empty or invalid distribution, version, or build component."
                )
            );
            return WheelFileName.Invalid;
        }

        var tags = ExpandTags(
            components[tagOffset],
            components[tagOffset + 1],
            components[tagOffset + 2],
            diagnostics
        );
        return new WheelFileName(
            components[0],
            components[1],
            hasBuild ? components[2] : null,
            tags
        );
    }

    private static List<PythonWheelTag> ExpandTags(
        string pythonComponent,
        string abiComponent,
        string platformComponent,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        var pythonTags = pythonComponent.Split('.');
        var abiTags = abiComponent.Split('.');
        var platformTags = platformComponent.Split('.');
        if (
            pythonTags.Any(tag => !IsTagToken(tag))
            || abiTags.Any(tag => !IsTagToken(tag))
            || platformTags.Any(tag => !IsTagToken(tag))
            || (long)pythonTags.Length * abiTags.Length * platformTags.Length > 10_000
        )
        {
            diagnostics.Add(
                Error(
                    PythonWheelDiagnosticCode.InvalidFileName,
                    "Wheel filename contains an invalid or excessively compressed compatibility tag set."
                )
            );
            return [];
        }

        var result = new List<PythonWheelTag>();
        foreach (var python in pythonTags)
        {
            foreach (var abi in abiTags)
            {
                foreach (var platform in platformTags)
                {
                    result.Add(CreateTag(python, abi, platform));
                }
            }
        }

        result.Sort((left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        return result;
    }

    private static bool TryParseExpandedTag(string value, out PythonWheelTag tag)
    {
        tag = null!;
        var components = value.Split('-');
        if (
            components.Length != 3
            || components.Any(component =>
                !IsTagToken(component) || component.Contains('.', StringComparison.Ordinal)
            )
        )
        {
            return false;
        }

        tag = CreateTag(components[0], components[1], components[2]);
        return true;
    }

    private static PythonWheelTag CreateTag(string python, string abi, string platform) =>
        new()
        {
            Python = python,
            Abi = abi,
            Platform = platform,
            IsFreeThreaded = IsFreeThreadedTag(python) || IsFreeThreadedTag(abi),
        };

    private static PythonWheelCompatibilityKind Classify(
        WheelFileName parsedName,
        IReadOnlyList<PythonWheelTag> tags,
        List<PythonWheelNativeBinary> binaries,
        List<PythonWheelDiagnostic> diagnostics
    )
    {
        if (parsedName == WheelFileName.Invalid || tags.Count == 0)
        {
            return PythonWheelCompatibilityKind.Invalid;
        }

        PythonWheelCompatibilityKind result;
        if (binaries.Any(binary => binary.IsHpyUniversal))
        {
            result = PythonWheelCompatibilityKind.HpyUniversal;
        }
        else if (tags.Any(tag => string.Equals(tag.Abi, "abi3", StringComparison.Ordinal)))
        {
            result = PythonWheelCompatibilityKind.CpythonStableAbi;
        }
        else if (
            tags.Any(tag =>
                tag.Python.StartsWith("cp", StringComparison.Ordinal)
                || tag.Abi.StartsWith("cp", StringComparison.Ordinal)
            )
        )
        {
            result = PythonWheelCompatibilityKind.CpythonVersionSpecific;
        }
        else if (
            binaries.Count == 0
            && tags.All(tag =>
                string.Equals(tag.Abi, "none", StringComparison.Ordinal)
                && string.Equals(tag.Platform, "any", StringComparison.Ordinal)
            )
        )
        {
            return PythonWheelCompatibilityKind.PurePython;
        }
        else
        {
            result = PythonWheelCompatibilityKind.UnknownBinary;
        }

        diagnostics.Add(
            Warning(
                PythonWheelDiagnosticCode.NativeRuntimeUnsupported,
                GetCompatibilitySummary(result, tags)
            )
        );
        return result;
    }

    private static string GetCompatibilitySummary(
        PythonWheelCompatibilityKind compatibility,
        IReadOnlyList<PythonWheelTag> tags
    ) =>
        compatibility switch
        {
            PythonWheelCompatibilityKind.PurePython =>
                "The wheel is platform-independent and contains no native binaries.",
            PythonWheelCompatibilityKind.CpythonStableAbi =>
                "The wheel targets the CPython Stable ABI (abi3), which dotpython-managed does not execute.",
            PythonWheelCompatibilityKind.CpythonVersionSpecific =>
                $"The wheel targets a version-specific CPython ABI ({string.Join(", ", tags.Select(tag => tag.Abi).Distinct(StringComparer.Ordinal))}), which dotpython-managed does not execute.",
            PythonWheelCompatibilityKind.HpyUniversal =>
                "The wheel contains an HPy Universal ABI extension; HPy execution is not implemented.",
            PythonWheelCompatibilityKind.UnknownBinary =>
                "The wheel advertises or contains a native binary contract that DotPython does not recognize or execute.",
            _ => "The artifact is not a structurally valid wheel.",
        };

    private static PythonWheelPlatform ParsePlatform(string tag)
    {
        if (string.Equals(tag, "any", StringComparison.Ordinal))
        {
            return CreatePlatform(tag, PythonWheelPlatformFamily.Any, "any");
        }

        if (tag.StartsWith("win", StringComparison.Ordinal))
        {
            var architecture = tag switch
            {
                "win32" => "x86",
                "win_amd64" => "x86_64",
                "win_arm64" => "arm64",
                _ => tag[(tag.IndexOf('_', StringComparison.Ordinal) + 1)..],
            };
            return CreatePlatform(tag, PythonWheelPlatformFamily.Windows, architecture);
        }

        if (tag.StartsWith("macosx_", StringComparison.Ordinal))
        {
            return ParseVersionedPlatform(tag, PythonWheelPlatformFamily.MacOS, "macosx", null);
        }

        if (tag.StartsWith("manylinux_", StringComparison.Ordinal))
        {
            return ParseVersionedPlatform(
                tag,
                PythonWheelPlatformFamily.Manylinux,
                "manylinux",
                "glibc"
            );
        }

        if (tag.StartsWith("manylinux", StringComparison.Ordinal))
        {
            var separator = tag.IndexOf('_', StringComparison.Ordinal);
            var architecture = separator < 0 ? "unknown" : tag[(separator + 1)..];
            var version =
                tag.StartsWith("manylinux2014", StringComparison.Ordinal)
                    ? new System.Version(2, 17)
                : tag.StartsWith("manylinux2010", StringComparison.Ordinal)
                    ? new System.Version(2, 12)
                : tag.StartsWith("manylinux1", StringComparison.Ordinal) ? new System.Version(2, 5)
                : null;
            return CreatePlatform(
                tag,
                PythonWheelPlatformFamily.Manylinux,
                architecture,
                "glibc",
                version
            );
        }

        if (tag.StartsWith("musllinux_", StringComparison.Ordinal))
        {
            return ParseVersionedPlatform(
                tag,
                PythonWheelPlatformFamily.Musllinux,
                "musllinux",
                "musl"
            );
        }

        if (tag.StartsWith("linux_", StringComparison.Ordinal))
        {
            return CreatePlatform(tag, PythonWheelPlatformFamily.Linux, tag[6..]);
        }

        if (tag.StartsWith("android_", StringComparison.Ordinal))
        {
            var parts = tag.Split('_');
            return CreatePlatform(
                tag,
                PythonWheelPlatformFamily.Android,
                parts.Length > 2 ? string.Join('_', parts[2..]) : "unknown",
                minimumVersion: parts.Length > 1 && int.TryParse(parts[1], out var api)
                    ? new System.Version(api, 0)
                    : null
            );
        }

        if (tag.StartsWith("ios_", StringComparison.Ordinal))
        {
            return ParseVersionedPlatform(tag, PythonWheelPlatformFamily.IOS, "ios", null);
        }

        var lastSeparator = tag.LastIndexOf('_');
        return CreatePlatform(
            tag,
            PythonWheelPlatformFamily.Other,
            lastSeparator < 0 ? "unknown" : tag[(lastSeparator + 1)..]
        );
    }

    private static PythonWheelPlatform ParseVersionedPlatform(
        string tag,
        PythonWheelPlatformFamily family,
        string prefix,
        string? libc
    )
    {
        var parts = tag.Split('_');
        var prefixParts = prefix.Split('_').Length;
        var majorIndex = prefixParts;
        if (
            parts.Length > majorIndex + 2
            && int.TryParse(parts[majorIndex], out var major)
            && int.TryParse(parts[majorIndex + 1], out var minor)
        )
        {
            return CreatePlatform(
                tag,
                family,
                string.Join('_', parts[(majorIndex + 2)..]),
                libc,
                new System.Version(major, minor)
            );
        }

        return CreatePlatform(tag, family, "unknown", libc);
    }

    private static PythonWheelPlatform CreatePlatform(
        string tag,
        PythonWheelPlatformFamily family,
        string architecture,
        string? libc = null,
        System.Version? minimumVersion = null
    ) =>
        new()
        {
            Tag = tag,
            Family = family,
            Architecture = architecture,
            Libc = libc,
            MinimumVersion = minimumVersion,
        };

    private static PythonWheelInspection CreateResult(
        string fileName,
        string sha256,
        WheelFileName name,
        PythonWheelCompatibilityKind compatibility,
        string summary,
        IReadOnlyList<PythonWheelTag> tags,
        IReadOnlyList<PythonWheelPlatform> platforms,
        IReadOnlyList<PythonWheelDiagnostic> diagnostics
    ) =>
        CreateResult(
            fileName,
            sha256,
            name,
            compatibility,
            summary,
            tags,
            platforms,
            [],
            diagnostics
        );

    private static PythonWheelInspection CreateResult(
        string fileName,
        string sha256,
        WheelFileName name,
        PythonWheelCompatibilityKind compatibility,
        string summary,
        IReadOnlyList<PythonWheelTag> tags,
        IReadOnlyList<PythonWheelPlatform> platforms,
        IReadOnlyList<PythonWheelNativeBinary> nativeBinaries,
        IReadOnlyList<PythonWheelDiagnostic> diagnostics
    ) =>
        new()
        {
            FileName = fileName,
            Sha256 = sha256,
            Distribution = name.Distribution,
            Version = name.Version,
            BuildTag = name.BuildTag,
            Compatibility = compatibility,
            CompatibilitySummary = summary,
            IsFreeThreaded = tags.Any(tag => tag.IsFreeThreaded),
            Tags = [.. tags.OrderBy(tag => tag.Value, StringComparer.Ordinal)],
            Platforms = [.. platforms],
            NativeBinaries = [.. nativeBinaries],
            Diagnostics = [.. diagnostics],
        };

    private static void ValidateOptions(PythonWheelInspectionOptions options)
    {
        if (
            options.MaximumWheelBytes <= 0
            || options.MaximumEntryCount <= 0
            || options.MaximumUncompressedBytes <= 0
            || options.MaximumMetadataBytes <= 0
            || options.MaximumNativeBinaryBytes <= 0
        )
        {
            throw new ArgumentException(
                "All wheel inspection limits must be positive.",
                nameof(options)
            );
        }
    }

    private static bool IsSafeArchivePath(string path)
    {
        var candidate = path.EndsWith('/') ? path[..^1] : path;
        if (
            candidate.Length == 0
            || candidate[0] is '/' or '\\'
            || candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Contains('\0', StringComparison.Ordinal)
            || candidate.Split('/')[0].Contains(':', StringComparison.Ordinal)
        )
        {
            return false;
        }

        return !candidate.Split('/').Any(component => component is "" or "." or "..");
    }

    private static bool IsNativeBinaryPath(string path) =>
        path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".pyd", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);

    private static bool IsHpyUniversalPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var marker = fileName.LastIndexOf(".hpy", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var index = marker + 4;
        while (index < fileName.Length && char.IsAsciiDigit(fileName[index]))
        {
            index++;
        }

        return index < fileName.Length && fileName[index] == '.';
    }

    private static bool IsFreeThreadedTag(string value)
    {
        if (!value.StartsWith("cp", StringComparison.Ordinal))
        {
            return false;
        }

        var index = 2;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            index++;
        }

        return index < value.Length && value[index] == 't';
    }

    private static bool IsTagToken(string value) =>
        value.Length != 0
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.');

    private static string NormalizeDistributionName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var separator = false;
        foreach (var character in value)
        {
            if (character is '-' or '_' or '.')
            {
                if (!separator)
                {
                    builder.Append('-');
                    separator = true;
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
            separator = false;
        }

        return builder.ToString();
    }

    private static bool TryAdd(long left, long right, out long result)
    {
        if (right > long.MaxValue - left)
        {
            result = 0;
            return false;
        }

        result = left + right;
        return true;
    }

    private static PythonWheelDiagnostic Error(PythonWheelDiagnosticCode code, string message) =>
        new(code, PythonWheelDiagnosticSeverity.Error, message);

    private static PythonWheelDiagnostic Warning(PythonWheelDiagnosticCode code, string message) =>
        new(code, PythonWheelDiagnosticSeverity.Warning, message);

    private sealed record WheelFileName(
        string Distribution,
        string Version,
        string? BuildTag,
        IReadOnlyList<PythonWheelTag> Tags
    )
    {
        internal static WheelFileName Invalid { get; } = new(string.Empty, string.Empty, null, []);
    }
}
