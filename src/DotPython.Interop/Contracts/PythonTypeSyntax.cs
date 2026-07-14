using DotPython.Language.Text;

namespace DotPython.Interop.Contracts;

internal abstract record PythonTypeSyntax(TextSpan Span);

internal sealed record PythonNamedTypeSyntax(
    string Name,
    IReadOnlyList<PythonTypeSyntax> TypeArguments,
    TextSpan TypeSpan
) : PythonTypeSyntax(TypeSpan);

internal sealed record PythonUnionTypeSyntax(
    IReadOnlyList<PythonTypeSyntax> Members,
    TextSpan TypeSpan
) : PythonTypeSyntax(TypeSpan);
