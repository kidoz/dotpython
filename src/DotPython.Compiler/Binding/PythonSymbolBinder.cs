using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Compiler.Binding;

public static class PythonSymbolBinder
{
    public static PythonBindingResult Bind(PythonModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var diagnostics = new List<Diagnostic>();
        var moduleScope = BindScope(
            PythonScopeKind.Module,
            "<module>",
            null,
            [],
            module.Statements,
            [],
            diagnostics
        );
        return new PythonBindingResult(moduleScope, diagnostics);
    }

    private static PythonBoundScope BindScope(
        PythonScopeKind kind,
        string name,
        PythonFunctionDefinitionStatement? definition,
        IReadOnlyList<PythonParameter> parameters,
        IReadOnlyList<PythonStatement> statements,
        IReadOnlyList<PythonBoundScope> ancestors,
        List<Diagnostic> diagnostics
    )
    {
        var parameterNames = new List<string>();
        var localNames = new List<string>();
        var localNameSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!localNameSet.Add(parameter.Name))
            {
                Report(
                    diagnostics,
                    "DPY3102",
                    $"Duplicate parameter '{parameter.Name}'.",
                    parameter.Span
                );
                continue;
            }

            parameterNames.Add(parameter.Name);
            localNames.Add(parameter.Name);
        }

        CollectBoundNames(statements, localNames, localNameSet);

        var references = new List<NameReference>();
        CollectReferences(statements, references, diagnostics, kind);
        var referencedNames = references
            .Select(reference => reference.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (kind == PythonScopeKind.Function)
        {
            foreach (var reference in references)
            {
                if (localNameSet.Contains(reference.Name))
                {
                    continue;
                }

                var closureOwner = ancestors.LastOrDefault(ancestor =>
                    ancestor.Kind == PythonScopeKind.Function && ancestor.IsLocal(reference.Name)
                );
                if (closureOwner is not null)
                {
                    Report(
                        diagnostics,
                        "DPY3101",
                        $"Closure variable '{reference.Name}' from function "
                            + $"'{closureOwner.Name}' is not supported yet.",
                        reference.Span
                    );
                }
            }
        }

        var children = new List<PythonBoundScope>();
        var scope = new PythonBoundScope(
            kind,
            name,
            definition,
            parameterNames,
            localNames,
            referencedNames,
            children
        );
        var childAncestors = ancestors.Append(scope).ToArray();
        foreach (var function in EnumerateFunctions(statements))
        {
            children.Add(
                BindScope(
                    PythonScopeKind.Function,
                    function.Name.Name,
                    function,
                    function.Parameters,
                    function.Body,
                    childAncestors,
                    diagnostics
                )
            );
        }

        return scope;
    }

    private static void CollectBoundNames(
        IReadOnlyList<PythonStatement> statements,
        List<string> localNames,
        HashSet<string> localNameSet
    )
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case PythonAssignmentStatement assignment:
                    AddLocal(assignment.Target.Name, localNames, localNameSet);
                    break;
                case PythonFunctionDefinitionStatement function:
                    AddLocal(function.Name.Name, localNames, localNameSet);
                    break;
                case PythonIfStatement conditional:
                    foreach (var clause in conditional.Clauses)
                    {
                        CollectBoundNames(clause.Body, localNames, localNameSet);
                    }

                    CollectBoundNames(conditional.ElseBody, localNames, localNameSet);
                    break;
                case PythonWhileStatement loop:
                    CollectBoundNames(loop.Body, localNames, localNameSet);
                    CollectBoundNames(loop.ElseBody, localNames, localNameSet);
                    break;
            }
        }
    }

    private static void CollectReferences(
        IReadOnlyList<PythonStatement> statements,
        List<NameReference> references,
        List<Diagnostic> diagnostics,
        PythonScopeKind scopeKind
    )
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case PythonAssignmentStatement assignment:
                    CollectReferences(assignment.Value, references);
                    break;
                case PythonExpressionStatement expression:
                    CollectReferences(expression.Expression, references);
                    break;
                case PythonReturnStatement returnStatement:
                    if (scopeKind != PythonScopeKind.Function)
                    {
                        Report(
                            diagnostics,
                            "DPY3103",
                            "'return' outside function.",
                            returnStatement.Span
                        );
                    }

                    if (returnStatement.Value is not null)
                    {
                        CollectReferences(returnStatement.Value, references);
                    }

                    break;
                case PythonIfStatement conditional:
                    foreach (var clause in conditional.Clauses)
                    {
                        CollectReferences(clause.Condition, references);
                        CollectReferences(clause.Body, references, diagnostics, scopeKind);
                    }

                    CollectReferences(conditional.ElseBody, references, diagnostics, scopeKind);
                    break;
                case PythonWhileStatement loop:
                    CollectReferences(loop.Condition, references);
                    CollectReferences(loop.Body, references, diagnostics, scopeKind);
                    CollectReferences(loop.ElseBody, references, diagnostics, scopeKind);
                    break;
                case PythonFunctionDefinitionStatement:
                    break;
            }
        }
    }

    private static void CollectReferences(
        PythonExpression expression,
        List<NameReference> references
    )
    {
        switch (expression)
        {
            case PythonNameExpression name:
                references.Add(new NameReference(name.Name, name.Span));
                break;
            case PythonUnaryExpression unary:
                CollectReferences(unary.Operand, references);
                break;
            case PythonBinaryExpression binary:
                CollectReferences(binary.Left, references);
                CollectReferences(binary.Right, references);
                break;
            case PythonComparisonExpression comparison:
                CollectReferences(comparison.Left, references);
                foreach (var part in comparison.Comparisons)
                {
                    CollectReferences(part.Right, references);
                }

                break;
            case PythonCallExpression call:
                CollectReferences(call.Target, references);
                foreach (var argument in call.Arguments)
                {
                    CollectReferences(argument, references);
                }

                break;
            case PythonParenthesizedExpression parenthesized:
                CollectReferences(parenthesized.Expression, references);
                break;
        }
    }

    private static IEnumerable<PythonFunctionDefinitionStatement> EnumerateFunctions(
        IReadOnlyList<PythonStatement> statements
    )
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case PythonFunctionDefinitionStatement function:
                    yield return function;
                    break;
                case PythonIfStatement conditional:
                    foreach (var clause in conditional.Clauses)
                    {
                        foreach (var nested in EnumerateFunctions(clause.Body))
                        {
                            yield return nested;
                        }
                    }

                    foreach (var nested in EnumerateFunctions(conditional.ElseBody))
                    {
                        yield return nested;
                    }

                    break;
                case PythonWhileStatement loop:
                    foreach (var nested in EnumerateFunctions(loop.Body))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateFunctions(loop.ElseBody))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static void AddLocal(string name, List<string> localNames, HashSet<string> localNameSet)
    {
        if (localNameSet.Add(name))
        {
            localNames.Add(name);
        }
    }

    private static void Report(
        List<Diagnostic> diagnostics,
        string code,
        string message,
        TextSpan span
    ) => diagnostics.Add(new Diagnostic(code, message, DiagnosticSeverity.Error, span));

    private readonly record struct NameReference(string Name, TextSpan Span);
}
