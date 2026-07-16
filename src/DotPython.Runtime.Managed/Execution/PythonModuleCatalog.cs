using System.Text;
using DotPython.Compiler.Artifacts;
using DotPython.Language.Ast;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonModuleCatalog
{
    private const int MaxArtifactFileLength = 65 * 1024 * 1024;
    private const int MaxDirectoryDepth = 64;
    private const int MaxDiscoveredEntries = 50_000;
    private const int MaxDiscoveredPayloadLength = 128 * 1024 * 1024;
    private const int MaxMetadataFileLength = 1024 * 1024;
    private const int MaxSourceFileLength = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private PythonModuleCatalog(IReadOnlyDictionary<string, PythonModuleDefinition> modules)
    {
        Modules = modules;
    }

    internal IReadOnlyDictionary<string, PythonModuleDefinition> Modules { get; }

    internal static PythonModuleCatalog Empty { get; } =
        new(new Dictionary<string, PythonModuleDefinition>(StringComparer.Ordinal));

    internal static PythonModuleCatalog Discover(ManagedModuleDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SearchPaths);
        var builder = new Builder(options.SearchPaths);
        builder.Discover();
        return new PythonModuleCatalog(builder.CreateModules());
    }

    internal static PythonModuleCatalog FromSources(
        IReadOnlyDictionary<string, SourceText>? sources
    )
    {
        if (sources is null)
        {
            return Empty;
        }

        var modules = new Dictionary<string, PythonModuleDefinition>(StringComparer.Ordinal);
        foreach (var (name, source) in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            modules.Add(name, PythonModuleDefinition.FromSource(source));
        }

        return new PythonModuleCatalog(modules);
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, DiscoveredDistribution> _distributions = new(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, DiscoveredModule> _modules = new(
            StringComparer.Ordinal
        );
        private readonly string[] _roots;
        private int _entryCount;
        private long _payloadLength;

        internal Builder(IReadOnlyList<string> searchPaths)
        {
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var uniquePaths = new HashSet<string>(pathComparer);
            _roots = new string[searchPaths.Count];
            for (var index = 0; index < searchPaths.Count; index++)
            {
                var path = searchPaths[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var fullPath = Path.GetFullPath(path);
                if (!uniquePaths.Add(fullPath))
                {
                    throw new ArgumentException(
                        $"Managed module search path '{fullPath}' is registered more than once.",
                        nameof(searchPaths)
                    );
                }

                var root = new DirectoryInfo(fullPath);
                if (!root.Exists)
                {
                    throw new ArgumentException(
                        $"Managed module search path '{fullPath}' does not exist.",
                        nameof(searchPaths)
                    );
                }

                if (IsLink(root))
                {
                    throw new ArgumentException(
                        $"Managed module search path '{fullPath}' cannot be a symbolic link or reparse point.",
                        nameof(searchPaths)
                    );
                }

                _roots[index] = root.FullName;
            }
        }

        internal Dictionary<string, PythonModuleDefinition> CreateModules()
        {
            AddMetadataModules();
            return _modules.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Definition,
                StringComparer.Ordinal
            );
        }

        internal void Discover()
        {
            for (var rootIndex = 0; rootIndex < _roots.Length; rootIndex++)
            {
                ScanDirectory(
                    new DirectoryInfo(_roots[rootIndex]),
                    relativeParts: [],
                    rootIndex,
                    depth: 0
                );
            }
        }

        private void AddMetadataModules()
        {
            AddInternalModule(
                "importlib",
                PythonModuleDefinition.Native("<dotpython importlib>", isPackage: true, _ => { })
            );

            var versions = _distributions.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Version,
                StringComparer.Ordinal
            );
            AddInternalModule(
                "importlib.metadata",
                PythonModuleDefinition.Native(
                    "<dotpython importlib.metadata>",
                    isPackage: false,
                    globals =>
                        globals.SetValue(
                            "version",
                            new PythonBuiltinFunctionValue(
                                "version",
                                (arguments, span) =>
                                    ResolveDistributionVersion(arguments, versions, span)
                            )
                        )
                )
            );
        }

        private void AddInternalModule(string name, PythonModuleDefinition definition)
        {
            _modules[name] = new DiscoveredModule(definition, -1);
        }

        private void AddModule(string name, PythonModuleDefinition definition, int rootIndex)
        {
            if (!IsDottedModuleName(name))
            {
                return;
            }

            if (_modules.TryGetValue(name, out var existing))
            {
                if (existing.RootIndex < rootIndex)
                {
                    return;
                }

                throw new InvalidDataException(
                    $"Managed module '{name}' is provided more than once in search root '{_roots[rootIndex]}'."
                );
            }

            _modules.Add(name, new DiscoveredModule(definition, rootIndex));
        }

        private void AddDistribution(string name, string version, string origin, int rootIndex)
        {
            var normalizedName = NormalizeDistributionName(name);
            if (_distributions.TryGetValue(normalizedName, out var existing))
            {
                if (existing.RootIndex < rootIndex)
                {
                    return;
                }

                throw new InvalidDataException(
                    $"Distribution metadata for '{name}' is provided more than once in search root '{_roots[rootIndex]}'."
                );
            }

            _distributions.Add(
                normalizedName,
                new DiscoveredDistribution(version, origin, rootIndex)
            );
        }

        private void CountEntry(FileSystemInfo entry)
        {
            _entryCount++;
            if (_entryCount > MaxDiscoveredEntries)
            {
                throw new InvalidDataException(
                    $"Managed module discovery exceeds the {MaxDiscoveredEntries} entry limit near '{entry.FullName}'."
                );
            }
        }

        private void CountPayload(FileInfo file, long length)
        {
            _payloadLength += length;
            if (_payloadLength > MaxDiscoveredPayloadLength)
            {
                throw new InvalidDataException(
                    $"Managed module discovery exceeds the {MaxDiscoveredPayloadLength} byte aggregate payload limit at '{file.FullName}'."
                );
            }
        }

        private static string GetModuleName(
            IReadOnlyList<string> relativeParts,
            string fileName,
            string extension,
            out bool isPackage
        )
        {
            var stem = fileName[..^extension.Length];
            isPackage = string.Equals(stem, "__init__", StringComparison.Ordinal);
            var parts = isPackage ? relativeParts.ToArray() : [.. relativeParts, stem];
            return string.Join('.', parts);
        }

        private static bool IsDottedModuleName(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }

            var result = PythonParser.Parse(new SourceText($"import {name}"));
            return result.Success
                && result.Module.Statements
                    is [
                        PythonImportStatement
                        {
                            Imports: [PythonImportAlias { Name: var importedName, Alias: null }],
                        },
                    ]
                && string.Equals(name, importedName, StringComparison.Ordinal);
        }

        private static bool IsLink(FileSystemInfo entry) =>
            entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0;

        private static bool IsNativeExtension(string extension) =>
            extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pyd", StringComparison.OrdinalIgnoreCase);

        private bool IsShadowed(string name, int rootIndex) =>
            _modules.TryGetValue(name, out var existing) && existing.RootIndex < rootIndex;

        private bool ShouldSkip(string name, int rootIndex) =>
            !IsDottedModuleName(name) || IsShadowed(name, rootIndex);

        private static string NormalizeDistributionName(string name)
        {
            var builder = new StringBuilder(name.Length);
            var separatorPending = false;
            foreach (var character in name)
            {
                if (character is '-' or '_' or '.')
                {
                    separatorPending = builder.Length != 0;
                    continue;
                }

                if (separatorPending)
                {
                    builder.Append('-');
                    separatorPending = false;
                }

                builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }

        private void ReadDistributionMetadata(DirectoryInfo directory, int rootIndex)
        {
            var metadata = new FileInfo(Path.Combine(directory.FullName, "METADATA"));
            if (!metadata.Exists || IsLink(metadata))
            {
                return;
            }

            CountEntry(metadata);
            var content = ReadUtf8File(metadata, MaxMetadataFileLength, "distribution metadata");
            string? name = null;
            string? version = null;
            foreach (var line in content.Split('\n'))
            {
                if (name is null && line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                {
                    name = line[5..].Trim();
                }
                else if (
                    version is null
                    && line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
                )
                {
                    version = line[8..].Trim();
                }

                if (name is not null && version is not null)
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidDataException(
                    $"Distribution metadata '{metadata.FullName}' must define Name and Version headers."
                );
            }

            AddDistribution(name, version, metadata.FullName, rootIndex);
        }

        private static PythonTextValue ResolveDistributionVersion(
            IReadOnlyList<PythonValue> arguments,
            Dictionary<string, string> versions,
            TextSpan span
        )
        {
            if (arguments.Count != 1 || arguments[0] is not PythonTextValue name)
            {
                throw new PythonRuntimeException(
                    "DPY4028",
                    "importlib.metadata.version() requires one distribution-name string.",
                    span
                );
            }

            var normalizedName = NormalizeDistributionName(name.Value);
            if (!versions.TryGetValue(normalizedName, out var version))
            {
                throw new PythonRuntimeException(
                    "DPY4028",
                    $"No installed distribution metadata was found for '{name.Value}'.",
                    span
                );
            }

            return new PythonTextValue(version);
        }

        private void ScanDirectory(
            DirectoryInfo directory,
            IReadOnlyList<string> relativeParts,
            int rootIndex,
            int depth
        )
        {
            if (depth > MaxDirectoryDepth)
            {
                throw new InvalidDataException(
                    $"Managed module discovery exceeds the {MaxDirectoryDepth} directory-depth limit at '{directory.FullName}'."
                );
            }

            var entries = new List<FileSystemInfo>();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                CountEntry(entry);
                entries.Add(entry);
            }

            entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            foreach (var entry in entries)
            {
                if (IsLink(entry))
                {
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    if (
                        depth == 0
                        && childDirectory.Name.EndsWith(
                            ".dist-info",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        ReadDistributionMetadata(childDirectory, rootIndex);
                        continue;
                    }

                    if (!IsRegularPackageDirectory(childDirectory))
                    {
                        continue;
                    }

                    ScanDirectory(
                        childDirectory,
                        [.. relativeParts, childDirectory.Name],
                        rootIndex,
                        depth + 1
                    );
                    continue;
                }

                if (entry is FileInfo file)
                {
                    ScanFile(file, relativeParts, rootIndex);
                }
            }
        }

        private void ScanFile(FileInfo file, IReadOnlyList<string> relativeParts, int rootIndex)
        {
            var extension = file.Extension;
            if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                var sourceName = GetModuleName(
                    relativeParts,
                    file.Name,
                    extension,
                    out var isPackage
                );
                if (ShouldSkip(sourceName, rootIndex))
                {
                    return;
                }

                var source = new SourceText(
                    ReadUtf8File(file, MaxSourceFileLength, "Python source"),
                    file.FullName
                );
                AddModule(
                    sourceName,
                    PythonModuleDefinition.FromSource(source, isPackage),
                    rootIndex
                );
                return;
            }

            if (
                extension.Equals(
                    DotPythonModuleArtifactFormat.FileExtension,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var expectedName = GetModuleName(
                    relativeParts,
                    file.Name,
                    extension,
                    out var isPackage
                );
                if (ShouldSkip(expectedName, rootIndex))
                {
                    return;
                }

                using var stream = OpenBoundedRead(
                    file,
                    MaxArtifactFileLength,
                    "DotPython module artifact"
                );
                CountPayload(file, stream.Length);
                var artifact = DotPythonModuleArtifactSerializer.Deserialize(stream);
                if (
                    !string.Equals(
                        expectedName,
                        artifact.Manifest.ModuleName,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new InvalidDataException(
                        $"DotPython artifact '{file.FullName}' declares module '{artifact.Manifest.ModuleName}' instead of path identity '{expectedName}'."
                    );
                }

                AddModule(
                    expectedName,
                    PythonModuleDefinition.FromArtifact(artifact, file.FullName, isPackage),
                    rootIndex
                );
                return;
            }

            if (!IsNativeExtension(extension))
            {
                return;
            }

            var stem = file.Name[..^extension.Length];
            var tagSeparator = stem.IndexOf('.', StringComparison.Ordinal);
            if (tagSeparator >= 0)
            {
                stem = stem[..tagSeparator];
            }

            var nativeName = string.Join('.', relativeParts.Append(stem));
            if (ShouldSkip(nativeName, rootIndex))
            {
                return;
            }

            AddModule(
                nativeName,
                PythonModuleDefinition.UnsupportedNativeExtension(file.FullName),
                rootIndex
            );
        }

        private static bool IsRegularPackageDirectory(DirectoryInfo directory)
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "__init__.py"));
            if (source.Exists && !IsLink(source))
            {
                return true;
            }

            var artifact = new FileInfo(
                Path.Combine(
                    directory.FullName,
                    "__init__" + DotPythonModuleArtifactFormat.FileExtension
                )
            );
            return artifact.Exists && !IsLink(artifact);
        }

        private static FileStream OpenBoundedRead(FileInfo file, int maximumLength, string kind)
        {
            var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan
            );
            if (stream.Length <= maximumLength)
            {
                return stream;
            }

            stream.Dispose();
            throw new InvalidDataException(
                $"The {kind} file '{file.FullName}' exceeds the {maximumLength} byte limit."
            );
        }

        private string ReadUtf8File(FileInfo file, int maximumLength, string kind)
        {
            using var stream = OpenBoundedRead(file, maximumLength, kind);
            CountPayload(file, stream.Length);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"The {kind} file '{file.FullName}' exceeds the {maximumLength} byte limit."
                );
            }

            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? 3 : 0;
            return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }

        private sealed record DiscoveredDistribution(string Version, string Origin, int RootIndex);

        private sealed record DiscoveredModule(PythonModuleDefinition Definition, int RootIndex);
    }
}
