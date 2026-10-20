using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `traceback` module: formatting of an exception's captured frames and its
/// cause/context chain. The frames are the snapshots the VM takes when an exception
/// unwinds; the 3.14 column-anchor lines are not rendered because the runtime's
/// instruction spans do not carry the per-range positions CPython draws them from.
/// </summary>
internal static class PythonTracebackModule
{
    private const string CauseSeparator =
        "\nThe above exception was the direct cause of the following exception:\n\n";

    private const string ContextSeparator =
        "\nDuring handling of the above exception, another exception occurred:\n\n";

    /// <summary>Marks an optional argument the caller did not supply.</summary>
    private sealed record SentinelValue : PythonValue
    {
        internal static SentinelValue Instance { get; } = new();

        internal override string ToDisplayString() => "<sentinel>";
    }

    private static readonly string[] ExportedNames =
    [
        "format_exception",
        "format_exception_only",
        "format_tb",
        "format_exc",
        "print_exception",
        "print_exc",
        "print_tb",
    ];

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        Register(globals, "format_exception", FormatExceptionCall);
        Register(globals, "format_exception_only", FormatExceptionOnlyCall);
        Register(globals, "format_tb", FormatTbCall);
        Register(globals, "format_exc", FormatExcCall);
        Register(globals, "print_exception", PrintExceptionCall);
        Register(globals, "print_exc", PrintExcCall);
        Register(globals, "print_tb", PrintTbCall);
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. ExportedNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
    }

    private static void Register(
        PythonGlobalNamespace globals,
        string name,
        BuiltinKeywordInvoker invoke
    ) =>
        globals.SetValue(
            name,
            new PythonBuiltinFunctionValue(
                name,
                (arguments, span) => invoke(arguments, [], [], span),
                invoke
            )
        );

    private static PythonListValue FormatExceptionCall(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind(
            "format_exception",
            ["exc", "value", "tb", "limit", "chain"],
            1,
            [
                null,
                SentinelValue.Instance,
                SentinelValue.Instance,
                PythonNoneValue.Instance,
                PythonTruthValue.True,
            ],
            arguments,
            names,
            values,
            span
        );
        var (value, traceback) = ExceptionValueAndTraceback(slots);
        return Lines(
            FormatException(
                value,
                traceback,
                Limit(slots[3]!, span),
                ManagedObjectProtocols.IsTrue(slots[4]!),
                span
            )
        );
    }

    private static PythonListValue FormatExceptionOnlyCall(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind(
            "format_exception_only",
            ["exc", "value"],
            1,
            [null, SentinelValue.Instance],
            arguments,
            names,
            values,
            span
        );
        var value = RequireExceptionValue(slots[1] is SentinelValue ? slots[0]! : slots[1]!, span);
        return Lines([
            value is PythonExceptionValue exception
                ? FormatExceptionOnly(exception)
                : "NoneType: None\n",
        ]);
    }

    private static PythonListValue FormatTbCall(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind(
            "format_tb",
            ["tb", "limit"],
            1,
            [null, PythonNoneValue.Instance],
            arguments,
            names,
            values,
            span
        );
        return Lines([.. FormatFrames(slots[0]!, Limit(slots[1]!, span))]);
    }

    private static PythonTextValue FormatExcCall(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind(
            "format_exc",
            ["limit", "chain"],
            0,
            [PythonNoneValue.Instance, PythonTruthValue.True],
            arguments,
            names,
            values,
            span
        );
        var value = CurrentException();
        var traceback = value is PythonExceptionValue exception
            ? PythonTracebacks.Get(exception)
            : PythonNoneValue.Instance;
        return new PythonTextValue(
            string.Concat(
                FormatException(
                    value,
                    traceback,
                    Limit(slots[0]!, span),
                    ManagedObjectProtocols.IsTrue(slots[1]!),
                    span
                )
            )
        );
    }

    private static PythonNoneValue PrintExceptionCall(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind(
            "print_exception",
            ["exc", "value", "tb", "limit", "file", "chain"],
            1,
            [
                null,
                SentinelValue.Instance,
                SentinelValue.Instance,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
                PythonTruthValue.True,
            ],
            arguments,
            names,
            values,
            span
        );
        var (value, traceback) = ExceptionValueAndTraceback(slots);
        Write(
            slots[4]!,
            string.Concat(
                FormatException(
                    value,
                    traceback,
                    Limit(slots[3]!, span),
                    ManagedObjectProtocols.IsTrue(slots[5]!),
                    span
                )
            ),
            span
        );
        return PythonNoneValue.Instance;
    }

    private static PythonNoneValue PrintExcCall(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind(
            "print_exc",
            ["limit", "file", "chain"],
            0,
            [PythonNoneValue.Instance, PythonNoneValue.Instance, PythonTruthValue.True],
            arguments,
            names,
            values,
            span
        );
        var value = CurrentException();
        var traceback = value is PythonExceptionValue exception
            ? PythonTracebacks.Get(exception)
            : PythonNoneValue.Instance;
        Write(
            slots[1]!,
            string.Concat(
                FormatException(
                    value,
                    traceback,
                    Limit(slots[0]!, span),
                    ManagedObjectProtocols.IsTrue(slots[2]!),
                    span
                )
            ),
            span
        );
        return PythonNoneValue.Instance;
    }

    private static PythonNoneValue PrintTbCall(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = Bind(
            "print_tb",
            ["tb", "limit", "file"],
            1,
            [null, PythonNoneValue.Instance, PythonNoneValue.Instance],
            arguments,
            names,
            values,
            span
        );
        Write(slots[2]!, string.Concat(FormatFrames(slots[0]!, Limit(slots[1]!, span))), span);
        return PythonNoneValue.Instance;
    }

    private static (PythonValue Value, PythonValue Traceback) ExceptionValueAndTraceback(
        PythonValue?[] slots
    )
    {
        var exception = slots[0]!;
        var value = slots[1] is SentinelValue ? exception : slots[1]!;
        var traceback =
            slots[2] is SentinelValue
                ? value is PythonExceptionValue exceptionValue
                    ? PythonTracebacks.Get(exceptionValue)
                    : PythonNoneValue.Instance
                : slots[2]!;
        return (value, traceback);
    }

    /// <summary>
    /// Formats one exception and its cause/context chain. Each segment contributes its
    /// traceback header (when it has frames, after the limit) and the exception-only
    /// line; the separators between segments are CPython's.
    /// </summary>
    private static List<string> FormatException(
        PythonValue value,
        PythonValue traceback,
        int? limit,
        bool chain,
        TextSpan span
    )
    {
        if (value is PythonNoneValue)
        {
            return ["NoneType: None\n"];
        }
        if (value is not PythonExceptionValue exception)
        {
            throw TypeError(
                $"Exception expected for value, {ManagedObjectProtocols.GetTypeName(value)} found",
                span
            );
        }
        var segments = new List<(PythonExceptionValue Exception, string? Separator)>();
        var visited = new HashSet<PythonExceptionValue>();
        var cursor = exception;
        while (true)
        {
            if (!visited.Add(cursor) || segments.Count >= 100)
            {
                break;
            }
            PythonExceptionValue? earlier = cursor.Cause;
            var separator = CauseSeparator;
            if (earlier is null && !cursor.SuppressContext)
            {
                earlier = cursor.Context;
                separator = ContextSeparator;
            }
            segments.Add((cursor, earlier is null || !chain ? null : separator));
            if (earlier is null || !chain)
            {
                break;
            }
            cursor = earlier;
        }
        segments.Reverse();
        var output = new List<string>();
        foreach (var (segment, separator) in segments)
        {
            if (separator is not null)
            {
                output.Add(separator);
            }
            var segmentTraceback = ReferenceEquals(segment, exception)
                ? traceback
                : PythonTracebacks.Get(segment);
            var frames = FormatFrames(segmentTraceback, limit);
            if (frames.Count != 0)
            {
                output.Add("Traceback (most recent call last):\n");
                output.AddRange(frames);
            }
            output.Add(FormatExceptionOnly(segment));
        }
        return output;
    }

    /// <summary>The `  File "...", line N, in name` lines and their source text.</summary>
    private static List<string> FormatFrames(PythonValue traceback, int? limit)
    {
        var entries = Entries(traceback);
        if (limit is { } count)
        {
            entries = count >= 0 ? [.. entries.Take(count)] : [.. entries.TakeLast(-count)];
        }
        var lines = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            var file = entry.Data.FileName;
            var line = entry.Data.Line;
            var text = new System.Text.StringBuilder(
                $"  File \"{file}\", line {line}, in {entry.Data.CodeName}\n"
            );
            if (PythonSourceLocations.TryGetLineForCode(entry.Data.Code, line) is { } source)
            {
                text.Append("    ").Append(source.Trim()).Append('\n');
            }
            lines.Add(text.ToString());
        }
        return lines;
    }

    private static List<PythonTracebackEntry> Entries(PythonValue traceback)
    {
        if (traceback is PythonNoneValue)
        {
            return [];
        }
        if (traceback is PythonExternalObjectValue { Protocol: PythonTracebackEntry head })
        {
            var entries = new List<PythonTracebackEntry>();
            for (var entry = head; entry is not null; entry = entry.Next)
            {
                entries.Add(entry);
            }
            return entries;
        }
        throw ManagedObjectProtocols.Fault(
            "DPY4022",
            $"'{ManagedObjectProtocols.GetTypeName(traceback)}' object has no attribute 'tb_frame'",
            default,
            "AttributeError"
        );
    }

    private static string FormatExceptionOnly(PythonExceptionValue exception)
    {
        var message = exception.ToDisplayString();
        var name = ExceptionTypeName(exception);
        return message.Length == 0 ? $"{name}\n" : $"{name}: {message}\n";
    }

    private static PythonValue RequireExceptionValue(PythonValue value, TextSpan span) =>
        value is PythonNoneValue or PythonExceptionValue
            ? value
            : throw TypeError(
                $"Exception expected for value, {ManagedObjectProtocols.GetTypeName(value)} found",
                span
            );

    private static string ExceptionTypeName(PythonExceptionValue exception)
    {
        if (exception.ManagedType is not { } type)
        {
            return exception.TypeName;
        }
        var module =
            type.Attributes.TryGetValue("__module__", out var moduleValue)
            && moduleValue is PythonTextValue moduleText
                ? moduleText.Value
                : null;
        var qualname =
            type.Attributes.TryGetValue("__qualname__", out var qualnameValue)
            && qualnameValue is PythonTextValue qualnameText
                ? qualnameText.Value
                : type.Name;
        return module is null or "builtins" or "__main__" ? qualname : $"{module}.{qualname}";
    }

    private static PythonValue CurrentException() =>
        UserObjectProtocols.Dispatcher?.CurrentException is { } exception
            ? exception
            : PythonNoneValue.Instance;

    private static int? Limit(PythonValue value, TextSpan span) =>
        value is PythonNoneValue
            ? null
            : (int)
                BigInteger.Clamp(
                    PythonBuiltinFunctions.RequireIndex(value, span),
                    int.MinValue,
                    int.MaxValue
                );

    private static void Write(PythonValue? file, string text, TextSpan span)
    {
        if (file is null or PythonNoneValue)
        {
            var dispatcher =
                UserObjectProtocols.Dispatcher
                ?? throw ManagedObjectProtocols.Fault(
                    "DPY4016",
                    "The standard streams are only available while a program is running.",
                    span,
                    "RuntimeError"
                );
            dispatcher.StandardError.Write(text);
            return;
        }
        ManagedObjectProtocols.Call(
            ManagedObjectProtocols.GetAttribute(file, "write", span),
            [new PythonTextValue(text)],
            span
        );
    }

    private static PythonListValue Lines(IEnumerable<string> lines) =>
        new([.. lines.Select(line => (PythonValue)new PythonTextValue(line))]);

    /// <summary>
    /// Binds a CPython-style signature: parameters, the first <paramref name="positionalOnly"/>
    /// of which reject keyword binding, and one default per parameter (null meaning the
    /// caller must supply it).
    /// </summary>
    private static PythonValue?[] Bind(
        string name,
        string[] parameters,
        int positionalOnly,
        PythonValue?[] defaults,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count > parameters.Length)
        {
            throw TypeError(
                $"{name}() takes at most {parameters.Length} arguments "
                    + $"({positional.Count} given)",
                span
            );
        }
        var slots = new PythonValue?[parameters.Length];
        for (var index = 0; index < positional.Count; index++)
        {
            slots[index] = positional[index];
        }
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0)
            {
                throw TypeError(
                    $"{name}() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }
            if (slot < positionalOnly)
            {
                throw TypeError(
                    $"{name}() got some positional-only arguments passed as keyword "
                        + $"arguments: '{keywordNames[index]}'",
                    span
                );
            }
            if (slots[slot] is not null)
            {
                throw TypeError(
                    $"{name}() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            }
            slots[slot] = keywordValues[index];
        }
        for (var index = 0; index < parameters.Length; index++)
        {
            if (slots[index] is null)
            {
                slots[index] =
                    defaults[index]
                    ?? throw TypeError(
                        $"{name}() missing 1 required positional argument: "
                            + $"'{parameters[index]}'",
                        span
                    );
            }
        }
        return slots;
    }

    private static PythonRuntimeException TypeError(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4039", message, span, "TypeError");
}
