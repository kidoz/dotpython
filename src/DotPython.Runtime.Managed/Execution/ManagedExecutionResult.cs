using System.Collections.ObjectModel;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

public sealed class ManagedExecutionResult
{
    internal ManagedExecutionResult(SourceText source, IList<Diagnostic> diagnostics)
    {
        Source = source;
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
    }

    public SourceText Source { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool Success => Diagnostics.Count == 0;
}
