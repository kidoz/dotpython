// The `collections.OrderedDict` surface follows CPython 3.14.7 Modules/_collectionsmodule.c
// (ordereddict_*) and Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.OrderedDict</c> type: a dictionary whose order is part of its
/// interface, with moves, end-aware popping, reversal, and an equality that notices it.
/// </summary>
/// <remarks>
/// A static type in CPython, so refusals name it <c>collections.OrderedDict</c> and its
/// methods report as C methods. Its storage keeps the insertion order every dictionary of
/// this runtime keeps, so the type adds the order-aware surface rather than an order:
/// <c>popitem(last=)</c>, <c>move_to_end</c>, <c>reversed</c>, the order-sensitive
/// comparisons, the operators that keep the class, and the <c>odict_*</c> names its views
/// and iterators report.
/// </remarks>
internal static class PythonOrderedDict
{
    private const string Owner = "collections.OrderedDict";

    /// <summary>The `collections.OrderedDict` type object.</summary>
    internal static readonly PythonManagedTypeValue Type = CreateType();

    /// <summary>Whether a type is this one or descends from it.</summary>
    internal static bool IsOrderedDict(PythonManagedTypeValue type) =>
        type.Mro.Any(current => ReferenceEquals(current, Type));

    /// <summary>Whether a dictionary value is the storage of an ordered dictionary.</summary>
    internal static bool IsOrderedStorage(PythonDictionaryValue dictionary) =>
        dictionary.Owner is { } owner && IsOrderedDict(owner.Type);

    /// <summary>The name an iterator over this dictionary reports.</summary>
    internal static string IteratorName(PythonDictionaryValue dictionary) =>
        IsOrderedStorage(dictionary) ? "odict_iterator" : "dict_keyiterator";

    private static PythonManagedTypeValue CreateType()
    {
        var type = new PythonManagedTypeValue("OrderedDict")
        {
            Module = "collections",
            QualName = "OrderedDict",
            LayoutBase = PythonBuiltinTypes.Dict,
            ReportsQualifiedName = true,
        };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinTypes.Dict]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, PythonBuiltinTypes.Dict, PythonBuiltinFunctions.Object])
        );
        type.Attributes["__module__"] = new PythonTextValue("collections");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Dictionary that remembers insertion order"
        );
        type.Attributes["__init__"] = Method(
            "__init__",
            (receiver, arguments) => Initialize(receiver, arguments, [], []),
            (receiver, arguments, names, values) => Initialize(receiver, arguments, names, values),
            wrapper: true
        );
        type.Attributes["__repr__"] = Method(
            "__repr__",
            (receiver, arguments) =>
                new PythonTextValue(
                    PythonDictLikeTypes.Represent(
                        RequireBare(receiver, arguments, "__repr__"),
                        omitEmpty: true
                    )
                ),
            wrapper: true
        );
        type.Attributes["__eq__"] = Method(
            "__eq__",
            (receiver, arguments) => Compare(receiver, arguments, negate: false),
            wrapper: true
        );
        type.Attributes["__ne__"] = Method(
            "__ne__",
            (receiver, arguments) => Compare(receiver, arguments, negate: true),
            wrapper: true
        );
        type.Attributes["__or__"] = Method(
            "__or__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: false),
            wrapper: true
        );
        type.Attributes["__ror__"] = Method(
            "__ror__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: true),
            wrapper: true
        );
        type.Attributes["__ior__"] = Method(
            "__ior__",
            (receiver, arguments) => InPlaceMerge(receiver, arguments),
            wrapper: true
        );
        type.Attributes["popitem"] = Method(
            "popitem",
            (receiver, arguments) => PopItem(receiver, arguments, []),
            (receiver, arguments, names, values) =>
                PopItem(receiver, arguments, LastKeyword(names, values))
        );
        type.Attributes["move_to_end"] = Method(
            "move_to_end",
            (receiver, arguments) => MoveToEnd(receiver, arguments, [], []),
            (receiver, arguments, names, values) => MoveToEnd(receiver, arguments, names, values)
        );
        type.Attributes["__reversed__"] = Method(
            "__reversed__",
            (receiver, arguments) => Reverse(RequireBare(receiver, arguments, "__reversed__"))
        );
        type.Attributes["__reduce__"] = Method(
            "__reduce__",
            (receiver, arguments) => Reduce(RequireBare(receiver, arguments, "__reduce__"))
        );
        type.Attributes["copy"] = Method(
            "copy",
            (receiver, arguments) => Copy(RequireBare(receiver, arguments, "copy"))
        );
        foreach (
            var name in new[] { "update", "setdefault", "pop", "clear", "keys", "values", "items" }
        )
            type.Attributes[name] = Method(
                name,
                (receiver, arguments) => Defer(name, receiver, arguments, [], []),
                (receiver, arguments, names, values) =>
                    Defer(name, receiver, arguments, names, values)
            );
        return type;
    }

    /// <summary>A method of the static type, reported the way the C type reports it.</summary>
    private static PythonProtocolFunctionValue Method(
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null,
        bool wrapper = false
    ) => new(name, body, keywords) { DeclaringType = Owner, IsSlotWrapper = wrapper };

    private static (
        PythonManagedObjectValue Instance,
        IReadOnlyList<PythonValue> RestArguments
    ) Bound(PythonValue? receiver, IReadOnlyList<PythonValue> arguments) =>
        PythonDictLikeTypes.Bound(receiver, arguments);

    private static PythonManagedObjectValue RequireBare(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        string name
    )
    {
        var (instance, rest) = Bound(receiver, arguments);
        if (rest.Count != 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"OrderedDict.{name}() takes 1 positional argument but {rest.Count + 1} were given",
                default,
                "TypeError"
            );
        return instance;
    }

    /// <summary>
    /// `OrderedDict(...)`: the same arguments `dict` takes, which is how an ordered
    /// dictionary is built beside the one it is copied from.
    /// </summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (instance, arguments) = Bound(receiver, given);
        // The C type words its own refusals, without a class name in front.
        if (arguments.Count > 1)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"expected at most 1 argument, got {arguments.Count}",
                default,
                "TypeError"
            );
        }
        PythonDictLikeTypes.Fill(instance, arguments, keywordNames, keywordValues, default);
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// `__eq__`/`__ne__`: order matters against another ordered dictionary, and is ignored
    /// against anything else — `OrderedDict([('a', 1), ('b', 2)])` equals the reversed plain
    /// dict but not the reversed ordered one.
    /// </summary>
    private static PythonValue Compare(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        bool negate
    )
    {
        var (instance, arguments) = Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__{(negate ? "ne" : "eq")}__() takes exactly one argument "
                    + $"({arguments.Count} given)",
                default,
                "TypeError"
            );
        var other = arguments[0];
        bool equal;
        if (other is PythonManagedObjectValue otherInstance && IsOrderedDict(otherInstance.Type))
        {
            var left = PythonDictLikeTypes.Storage(instance).Items;
            var right = PythonDictLikeTypes.Storage(other).Items;
            equal = left.Count == right.Count;
            for (var index = 0; equal && index < left.Count; index++)
            {
                equal =
                    ManagedObjectProtocols.AreEqual(left[index].Key, right[index].Key)
                    && ManagedObjectProtocols.AreEqual(left[index].Value, right[index].Value);
            }
        }
        else if (
            other is PythonDictionaryValue
            || PythonSubclassStorage.StorageKindOf(other) == "dict"
        )
            equal = ManagedObjectProtocols.AreEqual(
                PythonDictLikeTypes.Storage(instance),
                PythonSubclassStorage.Resolve(other)
            );
        else
            return PythonNotImplementedValue.Instance;
        return PythonTruthValue.FromBoolean(negate ? !equal : equal);
    }

    /// <summary>
    /// `__or__`, `__ror__` and `__ior__`: the union keeps the class, so an ordered dictionary
    /// unioned with anything containing a mapping is still ordered.
    /// </summary>
    private static PythonValue Merge(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        bool reflected
    )
    {
        var (instance, arguments) = Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__{(reflected ? "ror" : "or")}__() takes exactly one argument "
                    + $"({arguments.Count} given)",
                default,
                "TypeError"
            );
        var other = arguments[0];
        if (!PythonDictLikeTypes.IsMapping(other))
            return PythonNotImplementedValue.Instance;
        var result = PythonDictLikeTypes.Allocate(instance.Type);
        var left = reflected ? other : instance;
        var right = reflected ? instance : other;
        PythonDictLikeTypes.Merge(
            PythonDictLikeTypes.Storage(result),
            PythonDictLikeTypes.MappingStorage(left)
        );
        PythonDictLikeTypes.Merge(
            PythonDictLikeTypes.Storage(result),
            PythonDictLikeTypes.MappingStorage(right)
        );
        return result;
    }

    private static PythonManagedObjectValue InPlaceMerge(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given
    )
    {
        var (instance, arguments) = Bound(receiver, given);
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__ior__() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        // `__ior__` is `self.update(other)`, which takes any mapping or iterable of pairs.
        PythonBuiltinMethods.MergeInto(
            PythonDictLikeTypes.Storage(instance),
            arguments[0],
            default
        );
        return instance;
    }

    /// <summary>
    /// `popitem(last=True)`: the last item, or the first when `last` is false, with
    /// CPython's own refusal for an empty dictionary.
    /// </summary>
    private static PythonTupleValue PopItem(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        List<PythonValue> lastKeyword
    )
    {
        var (instance, arguments) = Bound(receiver, given);
        if (arguments.Count > 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"popitem() takes at most 1 argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        if (arguments.Count == 1 && lastKeyword.Count == 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "popitem() got multiple values for argument 'last'",
                default,
                "TypeError"
            );
        var lastArgument =
            arguments.Count == 1 ? arguments[0]
            : lastKeyword.Count == 1 ? lastKeyword[0]
            : PythonNoneValue.Instance;
        var items = PythonDictLikeTypes.Storage(instance).Items;
        if (items.Count == 0)
            throw new PythonRaisedException(
                new PythonExceptionValue("KeyError", "'dictionary is empty'")
                {
                    Arguments = [new PythonTextValue("dictionary is empty")],
                }
            );
        var last = lastArgument is PythonNoneValue || ManagedObjectProtocols.IsTrue(lastArgument);
        var item = last ? items[^1] : items[0];
        ManagedObjectProtocols.DeleteItem(PythonDictLikeTypes.Storage(instance), item.Key, default);
        return new PythonTupleValue([item.Key, item.Value]);
    }

    /// <summary>`popitem`'s `last=` keyword, or nothing when it was not given.</summary>
    private static List<PythonValue> LastKeyword(
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index] != "last")
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"popitem() got an unexpected keyword argument '{names[index]}'",
                    default,
                    "TypeError"
                );
        }
        return names.Count == 1 ? [values[0]] : [];
    }

    /// <summary>
    /// A method's keyword arguments folded onto its positional form, with CPython's refusal
    /// for a name the C signature does not declare.
    /// </summary>
    private static List<PythonValue> Keyword(
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var keywordWhere = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index] is not ("last" or "key") || !keywordWhere.TryAdd(names[index], index))
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"got an unexpected keyword argument '{names[index]}'",
                    default,
                    "TypeError"
                );
            }
        }
        var result = new List<PythonValue>();
        if (keywordWhere.TryGetValue("key", out var keyIndex))
            result.Add(values[keyIndex]);
        else
            result.Add(PythonNoneValue.Instance);
        if (keywordWhere.TryGetValue("last", out var lastIndex))
            result.Add(values[lastIndex]);
        return result;
    }

    /// <summary>
    /// `move_to_end(key, last=True)`: the key is moved to the end, or to the front when
    /// `last` is false. A key the dictionary does not hold is a KeyError.
    /// </summary>
    private static PythonNoneValue MoveToEnd(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (instance, arguments) = Bound(receiver, given);
        if (arguments.Count + keywordNames.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "move_to_end() missing required argument 'key' (pos 1)",
                default,
                "TypeError"
            );
        if (arguments.Count + keywordNames.Count > 2)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"move_to_end() takes at most 2 arguments "
                    + $"({arguments.Count + keywordNames.Count} given)",
                default,
                "TypeError"
            );
        var key = arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance;
        var last = true;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            if (keywordNames[index] == "key")
            {
                if (arguments.Count > 0)
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        "move_to_end() got multiple values for argument 'key'",
                        default,
                        "TypeError"
                    );
                key = keywordValues[index];
            }
            else if (keywordNames[index] == "last")
                last = ManagedObjectProtocols.IsTrue(keywordValues[index]);
            else
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"move_to_end() got an unexpected keyword argument '{keywordNames[index]}'",
                    default,
                    "TypeError"
                );
        }
        if (arguments.Count > 1)
            last = ManagedObjectProtocols.IsTrue(arguments[1]);
        var storage = PythonDictLikeTypes.Storage(instance);
        if (!ManagedObjectProtocols.TryFindDictionaryItem(storage, key, out var item))
            throw ManagedObjectProtocols.MissingKey(key);
        // The entry keeps its slot and moves to either end of the order.
        if (last)
            storage.MoveItemToEnd(item);
        else
            storage.MoveItemToFront(item);
        return PythonNoneValue.Instance;
    }

    /// <summary>`reversed(od)`: the keys from the last to the first, as an odict iterator.</summary>
    private static PythonValue Reverse(PythonManagedObjectValue instance) =>
        PythonReverseIterators.Create(PythonDictLikeTypes.Storage(instance), default);

    /// <summary>
    /// `__reduce__`: the class and an iterator over its items, which is the shape
    /// `copy.copy` and `copy.deepcopy` rebuild it from.
    /// </summary>
    private static PythonTupleValue Reduce(PythonManagedObjectValue instance) =>
        new([
            instance.Type,
            new PythonTupleValue([]),
            PythonNoneValue.Instance,
            PythonNoneValue.Instance,
            ManagedObjectProtocols.GetIterator(
                new PythonDictionaryViewValue("dict_items", PythonDictLikeTypes.Storage(instance))
            ),
        ]);

    private static PythonManagedObjectValue Copy(PythonManagedObjectValue instance)
    {
        var copy = PythonDictLikeTypes.Allocate(instance.Type);
        PythonDictLikeTypes.Merge(
            PythonDictLikeTypes.Storage(copy),
            PythonDictLikeTypes.Storage(instance)
        );
        return copy;
    }

    /// <summary>
    /// The methods the C type re-implements but answers exactly as the dictionary does:
    /// `update`, `setdefault`, `pop`, `clear` and the views. They keep the C type's name in
    /// their reprs while the work stays the builtin's.
    /// </summary>
    private static PythonValue Defer(
        string name,
        PythonValue? receiver,
        IReadOnlyList<PythonValue> given,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (instance, arguments) = Bound(receiver, given);
        var storage = PythonDictLikeTypes.Storage(instance);
        if (!PythonBuiltinMethods.TryGet(storage, name, out var method))
            throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'{Owner}' object has no attribute '{name}'",
                default,
                "AttributeError"
            );
        if (keywordNames.Count == 0)
            return method.Invoke(storage, arguments);
        if (method.InvokeWithKeywords is not { } keywords)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"OrderedDict.{name}() takes no keyword arguments",
                default,
                "TypeError"
            );
        return keywords(storage, arguments, keywordNames, keywordValues);
    }
}
