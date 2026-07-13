using System.Collections.ObjectModel;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.ParserGenerator;

public sealed class PythonParseResult
{
    internal PythonParseResult(
        SourceText source,
        PythonModule module,
        IList<Diagnostic> diagnostics
    )
    {
        Source = source;
        Module = module;
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
    }

    public SourceText Source { get; }

    public PythonModule Module { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool Success => Diagnostics.Count == 0;
}
