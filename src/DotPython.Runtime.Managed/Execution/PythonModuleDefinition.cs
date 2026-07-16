using DotPython.Compiler.Artifacts;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonModuleDefinition
{
    private PythonModuleDefinition(
        string origin,
        SourceText? source = null,
        DotPythonModuleArtifact? artifact = null,
        bool isPackage = false,
        Action<PythonGlobalNamespace>? initialize = null,
        string? nativeExtensionPath = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        Source = source;
        Artifact = artifact;
        Origin = origin;
        IsPackage = isPackage;
        Initialize = initialize;
        NativeExtensionPath = nativeExtensionPath;
    }

    internal DotPythonModuleArtifact? Artifact { get; }

    internal Action<PythonGlobalNamespace>? Initialize { get; }

    internal bool IsNativeExtension => NativeExtensionPath is not null;

    internal bool IsPackage { get; }

    internal string? NativeExtensionPath { get; }

    internal string Origin { get; }

    internal SourceText? Source { get; }

    internal static PythonModuleDefinition FromArtifact(
        DotPythonModuleArtifact artifact,
        string origin,
        bool isPackage
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new PythonModuleDefinition(origin, artifact: artifact, isPackage: isPackage);
    }

    internal static PythonModuleDefinition FromSource(SourceText source, bool isPackage = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PythonModuleDefinition(
            source.FilePath ?? "<module>",
            source: source,
            isPackage: isPackage
        );
    }

    internal static PythonModuleDefinition Native(
        string origin,
        bool isPackage,
        Action<PythonGlobalNamespace> initialize
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentNullException.ThrowIfNull(initialize);
        return new PythonModuleDefinition(origin, isPackage: isPackage, initialize: initialize);
    }

    internal static PythonModuleDefinition UnsupportedNativeExtension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PythonModuleDefinition(path, nativeExtensionPath: path);
    }
}
