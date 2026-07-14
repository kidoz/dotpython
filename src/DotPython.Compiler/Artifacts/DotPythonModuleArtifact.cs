using DotPython.Compiler.Bytecode;

namespace DotPython.Compiler.Artifacts;

public sealed class DotPythonModuleArtifact
{
    internal DotPythonModuleArtifact(DotPythonModuleManifest manifest, PythonCodeObject code)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(code);
        if (manifest.FormatVersion != DotPythonModuleArtifactFormat.CurrentVersion)
        {
            throw new ArgumentException(
                "The module artifact format is not supported.",
                nameof(manifest)
            );
        }

        if (manifest.BytecodeFormatVersion != code.FormatVersion)
        {
            throw new ArgumentException(
                "The manifest and code object bytecode versions do not match.",
                nameof(manifest)
            );
        }

        Manifest = manifest;
        Code = code;
    }

    public DotPythonModuleManifest Manifest { get; }

    public PythonCodeObject Code { get; }

    public static DotPythonModuleArtifact Create(
        string moduleName,
        PythonCodeObject code,
        IEnumerable<DotPythonModuleExport>? exports = null
    )
    {
        ArgumentNullException.ThrowIfNull(code);
        return new DotPythonModuleArtifact(
            DotPythonModuleManifest.Create(moduleName, code.FormatVersion, exports),
            code
        );
    }
}
