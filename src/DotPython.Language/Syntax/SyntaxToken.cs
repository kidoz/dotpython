using DotPython.Language.Text;

namespace DotPython.Language.Syntax;

public sealed record SyntaxToken(SyntaxTokenKind Kind, TextSpan Span, string Text);
