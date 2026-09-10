using System.Collections.Frozen;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Syntax;
using DotPython.Language.Text;

namespace DotPython.Compiler.Binding;

internal sealed class PythonSemanticModelBuilder
{
    private readonly PythonBindingResult _binding;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<PythonBoundScope, PythonBoundScope?> _parents = [];
    private readonly Dictionary<PythonNode, PythonBoundScope> _scopes = new(
        ReferenceEqualityComparer.Instance
    );
    private readonly Dictionary<(PythonBoundScope Scope, string Name), PythonSymbol> _symbols = [];
    private readonly List<RawOccurrence> _occurrences = [];
    private readonly List<(
        int Index,
        string Module,
        string? Name,
        bool Reexport,
        bool Guarded
    )> _imports = [];
    private readonly HashSet<string> _exports = new(StringComparer.Ordinal);
    private readonly HashSet<PythonBoundScope> _dynamicScopes = [];
    private readonly HashSet<(PythonBoundScope Scope, string Name)> _walrusBindings = [];
    private readonly List<Diagnostic> _extraDiagnostics = [];
    private bool _dynamicExports;

    private PythonSemanticModelBuilder(PythonBindingResult binding, CancellationToken cancellation)
    {
        _binding = binding;
        _cancellation = cancellation;
        Register(binding.ModuleScope, null);
    }

    internal static PythonSemanticModel Build(
        PythonModule module,
        PythonBindingResult binding,
        CancellationToken cancellation
    )
    {
        var builder = new PythonSemanticModelBuilder(binding, cancellation);
        if (binding.Success)
            builder.Visit(module, binding.ModuleScope, 0, false);
        return builder.Finish();
    }

    private void Register(PythonBoundScope scope, PythonBoundScope? parent)
    {
        _parents.Add(scope, parent);
        if (scope.Definition is { } definition)
            _scopes.TryAdd(definition, scope);
        foreach (var name in scope.LocalNames)
            Symbol(scope, name);
        foreach (var child in scope.Children)
            Register(child, scope);
    }

    private PythonBoundScope Nested(PythonNode definition, PythonBoundScope parent)
    {
        if (_scopes.TryGetValue(definition, out var scope))
            return scope;
        // Execution binding deliberately omits annotations. Tooling may still encounter
        // expression scopes there, or inside template-string interpolation expressions.
        if (definition is not PythonExpression expression)
            throw new InvalidOperationException("A statement scope was not bound.");
        var ancestors = new List<PythonBoundScope>();
        for (var current = parent; current is not null; current = _parents[current])
            ancestors.Add(current);
        ancestors.Reverse();
        scope = PythonSymbolBinder.BindExpressionScope(
            expression,
            [.. ancestors],
            _extraDiagnostics
        );
        Register(scope, parent);
        return scope;
    }

    private PythonSymbol Symbol(PythonBoundScope scope, string name)
    {
        if (!_symbols.TryGetValue((scope, name), out var symbol))
            _symbols.Add((scope, name), symbol = new PythonSymbol(scope, name));
        return symbol;
    }

    private PythonBoundScope? Owner(PythonBoundScope scope, string name, bool definition)
    {
        if (scope.IsDeclaredGlobal(name))
            return _binding.ModuleScope;
        if (scope.IsDeclaredNonlocal(name))
            return Enclosing(scope, name, nonlocal: true);
        if (_walrusBindings.Contains((scope, name)))
            return Owner(_parents[scope]!, name, definition);
        if (definition || scope.IsLocal(name) || _symbols.ContainsKey((scope, name)))
            return scope;
        return Enclosing(scope, name, nonlocal: false);
    }

    private PythonBoundScope? Enclosing(PythonBoundScope scope, string name, bool nonlocal)
    {
        for (var current = _parents[scope]; current is not null; current = _parents[current])
        {
            if (current.Kind == PythonScopeKind.Class)
            {
                if (name == "__class__" && current.IsLocal(name))
                    return current;
                continue;
            }
            if (current.Kind == PythonScopeKind.Module)
                return nonlocal ? null : current;
            if (current.IsDeclaredGlobal(name))
                return nonlocal ? null : _binding.ModuleScope;
            if (
                (current.IsLocal(name) || _symbols.ContainsKey((current, name)))
                && !_walrusBindings.Contains((current, name))
            )
                return current;
        }
        return nonlocal ? null : _binding.ModuleScope;
    }

    private int Add(
        string name,
        TextSpan span,
        PythonNameOccurrenceKind kind,
        PythonBoundScope scope,
        int offset,
        bool knownExport = false
    )
    {
        var index = _occurrences.Count;
        _occurrences.Add(new(name, new TextSpan(span.Start + offset, span.Length), kind, scope));
        if (
            kind
            is PythonNameOccurrenceKind.Definition
                or PythonNameOccurrenceKind.Import
                or PythonNameOccurrenceKind.Parameter
                or PythonNameOccurrenceKind.Delete
        )
        {
            var mangled = scope.MangleName(name);
            if (Owner(scope, mangled, true) is { } owner)
            {
                Symbol(owner, mangled);
                if (name == "__all__" && owner == _binding.ModuleScope && !knownExport)
                    _dynamicExports = true;
            }
        }
        return index;
    }

    private void Visit(
        PythonNode? node,
        PythonBoundScope scope,
        int offset,
        bool guarded,
        bool annotation = false
    )
    {
        _cancellation.ThrowIfCancellationRequested();
        if (node is null)
            return;
        switch (node)
        {
            case PythonNameExpression name:
                Add(
                    name.Name,
                    name.Span,
                    annotation
                        ? PythonNameOccurrenceKind.Annotation
                        : PythonNameOccurrenceKind.Read,
                    scope,
                    offset
                );
                if (
                    name.Name == "__all__"
                    && Owner(scope, name.Name, false) == _binding.ModuleScope
                )
                    _dynamicExports = true;
                return;
            case PythonImportStatement import:
                foreach (var alias in import.Imports)
                    Import(alias, alias.Name, null, scope, offset, guarded);
                return;
            case PythonFromImportStatement import:
                foreach (var alias in import.Imports)
                    Import(alias, import.ModuleName, alias.Name, scope, offset, guarded);
                return;
            case PythonFunctionDefinitionStatement function:
                Add(
                    function.Name.Name,
                    function.Name.Span,
                    PythonNameOccurrenceKind.Definition,
                    scope,
                    offset
                );
                foreach (var decorator in function.Decorators)
                    Visit(decorator, scope, offset, guarded);
                Parameters(function.Parameters, scope, Nested(function, scope), offset, guarded);
                Visit(function.ReturnAnnotation, scope, offset, guarded, true);
                foreach (var statement in function.Body)
                    Visit(statement, Nested(function, scope), offset, guarded);
                return;
            case PythonClassDefinitionStatement definition:
                Add(
                    definition.Name.Name,
                    definition.Name.Span,
                    PythonNameOccurrenceKind.Definition,
                    scope,
                    offset
                );
                foreach (var decorator in definition.Decorators)
                    Visit(decorator, scope, offset, guarded);
                foreach (var basis in definition.Bases)
                    Visit(basis, scope, offset, guarded);
                foreach (var keyword in definition.KeywordArguments ?? [])
                    Visit(keyword, scope, offset, guarded);
                foreach (var statement in definition.Body)
                    Visit(statement, Nested(definition, scope), offset, guarded);
                return;
            case PythonLambdaExpression lambda:
                Parameters(lambda.Parameters, scope, Nested(lambda, scope), offset, guarded);
                Visit(lambda.Body, Nested(lambda, scope), offset, guarded);
                return;
            case PythonListComprehensionExpression list:
                Comprehension(list, list.Clauses, [list.Element], scope, offset, guarded);
                return;
            case PythonSetComprehensionExpression set:
                Comprehension(set, set.Clauses, [set.Element], scope, offset, guarded);
                return;
            case PythonDictionaryComprehensionExpression dictionary:
                Comprehension(
                    dictionary,
                    dictionary.Clauses,
                    [dictionary.Key, dictionary.Value],
                    scope,
                    offset,
                    guarded
                );
                return;
            case PythonGeneratorExpression generator:
                Comprehension(
                    generator,
                    generator.Clauses,
                    [generator.Element],
                    scope,
                    offset,
                    guarded
                );
                return;
            case PythonAssignmentStatement assignment:
                foreach (var target in assignment.Targets)
                {
                    AssignmentTarget(target, scope, offset, guarded);
                    Exports(target, assignment.Value, scope);
                }
                Visit(assignment.Value, scope, offset, guarded);
                return;
            case PythonAnnotatedAssignmentStatement annotated:
                AssignmentTarget(annotated.Target, scope, offset, guarded);
                Visit(annotated.Annotation, scope, offset, guarded, true);
                Visit(annotated.Value, scope, offset, guarded);
                if (annotated.Value is { } value)
                    Exports(annotated.Target, value, scope);
                return;
            case PythonAugmentedAssignmentStatement augmented:
                if (IsAll(augmented.Target, scope))
                {
                    Add(
                        "__all__",
                        augmented.Target.Span,
                        PythonNameOccurrenceKind.Read,
                        scope,
                        offset
                    );
                    if (augmented.Operator == PythonBinaryOperator.Add)
                        RecordExports(augmented.Value);
                    else
                        _dynamicExports = true;
                }
                else
                    Visit(augmented.Target, scope, offset, guarded);
                AssignmentTarget(augmented.Target, scope, offset, guarded);
                Visit(augmented.Value, scope, offset, guarded);
                return;
            case PythonAssignmentExpression walrus:
                var owner = scope;
                while (IsComprehension(owner.Definition))
                {
                    _walrusBindings.Add((owner, owner.MangleName(walrus.Target.Name)));
                    owner = _parents[owner]!;
                }
                Target(walrus.Target, owner, offset, guarded);
                Visit(walrus.Value, scope, offset, guarded);
                return;
            case PythonForStatement loop:
                Visit(loop.Iterable, scope, offset, guarded);
                Target(loop.Target, scope, offset, guarded);
                foreach (var statement in loop.Body.Concat(loop.ElseBody))
                    Visit(statement, scope, offset, guarded);
                return;
            case PythonWithItem item:
                Visit(item.Context, scope, offset, guarded);
                Target(item.Target, scope, offset, guarded);
                return;
            case PythonExceptHandler handler:
                Visit(handler.Type, scope, offset, true);
                Target(handler.Target, scope, offset, true);
                foreach (var statement in handler.Body)
                    Visit(statement, scope, offset, true);
                return;
            case PythonDeleteStatement deletion:
                foreach (var target in deletion.Targets)
                    Target(target, scope, offset, guarded, PythonNameOccurrenceKind.Delete);
                return;
            case PythonGlobalStatement global:
                foreach (var name in global.Names)
                    Add(name.Name, name.Span, PythonNameOccurrenceKind.Declaration, scope, offset);
                return;
            case PythonNonlocalStatement nonlocal:
                foreach (var name in nonlocal.Names)
                    Add(name.Name, name.Span, PythonNameOccurrenceKind.Declaration, scope, offset);
                return;
            case PythonCapturePattern capture when capture.Name is { } name:
                Add(name, capture.Span, PythonNameOccurrenceKind.Definition, scope, offset);
                return;
            case PythonAsPattern pattern:
                Add(pattern.Name, pattern.Span, PythonNameOccurrenceKind.Definition, scope, offset);
                break;
            case PythonSequencePattern pattern when pattern.StarName is { } name:
                Add(name, pattern.Span, PythonNameOccurrenceKind.Definition, scope, offset);
                break;
            case PythonMappingPattern pattern when pattern.RestName is { } name:
                Add(name, pattern.Span, PythonNameOccurrenceKind.Definition, scope, offset);
                break;
            case PythonConstantExpression { ConstantKind: PythonConstantKind.StringLiteral } literal
                when annotation:
                QuotedAnnotation(literal, scope, offset);
                return;
            case PythonCallExpression call:
                if (
                    call.Target is PythonNameExpression
                    {
                        Name: "exec" or "eval" or "globals" or "locals" or "vars"
                    }
                )
                    for (var current = scope; current is not null; current = _parents[current])
                        _dynamicScopes.Add(current);
                if (
                    call.Target is PythonAttributeExpression attribute
                    && IsAll(attribute.Target, scope)
                )
                {
                    Add(
                        "__all__",
                        attribute.Target.Span,
                        PythonNameOccurrenceKind.Read,
                        scope,
                        offset
                    );
                    if (call.Arguments.Count == 1 && call.KeywordArguments.Count == 0)
                    {
                        if (
                            attribute.AttributeName == "append"
                            && StaticString(call.Arguments[0]) is { } export
                        )
                            _exports.Add(export);
                        else if (attribute.AttributeName == "extend")
                            RecordExports(call.Arguments[0]);
                        else
                            _dynamicExports = true;
                    }
                    else
                        _dynamicExports = true;
                    foreach (var argument in call.Arguments)
                        Visit(argument, scope, offset, guarded);
                    foreach (var keyword in call.KeywordArguments)
                        Visit(keyword, scope, offset, guarded);
                    return;
                }
                break;
        }
        var childOffset = node is PythonFormattedStringInterpolationPart interpolation
            ? offset + interpolation.Span.Start
            : offset;
        foreach (var child in PythonAstTraversal.GetChildren(node))
            Visit(child, scope, childOffset, guarded || node is PythonTryStatement, annotation);
    }

    private void Parameters(
        IReadOnlyList<PythonParameter> parameters,
        PythonBoundScope outer,
        PythonBoundScope inner,
        int offset,
        bool guarded
    )
    {
        foreach (var parameter in parameters)
        {
            Add(parameter.Name, parameter.Span, PythonNameOccurrenceKind.Parameter, inner, offset);
            Visit(parameter.Default, outer, offset, guarded);
            Visit(parameter.Annotation, outer, offset, guarded, true);
        }
    }

    private void Target(
        PythonExpression? target,
        PythonBoundScope scope,
        int offset,
        bool guarded,
        PythonNameOccurrenceKind kind = PythonNameOccurrenceKind.Definition
    )
    {
        _cancellation.ThrowIfCancellationRequested();
        switch (target)
        {
            case null:
                return;
            case PythonNameExpression name:
                Add(name.Name, name.Span, kind, scope, offset);
                if (
                    name.Name == "__all__"
                    && scope.Kind != PythonScopeKind.Module
                    && Owner(scope, name.Name, true) == _binding.ModuleScope
                )
                    _dynamicExports = true;
                return;
            case PythonTupleExpression tuple:
                foreach (var element in tuple.Elements)
                    Target(element, scope, offset, guarded, kind);
                return;
            case PythonListExpression list:
                foreach (var element in list.Elements)
                    Target(element, scope, offset, guarded, kind);
                return;
            case PythonParenthesizedExpression parenthesized:
                Target(parenthesized.Expression, scope, offset, guarded, kind);
                return;
            case PythonStarredExpression starred:
                Target(starred.Operand, scope, offset, guarded, kind);
                return;
            default:
                Visit(target, scope, offset, guarded);
                return;
        }
    }

    private void Comprehension(
        PythonExpression expression,
        IReadOnlyList<PythonComprehensionClause> clauses,
        PythonExpression[] elements,
        PythonBoundScope outer,
        int offset,
        bool guarded
    )
    {
        var inner = Nested(expression, outer);
        for (var index = 0; index < clauses.Count; index++)
        {
            if (clauses[index] is PythonComprehensionForClause loop)
            {
                Visit(loop.Iterable, index == 0 ? outer : inner, offset, guarded);
                Target(loop.Target, inner, offset, guarded);
            }
            else if (clauses[index] is PythonComprehensionIfClause filter)
                Visit(filter.Condition, inner, offset, guarded);
        }
        foreach (var element in elements)
            Visit(element, inner, offset, guarded);
    }

    private static bool IsComprehension(PythonNode? node) =>
        node
            is PythonListComprehensionExpression
                or PythonSetComprehensionExpression
                or PythonDictionaryComprehensionExpression
                or PythonGeneratorExpression;

    private void Import(
        PythonImportAlias alias,
        string module,
        string? importedName,
        PythonBoundScope scope,
        int offset,
        bool guarded
    )
    {
        var name = alias.Alias ?? (importedName ?? module.Split('.')[0]);
        var index = Add(name, alias.Span, PythonNameOccurrenceKind.Import, scope, offset);
        _imports.Add(
            (index, module, importedName, alias.Alias == (importedName ?? module), guarded)
        );
    }

    private static PythonExpression Unwrap(PythonExpression expression)
    {
        while (expression is PythonParenthesizedExpression parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private static string? StaticString(PythonExpression expression) =>
        Unwrap(expression)
            is PythonConstantExpression { ConstantKind: PythonConstantKind.StringLiteral } literal
            ? PythonLiteralDecoder.Decode(literal, new List<Diagnostic>()).Value as string
            : null;

    private void QuotedAnnotation(
        PythonConstantExpression literal,
        PythonBoundScope scope,
        int offset
    )
    {
        if (StaticString(literal) is not { } text)
            return;
        var pending = new Stack<string>();
        pending.Push(text);
        while (pending.TryPop(out var content))
        {
            var tokens = PythonTokenizer.Tokenize(new SourceText(content)).Tokens;
            for (var index = 0; index < tokens.Count; index++)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (
                    tokens[index].Kind == SyntaxTokenKind.Identifier
                    && (index == 0 || tokens[index - 1].Kind != SyntaxTokenKind.Dot)
                )
                    Add(
                        tokens[index].Text,
                        literal.Span,
                        PythonNameOccurrenceKind.Annotation,
                        scope,
                        offset
                    );
                else if (
                    tokens[index].Kind == SyntaxTokenKind.StringLiteral
                    && StaticString(
                        new PythonConstantExpression(
                            PythonConstantKind.StringLiteral,
                            tokens[index].Text,
                            literal.Span
                        )
                    )
                        is { } nested
                )
                    pending.Push(nested);
            }
        }
    }

    private static bool IsAll(PythonExpression expression, PythonBoundScope scope) =>
        scope.Kind == PythonScopeKind.Module
        && Unwrap(expression) is PythonNameExpression { Name: "__all__" };

    private void Exports(PythonExpression target, PythonExpression value, PythonBoundScope scope)
    {
        if (IsAll(target, scope))
            RecordExports(value);
    }

    private void AssignmentTarget(
        PythonExpression target,
        PythonBoundScope scope,
        int offset,
        bool guarded
    )
    {
        if (IsAll(target, scope))
            Add(
                "__all__",
                target.Span,
                PythonNameOccurrenceKind.Definition,
                scope,
                offset,
                knownExport: true
            );
        else
            Target(target, scope, offset, guarded);
    }

    private void RecordExports(PythonExpression expression)
    {
        expression = Unwrap(expression);
        if (expression is PythonBinaryExpression { Operator: PythonBinaryOperator.Add } binary)
        {
            RecordExports(binary.Left);
            RecordExports(binary.Right);
            return;
        }
        var elements = expression switch
        {
            PythonListExpression list => list.Elements,
            PythonTupleExpression tuple => tuple.Elements,
            _ => null,
        };
        if (elements is null)
        {
            _dynamicExports = true;
            return;
        }
        foreach (var element in elements)
        {
            if (StaticString(element) is { } name)
                _exports.Add(name);
            else
                _dynamicExports = true;
        }
    }

    private PythonSemanticModel Finish()
    {
        var resolved = new List<PythonNameOccurrence>();
        foreach (var occurrence in _occurrences)
        {
            _cancellation.ThrowIfCancellationRequested();
            var name = occurrence.Scope.MangleName(occurrence.Name);
            var definition =
                occurrence.Kind
                is PythonNameOccurrenceKind.Definition
                    or PythonNameOccurrenceKind.Import
                    or PythonNameOccurrenceKind.Parameter
                    or PythonNameOccurrenceKind.Delete;
            var owner = Owner(occurrence.Scope, name, definition);
            PythonSymbol? symbol = null;
            if (owner is not null)
                _symbols.TryGetValue((owner, name), out symbol);
            PythonSymbol? fallback = null;
            if (!definition && owner == occurrence.Scope && owner.Kind == PythonScopeKind.Class)
                _symbols.TryGetValue((_binding.ModuleScope, name), out fallback);
            resolved.Add(
                new(
                    occurrence.Name,
                    occurrence.Span,
                    occurrence.Kind,
                    occurrence.Scope,
                    symbol,
                    fallback
                )
            );
        }
        var imports = _imports
            .Select(import => new PythonImportBinding(
                resolved[import.Index],
                import.Module,
                import.Name,
                import.Reexport,
                import.Guarded
            ))
            .ToArray();
        return new PythonSemanticModel(
            new PythonBindingResult(
                _binding.ModuleScope,
                [.. _binding.Diagnostics, .. _extraDiagnostics]
            ),
            Array.AsReadOnly(_symbols.Values.ToArray()),
            Array.AsReadOnly(resolved.OrderBy(occurrence => occurrence.Span.Start).ToArray()),
            Array.AsReadOnly(imports),
            _exports.ToFrozenSet(StringComparer.Ordinal),
            _dynamicExports,
            _dynamicScopes.ToFrozenSet()
        );
    }

    private sealed record RawOccurrence(
        string Name,
        TextSpan Span,
        PythonNameOccurrenceKind Kind,
        PythonBoundScope Scope
    );
}
