using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal static class PythonBytesConstruction
{
    private const int MaximumSize = 10_000_000;
    private static readonly string[] Parameters = ["source", "encoding", "errors"];

    internal static PythonByteSequenceValue Construct(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => ConstructWithKeywords(arguments, [], [], span);

    internal static PythonByteSequenceValue ConstructWithKeywords(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        if (positional.Count + names.Count > 3)
            throw Error(
                $"bytes() takes at most 3 arguments ({positional.Count + names.Count} given)",
                "TypeError",
                span
            );
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(Parameters, names[index]);
            if (slot >= 0 && slot < positional.Count)
                throw Error(
                    $"argument for bytes() given by name ('{names[index]}') and position ({slot + 1})",
                    "TypeError",
                    span
                );
        }
        var slots = PythonKeywordArguments.Bind(
            "bytes",
            Parameters,
            0,
            positional,
            names,
            values,
            span
        );
        var encoding = TextArgument(slots[1], "encoding", span);
        var errors = TextArgument(slots[2], "errors", span);
        var source = slots[0];
        if (source is null)
        {
            if (encoding is not null || errors is not null)
                throw Error(
                    encoding is not null
                        ? "encoding without a string argument"
                        : "errors without a string argument",
                    "TypeError",
                    span
                );
            return PythonByteSequenceValue.Empty;
        }
        if (encoding is not null)
        {
            if (source is not PythonTextValue text)
                throw Error("encoding without a string argument", "TypeError", span);
            return PythonByteSequenceValue.Create(
                PythonTextCodecs.Encode(text.Value, encoding, errors ?? "strict", span)
            );
        }
        if (errors is not null)
            throw Error(
                source is PythonTextValue
                    ? "string argument without an encoding"
                    : "errors without a string argument",
                "TypeError",
                span
            );
        if (source is PythonByteSequenceValue bytes)
            return bytes;
        if (UserObjectProtocols.TryGetSpecialMethod(source, "__bytes__", out var hook, out _))
        {
            var result = UserObjectProtocols.Dispatcher!.Invoke(hook, [], span);
            return result as PythonByteSequenceValue
                ?? throw Error(
                    $"__bytes__ returned non-bytes (type {ManagedObjectProtocols.GetTypeName(result)})",
                    "TypeError",
                    span
                );
        }
        if (source is PythonTextValue)
            throw Error("string argument without an encoding", "TypeError", span);
        BigInteger count;
        var hasCount = false;
        try
        {
            hasCount = TryIndex(source, span, out count);
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "TypeError"))
        {
            // CPython retries as an iterable only when count conversion raises TypeError.
            count = 0;
        }
        if (hasCount)
        {
            var minimum =
                IntPtr.Size == sizeof(long) ? new BigInteger(long.MinValue) : int.MinValue;
            var maximum =
                IntPtr.Size == sizeof(long) ? new BigInteger(long.MaxValue) : int.MaxValue;
            if (count < minimum || count > maximum)
                throw Error(
                    $"cannot fit '{ManagedObjectProtocols.GetTypeName(source)}' into an index-sized integer",
                    "OverflowError",
                    span
                );
            if (count < 0)
                throw Error("negative count", "ValueError", span);
            CheckSize(count, span);
            for (var index = 0; index < (int)count; index += 256)
                CheckWork(span);
            return count.IsZero
                ? PythonByteSequenceValue.Empty
                : new PythonByteSequenceValue(new byte[(int)count]);
        }
        return FromIterable(source, span);
    }

    private static PythonByteSequenceValue FromIterable(PythonValue source, TextSpan span)
    {
        var result = new List<byte>();
        bool smallBuffer;
        if (source is PythonListValue list)
        {
            CheckSize(list.Elements.Count, span);
            smallBuffer = list.Elements.Count <= 512;
            for (var index = 0; index < list.Elements.Count; index++)
            {
                CheckWork(span);
                Append(result, list.Elements[index], span);
            }
        }
        else if (source is PythonTupleValue tuple)
        {
            CheckSize(tuple.Elements.Length, span);
            smallBuffer = false;
            foreach (var item in tuple.Elements)
            {
                CheckWork(span);
                Append(result, item, span);
            }
        }
        else
        {
            PythonIteratorValue iterator;
            try
            {
                iterator = ManagedObjectProtocols.GetIterator(source, span);
            }
            catch (Exception error)
                when (PythonNamespaceMapping.IsPythonException(error, "TypeError"))
            {
                throw Error(
                    $"cannot convert '{ManagedObjectProtocols.GetTypeName(source)}' object to bytes",
                    "TypeError",
                    span
                );
            }
            // Validate and observe hints, but grow from actual items instead of trusting
            // an untrusted estimate. Preserve the writer's singleton identity boundary.
            smallBuffer = PythonLengthHints.GetLengthHint(source, span, defaultValue: 64) <= 512;
            while (ManagedObjectProtocols.TryGetNext(iterator, out var item, span))
            {
                CheckWork(span);
                Append(result, item, span);
            }
        }
        var buffer = result.ToArray();
        return buffer.Length == 0 || smallBuffer
            ? PythonByteSequenceValue.Create(buffer)
            : new PythonByteSequenceValue(buffer);
    }

    private static void Append(List<byte> result, PythonValue item, TextSpan span)
    {
        if (!TryIndex(item, span, out var value))
            throw Error(
                $"'{ManagedObjectProtocols.GetTypeName(item)}' object cannot be interpreted as an integer",
                "TypeError",
                span
            );
        if (value < 0 || value > 255)
            throw Error("bytes must be in range(0, 256)", "ValueError", span);
        CheckSize(result.Count + 1, span);
        result.Add((byte)value);
    }

    private static bool TryIndex(PythonValue value, TextSpan span, out BigInteger index)
    {
        if (value is PythonWholeNumberValue whole)
        {
            index = whole.Value;
            return true;
        }
        if (value is PythonTruthValue truth)
        {
            index = truth.Value ? 1 : 0;
            return true;
        }
        return UserObjectProtocols.TryConvertToIndex(value, span, out index);
    }

    private static string? TextArgument(PythonValue? value, string name, TextSpan span)
    {
        if (value is null)
            return null;
        if (value is not PythonTextValue text)
            throw Error(
                $"bytes() argument '{name}' must be str, not {(value is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(value))}",
                "TypeError",
                span
            );
        if (text.Value.Contains('\0', StringComparison.Ordinal))
            throw Error("embedded null character", "ValueError", span);
        return text.Value;
    }

    private static void CheckSize(BigInteger size, TextSpan span)
    {
        if (size > MaximumSize)
            throw Error("The bytes result exceeds the supported size.", "OverflowError", span);
    }

    private static void CheckWork(TextSpan span) =>
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);

    private static PythonRuntimeException Error(string message, string type, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
