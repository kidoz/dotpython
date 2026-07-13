using DotPython.Language.Text;

namespace DotPython.Language.Diagnostics;

public sealed record Diagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    TextSpan Span
);
