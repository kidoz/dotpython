using System.Collections.ObjectModel;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Language.Syntax;

public sealed class TokenizationResult
{
    internal TokenizationResult(
        SourceText source,
        IList<SyntaxToken> tokens,
        IList<Diagnostic> diagnostics,
        IList<TextSpan> comments
    )
    {
        Source = source;
        Tokens = new ReadOnlyCollection<SyntaxToken>(tokens);
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
        Comments = new ReadOnlyCollection<TextSpan>(comments);
    }

    public SourceText Source { get; }

    public IReadOnlyList<SyntaxToken> Tokens { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Physical comments, excluding string contents, outside the grammar token stream.</summary>
    public IReadOnlyList<TextSpan> Comments { get; }
}
