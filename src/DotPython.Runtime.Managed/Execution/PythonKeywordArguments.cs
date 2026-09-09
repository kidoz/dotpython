using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Binds positional and keyword arguments of builtin callables onto declared parameter
/// slots with CPython's error surface, so a builtin declares its signature once and keeps
/// a purely positional implementation.
/// </summary>
internal static class PythonKeywordArguments
{
    /// <summary>
    /// Declares a builtin's parameter names (with <paramref name="positionalOnly"/> leading
    /// slots that reject keywords) and produces the keyword-call adapter that binds
    /// arguments, fills defaults, and forwards the positional form to the implementation.
    /// </summary>
    internal static BuiltinKeywordInvoker Adapt(
        string name,
        string[] parameters,
        PythonValue?[] defaults,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> implementation,
        int positionalOnly = 0,
        bool typeStyleErrors = false
    ) =>
        (positional, keywordNames, keywordValues, span) =>
            implementation(
                ToPositional(
                    name,
                    parameters,
                    defaults,
                    Bind(
                        name,
                        parameters,
                        positionalOnly,
                        positional,
                        keywordNames,
                        keywordValues,
                        span,
                        typeStyleErrors
                    ),
                    span
                ),
                span
            );

    /// <summary>The method form: the bound target stays separate from the parameters.</summary>
    internal static ProtocolKeywordInvoker AdaptMethod(
        string name,
        string[] parameters,
        PythonValue?[] defaults,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> implementation,
        int positionalOnly = 0
    ) =>
        (target, positional, keywordNames, keywordValues) =>
            implementation(
                target,
                ToPositional(
                    name,
                    parameters,
                    defaults,
                    Bind(
                        name,
                        parameters,
                        positionalOnly,
                        positional,
                        keywordNames,
                        keywordValues,
                        default
                    ),
                    default
                )
            );

    internal static PythonValue?[] Bind(
        string name,
        string[] parameters,
        int positionalOnly,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span,
        bool typeStyleErrors = false
    )
    {
        if (positional.Count > parameters.Length)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() takes at most {parameters.Length} argument{(parameters.Length == 1 ? "" : "s")} ({positional.Count} given)",
                span,
                "TypeError"
            );
        }

        var slots = new PythonValue?[parameters.Length];
        for (var index = 0; index < positional.Count; index++)
        {
            slots[index] = positional[index];
        }

        for (var index = 0; index < keywordNames.Count; index++)
        {
            var keyword = keywordNames[index];
            var slot = Array.IndexOf(parameters, keyword);
            if (slot >= 0 && slot < positionalOnly)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4009",
                    $"{name}() takes at least {positionalOnly} positional argument{(positionalOnly == 1 ? "" : "s")} ({positional.Count} given)",
                    span,
                    "TypeError"
                );
            }

            if (slot < 0)
            {
                // Builtin types report unknown keywords in CPython's type style.
                throw ManagedObjectProtocols.Fault(
                    "DPY4009",
                    typeStyleErrors
                        ? $"'{keyword}' is an invalid keyword argument for {name}()"
                        : $"{name}() got an unexpected keyword argument '{keyword}'",
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

            slots[slot] = keywordValues[index];
        }

        return slots;
    }

    /// <summary>
    /// Converts bound slots to the positional form: trailing unset slots are dropped,
    /// interior gaps take their defaults, and a gap without a default is an error.
    /// </summary>
    internal static PythonValue[] ToPositional(
        string name,
        string[] parameters,
        PythonValue?[] defaults,
        PythonValue?[] slots,
        TextSpan span
    )
    {
        var count = slots.Length;
        while (count > 0 && slots[count - 1] is null)
        {
            count--;
        }

        var positional = new PythonValue[count];
        for (var index = 0; index < count; index++)
        {
            positional[index] =
                slots[index]
                ?? defaults[index]
                ?? throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() missing required argument '{parameters[index]}' (pos {index + 1})",
                    span,
                    "TypeError"
                );
        }

        return positional;
    }
}
