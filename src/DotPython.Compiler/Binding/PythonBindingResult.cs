using System.Collections.ObjectModel;
using DotPython.Language.Diagnostics;

namespace DotPython.Compiler.Binding;

public sealed class PythonBindingResult
{
    internal PythonBindingResult(PythonBoundScope moduleScope, IList<Diagnostic> diagnostics)
    {
        ModuleScope = moduleScope;
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
    }

    public PythonBoundScope ModuleScope { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool Success => Diagnostics.Count == 0;
}
