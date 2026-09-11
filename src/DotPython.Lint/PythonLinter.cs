using DotPython.Compiler.Binding;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Lint;

/// <summary>Static linting for the current DotPython parser profile; never executes Python.</summary>
public static class PythonLinter
{
    public static IReadOnlyList<PythonLintRule> Rules { get; } =
        Array.AsReadOnly<PythonLintRule>([
            new(
                "DPYL001",
                "bare-except",
                "Specify an exception type instead of catching every exception."
            ),
            new("DPYL002", "mutable-default", "A mutable literal default is shared between calls."),
            new(
                "DPYL003",
                "tuple-assert",
                "A tuple with a non-starred element is always truthy; assert its intended condition."
            ),
            new(
                "DPYL004",
                "unused-import",
                "The imported binding is never referenced in a visible lexical scope."
            ),
            new("DPYL005", "undefined-name", "The name has no visible definition."),
        ]);

    /// <summary>Validates exact rule identifiers and additional global names. An invalid configuration throws ArgumentException.</summary>
    public static void ValidateOptions(PythonLintOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Ignore);
        ArgumentNullException.ThrowIfNull(options.KnownGlobals);
        foreach (var name in options.KnownGlobals)
        {
            if (!IsIdentifier(name))
                throw new ArgumentException(
                    $"Invalid known global '{name}'; expected a Python identifier.",
                    nameof(options)
                );
        }
        foreach (var code in (options.Select ?? []).Concat(options.Ignore))
        {
            if (!Rules.Any(rule => string.Equals(rule.Code, code, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Unknown lint rule '{code}'.", nameof(options));
            }
        }
    }

    /// <summary>
    /// Checks cancellation before and after parsing and during traversal. Parsing itself is not preemptible.
    /// Parser diagnostics are always returned and prevent lint rules from running on a partial tree.
    /// </summary>
    public static PythonLintResult Analyze(
        SourceText source,
        PythonLintOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new PythonLintOptions();
        ValidateOptions(options);
        var enabled = new HashSet<string>(
            options.Select ?? Rules.Select(rule => rule.Code).ToArray(),
            StringComparer.Ordinal
        );
        enabled.ExceptWith(options.Ignore);

        var parse = PythonParser.Parse(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!parse.Success)
        {
            return new PythonLintResult(source, parse.Diagnostics);
        }

        var suppressions = ReadSuppressions(source, parse.Comments, cancellationToken);
        var diagnostics = new List<Diagnostic>();
        var pending = new Stack<(PythonNode Node, int Offset)>();
        pending.Push((parse.Module, 0));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, offset) = item;
            switch (node)
            {
                case PythonExceptHandler { Type: null } handler:
                    Report(Rules[0], new TextSpan(handler.Span.Start, "except".Length), offset);
                    break;
                case PythonParameter { Default: { } value }:
                    if (
                        Unwrap(value)
                        is PythonListExpression
                            or PythonDictionaryExpression
                            or PythonSetExpression
                    )
                    {
                        Report(Rules[1], value.Span, offset);
                    }
                    break;
                case PythonAssertStatement assertion:
                    if (
                        Unwrap(assertion.Condition) is PythonTupleExpression tuple
                        && tuple.Elements.Any(element => element is not PythonStarredExpression)
                    )
                    {
                        Report(Rules[2], assertion.Condition.Span, offset);
                    }
                    break;
            }

            foreach (var child in PythonAstTraversal.GetChildren(node))
            {
                if (child is not null)
                {
                    // Embedded f/t-string expressions are parsed in their own source buffer.
                    // Their spans are relative to the interpolation, including for nested strings.
                    var childOffset = node is PythonFormattedStringInterpolationPart interpolation
                        ? offset + interpolation.Span.Start
                        : offset;
                    pending.Push((child, childOffset));
                }
            }
        }

        if (enabled.Contains("DPYL004") || enabled.Contains("DPYL005"))
        {
            var model = PythonSymbolBinder.Analyze(parse.Module, cancellationToken);
            if (enabled.Contains("DPYL004"))
                foreach (var import in UnusedImportAnalysis.Find(model, cancellationToken))
                    Report(Rules[3], import.Occurrence.Span, 0);
            if (enabled.Contains("DPYL005"))
                foreach (
                    var occurrence in UndefinedNameAnalysis.Find(
                        model,
                        options.KnownGlobals,
                        cancellationToken
                    )
                )
                    Report(
                        Rules[4],
                        occurrence.Span,
                        0,
                        $"Name '{occurrence.Name}' has no visible definition."
                    );
        }

        return new PythonLintResult(source, diagnostics);

        void Report(PythonLintRule rule, TextSpan span, int offset, string? message = null)
        {
            if (!enabled.Contains(rule.Code))
            {
                return;
            }
            span = new TextSpan(span.Start + offset, span.Length);
            var line = source.GetLinePosition(span.Start).Line;
            if (suppressions.TryGetValue(line, out var ignored) && ignored.Contains(rule.Code))
            {
                return;
            }
            diagnostics.Add(
                new Diagnostic(
                    rule.Code,
                    message ?? rule.Description,
                    DiagnosticSeverity.Warning,
                    span
                )
            );
        }
    }

    private static bool IsIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        var parsed = PythonParser.Parse(new SourceText(name));
        return parsed.Success
            && parsed.Module.Statements
                is [PythonExpressionStatement { Expression: PythonNameExpression identifier }]
            && identifier.Name == name
            && identifier.Span.Length == name.Length;
    }

    private static PythonExpression Unwrap(PythonExpression expression)
    {
        while (expression is PythonParenthesizedExpression parenthesized)
        {
            expression = parenthesized.Expression;
        }
        return expression;
    }

    private static Dictionary<int, HashSet<string>> ReadSuppressions(
        SourceText source,
        IReadOnlyList<TextSpan> comments,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<int, HashSet<string>>();
        const string prefix = "dotpython: ignore[";
        foreach (var span in comments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = source.GetText(span).AsSpan()[1..].Trim();
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            text = text[prefix.Length..];
            var end = text.IndexOf(']');
            if (end < 0)
            {
                continue;
            }
            // A trailing explanation is allowed after the closing bracket.
            var codes = text[..end].ToString().Split(',', StringSplitOptions.TrimEntries);
            result[source.GetLinePosition(span.Start).Line] = new HashSet<string>(
                codes,
                StringComparer.Ordinal
            );
        }
        return result;
    }
}
