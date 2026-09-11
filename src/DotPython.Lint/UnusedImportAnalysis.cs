using DotPython.Compiler.Binding;

namespace DotPython.Lint;

internal static class UnusedImportAnalysis
{
    internal static IEnumerable<PythonImportBinding> Find(
        PythonSemanticModel model,
        CancellationToken cancellationToken
    )
    {
        if (model.Diagnostics.Count != 0)
            yield break;

        var used = new HashSet<PythonSymbol>();
        foreach (var occurrence in model.Occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                occurrence.Kind
                is PythonNameOccurrenceKind.Read
                    or PythonNameOccurrenceKind.Annotation
                    or PythonNameOccurrenceKind.Delete
            )
            {
                if (occurrence.Symbol is { } symbol)
                    used.Add(symbol);
                if (occurrence.FallbackSymbol is { } fallback)
                    used.Add(fallback);
            }
        }

        foreach (var import in model.Imports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occurrence = import.Occurrence;
            if (
                occurrence.Symbol is not { } symbol
                || used.Contains(symbol)
                || import.IsExplicitReexport
                || import.IsInTryStatement
                || import.ModuleName == "__future__"
                || occurrence.Scope.Kind == PythonScopeKind.Class
                || model.DynamicScopes.Contains(occurrence.Scope)
                || model.DynamicScopes.Contains(symbol.Scope)
            )
                continue;
            if (
                symbol.Scope.Kind == PythonScopeKind.Module
                && (model.HasDynamicExports || model.ExportedNames.Contains(symbol.Name))
            )
                continue;
            yield return import;
        }
    }
}
