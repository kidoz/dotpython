using System.Collections.ObjectModel;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Language.Syntax;

public sealed class TokenizationResult
{
    internal TokenizationResult(
        SourceText source,
        IList<SyntaxToken> tokens,
        IList<Diagnostic> diagnostics
    )
    {
        Source = source;
        Tokens = new ReadOnlyCollection<SyntaxToken>(tokens);
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
    }

    public SourceText Source { get; }

    public IReadOnlyList<SyntaxToken> Tokens { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}
