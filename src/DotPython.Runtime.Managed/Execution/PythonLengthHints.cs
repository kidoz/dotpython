using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Length estimates and their Python-visible callback/validation semantics.</summary>
internal static class PythonLengthHints
{
    internal static readonly PythonProtocolFunctionValue IteratorMethod = new(
        "__length_hint__",
        (receiver, arguments) =>
        {
            if (arguments.Count != 0)
                throw Error(
                    $"{PythonBuiltinTypes.GetRuntimeTypeName(receiver!)}.__length_hint__() takes no arguments ({arguments.Count} given)",
                    "TypeError",
                    default
                );
            return GetIteratorHint((PythonIteratorValue)receiver!, default);
        },
        (receiver, arguments, keywordNames, _) =>
            keywordNames.Count == 0
                ? IteratorMethod!.Invoke(receiver, arguments)
                : throw Error(
                    $"{PythonBuiltinTypes.GetRuntimeTypeName(receiver!)}.__length_hint__() takes no keyword arguments",
                    "TypeError",
                    default
                )
    );

    internal static long GetLengthHint(PythonValue value, TextSpan span, long defaultValue = 8)
    {
        try
        {
            if (TryGetLength(value, span, out var length))
                return length;
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "TypeError"))
        { }

        PythonValue? method = null;
        var iterator = value as PythonIteratorValue;
        if (iterator is null || !SupportsIterator(iterator))
        {
            iterator = null;
            if (!ManagedObjectProtocols.TryGetSpecialMethod(value, "__length_hint__", out method))
                return defaultValue;
        }

        PythonValue result;
        try
        {
            result = iterator is not null
                ? GetIteratorHint(iterator, span)
                : UserObjectProtocols.Dispatcher!.Invoke(method!, [], span);
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "TypeError"))
        {
            return defaultValue;
        }
        if (result is PythonNotImplementedValue)
            return defaultValue;
        var integer = result switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => throw Error(
                $"__length_hint__ must be an integer, not {ManagedObjectProtocols.GetTypeName(result)}",
                "TypeError",
                span
            ),
        };
        var hint = ToIndexSize(integer, span);
        if (hint < 0)
            throw Error("__length_hint__() should return >= 0", "ValueError", span);
        return hint;
    }

    internal static bool SupportsIterator(PythonIteratorValue iterator) =>
        iterator.Iterable
            is PythonSequenceIteratorSourceValue
                or PythonListValue
                or PythonTupleValue
                or PythonRangeValue
                or PythonTextValue
                or PythonByteSequenceValue
                or PythonDictionaryValue
                or PythonDictionaryViewValue
                or PythonSetValue;

    private static PythonValue GetIteratorHint(PythonIteratorValue iterator, TextSpan span)
    {
        if (iterator.IsExhausted || iterator.IsInvalidated)
            return PythonWholeNumberValue.Create(0);
        return iterator.Iterable switch
        {
            PythonListValue list => PythonWholeNumberValue.Create(
                Math.Max(0, list.Elements.Count - iterator.Index)
            ),
            PythonTupleValue tuple => PythonWholeNumberValue.Create(
                Math.Max(0, tuple.Elements.Length - iterator.Index)
            ),
            PythonRangeValue range => PythonWholeNumberValue.Create(
                BigInteger.Max(0, range.Count - iterator.RangeIndex)
            ),
            PythonTextValue text => PythonWholeNumberValue.Create(
                Math.Max(0, ManagedObjectProtocols.GetLength(text, span) - iterator.Index)
            ),
            PythonByteSequenceValue bytes => PythonWholeNumberValue.Create(
                Math.Max(0, bytes.Value.Length - iterator.Index)
            ),
            PythonDictionaryValue dictionary => GetCollectionHint(iterator, dictionary.Items.Count),
            PythonDictionaryViewValue view => GetCollectionHint(
                iterator,
                view.Dictionary.Items.Count
            ),
            PythonSetValue set => GetCollectionHint(iterator, set.Elements.Count),
            _ => GetSequenceIteratorHint(iterator, span),
        };
    }

    private static PythonWholeNumberValue GetCollectionHint(
        PythonIteratorValue iterator,
        int count
    ) =>
        PythonWholeNumberValue.Create(
            count == iterator.ExpectedCollectionSize ? Math.Max(0, count - iterator.Index) : 0
        );

    private static PythonValue GetSequenceIteratorHint(PythonIteratorValue iterator, TextSpan span)
    {
        var source = (PythonSequenceIteratorSourceValue)iterator.Iterable;
        if (source.Sequence is not { } sequence)
            return PythonWholeNumberValue.Create(0);
        if (!TryGetLength(sequence, span, out var length))
            return PythonNotImplementedValue.Instance;
        // __len__ may advance or exhaust the iterator, so read its index after the callback.
        return PythonWholeNumberValue.Create(Math.Max(0, length - source.NextIndex));
    }

    private static bool TryGetLength(PythonValue value, TextSpan span, out long length)
    {
        if (ManagedObjectProtocols.GetManagedType(value) is not null)
        {
            if (!ManagedObjectProtocols.TryGetSpecialMethod(value, "__len__", out var method))
            {
                length = 0;
                return false;
            }
            var result = UserObjectProtocols.Dispatcher!.Invoke(method, [], span);
            BigInteger integer;
            if (result is PythonWholeNumberValue whole)
                integer = whole.Value;
            else if (result is PythonTruthValue truth)
                integer = truth.Value ? BigInteger.One : BigInteger.Zero;
            else if (!UserObjectProtocols.TryConvertToIndex(result, span, out integer))
                throw Error(
                    $"'{ManagedObjectProtocols.GetTypeName(result)}' object cannot be interpreted as an integer",
                    "TypeError",
                    span
                );
            length = ToIndexSize(integer, span, "cannot fit 'int' into an index-sized integer");
            if (length < 0)
                throw Error("__len__() should return >= 0", "ValueError", span);
            return true;
        }
        if (value is PythonRangeValue range)
        {
            length = ToIndexSize(range.Count, span, "Python int too large to convert to C ssize_t");
            return true;
        }
        if (
            value
            is PythonTextValue
                or PythonByteSequenceValue
                or PythonListValue
                or PythonTupleValue
                or PythonMappingProxyValue
                or PythonDictionaryValue
                or PythonDictionaryViewValue
                or PythonSetValue
                or PythonExternalObjectValue
        )
        {
            length = ManagedObjectProtocols.GetLength(value, span);
            return true;
        }
        length = 0;
        return false;
    }

    private static long ToIndexSize(
        BigInteger value,
        TextSpan span,
        string message = "Python int too large to convert to C ssize_t"
    ) =>
        value < long.MinValue || value > long.MaxValue
            ? throw Error(message, "OverflowError", span)
            : (long)value;

    private static PythonRuntimeException Error(string message, string type, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
