using System.Collections.ObjectModel;
using DotPython.Contracts;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Interop.Contracts;

/// <summary>Contains the result of statically compiling a Python stub contract.</summary>
public sealed class PythonContractCompilationResult
{
    internal PythonContractCompilationResult(
        SourceText source,
        PythonModuleContract? contract,
        IList<Diagnostic> diagnostics
    )
    {
        Source = source;
        Contract = contract;
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
    }

    /// <summary>Gets the source stub.</summary>
    public SourceText Source { get; }

    /// <summary>Gets the compiled contract, or <see langword="null"/> when errors occurred.</summary>
    public PythonModuleContract? Contract { get; }

    /// <summary>Gets deterministic source diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Gets whether the stub produced a complete contract.</summary>
    public bool Success => Contract is not null && Diagnostics.Count == 0;
}
