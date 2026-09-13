using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The independent member state of UnicodeEncodeError and UnicodeDecodeError. Initialization and
/// formatting follow CPython 3.14.7 Objects/exceptions.c, including failed reinit.
/// </summary>
internal static class PythonUnicodeErrors
{
    [ThreadStatic]
    private static int _formatDepth;

    internal sealed class State
    {
        // A null member models CPython's unset pointer, which is distinct from
        // an explicitly assigned Python None even though both read as None.
        internal bool IsEncode { get; set; }
        internal PythonValue? Encoding { get; set; }
        internal PythonValue? Object { get; set; }
        internal long Start { get; set; }
        internal long End { get; set; }
        internal PythonValue? Reason { get; set; }
    }

    internal static bool IsApplicable(PythonExceptionValue exception) =>
        exception.UnicodeErrorState is not null || GetBuiltinKind(exception) is not null;

    internal static bool IsEncode(PythonExceptionValue exception) =>
        exception.UnicodeErrorState?.IsEncode ?? GetBuiltinKind(exception) == "UnicodeEncodeError";

    private static string? GetBuiltinKind(PythonExceptionValue exception)
    {
        if (exception.ManagedType is null)
            return exception.TypeName is "UnicodeEncodeError" or "UnicodeDecodeError"
                ? exception.TypeName
                : null;
        foreach (var type in PythonBuiltinTypes.GetMro(exception.ManagedType).Elements)
            if (
                type is PythonExceptionTypeValue
                {
                    Name: "UnicodeEncodeError" or "UnicodeDecodeError"
                } builtin
            )
                return builtin.Name;
        return null;
    }

    internal static void Initialize(
        PythonExceptionValue exception,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        // BaseException.__init__ has already rejected keywords. Its args write
        // precedes all positional validation, and is retained if validation fails.
        exception.Arguments = [.. arguments];
        if (arguments.Count != 5)
            throw Error($"function takes exactly 5 arguments ({arguments.Count} given)", span);
        if (arguments[0] is not PythonTextValue)
            throw Error(
                $"argument 1 must be str, not {(arguments[0] is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(arguments[0]))}",
                span
            );
        var isEncode = IsEncode(exception);
        if (isEncode && arguments[1] is not PythonTextValue)
            throw Error(
                $"argument 2 must be str, not {(arguments[1] is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(arguments[1]))}",
                span
            );
        var start = ReadIndex(arguments[2], allowIndexProtocol: true, span);
        var end = ReadIndex(arguments[3], allowIndexProtocol: true, span);
        if (arguments[4] is not PythonTextValue)
            throw Error(
                $"argument 5 must be str, not {(arguments[4] is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(arguments[4]))}",
                span
            );

        // Buffer acquisition comes after both index conversions and reason
        // validation. Exact bytes retain identity; additional Python buffer
        // exporters are outside the represented constructor's current surface.
        if (!isEncode && arguments[1] is not PythonByteSequenceValue)
            throw Error(
                $"a bytes-like object is required, not '{ManagedObjectProtocols.GetTypeName(arguments[1])}'",
                span
            );

        // Callbacks may have reinitialized this same exception. Publish to its
        // current state only after every conversion succeeds; never rebind args
        // again, since a callback's args assignment must survive this publication.
        var state = exception.UnicodeErrorState ??= new State { IsEncode = IsEncode(exception) };
        state.IsEncode = isEncode;
        state.Encoding = arguments[0];
        state.Object = arguments[1];
        state.Start = start;
        state.End = end;
        state.Reason = arguments[4];
    }

    internal static string Format(PythonExceptionValue exception, TextSpan span = default)
    {
        var state = exception.UnicodeErrorState;
        if (state?.Object is null)
            return string.Empty;
        if (_formatDepth >= 128)
            throw Error(
                "maximum recursion depth exceeded while getting the str of an object",
                span,
                "RecursionError"
            );
        ++_formatDepth;
        try
        {
            // Order matters: converting reason can replace encoding, and either
            // callback can replace/delete object or change the numeric members.
            var reason = FormatMember(state.Reason, span);
            state = exception.UnicodeErrorState!;
            var encoding = FormatMember(state.Encoding, span);
            state = exception.UnicodeErrorState!;
            if (state.Object is null)
                throw Error("UnicodeError 'object' attribute is not set", span);
            var start = state.Start;
            var end = state.End;
            if (state.IsEncode)
            {
                if (state.Object is not PythonTextValue text)
                    throw Error("UnicodeError 'object' attribute must be a string", span);
                if (
                    start >= 0
                    && end >= 0
                    && end == start + 1
                    && TryGetCodePoint(text.Value, start, span, out var scalar)
                )
                {
                    var escaped =
                        scalar <= 0xff ? $"\\x{scalar:x2}"
                        : scalar <= 0xffff ? $"\\u{scalar:x4}"
                        : $"\\U{scalar:x8}";
                    return $"'{encoding}' codec can't encode character '{escaped}' in position {start}: {reason}";
                }
            }
            else
            {
                if (state.Object is not PythonByteSequenceValue bytes)
                    throw Error("UnicodeError 'object' attribute must be a bytes", span);
                if (
                    start >= 0
                    && start < bytes.Value.Length
                    && end >= 0
                    && end <= bytes.Value.Length
                    && end == start + 1
                )
                    return $"'{encoding}' codec can't decode byte 0x{bytes.Value[(int)start]:x2} in position {start}: {reason}";
            }
            var last = IntPtr.Size == sizeof(int) ? unchecked((int)end - 1) : unchecked(end - 1);
            var operation = state.IsEncode ? "encode characters" : "decode bytes";
            return $"'{encoding}' codec can't {operation} in position {start}-{last}: {reason}";
        }
        finally
        {
            --_formatDepth;
        }
    }

    private static bool TryGetCodePoint(string text, long position, TextSpan span, out int scalar)
    {
        scalar = 0;
        if (position < 0 || position >= text.Length)
            return false;
        var ordinal = 0;
        for (var offset = 0; offset < text.Length; ++offset, ++ordinal)
        {
            if ((ordinal & 127) == 0)
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            var first = text[offset];
            var paired =
                char.IsHighSurrogate(first)
                && offset + 1 < text.Length
                && char.IsLowSurrogate(text[offset + 1]);
            if (ordinal == position)
            {
                scalar = paired ? char.ConvertToUtf32(first, text[offset + 1]) : first;
                return true;
            }
            if (paired)
                ++offset;
        }
        return false;
    }

    private static string FormatMember(PythonValue? value, TextSpan span)
    {
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
        string text;
        if (
            value is PythonManagedObjectValue instance
            && UserObjectProtocols.Dispatcher is { } dispatcher
        )
        {
            var name = "__str__";
            if (!UserObjectProtocols.TryGetSpecialMethod(instance, name, out var method, out _))
            {
                name = "__repr__";
                if (!UserObjectProtocols.TryGetSpecialMethod(instance, name, out method, out _))
                    return instance.DefaultRepresentation;
            }
            // Binding happens once, and an explicit None slot is invoked too.
            var result = dispatcher.Invoke(method, [], span);
            text = result is PythonTextValue resultText
                ? resultText.Value
                : throw Error(
                    $"{name} returned non-string (type {ManagedObjectProtocols.GetTypeName(result)})",
                    span
                );
        }
        else
        {
            text = value?.ToDisplayString() ?? "<NULL>";
        }
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
        return text;
    }

    internal static bool TryGet(PythonExceptionValue exception, string name, out PythonValue value)
    {
        if (!IsApplicable(exception))
        {
            value = null!;
            return false;
        }
        var state = exception.UnicodeErrorState;
        value = name switch
        {
            "encoding" => state?.Encoding ?? PythonNoneValue.Instance,
            "object" => state?.Object ?? PythonNoneValue.Instance,
            "start" => PythonWholeNumberValue.Create(state?.Start ?? 0),
            "end" => PythonWholeNumberValue.Create(state?.End ?? 0),
            "reason" => state?.Reason ?? PythonNoneValue.Instance,
            _ => null!,
        };
        return value is not null;
    }

    internal static bool TrySet(
        PythonExceptionValue exception,
        string name,
        PythonValue value,
        TextSpan span
    )
    {
        if (!IsApplicable(exception) || !IsMember(name))
            return false;
        var state = exception.UnicodeErrorState ??= new State { IsEncode = IsEncode(exception) };
        switch (name)
        {
            case "encoding":
                state.Encoding = value;
                break;
            case "object":
                state.Object = value;
                break;
            case "start":
                state.Start = ReadIndex(value, allowIndexProtocol: false, span);
                break;
            case "end":
                state.End = ReadIndex(value, allowIndexProtocol: false, span);
                break;
            case "reason":
                state.Reason = value;
                break;
        }
        return true;
    }

    internal static bool TryDelete(PythonExceptionValue exception, string name, TextSpan span)
    {
        if (!IsApplicable(exception) || !IsMember(name))
            return false;
        if (name is "start" or "end")
            throw Error("can't delete numeric/char attribute", span);
        var state = exception.UnicodeErrorState ??= new State { IsEncode = IsEncode(exception) };
        switch (name)
        {
            case "encoding":
                state.Encoding = null;
                break;
            case "object":
                state.Object = null;
                break;
            case "reason":
                state.Reason = null;
                break;
        }
        return true;
    }

    private static bool IsMember(string name) =>
        name is "encoding" or "object" or "start" or "end" or "reason";

    private static long ReadIndex(PythonValue value, bool allowIndexProtocol, TextSpan span)
    {
        BigInteger result;
        if (value is PythonWholeNumberValue whole)
            result = whole.Value;
        else if (value is PythonTruthValue truth)
            result = truth.Value ? BigInteger.One : BigInteger.Zero;
        else if (!allowIndexProtocol)
            throw Error("an integer is required", span);
        else if (!UserObjectProtocols.TryConvertToIndex(value, span, out result))
            throw Error(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
                span
            );
        var minimum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MinValue) : int.MinValue;
        var maximum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MaxValue) : int.MaxValue;
        if (result < minimum || result > maximum)
            throw Error("Python int too large to convert to C ssize_t", span, "OverflowError");
        return (long)result;
    }

    private static PythonRuntimeException Error(
        string message,
        TextSpan span,
        string type = "TypeError"
    ) => ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
