using System.Collections.ObjectModel;
using DotPython.Language;

namespace DotPython.Compiler.Artifacts;

public sealed class DotPythonModuleManifest
{
    internal DotPythonModuleManifest(
        int formatVersion,
        string moduleName,
        string languageVersion,
        int bytecodeFormatVersion,
        IList<DotPythonModuleExport> exports
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageVersion);
        ValidateExports(exports);

        FormatVersion = formatVersion;
        ModuleName = moduleName;
        LanguageVersion = languageVersion;
        BytecodeFormatVersion = bytecodeFormatVersion;
        Exports = new ReadOnlyCollection<DotPythonModuleExport>(exports);
    }

    public int FormatVersion { get; }

    public string ModuleName { get; }

    public string LanguageVersion { get; }

    public int BytecodeFormatVersion { get; }

    public IReadOnlyList<DotPythonModuleExport> Exports { get; }

    internal static DotPythonModuleManifest Create(
        string moduleName,
        int bytecodeFormatVersion,
        IEnumerable<DotPythonModuleExport>? exports
    )
    {
        var orderedExports = (exports ?? [])
            .OrderBy(export => export.PythonName, StringComparer.Ordinal)
            .ThenBy(export => export.ContractName, StringComparer.Ordinal)
            .ThenBy(export => export.Kind)
            .ToList();
        return new DotPythonModuleManifest(
            DotPythonModuleArtifactFormat.CurrentVersion,
            moduleName,
            PythonLanguageVersion.Current.ToString(2),
            bytecodeFormatVersion,
            orderedExports
        );
    }

    private static void ValidateExports(IList<DotPythonModuleExport> exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        var pythonNames = new HashSet<string>(StringComparer.Ordinal);
        var contractNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in exports)
        {
            ArgumentNullException.ThrowIfNull(export);
            if (!pythonNames.Add(export.PythonName))
            {
                throw new ArgumentException(
                    $"Duplicate Python export name '{export.PythonName}'.",
                    nameof(exports)
                );
            }

            if (!contractNames.Add(export.ContractName))
            {
                throw new ArgumentException(
                    $"Duplicate contract export name '{export.ContractName}'.",
                    nameof(exports)
                );
            }
        }
    }
}
