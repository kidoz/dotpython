using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Lint;

/// <summary>Source and ordered lint or parser diagnostics. Parser failures skip lint rules.</summary>
public sealed class PythonLintResult
{
    internal PythonLintResult(SourceText source, IEnumerable<Diagnostic> diagnostics)
    {
        Source = source;
        Diagnostics = Array.AsReadOnly(
            diagnostics
                .OrderBy(diagnostic => diagnostic.Span.Start)
                .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ToArray()
        );
    }

    public SourceText Source { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool IsClean => Diagnostics.Count == 0;
}
