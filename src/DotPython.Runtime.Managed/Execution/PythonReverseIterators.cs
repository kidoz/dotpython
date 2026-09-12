using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Lazy reverse cursors; user callbacks always resolve through the active dispatcher.</summary>
internal static class PythonReverseIterators
{
    internal static readonly PythonProtocolFunctionValue Method = new(
        "__reversed__",
        (receiver, arguments) =>
        {
            if (arguments.Count != 0)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{PythonBuiltinTypes.GetRuntimeTypeName(receiver!)}.__reversed__() takes no arguments ({arguments.Count} given)",
                    default,
                    "TypeError"
                );
            return Create(receiver!, default);
        },
        (receiver, arguments, names, _) =>
            names.Count == 0
                ? Method!.Invoke(receiver, arguments)
                : throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{PythonBuiltinTypes.GetRuntimeTypeName(receiver!)}.__reversed__() takes no keyword arguments",
                    default,
                    "TypeError"
                )
    );

    internal static PythonValue Create(PythonValue sequence, TextSpan span)
    {
        if (sequence is PythonMappingProxyValue proxy)
            return Create(proxy.Mapping, span);
        if (sequence is PythonManagedObjectValue instance)
        {
            if (
                ManagedObjectProtocols.TryGetSpecialMethod(instance, "__reversed__", out var method)
            )
            {
                if (method is PythonNoneValue)
                    throw NotReversible(sequence, span);
                return UserObjectProtocols.Dispatcher!.Invoke(method, [], span);
            }
            if (!UserObjectProtocols.DefinesSpecialMethod(instance, "__getitem__"))
                throw NotReversible(sequence, span);
        }
        else if (
            sequence
            is not (
                PythonListValue
                or PythonTupleValue
                or PythonTextValue
                or PythonByteSequenceValue
                or PythonRangeValue
                or PythonDictionaryValue
                or PythonDictionaryViewValue
            )
        )
            throw NotReversible(sequence, span);

        var count = sequence switch
        {
            PythonRangeValue => 0,
            _ => PythonLengthHints.GetSequenceLength(sequence, span),
        };
        var name = sequence switch
        {
            PythonListValue => "list_reverseiterator",
            PythonRangeValue range => GetRangeTypeName(range),
            PythonDictionaryValue => "dict_reversekeyiterator",
            PythonDictionaryViewValue { Kind: "dict_values" } => "dict_reversevalueiterator",
            PythonDictionaryViewValue { Kind: "dict_items" } => "dict_reverseitemiterator",
            PythonDictionaryViewValue => "dict_reversekeyiterator",
            _ => "reversed",
        };
        return new PythonIteratorValue(
            new PythonReverseIteratorSourceValue
            {
                Sequence = sequence,
                NextIndex = sequence switch
                {
                    PythonDictionaryValue dictionary => dictionary.EntryCount - 1,
                    PythonDictionaryViewValue view => view.Dictionary.EntryCount - 1,
                    _ => count - 1,
                },
                TypeName = name,
            },
            sequence is PythonDictionaryValue or PythonDictionaryViewValue ? (int)count : -1
        );
    }

    internal static bool TryGetNext(
        PythonIteratorValue iterator,
        PythonReverseIteratorSourceValue source,
        out PythonValue value,
        TextSpan span
    )
    {
        value = PythonNoneValue.Instance;
        if (source.Sequence is not { } sequence)
            return false;
        if (sequence is PythonRangeValue range)
        {
            if (iterator.RangeIndex < range.Count)
            {
                value = PythonWholeNumberValue.Create(
                    range.Start + (range.Count - 1 - iterator.RangeIndex) * range.Step
                );
                iterator.RangeIndex++;
                return true;
            }
            Exhaust(iterator, source);
            return false;
        }
        var dictionary = sequence switch
        {
            PythonDictionaryValue mapping => mapping,
            PythonDictionaryViewValue view => view.Dictionary,
            _ => null,
        };
        if (dictionary is not null)
        {
            ManagedObjectProtocols.ValidateIteratorSize(
                iterator,
                dictionary.Items.Count,
                "dictionary",
                span
            );
            if (source.NextIndex >= dictionary.EntryCount)
            {
                Exhaust(iterator, source);
                return false;
            }
            while (source.NextIndex >= 0)
            {
                var item = dictionary.GetEntry((int)source.NextIndex);
                if (item is null)
                {
                    UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                    source.NextIndex--;
                    continue;
                }
                source.NextIndex--;
                iterator.Index++;
                value = PythonMappingProxies.ViewItem(
                    item,
                    sequence is PythonDictionaryViewValue view ? view.Kind : "dict_keys"
                );
                return true;
            }
            Exhaust(iterator, source);
            return false;
        }
        if (source.NextIndex < 0)
        {
            Exhaust(iterator, source);
            return false;
        }
        var index = source.NextIndex;
        try
        {
            value = GetSequenceItem(sequence, index, span);
            source.NextIndex = index - 1;
            return true;
        }
        catch (Exception error)
            when (error
                    is PythonRaisedException
                        or PythonRuntimeException { PythonExceptionTypeName: not null }
            )
        {
            Exhaust(iterator, source);
            if (
                PythonNamespaceMapping.IsPythonException(error, "IndexError")
                || PythonNamespaceMapping.IsPythonException(error, "StopIteration")
            )
                return false;
            throw;
        }
    }

    private static PythonValue GetSequenceItem(PythonValue sequence, long index, TextSpan span)
    {
        var key = PythonWholeNumberValue.Create(index);
        if (sequence is not PythonManagedObjectValue)
            return ManagedObjectProtocols.GetItem(sequence, key, span);
        if (ManagedObjectProtocols.TryGetSpecialMethod(sequence, "__getitem__", out var method))
            return UserObjectProtocols.Dispatcher!.Invoke(method, [key], span);
        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"'{ManagedObjectProtocols.GetTypeName(sequence)}' object does not support indexing",
            span,
            "TypeError"
        );
    }

    internal static PythonWholeNumberValue GetHint(
        PythonIteratorValue iterator,
        PythonReverseIteratorSourceValue source,
        TextSpan span
    )
    {
        if (source.Sequence is not { } sequence)
            return PythonWholeNumberValue.Create(0);
        if (sequence is PythonRangeValue range)
            return PythonWholeNumberValue.Create(
                BigInteger.Max(0, range.Count - iterator.RangeIndex)
            );
        if (sequence is PythonDictionaryValue or PythonDictionaryViewValue)
        {
            var count = sequence is PythonDictionaryValue dictionary
                ? dictionary.Items.Count
                : ((PythonDictionaryViewValue)sequence).Dictionary.Items.Count;
            return PythonWholeNumberValue.Create(
                count == iterator.ExpectedCollectionSize ? Math.Max(0, count - iterator.Index) : 0
            );
        }
        // CPython snapshots the position before invoking a potentially reentrant __len__.
        var position = source.NextIndex + 1;
        if (position <= 0)
            return PythonWholeNumberValue.Create(0);
        var length = PythonLengthHints.GetSequenceLength(sequence, span);
        return PythonWholeNumberValue.Create(length < position ? 0 : position);
    }

    private static void Exhaust(
        PythonIteratorValue iterator,
        PythonReverseIteratorSourceValue source
    )
    {
        source.NextIndex = -1;
        source.Sequence = null;
        iterator.IsExhausted = true;
    }

    private static string GetRangeTypeName(PythonRangeValue range)
    {
        // CPython's reversed-range admission differs from forward range_iter.
        var narrow = OperatingSystem.IsWindows() || IntPtr.Size == 4;
        var minimum = new BigInteger(narrow ? int.MinValue : long.MinValue);
        var maximum = new BigInteger(narrow ? int.MaxValue : long.MaxValue);
        return
            range.Start < minimum
            || range.Start > maximum
            || range.Stop < minimum
            || range.Stop > maximum
            || range.Step <= minimum
            || range.Step > maximum
            || range.Start - range.Step < minimum
            || range.Start - range.Step > maximum
            || range.Count > maximum
            ? "longrange_iterator"
            : "range_iterator";
    }

    private static PythonRuntimeException NotReversible(PythonValue value, TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4003",
            $"'{ManagedObjectProtocols.GetTypeName(value)}' object is not reversible",
            span,
            "TypeError"
        );
}
