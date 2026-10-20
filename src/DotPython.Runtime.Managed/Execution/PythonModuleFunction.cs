using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The module functions of `fnmatch` and `glob`: positional parameters first, then
/// keyword-only ones. The runtime's positional call path bypasses signature binding, so
/// the wrapper validates the counts itself and reports them the way CPython's argument
/// parser does; a keyword call binds the positional parameters by name too, and the body
/// always sees every parameter, defaults filled in.
/// </summary>
internal static class PythonModuleFunction
{
    internal static PythonBuiltinFunctionValue Create(
        string name,
        string[] positionalNames,
        string[] keywordOnlyNames,
        PythonValue[] keywordOnlyDefaults,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body
    )
    {
        ArgumentNullException.ThrowIfNull(keywordOnlyNames);
        ArgumentNullException.ThrowIfNull(keywordOnlyDefaults);
        return new PythonBuiltinFunctionValue(
            name,
            (arguments, span) =>
            {
                if (arguments.Count > positionalNames.Length)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4009",
                        $"{name}() takes {positionalNames.Length} positional argument"
                            + (positionalNames.Length == 1 ? "" : "s")
                            + $" but {arguments.Count} were given",
                        span,
                        "TypeError"
                    );
                }
                if (arguments.Count < positionalNames.Length)
                {
                    throw Missing(name, positionalNames, arguments.Count, span);
                }
                var complete = new List<PythonValue>(arguments);
                complete.AddRange(keywordOnlyDefaults);
                return body(complete, span);
            },
            (positional, names, values, span) =>
            {
                if (positional.Count > positionalNames.Length)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4009",
                        $"{name}() takes {positionalNames.Length} positional argument"
                            + (positionalNames.Length == 1 ? "" : "s")
                            + $" but {positional.Count} were given",
                        span,
                        "TypeError"
                    );
                }
                var slots = new PythonValue?[positionalNames.Length + keywordOnlyNames.Length];
                for (var index = 0; index < positional.Count; index++)
                {
                    slots[index] = positional[index];
                }
                for (var index = 0; index < names.Count; index++)
                {
                    var keyword = names[index];
                    var slot = Array.IndexOf(positionalNames, keyword);
                    if (slot < 0)
                    {
                        var keywordOnly = Array.IndexOf(keywordOnlyNames, keyword);
                        slot = keywordOnly < 0 ? -1 : positionalNames.Length + keywordOnly;
                    }
                    if (slot < 0)
                    {
                        throw ManagedObjectProtocols.Fault(
                            "DPY4009",
                            $"{name}() got an unexpected keyword argument '{keyword}'",
                            span,
                            "TypeError"
                        );
                    }
                    if (slots[slot] is not null)
                    {
                        throw ManagedObjectProtocols.Fault(
                            "DPY4009",
                            $"{name}() got multiple values for argument '{keyword}'",
                            span,
                            "TypeError"
                        );
                    }
                    slots[slot] = values[index];
                }
                for (var index = 0; index < positionalNames.Length; index++)
                {
                    if (slots[index] is null)
                    {
                        throw Missing(name, positionalNames, index, span);
                    }
                }
                var complete = new List<PythonValue>(slots.Length);
                for (var index = 0; index < slots.Length; index++)
                {
                    complete.Add(
                        slots[index] ?? keywordOnlyDefaults[index - positionalNames.Length]
                    );
                }
                return body(complete, span);
            }
        );
    }

    private static PythonRuntimeException Missing(
        string name,
        string[] positionalNames,
        int supplied,
        TextSpan span
    )
    {
        var missing = new List<string>();
        for (var index = supplied; index < positionalNames.Length; index++)
        {
            missing.Add($"'{positionalNames[index]}'");
        }
        return ManagedObjectProtocols.Fault(
            "DPY4009",
            $"{name}() missing {missing.Count} required positional argument"
                + (missing.Count == 1 ? "" : "s")
                + ": "
                + JoinNames(missing),
            span,
            "TypeError"
        );
    }

    private static string JoinNames(List<string> names) =>
        names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + $", and {names[^1]}",
        };
}
