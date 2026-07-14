namespace DotPython.Compiler.Artifacts;

public sealed record DotPythonModuleExport
{
    public DotPythonModuleExport(string pythonName, string contractName, DotPythonExportKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
        if (!Enum.IsDefined(kind) || kind == DotPythonExportKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        PythonName = pythonName;
        ContractName = contractName;
        Kind = kind;
    }

    public string PythonName { get; }

    public string ContractName { get; }

    public DotPythonExportKind Kind { get; }
}
