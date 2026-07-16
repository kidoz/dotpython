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
        ResolveClosureVariables(moduleScope, []);
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

        var children = new List<PythonBoundScope>();
        var cellVariableNames = new List<string>();
        var freeVariableNames = new List<string>();
        var scope = new PythonBoundScope(
            kind,
            name,
            definition,
            parameterNames,
            localNames,
            referencedNames,
            cellVariableNames,
            freeVariableNames,
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

    private static void ResolveClosureVariables(
        PythonBoundScope scope,
        IReadOnlyList<PythonBoundScope> enclosingFunctions
    )
    {
        if (scope.Kind == PythonScopeKind.Function)
        {
            foreach (var name in scope.ReferencedNames)
            {
                if (!scope.IsLocal(name) && FindClosureOwner(enclosingFunctions, name) is not null)
                {
                    scope.AddFreeVariable(name);
                }
            }
        }

        var childEnclosingFunctions =
            scope.Kind == PythonScopeKind.Function
                ? enclosingFunctions.Append(scope).ToArray()
                : enclosingFunctions;
        foreach (var child in scope.Children)
        {
            ResolveClosureVariables(child, childEnclosingFunctions);
        }

        if (scope.Kind != PythonScopeKind.Function)
        {
            return;
        }

        foreach (var child in scope.Children)
        {
            foreach (var name in child.FreeVariableNames)
            {
                if (scope.IsLocal(name))
                {
                    scope.AddCellVariable(name);
                }
                else if (FindClosureOwner(enclosingFunctions, name) is not null)
                {
                    scope.AddFreeVariable(name);
                }
            }
        }

        scope.OrderCellVariablesByLocalDeclaration();
    }

    private static PythonBoundScope? FindClosureOwner(
        IReadOnlyList<PythonBoundScope> enclosingFunctions,
        string name
    )
    {
        for (var index = enclosingFunctions.Count - 1; index >= 0; index--)
        {
            if (enclosingFunctions[index].IsLocal(name))
            {
                return enclosingFunctions[index];
            }
        }

        return null;
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
                    if (assignment.Target is PythonNameExpression assignmentTarget)
                    {
                        AddLocal(assignmentTarget.Name, localNames, localNameSet);
                    }

                    break;
                case PythonFunctionDefinitionStatement function:
                    AddLocal(function.Name.Name, localNames, localNameSet);
                    break;
                case PythonImportStatement importStatement:
                    foreach (var import in importStatement.Imports)
                    {
                        AddLocal(
                            import.Alias ?? GetTopLevelModuleName(import.Name),
                            localNames,
                            localNameSet
                        );
                    }

                    break;
                case PythonFromImportStatement fromImportStatement:
                    foreach (var import in fromImportStatement.Imports)
                    {
                        AddLocal(import.Alias ?? import.Name, localNames, localNameSet);
                    }

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
                case PythonForStatement loop:
                    AddLocal(loop.Target.Name, localNames, localNameSet);
                    CollectBoundNames(loop.Body, localNames, localNameSet);
                    CollectBoundNames(loop.ElseBody, localNames, localNameSet);
                    break;
                case PythonTryStatement tryStatement:
                    CollectBoundNames(tryStatement.Body, localNames, localNameSet);
                    foreach (var handler in tryStatement.Handlers)
                    {
                        if (handler.Target is not null)
                        {
                            AddLocal(handler.Target.Name, localNames, localNameSet);
                        }

                        CollectBoundNames(handler.Body, localNames, localNameSet);
                    }

                    CollectBoundNames(tryStatement.ElseBody, localNames, localNameSet);
                    CollectBoundNames(tryStatement.FinallyBody, localNames, localNameSet);
                    break;
            }
        }
    }

    private static string GetTopLevelModuleName(string name)
    {
        var separator = name.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? name : name[..separator];
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
                    if (assignment.Target is PythonSubscriptionExpression subscription)
                    {
                        CollectReferences(subscription.Target, references);
                        CollectReferences(subscription.Index, references);
                    }

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
                case PythonRaiseStatement raiseStatement:
                    if (raiseStatement.Exception is not null)
                    {
                        CollectReferences(raiseStatement.Exception, references);
                    }

                    if (raiseStatement.Cause is not null)
                    {
                        CollectReferences(raiseStatement.Cause, references);
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
                case PythonForStatement loop:
                    CollectReferences(loop.Iterable, references);
                    CollectReferences(loop.Body, references, diagnostics, scopeKind);
                    CollectReferences(loop.ElseBody, references, diagnostics, scopeKind);
                    break;
                case PythonTryStatement tryStatement:
                    CollectReferences(tryStatement.Body, references, diagnostics, scopeKind);
                    foreach (var handler in tryStatement.Handlers)
                    {
                        if (handler.Type is not null)
                        {
                            CollectReferences(handler.Type, references);
                        }

                        CollectReferences(handler.Body, references, diagnostics, scopeKind);
                    }

                    CollectReferences(tryStatement.ElseBody, references, diagnostics, scopeKind);
                    CollectReferences(tryStatement.FinallyBody, references, diagnostics, scopeKind);
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
            case PythonListExpression list:
                foreach (var element in list.Elements)
                {
                    CollectReferences(element, references);
                }

                break;
            case PythonTupleExpression tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectReferences(element, references);
                }

                break;
            case PythonDictionaryExpression dictionary:
                foreach (var item in dictionary.Items)
                {
                    CollectReferences(item.Key, references);
                    CollectReferences(item.Value, references);
                }

                break;
            case PythonSubscriptionExpression subscription:
                CollectReferences(subscription.Target, references);
                CollectReferences(subscription.Index, references);
                break;
            case PythonAttributeExpression attribute:
                CollectReferences(attribute.Target, references);
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
                case PythonForStatement loop:
                    foreach (var nested in EnumerateFunctions(loop.Body))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateFunctions(loop.ElseBody))
                    {
                        yield return nested;
                    }

                    break;
                case PythonTryStatement tryStatement:
                    foreach (var nested in EnumerateFunctions(tryStatement.Body))
                    {
                        yield return nested;
                    }

                    foreach (var handler in tryStatement.Handlers)
                    {
                        foreach (var nested in EnumerateFunctions(handler.Body))
                        {
                            yield return nested;
                        }
                    }

                    foreach (var nested in EnumerateFunctions(tryStatement.ElseBody))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateFunctions(tryStatement.FinallyBody))
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
