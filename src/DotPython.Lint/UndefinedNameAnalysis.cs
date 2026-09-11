using System.Collections.Frozen;
using DotPython.Compiler.Binding;

namespace DotPython.Lint;

internal static class UndefinedNameAnalysis
{
    // Python 3.14 builtin vocabulary (including site helpers and the Windows alias).
    // This is a static language catalogue, not a promise of managed-runtime support.
    // https://docs.python.org/3.14/library/functions.html
    // https://docs.python.org/3.14/library/exceptions.html
    private static readonly FrozenSet<string> Builtins = """
        ArithmeticError AssertionError AttributeError BaseException BaseExceptionGroup BlockingIOError
        BrokenPipeError BufferError BytesWarning ChildProcessError ConnectionAbortedError ConnectionError
        ConnectionRefusedError ConnectionResetError DeprecationWarning EOFError Ellipsis EncodingWarning
        EnvironmentError Exception ExceptionGroup False FileExistsError FileNotFoundError FloatingPointError
        FutureWarning GeneratorExit IOError ImportError ImportWarning IndentationError IndexError
        InterruptedError IsADirectoryError KeyError KeyboardInterrupt LookupError MemoryError
        ModuleNotFoundError NameError None NotADirectoryError NotImplemented NotImplementedError OSError
        OverflowError PendingDeprecationWarning PermissionError ProcessLookupError PythonFinalizationError
        RecursionError ReferenceError ResourceWarning RuntimeError RuntimeWarning StopAsyncIteration
        StopIteration SyntaxError SyntaxWarning SystemError SystemExit TabError TimeoutError True TypeError
        UnboundLocalError UnicodeDecodeError UnicodeEncodeError UnicodeError UnicodeTranslateError
        UnicodeWarning UserWarning ValueError Warning WindowsError ZeroDivisionError __build_class__
        __debug__ __import__ abs aiter all anext any ascii bin bool breakpoint bytearray bytes callable chr
        classmethod compile complex copyright credits delattr dict dir divmod enumerate eval exec exit
        filter float format frozenset getattr globals hasattr hash help hex id input int isinstance
        issubclass iter len license list locals map max memoryview min next object oct open ord pow print
        property quit range repr reversed round set setattr slice sorted staticmethod str sum super tuple
        type vars zip
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(
        StringComparer.Ordinal
    );

    // Module execution/import metadata can be provided by the loader or host.
    private static readonly FrozenSet<string> ModuleGlobals = new[]
    {
        "__name__",
        "__doc__",
        "__package__",
        "__loader__",
        "__spec__",
        "__file__",
        "__cached__",
        "__builtins__",
        "__annotations__",
        "__annotate__",
        "__path__",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static IEnumerable<PythonNameOccurrence> Find(
        PythonSemanticModel model,
        IReadOnlyCollection<string> knownGlobals,
        CancellationToken cancellationToken
    )
    {
        // DynamicScopes includes ancestors: namespace observation anywhere in this
        // module can expose dictionaries to arbitrary code. Avoid guessing their keys.
        if (model.Diagnostics.Count != 0 || model.DynamicScopes.Contains(model.Binding.ModuleScope))
            yield break;
        var additional = knownGlobals.ToFrozenSet(StringComparer.Ordinal);
        foreach (var occurrence in model.Occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                occurrence.Kind
                    is not (PythonNameOccurrenceKind.Read or PythonNameOccurrenceKind.Annotation)
                || occurrence.IsQuotedAnnotation
                || occurrence.Symbol is not null
                || occurrence.FallbackSymbol is not null
            )
                continue;
            var name = occurrence.LookupName;
            if (
                Builtins.Contains(name)
                || ModuleGlobals.Contains(name)
                || additional.Contains(name)
            )
                continue;
            if (
                occurrence.Scope.Kind == PythonScopeKind.Class
                && name is "__module__" or "__qualname__"
            )
                continue;
            yield return occurrence;
        }
    }
}
