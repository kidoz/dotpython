using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Compiler.Binding;

/// <summary>A lexical name identity. It does not identify one reaching assignment or runtime value.</summary>
public sealed class PythonSymbol
{
    internal PythonSymbol(PythonBoundScope scope, string name)
    {
        Scope = scope;
        Name = name;
    }

    public PythonBoundScope Scope { get; }

    /// <summary>The binding name, including Python private-name mangling where applicable.</summary>
    public string Name { get; }
}

public enum PythonNameOccurrenceKind
{
    Read,
    Definition,
    Import,
    Parameter,
    Delete,
    Declaration,
    Annotation,
}

/// <summary>
/// One source occurrence. Symbol is null for unresolved globals/builtins.
/// FallbackSymbol captures class-body global lookup when a class local is unbound.
/// Quoted annotation references cover the entire string literal span.
/// Import aliases, parameters, and pattern captures use their AST declaration spans;
/// ordinary name reads use identifier spans.
/// </summary>
public sealed record PythonNameOccurrence(
    string Name,
    TextSpan Span,
    PythonNameOccurrenceKind Kind,
    PythonBoundScope Scope,
    PythonSymbol? Symbol,
    PythonSymbol? FallbackSymbol
)
{
    /// <summary>The lookup spelling after Python private-name mangling, even when unresolved.</summary>
    public string LookupName => Scope.MangleName(Name);

    /// <summary>True for conservatively tokenized string contents, which may include type metadata.</summary>
    public bool IsQuotedAnnotation { get; init; }
}

public sealed record PythonImportBinding(
    PythonNameOccurrence Occurrence,
    string ModuleName,
    string? ImportedName,
    bool IsExplicitReexport,
    bool IsInTryStatement
);

/// <summary>
/// Optional source-tooling information layered over execution binding. Resolution is lexical,
/// not flow-sensitive. No Python is executed. Consumers must inspect binding diagnostics.
/// </summary>
public sealed class PythonSemanticModel
{
    internal PythonSemanticModel(
        PythonBindingResult binding,
        IReadOnlyList<PythonSymbol> symbols,
        IReadOnlyList<PythonNameOccurrence> occurrences,
        IReadOnlyList<PythonImportBinding> imports,
        IReadOnlySet<string> exportedNames,
        bool hasDynamicExports,
        IReadOnlySet<PythonBoundScope> dynamicScopes
    )
    {
        Binding = binding;
        Symbols = symbols;
        Occurrences = occurrences;
        Imports = imports;
        ExportedNames = exportedNames;
        HasDynamicExports = hasDynamicExports;
        DynamicScopes = dynamicScopes;
    }

    public PythonBindingResult Binding { get; }
    public IReadOnlyList<Diagnostic> Diagnostics => Binding.Diagnostics;
    public IReadOnlyList<PythonSymbol> Symbols { get; }
    public IReadOnlyList<PythonNameOccurrence> Occurrences { get; }
    public IReadOnlyList<PythonImportBinding> Imports { get; }

    /// <summary>Conservative union of literal names assigned/appended/extended into module __all__.</summary>
    public IReadOnlySet<string> ExportedNames { get; }

    /// <summary>True when module exports escape or cannot be determined from literal __all__ operations.</summary>
    public bool HasDynamicExports { get; }

    /// <summary>Scopes potentially observed through direct exec/eval/globals/locals/vars calls.</summary>
    public IReadOnlySet<PythonBoundScope> DynamicScopes { get; }
}
