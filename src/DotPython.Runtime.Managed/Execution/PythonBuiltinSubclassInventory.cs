namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Startup types represented by this runtime's builtins. Each direct-subclass
/// subsequence follows CPython 3.14's definition order; this is not an inventory
/// of CPython's internal, extension, or subsequently imported types.
/// </summary>
internal static class PythonBuiltinSubclassInventory
{
    private static readonly string[] StartupNames =
    [
        "object",
        "type",
        "bytes",
        "classmethod",
        "complex",
        "dict",
        "enumerate",
        "filter",
        "float",
        "frozenset",
        "list",
        "int",
        "map",
        "property",
        "range",
        "reversed",
        "set",
        "slice",
        "staticmethod",
        "super",
        "tuple",
        "str",
        "zip",
        "BaseException",
        "bool",
        "BaseExceptionGroup",
        "Exception",
        "GeneratorExit",
        "KeyboardInterrupt",
        "SystemExit",
        "ArithmeticError",
        "AssertionError",
        "AttributeError",
        "EOFError",
        "ImportError",
        "LookupError",
        "NameError",
        "OSError",
        "RuntimeError",
        "StopAsyncIteration",
        "StopIteration",
        "SyntaxError",
        "TypeError",
        "ValueError",
        // ExceptionGroup is also a direct child of Exception, after ValueError.
        "ExceptionGroup",
        "OverflowError",
        "ZeroDivisionError",
        "ModuleNotFoundError",
        "IndexError",
        "KeyError",
        "UnboundLocalError",
        "FileNotFoundError",
        "IsADirectoryError",
        "PermissionError",
        "RecursionError",
        "UnicodeError",
        "UnicodeDecodeError",
        "UnicodeEncodeError",
    ];

    internal static IEnumerable<PythonValue> GetStartupTypes(
        IReadOnlyDictionary<string, PythonValue> builtins
    )
    {
        foreach (var name in StartupNames)
            if (
                builtins.TryGetValue(name, out var value)
                && value
                    is PythonBuiltinTypeValue
                        or PythonExceptionTypeValue
                        or PythonManagedTypeValue
            )
                yield return value;
    }
}
