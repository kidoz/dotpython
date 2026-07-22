using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal enum PythonRichComparison
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

/// <summary>
/// Central protocol dispatch for managed values exposed through a native-compatibility boundary.
/// </summary>
internal static class ManagedObjectProtocols
{
    internal static PythonValue Call(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(arguments);

        return callable switch
        {
            PythonBuiltinFunctionValue builtin => builtin.Invoke(arguments, span),
            PythonProtocolFunctionValue function => function.Invoke(null, arguments),
            PythonBoundMethodValue method => method.Function.Invoke(method.Target, arguments),
            PythonExternalObjectValue external => external.Protocol.Call(arguments, span),
            PythonManagedTypeValue type when type.Construct is not null => type.Construct(
                arguments
            ),
            PythonManagedTypeValue type when arguments.Count == 0 => new PythonManagedObjectValue(
                type
            ),
            _ => throw Fault("DPY4009", "This value is not callable.", span, "TypeError"),
        };
    }

    internal static PythonValue GetAttribute(
        PythonValue target,
        string name,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        switch (target)
        {
            case PythonModuleValue module
                when module.Globals.TryGetValue(name, out var moduleValue):
                return moduleValue;
            case PythonModuleValue module:
                throw Fault(
                    "DPY4022",
                    $"Module '{module.Name}' has no attribute '{name}'.",
                    span,
                    "AttributeError"
                );
            case PythonManagedObjectValue instance:
                if (
                    TryGetTypeAttribute(instance.Type, name, out var typeValue)
                    && typeValue is PythonDescriptorValue { IsDataDescriptor: true } descriptor
                )
                {
                    return descriptor.Get(instance);
                }

                if (instance.Attributes.TryGetValue(name, out var instanceValue))
                {
                    return instanceValue;
                }

                if (typeValue is not null)
                {
                    return BindDescriptor(typeValue, instance);
                }

                throw MissingAttribute(instance.Type.Name, name, span);
            case PythonManagedTypeValue type when TryGetTypeAttribute(type, name, out var value):
                return value;
            case PythonManagedTypeValue type:
                throw MissingAttribute(type.Name, name, span);
            case PythonExternalObjectValue external:
                return external.Protocol.GetAttribute(name, span);
            case var builtin when PythonBuiltinMethods.SupportsMethods(builtin):
                if (PythonBuiltinMethods.TryGet(builtin, name, out var method))
                {
                    return new PythonBoundMethodValue(name, builtin, method);
                }

                throw Fault(
                    "DPY4023",
                    $"'{GetTypeName(builtin)}' object has no attribute '{name}'.",
                    span,
                    "AttributeError"
                );
            default:
                throw Fault(
                    "DPY4023",
                    "This value does not expose managed attributes.",
                    span,
                    "AttributeError"
                );
        }
    }

    internal static void SetAttribute(
        PythonValue target,
        string name,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        switch (target)
        {
            case PythonModuleValue module:
                module.Globals.SetValue(name, value);
                return;
            case PythonManagedObjectValue instance:
                if (
                    TryGetTypeAttribute(instance.Type, name, out var typeValue)
                    && typeValue is PythonDescriptorValue { IsDataDescriptor: true } descriptor
                )
                {
                    if (descriptor.Set is null)
                    {
                        throw Fault(
                            "DPY4023",
                            $"Attribute '{name}' is read-only.",
                            span,
                            "AttributeError"
                        );
                    }

                    descriptor.Set(instance, value);
                    return;
                }

                instance.Attributes[name] = value;
                return;
            case PythonManagedTypeValue type:
                type.Attributes[name] = value;
                return;
            default:
                throw Fault(
                    "DPY4023",
                    "This value does not expose writable managed attributes.",
                    span,
                    "AttributeError"
                );
        }
    }

    internal static int GetLength(PythonValue value, TextSpan span = default) =>
        value switch
        {
            PythonTextValue text => text.Value.EnumerateRunes().Count(),
            PythonByteSequenceValue bytes => bytes.Value.Length,
            PythonListValue list => list.Elements.Count,
            PythonTupleValue tuple => tuple.Elements.Length,
            PythonDictionaryValue dictionary => dictionary.Items.Count,
            PythonDictionaryViewValue view => view.Snapshot.Elements.Count,
            PythonExternalObjectValue external => external.Protocol.GetLength(span),
            _ => throw Fault("DPY4011", "This value has no managed length.", span, "TypeError"),
        };

    internal static PythonIteratorValue GetIterator(PythonValue value, TextSpan span = default)
    {
        if (value is PythonIteratorValue iterator)
        {
            return iterator;
        }

        if (value is PythonDictionaryViewValue view)
        {
            return GetIterator(view.Snapshot, span);
        }

        if (
            value
            is not (
                PythonListValue
                or PythonTupleValue
                or PythonDictionaryValue
                or PythonTextValue
                or PythonByteSequenceValue
            )
        )
        {
            throw Fault("DPY4015", "This value is not iterable.", span, "TypeError");
        }

        return new PythonIteratorValue(
            value,
            value is PythonDictionaryValue dictionary ? dictionary.SizeVersion : -1
        );
    }

    internal static bool TryGetNext(
        PythonIteratorValue iterator,
        out PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(iterator);
        switch (iterator.Iterable)
        {
            case PythonListValue list when iterator.Index < list.Elements.Count:
                value = list.Elements[iterator.Index++];
                return true;
            case PythonTupleValue tuple when iterator.Index < tuple.Elements.Length:
                value = tuple.Elements[iterator.Index++];
                return true;
            case PythonDictionaryValue dictionary:
                if (dictionary.SizeVersion != iterator.ExpectedDictionarySizeVersion)
                {
                    throw Fault(
                        "DPY4016",
                        "Dictionary size changed during iteration.",
                        span,
                        "RuntimeError"
                    );
                }

                if (iterator.Index < dictionary.Items.Count)
                {
                    value = dictionary.Items[iterator.Index++].Key;
                    return true;
                }

                break;
            case PythonTextValue text:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                if (iterator.Index < runes.Length)
                {
                    value = new PythonTextValue(runes[iterator.Index++].ToString());
                    return true;
                }

                break;
            }
            case PythonByteSequenceValue bytes when iterator.Index < bytes.Value.Length:
                value = PythonWholeNumberValue.Create(bytes.Value[iterator.Index++]);
                return true;
        }

        value = PythonNoneValue.Instance;
        return false;
    }

    internal static PythonValue GetItem(
        PythonValue target,
        PythonValue index,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(index);

        switch (target)
        {
            case PythonListValue list when index is PythonSliceValue slice:
            {
                var result = new List<PythonValue>();
                foreach (
                    var elementIndex in EnumerateSliceIndices(slice, list.Elements.Count, span)
                )
                {
                    result.Add(list.Elements[elementIndex]);
                }

                return new PythonListValue(result);
            }
            case PythonTupleValue tuple when index is PythonSliceValue slice:
            {
                var result = new List<PythonValue>();
                foreach (
                    var elementIndex in EnumerateSliceIndices(slice, tuple.Elements.Length, span)
                )
                {
                    result.Add(tuple.Elements[elementIndex]);
                }

                return new PythonTupleValue([.. result]);
            }
            case PythonTextValue text when index is PythonSliceValue slice:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                var builder = new StringBuilder();
                foreach (var elementIndex in EnumerateSliceIndices(slice, runes.Length, span))
                {
                    builder.Append(runes[elementIndex].ToString());
                }

                return new PythonTextValue(builder.ToString());
            }
            case PythonListValue list:
                return list.Elements[GetSequenceIndex(index, list.Elements.Count, span)];
            case PythonTupleValue tuple:
                return tuple.Elements[GetSequenceIndex(index, tuple.Elements.Length, span)];
            case PythonTextValue text:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                return new PythonTextValue(
                    runes[GetSequenceIndex(index, runes.Length, span)].ToString()
                );
            }
            case PythonByteSequenceValue bytes:
                return PythonWholeNumberValue.Create(
                    bytes.Value[GetSequenceIndex(index, bytes.Value.Length, span)]
                );
            case PythonDictionaryValue dictionary
                when TryFindDictionaryItem(dictionary, index, out var item):
                return item.Value;
            case PythonDictionaryValue:
                throw Fault("DPY4013", "The dictionary key was not found.", span, "KeyError");
            case PythonExternalObjectValue external:
                return external.Protocol.GetItem(index, span);
            default:
                throw Fault("DPY4011", "This value is not subscriptable.", span, "TypeError");
        }
    }

    internal static void SetItem(
        PythonValue target,
        PythonValue index,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(value);

        switch (target)
        {
            case PythonListValue list when index is PythonSliceValue slice:
                AssignListSlice(list, slice, value, span);
                return;
            case PythonListValue list:
                list.Elements[GetSequenceIndex(index, list.Elements.Count, span)] = value;
                return;
            case PythonDictionaryValue dictionary:
                SetDictionaryItem(dictionary, index, value, span);
                return;
            default:
                throw Fault(
                    "DPY4011",
                    "This value does not support item assignment.",
                    span,
                    "TypeError"
                );
        }
    }

    internal static void SetListItem(
        PythonListValue list,
        int index,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(value);
        if ((uint)index >= (uint)list.Elements.Count)
        {
            throw Fault("DPY4012", "The list index is out of range.", span, "IndexError");
        }

        list.Elements[index] = value;
    }

    internal static void SetTupleItem(
        PythonTupleValue tuple,
        int index,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(tuple);
        ArgumentNullException.ThrowIfNull(value);
        if ((uint)index >= (uint)tuple.Elements.Length)
        {
            throw Fault("DPY4012", "The tuple index is out of range.", span, "IndexError");
        }

        tuple.Elements[index] = value;
    }

    internal static bool Contains(PythonValue container, PythonValue item, TextSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(item);
        if (container is PythonTextValue text)
        {
            if (item is not PythonTextValue substring)
            {
                throw Fault(
                    "DPY4011",
                    "A string membership test requires a string operand.",
                    span,
                    "TypeError"
                );
            }

            return text.Value.Contains(substring.Value, StringComparison.Ordinal);
        }

        if (container is PythonDictionaryValue dictionary)
        {
            return TryFindDictionaryItem(dictionary, item, out _);
        }

        var iterator = GetIterator(container, span);
        while (TryGetNext(iterator, out var candidate, span))
        {
            if (AreEqual(candidate, item))
            {
                return true;
            }
        }

        return false;
    }

    internal static IEnumerable<int> EnumerateSliceIndices(
        PythonSliceValue slice,
        int length,
        TextSpan span
    )
    {
        var (start, stop, step) = GetSliceIndices(slice, length, span);
        if (step > 0)
        {
            for (var index = start; index < stop; index += step)
            {
                yield return index;
            }
        }
        else
        {
            for (var index = start; index > stop; index += step)
            {
                yield return index;
            }
        }
    }

    internal static (int Start, int Stop, int Step) GetSliceIndices(
        PythonSliceValue slice,
        int length,
        TextSpan span
    )
    {
        var step = slice.Step is PythonNoneValue ? 1 : GetSliceBound(slice.Step, span);
        if (step == 0)
        {
            throw Fault("DPY4012", "The slice step cannot be zero.", span, "ValueError");
        }

        var start =
            slice.Start is PythonNoneValue
                ? (step > 0 ? 0 : length - 1)
                : AdjustSliceIndex(GetSliceBound(slice.Start, span), length, step);
        var stop =
            slice.Stop is PythonNoneValue
                ? (step > 0 ? length : -1)
                : AdjustSliceIndex(GetSliceBound(slice.Stop, span), length, step);
        return (start, stop, step);
    }

    private static int AdjustSliceIndex(int index, int length, int step)
    {
        if (index < 0)
        {
            index += length;
            if (index < 0)
            {
                return step < 0 ? -1 : 0;
            }

            return index;
        }

        if (index >= length)
        {
            return step < 0 ? length - 1 : length;
        }

        return index;
    }

    private static int GetSliceBound(PythonValue value, TextSpan span)
    {
        var promoted = PromoteTruthValue(value);
        if (promoted is not PythonWholeNumberValue wholeNumber)
        {
            throw Fault("DPY4011", "Slice indices must be integers or None.", span, "TypeError");
        }

        if (wholeNumber.Value > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (wholeNumber.Value < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)wholeNumber.Value;
    }

    internal static void AssignListSlice(
        PythonListValue list,
        PythonSliceValue slice,
        PythonValue value,
        TextSpan span
    )
    {
        var values = MaterializeValues(value, span);
        var (start, stop, step) = GetSliceIndices(slice, list.Elements.Count, span);
        if (step == 1)
        {
            if (stop < start)
            {
                stop = start;
            }

            list.Elements.RemoveRange(start, stop - start);
            list.Elements.InsertRange(start, values);
            return;
        }

        var indices = EnumerateSliceIndices(slice, list.Elements.Count, span).ToList();
        if (indices.Count != values.Count)
        {
            throw Fault(
                "DPY4012",
                $"An extended slice of size {indices.Count} cannot accept "
                    + $"{values.Count} value(s).",
                span,
                "ValueError"
            );
        }

        for (var position = 0; position < indices.Count; position++)
        {
            list.Elements[indices[position]] = values[position];
        }
    }

    internal static void ExtendList(PythonListValue list, PythonValue iterable, TextSpan span)
    {
        var values = MaterializeValues(iterable, span);
        list.Elements.AddRange(values);
    }

    internal static void RepeatListInPlace(PythonListValue list, PythonValue count, TextSpan span)
    {
        var promoted = PromoteTruthValue(count);
        if (promoted is not PythonWholeNumberValue wholeNumber)
        {
            throw Fault(
                "DPY4011",
                "A list can only be repeated by an integer count.",
                span,
                "TypeError"
            );
        }

        if (wholeNumber.Value <= 0)
        {
            list.Elements.Clear();
            return;
        }

        var snapshot = list.Elements.ToArray();
        for (var repetition = 1; repetition < wholeNumber.Value; repetition++)
        {
            list.Elements.AddRange(snapshot);
        }
    }

    internal static List<PythonValue> MaterializeValues(PythonValue iterable, TextSpan span)
    {
        var values = new List<PythonValue>();
        var iterator = GetIterator(iterable, span);
        while (TryGetNext(iterator, out var value, span))
        {
            values.Add(value);
        }

        return values;
    }

    internal static bool IsTrue(PythonValue value) =>
        value switch
        {
            PythonNoneValue => false,
            PythonTruthValue truth => truth.Value,
            PythonWholeNumberValue whole => !whole.Value.IsZero,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value != 0,
            PythonComplexValue complex => complex.Value != Complex.Zero,
            PythonTextValue text => text.Value.Length != 0,
            PythonByteSequenceValue bytes => bytes.Value.Length != 0,
            PythonListValue list => list.Elements.Count != 0,
            PythonTupleValue tuple => tuple.Elements.Length != 0,
            PythonDictionaryValue dictionary => dictionary.Items.Count != 0,
            _ => true,
        };

    internal static PythonTruthValue RichCompare(
        PythonValue left,
        PythonValue right,
        PythonRichComparison comparison,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left is PythonExternalObjectValue leftExternal)
        {
            return leftExternal.Protocol.RichCompare(right, comparison, span);
        }

        if (right is PythonExternalObjectValue rightExternal)
        {
            return rightExternal.Protocol.RichCompare(left, Reverse(comparison), span);
        }

        if (comparison is PythonRichComparison.Equal or PythonRichComparison.NotEqual)
        {
            var equals = AreEqual(left, right);
            return PythonTruthValue.FromBoolean(
                comparison == PythonRichComparison.Equal ? equals : !equals
            );
        }

        if (HasUnorderedFloatingPointOperand(left, right))
        {
            return PythonTruthValue.False;
        }

        var ordered = CompareOrdered(left, right, span);
        return PythonTruthValue.FromBoolean(
            comparison switch
            {
                PythonRichComparison.LessThan => ordered < 0,
                PythonRichComparison.LessThanOrEqual => ordered <= 0,
                PythonRichComparison.GreaterThan => ordered > 0,
                PythonRichComparison.GreaterThanOrEqual => ordered >= 0,
                _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
            }
        );
    }

    private static PythonRichComparison Reverse(PythonRichComparison comparison) =>
        comparison switch
        {
            PythonRichComparison.LessThan => PythonRichComparison.GreaterThan,
            PythonRichComparison.LessThanOrEqual => PythonRichComparison.GreaterThanOrEqual,
            PythonRichComparison.Equal => PythonRichComparison.Equal,
            PythonRichComparison.NotEqual => PythonRichComparison.NotEqual,
            PythonRichComparison.GreaterThan => PythonRichComparison.LessThan,
            PythonRichComparison.GreaterThanOrEqual => PythonRichComparison.LessThanOrEqual,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
        };

    internal static int GetPythonHash(PythonValue value, TextSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            PythonNoneValue => 0x1462_0a3,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonWholeNumberValue whole => whole.Value.GetHashCode(),
            PythonFloatingPointValue floatingPoint => GetFloatingPointHash(floatingPoint.Value),
            PythonComplexValue complex => GetComplexHash(complex.Value),
            PythonTextValue text => StringComparer.Ordinal.GetHashCode(text.Value),
            PythonByteSequenceValue bytes => GetByteHash(bytes.Value),
            PythonTupleValue tuple => GetTupleHash(tuple, span),
            PythonManagedObjectValue instance => RuntimeHelpers.GetHashCode(instance),
            PythonExternalObjectValue external => RuntimeHelpers.GetHashCode(external),
            PythonManagedTypeValue type => RuntimeHelpers.GetHashCode(type),
            PythonBuiltinFunctionValue function => RuntimeHelpers.GetHashCode(function),
            PythonProtocolFunctionValue function => RuntimeHelpers.GetHashCode(function),
            PythonBoundMethodValue method => HashCode.Combine(
                RuntimeHelpers.GetHashCode(method.Target),
                RuntimeHelpers.GetHashCode(method.Function)
            ),
            PythonModuleValue module => RuntimeHelpers.GetHashCode(module),
            PythonListValue or PythonDictionaryValue => throw Fault(
                "DPY4014",
                "This managed value is not hashable.",
                span,
                "TypeError"
            ),
            _ => RuntimeHelpers.GetHashCode(value),
        };
    }

    internal static byte[] GetBytes(PythonByteSequenceValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return (byte[])value.Value.Clone();
    }

    internal static byte[] GetUtf8(PythonTextValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetBytes(value.Value);
    }

    internal static string GetTypeName(PythonValue value) =>
        value switch
        {
            PythonNoneValue => "NoneType",
            PythonTruthValue => "bool",
            PythonWholeNumberValue => "int",
            PythonFloatingPointValue => "float",
            PythonComplexValue => "complex",
            PythonTextValue => "str",
            PythonByteSequenceValue => "bytes",
            PythonListValue => "list",
            PythonTupleValue => "tuple",
            PythonDictionaryValue => "dict",
            PythonSliceValue => "slice",
            PythonDictionaryViewValue view => view.Kind,
            PythonIteratorValue => "iterator",
            PythonModuleValue => "module",
            PythonManagedTypeValue => "type",
            PythonManagedObjectValue instance => instance.Type.Name,
            PythonExternalObjectValue => "object",
            PythonBuiltinFunctionValue or PythonProtocolFunctionValue =>
                "builtin_function_or_method",
            PythonBoundMethodValue => "method",
            PythonExceptionTypeValue => "type",
            PythonExceptionValue exception => exception.TypeName,
            PythonFunctionValue => "function",
            _ => "object",
        };

    internal static PythonListValue CreateList(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return new PythonListValue(
            Enumerable.Repeat<PythonValue>(PythonNoneValue.Instance, size).ToList()
        );
    }

    internal static PythonTupleValue CreateTuple(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return new PythonTupleValue(
            Enumerable.Repeat<PythonValue>(PythonNoneValue.Instance, size).ToArray()
        );
    }

    internal static PythonDictionaryValue CreateDictionary() => new([]);

    internal static PythonModuleValue CreateModule(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PythonModuleValue(name, new PythonGlobalNamespace());
    }

    private static PythonValue BindDescriptor(
        PythonValue value,
        PythonManagedObjectValue instance
    ) =>
        value switch
        {
            PythonDescriptorValue descriptor => descriptor.Get(instance),
            PythonProtocolFunctionValue function => new PythonBoundMethodValue(
                function.Name,
                instance,
                function
            ),
            _ => value,
        };

    private static bool TryGetTypeAttribute(
        PythonManagedTypeValue type,
        string name,
        out PythonValue value
    )
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Attributes.TryGetValue(name, out value!))
            {
                return true;
            }
        }

        value = null!;
        return false;
    }

    internal static int GetSequenceIndex(PythonValue index, int count, TextSpan span)
    {
        var promoted = PromoteTruthValue(index);
        if (promoted is not PythonWholeNumberValue wholeNumber)
        {
            throw Fault("DPY4011", "Sequence indices must be integers.", span, "TypeError");
        }

        var value = wholeNumber.Value;
        if (value < 0)
        {
            value += count;
        }

        if (value < 0 || value >= count)
        {
            throw Fault("DPY4012", "The sequence index is out of range.", span, "IndexError");
        }

        return (int)value;
    }

    internal static void SetDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        PythonValue value,
        TextSpan span
    )
    {
        if (!IsHashable(key))
        {
            throw Fault("DPY4014", "The dictionary key is not hashable.", span, "TypeError");
        }

        if (TryFindDictionaryItem(dictionary, key, out var item))
        {
            item.Value = value;
            return;
        }

        dictionary.Items.Add(new PythonDictionaryItemValue(key, value));
        dictionary.SizeVersion++;
    }

    internal static bool TryFindDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        out PythonDictionaryItemValue item
    )
    {
        foreach (var candidate in dictionary.Items)
        {
            if (ReferenceEquals(candidate.Key, key) || AreEqual(candidate.Key, key))
            {
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    internal static bool IsHashable(PythonValue value) =>
        value switch
        {
            PythonListValue or PythonDictionaryValue => false,
            PythonTupleValue tuple => tuple.Elements.All(IsHashable),
            _ => true,
        };

    internal static bool AreEqual(PythonValue left, PythonValue right)
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                return ToComplex(left) == ToComplex(right);
            }

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                return ToDouble(left) == ToDouble(right);
            }

            return ((PythonWholeNumberValue)left).Value == ((PythonWholeNumberValue)right).Value;
        }

        return (left, right) switch
        {
            (PythonNoneValue, PythonNoneValue) => true,
            (PythonTextValue leftText, PythonTextValue rightText) => string.Equals(
                leftText.Value,
                rightText.Value,
                StringComparison.Ordinal
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceEqual(rightBytes.Value),
            (PythonListValue leftList, PythonListValue rightList) => AreSequencesEqual(
                leftList.Elements,
                rightList.Elements
            ),
            (PythonTupleValue leftTuple, PythonTupleValue rightTuple) => AreSequencesEqual(
                leftTuple.Elements,
                rightTuple.Elements
            ),
            (PythonDictionaryValue leftDictionary, PythonDictionaryValue rightDictionary) =>
                AreDictionariesEqual(leftDictionary, rightDictionary),
            _ => ReferenceEquals(left, right),
        };
    }

    private static bool AreSequencesEqual(
        IReadOnlyList<PythonValue> left,
        IReadOnlyList<PythonValue> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!AreEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreDictionariesEqual(
        PythonDictionaryValue left,
        PythonDictionaryValue right
    )
    {
        if (left.Items.Count != right.Items.Count)
        {
            return false;
        }

        foreach (var item in left.Items)
        {
            if (
                !TryFindDictionaryItem(right, item.Key, out var rightItem)
                || !AreEqual(item.Value, rightItem.Value)
            )
            {
                return false;
            }
        }

        return true;
    }

    internal static int CompareOrdered(PythonValue left, PythonValue right, TextSpan span)
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                throw Fault("DPY4005", "Complex numbers cannot be ordered.", span, "TypeError");
            }

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                var leftValue = ToDouble(left);
                var rightValue = ToDouble(right);
                return leftValue.CompareTo(rightValue);
            }

            return ((PythonWholeNumberValue)left).Value.CompareTo(
                ((PythonWholeNumberValue)right).Value
            );
        }

        return (left, right) switch
        {
            (PythonTextValue leftText, PythonTextValue rightText) => string.CompareOrdinal(
                leftText.Value,
                rightText.Value
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceCompareTo(rightBytes.Value),
            _ => throw Fault(
                "DPY4005",
                "Values of these types cannot be ordered.",
                span,
                "TypeError"
            ),
        };
    }

    private static int GetByteHash(byte[] bytes)
    {
        var hash = new HashCode();
        foreach (var value in bytes)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static int GetFloatingPointHash(double value) =>
        double.IsFinite(value) && double.IsInteger(value)
            ? new BigInteger(value).GetHashCode()
            : value.GetHashCode();

    private static int GetComplexHash(Complex value) =>
        value.Imaginary == 0 ? GetFloatingPointHash(value.Real) : value.GetHashCode();

    private static bool HasUnorderedFloatingPointOperand(PythonValue left, PythonValue right) =>
        left is PythonFloatingPointValue { Value: var leftValue } && double.IsNaN(leftValue)
        || right is PythonFloatingPointValue { Value: var rightValue } && double.IsNaN(rightValue);

    private static int GetTupleHash(PythonTupleValue tuple, TextSpan span)
    {
        var hash = new HashCode();
        foreach (var value in tuple.Elements)
        {
            hash.Add(GetPythonHash(value, span));
        }

        return hash.ToHashCode();
    }

    private static bool IsNumeric(PythonValue value) =>
        value is PythonWholeNumberValue or PythonFloatingPointValue or PythonComplexValue;

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : value;

    private static double ToDouble(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static Complex ToComplex(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => new Complex((double)whole.Value, 0),
            PythonFloatingPointValue floatingPoint => new Complex(floatingPoint.Value, 0),
            PythonComplexValue complex => complex.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static PythonRuntimeException MissingAttribute(
        string typeName,
        string name,
        TextSpan span
    ) => Fault("DPY4022", $"'{typeName}' has no attribute '{name}'.", span, "AttributeError");

    internal static PythonRuntimeException Fault(
        string code,
        string message,
        TextSpan span,
        string exceptionType
    ) => new(code, message, span, exceptionType);
}
