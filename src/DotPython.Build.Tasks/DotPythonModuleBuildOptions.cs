using DotPython.Contracts;

namespace DotPython.Build.Tasks;

internal sealed record DotPythonModuleBuildOptions(
    string SourcePath,
    string ContractPath,
    string ModuleName,
    string ClrNamespace,
    string ClrTypeName,
    PythonModuleStatePolicy StatePolicy,
    string ArtifactOutputPath,
    string ContractOutputPath,
    string FacadeOutputPath,
    string ArtifactResourceName
);
