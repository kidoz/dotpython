// The `collections.Counter` surface follows CPython 3.14.7 Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.Counter</c> type: a dict that counts what it is given, answers a
/// missing key with zero, and combines counts through its own arithmetic.
/// </summary>
/// <remarks>
/// CPython writes this class in Python, so it stays a heap type here: refusals name it
/// <c>Counter</c>, its methods report as functions, and its instances carry a
/// <c>__dict__</c> beside the storage. What the Python source spells as loops and helper
/// calls is spelled out in C# over the same storage a user subclass of <c>dict</c> uses.
/// </remarks>
internal static class PythonCounter
{
    private const string Owner = "Counter";

    /// <summary>The `collections.Counter` type object.</summary>
    internal static readonly PythonManagedTypeValue Type = CreateType();

    private static PythonManagedTypeValue CreateType()
    {
        var type = new PythonManagedTypeValue("Counter")
        {
            Module = "collections",
            QualName = "Counter",
            LayoutBase = PythonBuiltinTypes.Dict,
        };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinTypes.Dict]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, PythonBuiltinTypes.Dict, PythonBuiltinFunctions.Object])
        );
        type.Attributes["__module__"] = new PythonTextValue("collections");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Dict subclass for counting hashable items.  Sometimes called a bag\n"
                + "or multiset.  Elements are stored as dictionary keys and their counts\n"
                + "are stored as dictionary values.\n"
        );
        type.Attributes["__init__"] = Method(
            "__init__",
            (receiver, arguments) => Initialize(receiver, arguments, [], []),
            (receiver, arguments, names, values) => Initialize(receiver, arguments, names, values)
        );
        type.Attributes["__missing__"] = Method(
            "__missing__",
            (receiver, arguments) => Missing(receiver, arguments)
        );
        type.Attributes["__repr__"] = Method(
            "__repr__",
            (receiver, arguments) =>
                new PythonTextValue(Represent(RequireBare(receiver, arguments, "__repr__")))
        );
        foreach (var (name, subtract) in new[] { ("update", false), ("subtract", true) })
        {
            var negate = subtract;
            type.Attributes[name] = Method(
                name,
                (receiver, arguments) => Updated(receiver, arguments, [], [], negate),
                (receiver, arguments, names, values) =>
                    Updated(receiver, arguments, names, values, negate)
            );
        }
        foreach (
            var (name, body) in new (string, Func<PythonManagedObjectValue, PythonValue>)[]
            {
                ("elements", Elements),
                ("total", Total),
                ("copy", Copy),
                ("_keep_positive", KeepPositive),
                ("__pos__", Pos),
                ("__neg__", Neg),
            }
        )
        {
            type.Attributes[name] = Method(
                name,
                (receiver, arguments) => body(RequireBare(receiver, arguments, name))
            );
        }
        type.Attributes["most_common"] = Method(
            "most_common",
            (receiver, arguments) => MostCommon(receiver, arguments, [], []),
            (receiver, arguments, names, values) => MostCommon(receiver, arguments, names, values)
        );
        type.Attributes["__delitem__"] = Method(
            "__delitem__",
            (receiver, arguments) => DeleteItem(receiver, arguments)
        );
        type.Attributes["__reduce__"] = Method(
            "__reduce__",
            (receiver, arguments) => Reduce(RequireBare(receiver, arguments, "__reduce__"))
        );
        foreach (
            var (name, comparison) in new[]
            {
                ("__eq__", "eq"),
                ("__ne__", "ne"),
                ("__le__", "le"),
                ("__lt__", "lt"),
                ("__ge__", "ge"),
                ("__gt__", "gt"),
            }
        )
        {
            type.Attributes[name] = Method(
                name,
                (receiver, arguments) => CompareCounts(comparison, receiver, arguments)
            );
        }
        type.Attributes["fromkeys"] = new PythonClassMethodValue(Method("fromkeys", FromKeys));
        foreach (var op in new[] { "add", "sub", "and", "or" })
        {
            type.Attributes[$"__{op}__"] = Method(
                $"__{op}__",
                (receiver, arguments) => Binary(op, receiver, arguments)
            );
            type.Attributes[$"__i{op}__"] = Method(
                $"__i{op}__",
                (receiver, arguments) => InPlace(op, receiver, arguments)
            );
        }
        return type;
    }

    /// <summary>
    /// A method of the class. `Counter` is a class written in Python, so its methods report
    /// as functions, and the ones whose Python signature declares parameters accept the
    /// keyword calls that signature allows.
    /// </summary>
    private static PythonProtocolFunctionValue Method(
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null
    ) => new(name, body, keywords) { DeclaringType = Owner, IsPythonMethod = true };

    // -------------------------------------------------------------------------
    // Construction and updating
    // -------------------------------------------------------------------------

    /// <summary>
    /// `Counter(iterable=None, /, **kwds)`: an iterable's elements are counted, a mapping's
    /// values become counts, and the keywords are counted like a mapping's items.
    /// </summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (instance, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count > 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "Counter.__init__() takes from 1 to 2 positional arguments but "
                    + $"{arguments.Count + 1} were given",
                default,
                "TypeError"
            );
        if (arguments.Count == 1 && arguments[0] is not PythonNoneValue)
            Count(instance, arguments[0], subtract: false);
        CountKeywords(instance, keywordNames, keywordValues, subtract: false);
        return PythonNoneValue.Instance;
    }

    /// <summary>`update` and `subtract`: count an iterable, or add and drop a mapping's counts.</summary>
    private static PythonNoneValue Updated(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        bool subtract
    )
    {
        var (instance, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count > 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Counter.{(subtract ? "subtract" : "update")}() takes from 1 to 2 positional "
                    + $"arguments but {arguments.Count + 1} were given",
                default,
                "TypeError"
            );
        if (arguments.Count == 1 && arguments[0] is not PythonNoneValue)
            Count(instance, arguments[0], subtract);
        CountKeywords(instance, keywordNames, keywordValues, subtract);
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// Counting one source into a counter: a mapping's values are its counts, added through
    /// `self.get(key, 0)`; anything else is iterated and every element counted once, through
    /// `self[element]`, so a subclass's `__missing__` sees it.
    /// </summary>
    private static void Count(PythonValue instance, PythonValue source, bool subtract)
    {
        if (IsMapping(source))
        {
            var storage = MappingStorage(source);
            foreach (var item in new List<PythonDictionaryItemValue>(storage.Items))
            {
                // The mapping path of an empty counter copies its values as they are, which
                // is how `Counter(a='x')` keeps a count that cannot be added to zero.
                if (!subtract && PythonDictLikeTypes.Storage(instance).Items.Count == 0)
                {
                    ManagedObjectProtocols.SetItem(instance, item.Key, item.Value, default);
                    continue;
                }
                var count = ExistingCount(instance, item.Key);
                ManagedObjectProtocols.SetItem(
                    instance,
                    item.Key,
                    Combine(
                        subtract ? PythonOpCode.BinarySubtract : PythonOpCode.BinaryAdd,
                        count,
                        item.Value
                    ),
                    default
                );
            }
            return;
        }
        var iterator = ManagedObjectProtocols.GetIterator(source);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, default))
        {
            var count = ExistingCount(instance, element);
            ManagedObjectProtocols.SetItem(
                instance,
                element,
                Combine(
                    subtract ? PythonOpCode.BinarySubtract : PythonOpCode.BinaryAdd,
                    count,
                    PythonWholeNumberValue.Create(1)
                ),
                default
            );
        }
    }

    private static void CountKeywords(
        PythonValue instance,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        bool subtract
    )
    {
        if (keywordNames.Count == 0)
            return;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var key = new PythonTextValue(keywordNames[index]);
            if (!subtract && PythonDictLikeTypes.Storage(instance).Items.Count == 0)
            {
                ManagedObjectProtocols.SetItem(instance, key, keywordValues[index], default);
                continue;
            }
            ManagedObjectProtocols.SetItem(
                instance,
                key,
                Combine(
                    subtract ? PythonOpCode.BinarySubtract : PythonOpCode.BinaryAdd,
                    ExistingCount(instance, key),
                    keywordValues[index]
                ),
                default
            );
        }
    }

    /// <summary>Whether a source contributes counts rather than being counted as elements.</summary>
    private static bool IsMapping(PythonValue value) =>
        value
            is PythonDictionaryValue
                or PythonMappingProxyValue
                or PythonManagedObjectValue { Payload: PythonDictionaryValue };

    /// <summary>`self.get(key, 0)`, which reads the storage without a `__missing__` hook.</summary>
    private static PythonValue ExistingCount(PythonValue instance, PythonValue key) =>
        ManagedObjectProtocols.TryFindDictionaryItem(
            PythonDictLikeTypes.Storage(instance),
            key,
            out var item
        )
            ? item.Value
            : PythonWholeNumberValue.Create(0);

    /// <summary>The dictionary a mapping's counts are read from.</summary>
    private static PythonDictionaryValue MappingStorage(PythonValue mapping) =>
        mapping switch
        {
            PythonDictionaryValue dictionary => dictionary,
            PythonMappingProxyValue { Mapping: PythonDictionaryValue inner } => inner,
            _ => PythonDictLikeTypes.Storage(mapping),
        };

    // -------------------------------------------------------------------------
    // Reading
    // -------------------------------------------------------------------------

    /// <summary>`Counter.__missing__(key)`: a key the counter does not hold counts zero.</summary>
    private static PythonWholeNumberValue Missing(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given
    )
    {
        var (_, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                arguments.Count == 0
                    ? "Counter.__missing__() missing 1 required positional argument: 'key'"
                    : "Counter.__missing__() takes 2 positional arguments but "
                        + $"{arguments.Count + 1} were given",
                default,
                "TypeError"
            );
        return PythonWholeNumberValue.Create(0);
    }

    /// <summary>
    /// `elements()`: every element repeated by its count, skipping counts that are not
    /// positive — the `itertools.chain` CPython builds out of `repeat`.
    /// </summary>
    private static PythonValue Elements(PythonManagedObjectValue instance)
    {
        var repeats = new List<PythonValue>();
        foreach (var item in PythonDictLikeTypes.Storage(instance).Items)
        {
            var count = AsWhole(item.Value);
            if (count is null or <= 0)
                continue;
            var repeated = new List<PythonValue>(count.Value);
            for (var index = 0; index < count.Value; index++)
                repeated.Add(item.Key);
            repeats.Add(new PythonListValue(repeated));
        }
        // `chain(...)` runs through the same call path user code takes, which is what gives
        // the type its `__new__`.
        return UserObjectProtocols.Dispatcher is { } dispatcher
            ? dispatcher.Invoke(PythonItertools.Chain, [.. repeats], default)
            : new PythonListValue(repeats);
    }

    /// <summary>
    /// `most_common(n=None)`: the items by count, highest first and ties in insertion order.
    /// With an `n` it follows `heapq.nlargest(n, self.items(), key=itemgetter(1))`, the call
    /// CPython makes, so a `n` that call refuses is refused the same way here.
    /// </summary>
    private static PythonListValue MostCommon(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (instance, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count + keywordNames.Count > 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "Counter.most_common() takes from 1 to 2 positional arguments but "
                    + $"{arguments.Count + keywordNames.Count + 1} were given",
                default,
                "TypeError"
            );
        var items = new List<PythonDictionaryItemValue>(
            PythonDictLikeTypes.Storage(instance).Items
        );
        if (arguments.Count == 0 && keywordNames.Count == 0)
            return Descending(items, limit: null);
        if (keywordNames.Count > 0 && keywordNames[0] != "n")
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Counter.most_common() got an unexpected keyword argument '{keywordNames[0]}'",
                default,
                "TypeError"
            );
        var n = arguments.Count > 0 ? arguments[0] : keywordValues[0];
        if (n is PythonNoneValue)
            return Descending(items, limit: null);
        if (ManagedObjectProtocols.AreEqual(n, PythonWholeNumberValue.Create(1)))
            return new PythonListValue([MostFrequent(items)]);
        if (
            ManagedObjectProtocols.CompareOrdered(
                n,
                PythonWholeNumberValue.Create(items.Count),
                default,
                ">="
            ) >= 0
        )
            return Descending(items, SliceLength(n));
        // The shorter lengths go through `nlargest`'s heap, which starts by treating `n` as
        // the bound of a `range`.
        _ = RequireIndex(n);
        return Descending(items, Math.Max(RequireIndex(n), 0));
    }

    /// <summary>
    /// The items by count, highest first, with equal counts in insertion order — the order
    /// `sorted(..., reverse=True)` and the heap selection both hand out.
    /// </summary>
    private static PythonListValue Descending(List<PythonDictionaryItemValue> items, int? limit)
    {
        // `sorted(..., reverse=True)` and the heap selection both answer with the largest
        // count first and equal counts in insertion order, which is what reversing the input,
        // sorting it ascending and reading it back to front gives.
        var values = new PythonValue[items.Count];
        var keys = new PythonValue[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            values[index] = PythonWholeNumberValue.Create(items.Count - 1 - index);
            keys[index] = items[items.Count - 1 - index].Value;
        }
        PythonStableSort.Sort(values, keys, default);
        var result = new List<PythonValue>();
        for (var position = values.Length - 1; position >= 0; position--)
        {
            if (limit is { } maximum && result.Count >= maximum)
                break;
            var item = items[(int)((PythonWholeNumberValue)values[position]).Value];
            result.Add(new PythonTupleValue([item.Key, item.Value]));
        }
        return new PythonListValue(result);
    }

    /// <summary>The largest item, which is what `nlargest(1, ...)` answers with.</summary>
    private static PythonTupleValue MostFrequent(List<PythonDictionaryItemValue> items)
    {
        if (items.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "max() arg is an empty sequence",
                default,
                "ValueError"
            );
        var best = items[0];
        foreach (var item in items)
        {
            if (ManagedObjectProtocols.CompareOrdered(item.Value, best.Value, default) > 0)
                best = item;
        }
        return new PythonTupleValue([best.Key, best.Value]);
    }

    /// <summary>`total()`: the counts summed, which the empty counter answers with zero.</summary>
    private static PythonValue Total(PythonManagedObjectValue instance)
    {
        PythonValue total = PythonWholeNumberValue.Create(0);
        foreach (var item in PythonDictLikeTypes.Storage(instance).Items)
            total = Combine(PythonOpCode.BinaryAdd, total, item.Value);
        return total;
    }

    private static PythonManagedObjectValue Copy(PythonManagedObjectValue instance)
    {
        // `self.__class__(self)`, so a subclass copies as itself, out of the counts it holds.
        var result = CreateCounter(instance.Type);
        Count(result, instance, subtract: false);
        return result;
    }

    // -------------------------------------------------------------------------
    // Arithmetic
    // -------------------------------------------------------------------------

    /// <summary>
    /// `+`, `-`, `&` and `|`: every count the left counter keeps, then what only the right
    /// holds. The result is a plain `Counter` even for a subclass, and a non-counter operand
    /// answers NotImplemented.
    /// </summary>
    private static PythonValue Binary(
        string op,
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given
    )
    {
        var (instance, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__{op}__() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        var other = arguments[0];
        if (!IsCounter(other))
            return PythonNotImplementedValue.Instance;
        var result = CreateCounter(Type);
        var leftStorage = PythonDictLikeTypes.Storage(instance);
        var rightStorage = PythonDictLikeTypes.Storage(other);
        foreach (var item in leftStorage.Items)
        {
            var count = item.Value;
            if (op is "add" or "sub")
            {
                var otherCount = Lookup(rightStorage, item.Key);
                count = Combine(
                    op == "add" ? PythonOpCode.BinaryAdd : PythonOpCode.BinarySubtract,
                    count,
                    otherCount
                );
            }
            else
            {
                count = CombineCounts(op, count, Lookup(rightStorage, item.Key));
            }
            if (Positive(count))
                ManagedObjectProtocols.SetItem(result, item.Key, count, default);
        }
        if (op is "add" or "sub" or "or")
        {
            foreach (var item in rightStorage.Items)
            {
                if (ManagedObjectProtocols.TryFindDictionaryItem(leftStorage, item.Key, out _))
                    continue;
                var count =
                    op == "sub"
                        ? Combine(
                            PythonOpCode.BinarySubtract,
                            PythonWholeNumberValue.Create(0),
                            item.Value
                        )
                        : item.Value;
                if (Positive(count))
                    ManagedObjectProtocols.SetItem(result, item.Key, count, default);
            }
        }
        return result;
    }

    /// <summary>
    /// The in-place forms. `+=` and `-=` walk the other mapping's items, `&=` reads
    /// `other[key]` — which a `Counter` answers with zero and a plain dict refuses — and
    /// `|=` keeps whichever count is larger; all of them answer with the receiver.
    /// </summary>
    private static PythonValue InPlace(
        string op,
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given
    )
    {
        var (instance, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__i{op}__() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        var other = arguments[0];
        switch (op)
        {
            case "and":
                foreach (
                    var item in new List<PythonDictionaryItemValue>(
                        PythonDictLikeTypes.Storage(instance).Items
                    )
                )
                {
                    var otherCount = ManagedObjectProtocols.GetItem(other, item.Key, default);
                    if (ManagedObjectProtocols.CompareOrdered(otherCount, item.Value, default) < 0)
                        ManagedObjectProtocols.SetItem(instance, item.Key, otherCount, default);
                }
                return KeepPositive(instance);
            case "or":
                foreach (var item in OtherItems(other))
                {
                    if (
                        ManagedObjectProtocols.CompareOrdered(
                            item.Value,
                            ManagedObjectProtocols.GetItem(instance, item.Key, default),
                            default
                        ) > 0
                    )
                        ManagedObjectProtocols.SetItem(instance, item.Key, item.Value, default);
                }
                return KeepPositive(instance);
            default:
                foreach (var item in OtherItems(other))
                {
                    var count = ExistingCount(instance, item.Key);
                    ManagedObjectProtocols.SetItem(
                        instance,
                        item.Key,
                        Combine(
                            op == "add" ? PythonOpCode.BinaryAdd : PythonOpCode.BinarySubtract,
                            count,
                            item.Value
                        ),
                        default
                    );
                }
                return KeepPositive(instance);
        }
    }

    /// <summary>`other.items()`, which any mapping answers and nothing else does.</summary>
    private static List<PythonDictionaryItemValue> OtherItems(PythonValue other) =>
        IsMapping(other)
            ? [.. MappingStorage(other).Items]
            : throw ManagedObjectProtocols.Fault(
                "DPY4002",
                $"'{ManagedObjectProtocols.GetTypeName(other)}' object has no attribute 'items'",
                default,
                "AttributeError"
            );

    /// <summary>
    /// `&` and `|` pick the smaller and larger count. `&` only keeps counts that are
    /// positive on both sides, so a count the right counter lacks zeroes it out.
    /// </summary>
    private static PythonValue CombineCounts(string op, PythonValue left, PythonValue right)
    {
        if (op == "and")
        {
            if (!Positive(left) || !Positive(right))
                return PythonWholeNumberValue.Create(0);
            return ManagedObjectProtocols.CompareOrdered(left, right, default) <= 0 ? left : right;
        }
        if (!Positive(left) && !Positive(right))
            return PythonWholeNumberValue.Create(0);
        return ManagedObjectProtocols.CompareOrdered(left, right, default) >= 0 ? left : right;
    }

    /// <summary>`_keep_positive`: every count that is not positive is dropped.</summary>
    private static PythonValue KeepPositive(PythonManagedObjectValue instance)
    {
        var instanceValue = (PythonValue)instance;
        var dropped = new List<PythonValue>();
        foreach (var item in PythonDictLikeTypes.Storage(instance).Items)
        {
            if (!Positive(item.Value))
                dropped.Add(item.Key);
        }
        foreach (var key in dropped)
            ManagedObjectProtocols.DeleteItem(instanceValue, key, default);
        return instanceValue;
    }

    private static PythonManagedObjectValue Pos(PythonManagedObjectValue instance)
    {
        var result = CreateCounter(Type);
        foreach (var item in PythonDictLikeTypes.Storage(instance).Items)
        {
            if (Positive(item.Value))
                ManagedObjectProtocols.SetItem(result, item.Key, item.Value, default);
        }
        return result;
    }

    private static PythonManagedObjectValue Neg(PythonManagedObjectValue instance)
    {
        var result = CreateCounter(Type);
        foreach (var item in PythonDictLikeTypes.Storage(instance).Items)
        {
            if (
                ManagedObjectProtocols.CompareOrdered(
                    item.Value,
                    PythonWholeNumberValue.Create(0),
                    default
                ) < 0
            )
                ManagedObjectProtocols.SetItem(
                    result,
                    item.Key,
                    Combine(
                        PythonOpCode.BinarySubtract,
                        PythonWholeNumberValue.Create(0),
                        item.Value
                    ),
                    default
                );
        }
        return result;
    }

    /// <summary>Whether a count survives: positive for `+`, `-` and `&`/`|`'s picks.</summary>
    private static bool Positive(PythonValue count) =>
        ManagedObjectProtocols.CompareOrdered(count, PythonWholeNumberValue.Create(0), default) > 0;

    private static PythonValue Lookup(PythonDictionaryValue storage, PythonValue key) =>
        ManagedObjectProtocols.TryFindDictionaryItem(storage, key, out var item)
            ? item.Value
            : PythonWholeNumberValue.Create(0);

    private static PythonValue Combine(PythonOpCode opCode, PythonValue left, PythonValue right) =>
        PythonVirtualMachine.ApplyBinaryOperator(opCode, left, right, default);

    /// <summary>
    /// The rich comparisons: counts a counter does not hold are zero, so `Counter(a=1)`
    /// equals `Counter(a=1, b=0)`, and the orderings read as subset and superset tests over
    /// the counts. Anything that is not a counter answers NotImplemented, which is how
    /// `==` falls through to `dict`'s own comparison.
    /// </summary>
    private static PythonValue CompareCounts(
        string comparison,
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given
    )
    {
        var (instance, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__{comparison}__() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        var other = arguments[0];
        if (!IsCounter(other))
            return PythonNotImplementedValue.Instance;
        var agrees = comparison switch
        {
            "eq" or "ne" => AllCounts(instance, other, "=="),
            "le" or "lt" => AllCounts(instance, other, "<="),
            _ => AllCounts(instance, other, ">="),
        };
        return comparison switch
        {
            "eq" or "le" or "ge" => PythonTruthValue.FromBoolean(agrees),
            "ne" => PythonTruthValue.FromBoolean(!agrees),
            "lt" => PythonTruthValue.FromBoolean(agrees && !AllCounts(instance, other, "==")),
            _ => PythonTruthValue.FromBoolean(agrees && !AllCounts(instance, other, "==")),
        };
    }

    private static bool AllCounts(PythonValue left, PythonValue right, string symbol)
    {
        var leftStorage = PythonDictLikeTypes.Storage(left);
        var rightStorage = PythonDictLikeTypes.Storage(right);
        foreach (var holder in new[] { leftStorage, rightStorage })
        {
            foreach (var item in holder.Items)
            {
                var leftCount = Lookup(leftStorage, item.Key);
                var rightCount = Lookup(rightStorage, item.Key);
                var comparison = symbol switch
                {
                    "==" => ManagedObjectProtocols.AreEqual(leftCount, rightCount),
                    "<=" => ManagedObjectProtocols.CompareOrdered(
                        leftCount,
                        rightCount,
                        default,
                        "<="
                    ) <= 0,
                    _ => ManagedObjectProtocols.CompareOrdered(leftCount, rightCount, default, ">=")
                        >= 0,
                };
                if (!comparison)
                    return false;
            }
        }
        return true;
    }

    /// <summary>`Counter.__delitem__`: deleting a missing key is not an error here.</summary>
    private static PythonNoneValue DeleteItem(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given
    )
    {
        var (instance, arguments) = PythonDictLikeTypes.Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__delitem__() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        // `if elem in self: super().__delitem__(elem)`: a key the counter does not hold is
        // not an error, and the deletion goes to the storage rather than back through here.
        var storage = PythonDictLikeTypes.Storage(instance);
        if (ManagedObjectProtocols.TryFindDictionaryItem(storage, arguments[0], out _))
            ManagedObjectProtocols.DeleteItem(storage, arguments[0], default);
        return PythonNoneValue.Instance;
    }

    /// <summary>`__reduce__`: the class and the plain dict of counts it is rebuilt from.</summary>
    private static PythonTupleValue Reduce(PythonManagedObjectValue instance)
    {
        var counts = new PythonDictionaryValue([]);
        PythonDictLikeTypes.Merge(counts, PythonDictLikeTypes.Storage(instance));
        return new PythonTupleValue([instance.Type, new PythonTupleValue([counts])]);
    }

    /// <summary>`isinstance(value, Counter)`, which its subclasses satisfy as well.</summary>
    private static bool IsCounter(PythonValue value) =>
        PythonBuiltinTypes
            .GetMro(PythonBuiltinTypes.GetRuntimeType(value))
            .Elements.Any(entry => ReferenceEquals(entry, Type));

    private static PythonManagedObjectValue CreateCounter(PythonManagedTypeValue type) =>
        PythonDictLikeTypes.Allocate(type);

    // -------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// `Counter.__repr__`: the counts from the most common down, which CPython builds by
    /// repr'ing `dict(self.most_common())`. Counts that cannot be ordered — `Counter(a='x')`
    /// — keep the insertion order they were stored in.
    /// </summary>
    private static string Represent(PythonManagedObjectValue instance)
    {
        var storage = PythonDictLikeTypes.Storage(instance);
        if (storage.Items.Count == 0)
            return $"{instance.Type.Name}()";
        PythonDictionaryValue ordered;
        try
        {
            ordered = new PythonDictionaryValue([]);
            foreach (var item in Descending([.. storage.Items], null).Elements)
            {
                var pair = (PythonTupleValue)item;
                ManagedObjectProtocols.SetDictionaryItem(
                    ordered,
                    pair.Elements[0],
                    pair.Elements[1],
                    default
                );
            }
        }
        catch (PythonRuntimeException error) when (error.PythonExceptionTypeName == "TypeError")
        {
            ordered = storage;
        }
        return $"{instance.Type.Name}({ordered.ToRepresentationString()})";
    }

    /// <summary>
    /// `fromkeys` is refused outright: CPython's `Counter.fromkeys()` is undefined because
    /// a count of one and a count of none are both plausible readings.
    /// </summary>
    private static PythonValue FromKeys(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    ) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "Counter.fromkeys() is undefined.  Use Counter(iterable) instead.",
            default,
            "NotImplementedError"
        );

    /// <summary>A method taking no arguments, with CPython's wording for the arity error.</summary>
    private static PythonManagedObjectValue RequireBare(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        string name
    )
    {
        var (instance, rest) = PythonDictLikeTypes.Bound(receiver, arguments);
        if (rest.Count != 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Counter.{name}() takes 1 positional argument but {rest.Count + 1} were given",
                default,
                "TypeError"
            );
        return instance;
    }

    /// <summary>A count as a repetition count, when it is one this runtime can repeat.</summary>
    private static int? AsWhole(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole
                when whole.Value >= int.MinValue && whole.Value <= int.MaxValue => (int)whole.Value,
            PythonWholeNumberValue => 0,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => null,
        };

    /// <summary>`sorted(...)[:n]`, whose slice needs an integer — an integral float works.</summary>
    private static int SliceLength(PythonValue n)
    {
        var value = Index(n, "slice indices must be integers or None or have an __index__ method");
        return value > int.MaxValue ? int.MaxValue
            : value < BigInteger.Zero ? 0
            : (int)value;
    }

    /// <summary>The `n` of `range(0, -n, -1)`, which refuses anything without `__index__`.</summary>
    private static int RequireIndex(PythonValue n) =>
        Index(n, "'{0}' object cannot be interpreted as an integer") is var value
        && value <= int.MaxValue
            ? (int)value
            : 0;

    /// <summary>The integer a value stands for, reported in the wording the caller uses.</summary>
    private static BigInteger Index(PythonValue value, string wording)
    {
        if (value is PythonWholeNumberValue whole)
            return whole.Value;
        if (value is PythonTruthValue truth)
            return truth.Value ? BigInteger.One : BigInteger.Zero;
        if (
            value is PythonFloatingPointValue number
            && double.IsFinite(number.Value)
            && Math.Floor(number.Value) == number.Value
        )
            return new BigInteger(number.Value);
        if (UserObjectProtocols.TryConvertToIndex(value, default, out var index))
            return index;
        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            wording.Contains("{0}", StringComparison.Ordinal)
                ? string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    wording,
                    ManagedObjectProtocols.GetTypeName(value)
                )
                : wording,
            default,
            "TypeError"
        );
    }
}
