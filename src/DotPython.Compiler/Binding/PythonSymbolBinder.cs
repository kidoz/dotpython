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
        ResolveClosureVariables(moduleScope, [], diagnostics);
        return new PythonBindingResult(moduleScope, diagnostics);
    }

    private static PythonBoundScope BindScope(
        PythonScopeKind kind,
        string name,
        PythonNode? definition,
        IReadOnlyList<PythonParameter> parameters,
        IReadOnlyList<PythonStatement> statements,
        IReadOnlyList<PythonBoundScope> ancestors,
        List<Diagnostic> diagnostics
    )
    {
        var declaredGlobalNames = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
        var declaredNonlocalNames = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
        CollectScopeDeclarations(
            kind,
            statements,
            declaredGlobalNames,
            declaredNonlocalNames,
            diagnostics
        );

        var parameterNames = new List<string>();
        var localNames = new List<string>();
        var localNameSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (declaredGlobalNames.TryGetValue(parameter.Name, out var globalSpan))
            {
                Report(
                    diagnostics,
                    "DPY3108",
                    $"The name '{parameter.Name}' is a parameter and cannot be declared global.",
                    globalSpan
                );
            }

            if (declaredNonlocalNames.TryGetValue(parameter.Name, out var nonlocalSpan))
            {
                Report(
                    diagnostics,
                    "DPY3108",
                    $"The name '{parameter.Name}' is a parameter and cannot be declared nonlocal.",
                    nonlocalSpan
                );
            }

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

        var excludedNames = new HashSet<string>(declaredGlobalNames.Keys, StringComparer.Ordinal);
        excludedNames.UnionWith(declaredNonlocalNames.Keys);
        CollectBoundNames(statements, localNames, localNameSet, excludedNames);

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
            children,
            declaredGlobalNames,
            declaredNonlocalNames
        );
        var childAncestors = ancestors.Append(scope).ToArray();
        foreach (var definitionNode in EnumerateScopeDefinitions(statements))
        {
            children.Add(
                definitionNode switch
                {
                    PythonFunctionDefinitionStatement function => BindScope(
                        PythonScopeKind.Function,
                        function.Name.Name,
                        function,
                        function.Parameters,
                        function.Body,
                        childAncestors,
                        diagnostics
                    ),
                    PythonClassDefinitionStatement @class => BindScope(
                        PythonScopeKind.Class,
                        @class.Name.Name,
                        @class,
                        [],
                        @class.Body,
                        childAncestors,
                        diagnostics
                    ),
                    PythonListComprehensionExpression or PythonDictionaryComprehensionExpression =>
                        BindComprehensionScope(
                            (PythonExpression)definitionNode,
                            childAncestors,
                            diagnostics
                        ),
                    PythonLambdaExpression lambdaExpression => BindLambdaScope(
                        lambdaExpression,
                        childAncestors,
                        diagnostics
                    ),
                    _ => throw new InvalidOperationException(
                        "The scope definition kind is invalid."
                    ),
                }
            );
        }

        return scope;
    }

    private static PythonBoundScope BindComprehensionScope(
        PythonExpression comprehension,
        IReadOnlyList<PythonBoundScope> ancestors,
        List<Diagnostic> diagnostics
    )
    {
        string name;
        IReadOnlyList<PythonComprehensionClause> clauses;
        var innerExpressions = new List<PythonExpression>();
        switch (comprehension)
        {
            case PythonListComprehensionExpression listComprehension:
                name = "<listcomp>";
                clauses = listComprehension.Clauses;
                innerExpressions.Add(listComprehension.Element);
                break;
            case PythonDictionaryComprehensionExpression dictionaryComprehension:
                name = "<dictcomp>";
                clauses = dictionaryComprehension.Clauses;
                innerExpressions.Add(dictionaryComprehension.Key);
                innerExpressions.Add(dictionaryComprehension.Value);
                break;
            default:
                throw new InvalidOperationException("The comprehension kind is invalid.");
        }

        for (var index = 0; index < clauses.Count; index++)
        {
            switch (clauses[index])
            {
                case PythonComprehensionForClause forClause when index != 0:
                    innerExpressions.Add(forClause.Iterable);
                    break;
                case PythonComprehensionIfClause ifClause:
                    innerExpressions.Add(ifClause.Condition);
                    break;
            }
        }

        var localNames = new List<string> { ".0" };
        var localNameSet = new HashSet<string>(StringComparer.Ordinal) { ".0" };
        var excludedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clause in clauses)
        {
            if (clause is PythonComprehensionForClause forClause)
            {
                CollectTargetNames(forClause.Target, localNames, localNameSet, excludedNames);
            }
        }

        var references = new List<NameReference>();
        foreach (var expression in innerExpressions)
        {
            CollectReferences(expression, references);
        }

        foreach (var clause in clauses)
        {
            if (clause is PythonComprehensionForClause forClause)
            {
                CollectTargetReferences(forClause.Target, references);
            }
        }

        var referencedNames = references
            .Select(reference => reference.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var children = new List<PythonBoundScope>();
        var scope = new PythonBoundScope(
            PythonScopeKind.Function,
            name,
            comprehension,
            [".0"],
            localNames,
            referencedNames,
            [],
            [],
            children,
            new Dictionary<string, TextSpan>(StringComparer.Ordinal),
            new Dictionary<string, TextSpan>(StringComparer.Ordinal)
        );
        var childAncestors = ancestors.Append(scope).ToArray();
        foreach (var expression in innerExpressions)
        {
            foreach (var nested in EnumerateComprehensions(expression))
            {
                children.Add(BindExpressionScope(nested, childAncestors, diagnostics));
            }
        }

        return scope;
    }

    private static PythonBoundScope BindExpressionScope(
        PythonExpression definition,
        IReadOnlyList<PythonBoundScope> ancestors,
        List<Diagnostic> diagnostics
    ) =>
        definition switch
        {
            PythonLambdaExpression lambdaExpression => BindLambdaScope(
                lambdaExpression,
                ancestors,
                diagnostics
            ),
            _ => BindComprehensionScope(definition, ancestors, diagnostics),
        };

    private static PythonBoundScope BindLambdaScope(
        PythonLambdaExpression lambdaExpression,
        IReadOnlyList<PythonBoundScope> ancestors,
        List<Diagnostic> diagnostics
    )
    {
        var parameterNames = new List<string>();
        var localNames = new List<string>();
        var localNameSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in lambdaExpression.Parameters)
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

        var references = new List<NameReference>();
        CollectReferences(lambdaExpression.Body, references);
        var referencedNames = references
            .Select(reference => reference.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var children = new List<PythonBoundScope>();
        var scope = new PythonBoundScope(
            PythonScopeKind.Function,
            "<lambda>",
            lambdaExpression,
            parameterNames,
            localNames,
            referencedNames,
            [],
            [],
            children,
            new Dictionary<string, TextSpan>(StringComparer.Ordinal),
            new Dictionary<string, TextSpan>(StringComparer.Ordinal)
        );
        var childAncestors = ancestors.Append(scope).ToArray();
        foreach (var nested in EnumerateComprehensions(lambdaExpression.Body))
        {
            children.Add(BindExpressionScope(nested, childAncestors, diagnostics));
        }

        return scope;
    }

    private static void CollectScopeDeclarations(
        PythonScopeKind kind,
        IReadOnlyList<PythonStatement> statements,
        Dictionary<string, TextSpan> declaredGlobalNames,
        Dictionary<string, TextSpan> declaredNonlocalNames,
        List<Diagnostic> diagnostics
    )
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case PythonGlobalStatement globalStatement:
                    foreach (var name in globalStatement.Names)
                    {
                        if (declaredNonlocalNames.ContainsKey(name.Name))
                        {
                            Report(
                                diagnostics,
                                "DPY3109",
                                $"The name '{name.Name}' is declared nonlocal and global.",
                                name.Span
                            );
                            continue;
                        }

                        declaredGlobalNames.TryAdd(name.Name, name.Span);
                    }

                    break;
                case PythonNonlocalStatement nonlocalStatement:
                    if (kind != PythonScopeKind.Function)
                    {
                        Report(
                            diagnostics,
                            "DPY3107",
                            "A nonlocal declaration is not allowed at module level.",
                            nonlocalStatement.Span
                        );
                        break;
                    }

                    foreach (var name in nonlocalStatement.Names)
                    {
                        if (declaredGlobalNames.ContainsKey(name.Name))
                        {
                            Report(
                                diagnostics,
                                "DPY3109",
                                $"The name '{name.Name}' is declared nonlocal and global.",
                                name.Span
                            );
                            continue;
                        }

                        declaredNonlocalNames.TryAdd(name.Name, name.Span);
                    }

                    break;
                case PythonIfStatement conditional:
                    foreach (var clause in conditional.Clauses)
                    {
                        CollectScopeDeclarations(
                            kind,
                            clause.Body,
                            declaredGlobalNames,
                            declaredNonlocalNames,
                            diagnostics
                        );
                    }

                    CollectScopeDeclarations(
                        kind,
                        conditional.ElseBody,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    break;
                case PythonWhileStatement loop:
                    CollectScopeDeclarations(
                        kind,
                        loop.Body,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    CollectScopeDeclarations(
                        kind,
                        loop.ElseBody,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    break;
                case PythonForStatement loop:
                    CollectScopeDeclarations(
                        kind,
                        loop.Body,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    CollectScopeDeclarations(
                        kind,
                        loop.ElseBody,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    break;
                case PythonWithStatement withStatement:
                    CollectScopeDeclarations(
                        kind,
                        withStatement.Body,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    break;
                case PythonTryStatement tryStatement:
                    CollectScopeDeclarations(
                        kind,
                        tryStatement.Body,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    foreach (var handler in tryStatement.Handlers)
                    {
                        CollectScopeDeclarations(
                            kind,
                            handler.Body,
                            declaredGlobalNames,
                            declaredNonlocalNames,
                            diagnostics
                        );
                    }

                    CollectScopeDeclarations(
                        kind,
                        tryStatement.ElseBody,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    CollectScopeDeclarations(
                        kind,
                        tryStatement.FinallyBody,
                        declaredGlobalNames,
                        declaredNonlocalNames,
                        diagnostics
                    );
                    break;
            }
        }
    }

    private static void ResolveClosureVariables(
        PythonBoundScope scope,
        IReadOnlyList<PythonBoundScope> enclosingFunctions,
        List<Diagnostic> diagnostics
    )
    {
        if (scope.Kind is PythonScopeKind.Function or PythonScopeKind.Class)
        {
            if (scope.Kind == PythonScopeKind.Function)
            {
                foreach (var (name, span) in scope.DeclaredNonlocalNames)
                {
                    if (FindClosureOwner(enclosingFunctions, name) is null)
                    {
                        Report(
                            diagnostics,
                            "DPY3110",
                            $"No binding for nonlocal '{name}' was found.",
                            span
                        );
                        continue;
                    }

                    scope.AddFreeVariable(name);
                }
            }

            foreach (var name in scope.ReferencedNames)
            {
                if (
                    !scope.IsLocal(name)
                    && !scope.IsDeclaredGlobal(name)
                    && FindClosureOwner(enclosingFunctions, name) is not null
                )
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
            ResolveClosureVariables(child, childEnclosingFunctions, diagnostics);
        }

        if (scope.Kind is not (PythonScopeKind.Function or PythonScopeKind.Class))
        {
            return;
        }

        foreach (var child in scope.Children)
        {
            foreach (var name in child.FreeVariableNames)
            {
                if (scope.Kind == PythonScopeKind.Function && scope.IsLocal(name))
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
        HashSet<string> localNameSet,
        HashSet<string> excludedNames
    )
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case PythonAssignmentStatement assignment:
                    CollectTargetNames(assignment.Target, localNames, localNameSet, excludedNames);
                    break;
                case PythonAugmentedAssignmentStatement augmented:
                    if (augmented.Target is PythonNameExpression augmentedTarget)
                    {
                        AddLocal(augmentedTarget.Name, localNames, localNameSet, excludedNames);
                    }

                    break;
                case PythonDeleteStatement deleteStatement:
                    foreach (var target in deleteStatement.Targets)
                    {
                        if (target is PythonNameExpression deletedName)
                        {
                            AddLocal(deletedName.Name, localNames, localNameSet, excludedNames);
                        }
                    }

                    break;
                case PythonFunctionDefinitionStatement function:
                    AddLocal(function.Name.Name, localNames, localNameSet, excludedNames);
                    break;
                case PythonClassDefinitionStatement @class:
                    AddLocal(@class.Name.Name, localNames, localNameSet, excludedNames);
                    break;
                case PythonImportStatement importStatement:
                    foreach (var import in importStatement.Imports)
                    {
                        AddLocal(
                            import.Alias ?? GetTopLevelModuleName(import.Name),
                            localNames,
                            localNameSet,
                            excludedNames
                        );
                    }

                    break;
                case PythonFromImportStatement fromImportStatement:
                    foreach (var import in fromImportStatement.Imports)
                    {
                        AddLocal(
                            import.Alias ?? import.Name,
                            localNames,
                            localNameSet,
                            excludedNames
                        );
                    }

                    break;
                case PythonIfStatement conditional:
                    foreach (var clause in conditional.Clauses)
                    {
                        CollectBoundNames(clause.Body, localNames, localNameSet, excludedNames);
                    }

                    CollectBoundNames(
                        conditional.ElseBody,
                        localNames,
                        localNameSet,
                        excludedNames
                    );
                    break;
                case PythonWhileStatement loop:
                    CollectBoundNames(loop.Body, localNames, localNameSet, excludedNames);
                    CollectBoundNames(loop.ElseBody, localNames, localNameSet, excludedNames);
                    break;
                case PythonForStatement loop:
                    CollectTargetNames(loop.Target, localNames, localNameSet, excludedNames);
                    CollectBoundNames(loop.Body, localNames, localNameSet, excludedNames);
                    CollectBoundNames(loop.ElseBody, localNames, localNameSet, excludedNames);
                    break;
                case PythonWithStatement withStatement:
                    foreach (var item in withStatement.Items)
                    {
                        if (item.Target is not null)
                        {
                            CollectTargetNames(
                                item.Target,
                                localNames,
                                localNameSet,
                                excludedNames
                            );
                        }
                    }

                    CollectBoundNames(withStatement.Body, localNames, localNameSet, excludedNames);
                    break;
                case PythonTryStatement tryStatement:
                    CollectBoundNames(tryStatement.Body, localNames, localNameSet, excludedNames);
                    foreach (var handler in tryStatement.Handlers)
                    {
                        if (handler.Target is not null)
                        {
                            AddLocal(handler.Target.Name, localNames, localNameSet, excludedNames);
                        }

                        CollectBoundNames(handler.Body, localNames, localNameSet, excludedNames);
                    }

                    CollectBoundNames(
                        tryStatement.ElseBody,
                        localNames,
                        localNameSet,
                        excludedNames
                    );
                    CollectBoundNames(
                        tryStatement.FinallyBody,
                        localNames,
                        localNameSet,
                        excludedNames
                    );
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
                    CollectTargetReferences(assignment.Target, references);
                    break;
                case PythonAugmentedAssignmentStatement augmented:
                    CollectReferences(augmented.Target, references);
                    CollectReferences(augmented.Value, references);
                    break;
                case PythonAssertStatement assertStatement:
                    CollectReferences(assertStatement.Condition, references);
                    if (assertStatement.Message is not null)
                    {
                        CollectReferences(assertStatement.Message, references);
                    }

                    break;
                case PythonDeleteStatement deleteStatement:
                    foreach (var target in deleteStatement.Targets)
                    {
                        CollectTargetReferences(target, references);
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
                case PythonWithStatement withStatement:
                    foreach (var item in withStatement.Items)
                    {
                        CollectReferences(item.Context, references);
                        if (item.Target is not null)
                        {
                            CollectTargetReferences(item.Target, references);
                        }
                    }

                    CollectReferences(withStatement.Body, references, diagnostics, scopeKind);
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
                case PythonFunctionDefinitionStatement function:
                    foreach (var decorator in function.Decorators)
                    {
                        CollectReferences(decorator, references);
                    }

                    foreach (var parameter in function.Parameters)
                    {
                        if (parameter.Default is not null)
                        {
                            CollectReferences(parameter.Default, references);
                        }
                    }

                    break;
                case PythonClassDefinitionStatement @class:
                    foreach (var decorator in @class.Decorators)
                    {
                        CollectReferences(decorator, references);
                    }

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

                foreach (var keywordArgument in call.KeywordArguments)
                {
                    CollectReferences(keywordArgument.Value, references);
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
            case PythonSliceExpression slice:
                if (slice.Start is not null)
                {
                    CollectReferences(slice.Start, references);
                }

                if (slice.Stop is not null)
                {
                    CollectReferences(slice.Stop, references);
                }

                if (slice.Step is not null)
                {
                    CollectReferences(slice.Step, references);
                }

                break;
            case PythonAttributeExpression attribute:
                CollectReferences(attribute.Target, references);
                break;
            case PythonParenthesizedExpression parenthesized:
                CollectReferences(parenthesized.Expression, references);
                break;
            case PythonSetExpression setExpression:
                foreach (var element in setExpression.Elements)
                {
                    CollectReferences(element, references);
                }

                break;
            case PythonFormattedStringExpression formatted:
                foreach (var part in formatted.Parts)
                {
                    if (part is PythonFormattedStringInterpolationPart interpolation)
                    {
                        CollectReferences(interpolation.Expression, references);
                    }
                }

                break;
            case PythonLambdaExpression lambdaExpression:
                foreach (var parameter in lambdaExpression.Parameters)
                {
                    if (parameter.Default is not null)
                    {
                        CollectReferences(parameter.Default, references);
                    }
                }

                break;
            case PythonListComprehensionExpression listComprehension:
                CollectFirstIterableReferences(listComprehension.Clauses, references);
                break;
            case PythonDictionaryComprehensionExpression dictionaryComprehension:
                CollectFirstIterableReferences(dictionaryComprehension.Clauses, references);
                break;
        }
    }

    private static void CollectFirstIterableReferences(
        IReadOnlyList<PythonComprehensionClause> clauses,
        List<NameReference> references
    )
    {
        if (clauses.Count != 0 && clauses[0] is PythonComprehensionForClause firstClause)
        {
            CollectReferences(firstClause.Iterable, references);
        }
    }

    private static IEnumerable<PythonNode> EnumerateScopeDefinitions(
        IReadOnlyList<PythonStatement> statements
    )
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case PythonFunctionDefinitionStatement function:
                    foreach (var decorator in function.Decorators)
                    {
                        foreach (var nested in EnumerateComprehensions(decorator))
                        {
                            yield return nested;
                        }
                    }

                    foreach (var parameter in function.Parameters)
                    {
                        if (parameter.Default is null)
                        {
                            continue;
                        }

                        foreach (var nested in EnumerateComprehensions(parameter.Default))
                        {
                            yield return nested;
                        }
                    }

                    yield return function;
                    break;
                case PythonClassDefinitionStatement @class:
                    foreach (var decorator in @class.Decorators)
                    {
                        foreach (var nested in EnumerateComprehensions(decorator))
                        {
                            yield return nested;
                        }
                    }

                    yield return @class;
                    break;
                case PythonAssignmentStatement assignment:
                    foreach (var nested in EnumerateComprehensions(assignment.Value))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateComprehensions(assignment.Target))
                    {
                        yield return nested;
                    }

                    break;
                case PythonAugmentedAssignmentStatement augmented:
                    foreach (var nested in EnumerateComprehensions(augmented.Target))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateComprehensions(augmented.Value))
                    {
                        yield return nested;
                    }

                    break;
                case PythonAssertStatement assertStatement:
                    foreach (var nested in EnumerateComprehensions(assertStatement.Condition))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateOptionalComprehensions(assertStatement.Message))
                    {
                        yield return nested;
                    }

                    break;
                case PythonDeleteStatement deleteStatement:
                    foreach (var target in deleteStatement.Targets)
                    {
                        foreach (var nested in EnumerateComprehensions(target))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case PythonExpressionStatement expressionStatement:
                    foreach (var nested in EnumerateComprehensions(expressionStatement.Expression))
                    {
                        yield return nested;
                    }

                    break;
                case PythonReturnStatement { Value: not null } returnStatement:
                    foreach (var nested in EnumerateComprehensions(returnStatement.Value))
                    {
                        yield return nested;
                    }

                    break;
                case PythonRaiseStatement raiseStatement:
                    foreach (
                        var nested in EnumerateOptionalComprehensions(raiseStatement.Exception)
                    )
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateOptionalComprehensions(raiseStatement.Cause))
                    {
                        yield return nested;
                    }

                    break;
                case PythonIfStatement conditional:
                    foreach (var clause in conditional.Clauses)
                    {
                        foreach (var nested in EnumerateComprehensions(clause.Condition))
                        {
                            yield return nested;
                        }

                        foreach (var nested in EnumerateScopeDefinitions(clause.Body))
                        {
                            yield return nested;
                        }
                    }

                    foreach (var nested in EnumerateScopeDefinitions(conditional.ElseBody))
                    {
                        yield return nested;
                    }

                    break;
                case PythonWhileStatement loop:
                    foreach (var nested in EnumerateComprehensions(loop.Condition))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateScopeDefinitions(loop.Body))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateScopeDefinitions(loop.ElseBody))
                    {
                        yield return nested;
                    }

                    break;
                case PythonForStatement loop:
                    foreach (var nested in EnumerateComprehensions(loop.Target))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateComprehensions(loop.Iterable))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateScopeDefinitions(loop.Body))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateScopeDefinitions(loop.ElseBody))
                    {
                        yield return nested;
                    }

                    break;
                case PythonWithStatement withStatement:
                    foreach (var item in withStatement.Items)
                    {
                        foreach (var nested in EnumerateComprehensions(item.Context))
                        {
                            yield return nested;
                        }

                        foreach (var nested in EnumerateOptionalComprehensions(item.Target))
                        {
                            yield return nested;
                        }
                    }

                    foreach (var nested in EnumerateScopeDefinitions(withStatement.Body))
                    {
                        yield return nested;
                    }

                    break;
                case PythonTryStatement tryStatement:
                    foreach (var nested in EnumerateScopeDefinitions(tryStatement.Body))
                    {
                        yield return nested;
                    }

                    foreach (var handler in tryStatement.Handlers)
                    {
                        foreach (var nested in EnumerateOptionalComprehensions(handler.Type))
                        {
                            yield return nested;
                        }

                        foreach (var nested in EnumerateScopeDefinitions(handler.Body))
                        {
                            yield return nested;
                        }
                    }

                    foreach (var nested in EnumerateScopeDefinitions(tryStatement.ElseBody))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateScopeDefinitions(tryStatement.FinallyBody))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<PythonExpression> EnumerateOptionalComprehensions(
        PythonExpression? expression
    ) => expression is null ? [] : EnumerateComprehensions(expression);

    private static IEnumerable<PythonExpression> EnumerateComprehensions(
        PythonExpression expression
    )
    {
        switch (expression)
        {
            case PythonListComprehensionExpression listComprehension:
                yield return listComprehension;
                foreach (
                    var nested in EnumerateFirstIterableComprehensions(listComprehension.Clauses)
                )
                {
                    yield return nested;
                }

                break;
            case PythonDictionaryComprehensionExpression dictionaryComprehension:
                yield return dictionaryComprehension;
                foreach (
                    var nested in EnumerateFirstIterableComprehensions(
                        dictionaryComprehension.Clauses
                    )
                )
                {
                    yield return nested;
                }

                break;
            case PythonUnaryExpression unary:
                foreach (var nested in EnumerateComprehensions(unary.Operand))
                {
                    yield return nested;
                }

                break;
            case PythonBinaryExpression binary:
                foreach (var nested in EnumerateComprehensions(binary.Left))
                {
                    yield return nested;
                }

                foreach (var nested in EnumerateComprehensions(binary.Right))
                {
                    yield return nested;
                }

                break;
            case PythonComparisonExpression comparison:
                foreach (var nested in EnumerateComprehensions(comparison.Left))
                {
                    yield return nested;
                }

                foreach (var part in comparison.Comparisons)
                {
                    foreach (var nested in EnumerateComprehensions(part.Right))
                    {
                        yield return nested;
                    }
                }

                break;
            case PythonCallExpression call:
                foreach (var nested in EnumerateComprehensions(call.Target))
                {
                    yield return nested;
                }

                foreach (var argument in call.Arguments)
                {
                    foreach (var nested in EnumerateComprehensions(argument))
                    {
                        yield return nested;
                    }
                }

                foreach (var keywordArgument in call.KeywordArguments)
                {
                    foreach (var nested in EnumerateComprehensions(keywordArgument.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case PythonListExpression list:
                foreach (var element in list.Elements)
                {
                    foreach (var nested in EnumerateComprehensions(element))
                    {
                        yield return nested;
                    }
                }

                break;
            case PythonSetExpression setExpression:
                foreach (var element in setExpression.Elements)
                {
                    foreach (var nested in EnumerateComprehensions(element))
                    {
                        yield return nested;
                    }
                }

                break;
            case PythonFormattedStringExpression formatted:
                foreach (var part in formatted.Parts)
                {
                    if (part is not PythonFormattedStringInterpolationPart interpolation)
                    {
                        continue;
                    }

                    foreach (var nested in EnumerateComprehensions(interpolation.Expression))
                    {
                        yield return nested;
                    }
                }

                break;
            case PythonLambdaExpression lambdaExpression:
                foreach (var parameter in lambdaExpression.Parameters)
                {
                    if (parameter.Default is null)
                    {
                        continue;
                    }

                    foreach (var nested in EnumerateComprehensions(parameter.Default))
                    {
                        yield return nested;
                    }
                }

                yield return lambdaExpression;
                break;
            case PythonTupleExpression tuple:
                foreach (var element in tuple.Elements)
                {
                    foreach (var nested in EnumerateComprehensions(element))
                    {
                        yield return nested;
                    }
                }

                break;
            case PythonDictionaryExpression dictionary:
                foreach (var item in dictionary.Items)
                {
                    foreach (var nested in EnumerateComprehensions(item.Key))
                    {
                        yield return nested;
                    }

                    foreach (var nested in EnumerateComprehensions(item.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case PythonSubscriptionExpression subscription:
                foreach (var nested in EnumerateComprehensions(subscription.Target))
                {
                    yield return nested;
                }

                foreach (var nested in EnumerateComprehensions(subscription.Index))
                {
                    yield return nested;
                }

                break;
            case PythonSliceExpression slice:
                foreach (var nested in EnumerateOptionalComprehensions(slice.Start))
                {
                    yield return nested;
                }

                foreach (var nested in EnumerateOptionalComprehensions(slice.Stop))
                {
                    yield return nested;
                }

                foreach (var nested in EnumerateOptionalComprehensions(slice.Step))
                {
                    yield return nested;
                }

                break;
            case PythonAttributeExpression attribute:
                foreach (var nested in EnumerateComprehensions(attribute.Target))
                {
                    yield return nested;
                }

                break;
            case PythonParenthesizedExpression parenthesized:
                foreach (var nested in EnumerateComprehensions(parenthesized.Expression))
                {
                    yield return nested;
                }

                break;
        }
    }

    private static IEnumerable<PythonExpression> EnumerateFirstIterableComprehensions(
        IReadOnlyList<PythonComprehensionClause> clauses
    ) =>
        clauses.Count != 0 && clauses[0] is PythonComprehensionForClause firstClause
            ? EnumerateComprehensions(firstClause.Iterable)
            : [];

    private static void CollectTargetNames(
        PythonExpression target,
        List<string> localNames,
        HashSet<string> localNameSet,
        HashSet<string> excludedNames
    )
    {
        switch (target)
        {
            case PythonNameExpression name:
                AddLocal(name.Name, localNames, localNameSet, excludedNames);
                break;
            case PythonParenthesizedExpression parenthesized:
                CollectTargetNames(
                    parenthesized.Expression,
                    localNames,
                    localNameSet,
                    excludedNames
                );
                break;
            case PythonTupleExpression tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectTargetNames(element, localNames, localNameSet, excludedNames);
                }

                break;
        }
    }

    private static void CollectTargetReferences(
        PythonExpression target,
        List<NameReference> references
    )
    {
        switch (target)
        {
            case PythonAttributeExpression attribute:
                CollectReferences(attribute.Target, references);
                break;
            case PythonSubscriptionExpression subscription:
                CollectReferences(subscription.Target, references);
                CollectReferences(subscription.Index, references);
                break;
            case PythonParenthesizedExpression parenthesized:
                CollectTargetReferences(parenthesized.Expression, references);
                break;
            case PythonTupleExpression tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectTargetReferences(element, references);
                }

                break;
        }
    }

    private static void AddLocal(
        string name,
        List<string> localNames,
        HashSet<string> localNameSet,
        HashSet<string> excludedNames
    )
    {
        if (!excludedNames.Contains(name) && localNameSet.Add(name))
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
