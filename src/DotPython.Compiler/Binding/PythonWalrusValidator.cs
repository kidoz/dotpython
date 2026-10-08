using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Compiler.Binding;

/// <summary>
/// The scope rules CPython enforces at compile time for assignment expressions, which
/// the grammar alone cannot express. A comprehension is its own scope, so a walrus
/// written inside one binds in the nearest enclosing scope — but several placements
/// are rejected outright rather than bound anywhere.
/// </summary>
internal sealed class PythonWalrusValidator
{
    private readonly List<Diagnostic> _diagnostics;
    private readonly List<HashSet<string>> _comprehensionTargets = [];
    private readonly List<PythonScopeKind> _scopes = [PythonScopeKind.Module];
    private bool _inComprehensionIterable;

    private PythonWalrusValidator(List<Diagnostic> diagnostics) => _diagnostics = diagnostics;

    /// <summary>Reports every assignment-expression placement the module may not use.</summary>
    internal static void Validate(PythonModule module, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var validator = new PythonWalrusValidator(diagnostics);
        foreach (var statement in module.Statements)
        {
            validator.Visit(statement);
        }
    }

    private void Visit(PythonNode? node)
    {
        switch (node)
        {
            case null:
                return;

            case PythonAssignmentExpression walrus:
                CheckPlacement(walrus);
                Visit(walrus.Value);
                return;

            case PythonExpressionStatement { Expression: PythonAssignmentExpression bare }:
                // `(x := 1)` is a legal statement; a bare `x := 1` is not.
                Report("DPY3122", "invalid syntax", bare.Span);
                Visit(bare);
                return;

            case PythonFunctionDefinitionStatement function:
                // Decorators, defaults and annotations evaluate in the enclosing scope.
                VisitAll(function.Decorators);
                VisitParameters(function.Parameters);
                Visit(function.ReturnAnnotation);
                EnterFunctionBody(function.Body);
                return;

            case PythonLambdaExpression lambda:
                VisitParameters(lambda.Parameters);
                EnterFunctionBody([lambda.Body]);
                return;

            case PythonClassDefinitionStatement @class:
                VisitAll(@class.Decorators);
                VisitAll(@class.Bases);
                if (@class.KeywordArguments is { } keywords)
                {
                    foreach (var keyword in keywords)
                    {
                        Visit(keyword.Value);
                    }
                }

                EnterClassBody(@class.Body);
                return;

            case PythonComprehensionForClause forClause:
                VisitIterable(forClause.Iterable);
                Visit(forClause.Target);
                return;

            case PythonListComprehensionExpression list:
                VisitComprehension([list.Element], list.Clauses);
                return;

            case PythonSetComprehensionExpression set:
                VisitComprehension([set.Element], set.Clauses);
                return;

            case PythonGeneratorExpression generator:
                VisitComprehension([generator.Element], generator.Clauses);
                return;

            case PythonDictionaryComprehensionExpression dictionary:
                VisitComprehension([dictionary.Key, dictionary.Value], dictionary.Clauses);
                return;
        }

        foreach (var child in PythonAstTraversal.GetChildren(node))
        {
            Visit(child);
        }
    }

    /// <summary>
    /// A comprehension evaluates its first iterable in the scope that contains it, then
    /// binds every `for` target before the element and the remaining clauses. All the
    /// targets are in scope at once, because a walrus may not rebind any of them.
    /// </summary>
    private void VisitComprehension(
        IReadOnlyList<PythonExpression> elements,
        IReadOnlyList<PythonComprehensionClause> clauses
    )
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clause in clauses)
        {
            if (clause is PythonComprehensionForClause forClause)
            {
                CollectTargetNames(forClause.Target, targets);
            }
        }

        if (clauses.Count > 0 && clauses[0] is PythonComprehensionForClause first)
        {
            VisitIterable(first.Iterable);
        }

        _comprehensionTargets.Add(targets);
        foreach (var element in elements)
        {
            Visit(element);
        }

        for (var index = 1; index < clauses.Count; index++)
        {
            Visit(clauses[index]);
        }

        _comprehensionTargets.RemoveAt(_comprehensionTargets.Count - 1);
    }

    /// <summary>
    /// Marks a comprehension `for` iterable. The rule is textual: it holds for every
    /// iterable of every comprehension and is inherited by everything nested inside,
    /// including lambdas and further comprehensions.
    /// </summary>
    private void VisitIterable(PythonNode? iterable)
    {
        var saved = _inComprehensionIterable;
        _inComprehensionIterable = true;
        Visit(iterable);
        _inComprehensionIterable = saved;
    }

    /// <summary>
    /// A function body is a fresh scope, so it neither inherits the enclosing
    /// comprehension's targets nor its own class-body state. The textual iterable rule
    /// does carry in.
    /// </summary>
    private void EnterFunctionBody(IReadOnlyList<PythonNode> body)
    {
        var savedTargets = _comprehensionTargets.ToArray();
        _comprehensionTargets.Clear();
        _scopes.Add(PythonScopeKind.Function);
        foreach (var node in body)
        {
            Visit(node);
        }

        _scopes.RemoveAt(_scopes.Count - 1);
        _comprehensionTargets.AddRange(savedTargets);
    }

    private void EnterClassBody(IReadOnlyList<PythonStatement> body)
    {
        _scopes.Add(PythonScopeKind.Class);
        foreach (var node in body)
        {
            Visit(node);
        }

        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void VisitAll(IReadOnlyList<PythonExpression> expressions)
    {
        foreach (var expression in expressions)
        {
            Visit(expression);
        }
    }

    private void VisitParameters(IReadOnlyList<PythonParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            Visit(parameter.Default);
            Visit(parameter.Annotation);
        }
    }

    private void CheckPlacement(PythonAssignmentExpression walrus)
    {
        if (_inComprehensionIterable)
        {
            Report(
                "DPY3120",
                "assignment expression cannot be used in a comprehension iterable expression",
                walrus.Span
            );
            return;
        }

        if (_comprehensionTargets.Count == 0)
        {
            return;
        }

        var name = walrus.Target.Name;
        foreach (var targets in _comprehensionTargets)
        {
            if (targets.Contains(name))
            {
                Report(
                    "DPY3119",
                    $"assignment expression cannot rebind comprehension iteration variable '{name}'",
                    walrus.Span
                );
                return;
            }
        }

        if (_scopes[^1] == PythonScopeKind.Class)
        {
            Report(
                "DPY3121",
                "assignment expression within a comprehension cannot be used in a class body",
                walrus.Span
            );
        }
    }

    /// <summary>Collects the names a `for` target binds, through tuple and starred forms.</summary>
    private static void CollectTargetNames(PythonExpression target, HashSet<string> names)
    {
        switch (target)
        {
            case PythonNameExpression name:
                names.Add(name.Name);
                return;

            case PythonTupleExpression tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectTargetNames(element, names);
                }

                return;

            case PythonListExpression list:
                foreach (var element in list.Elements)
                {
                    CollectTargetNames(element, names);
                }

                return;

            case PythonStarredExpression starred:
                CollectTargetNames(starred.Operand, names);
                return;

            default:
                // Attribute and subscript targets bind no name.
                return;
        }
    }

    private void Report(string code, string message, TextSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, DiagnosticSeverity.Error, span));
}
