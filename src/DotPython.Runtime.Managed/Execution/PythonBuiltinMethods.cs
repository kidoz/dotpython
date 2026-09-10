using System.Text;
using DotPython.Language.Text;

// CA1308 targets case-normalization before comparison; str.lower() and capitalize()
// produce lowercase text as their Python-visible result, not a comparison key.
#pragma warning disable CA1308

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Bound-method tables for the built-in str, list, dict, and tuple values.</summary>
internal static class PythonBuiltinMethods
{
    private static readonly Dictionary<string, PythonProtocolFunctionValue> TextMethods = new(
        StringComparer.Ordinal
    )
    {
        ["upper"] = Text("upper", 0, 0, (text, _) => new PythonTextValue(text.ToUpperInvariant())),
        ["lower"] = Text("lower", 0, 0, (text, _) => new PythonTextValue(text.ToLowerInvariant())),
        ["strip"] = Text(
            "strip",
            0,
            1,
            (text, arguments) =>
                new PythonTextValue(
                    arguments.Count == 0
                        ? text.Trim()
                        : text.Trim(RequireText("strip", arguments[0]).ToCharArray())
                )
        ),
        ["lstrip"] = Text(
            "lstrip",
            0,
            1,
            (text, arguments) =>
                new PythonTextValue(
                    arguments.Count == 0
                        ? text.TrimStart()
                        : text.TrimStart(RequireText("lstrip", arguments[0]).ToCharArray())
                )
        ),
        ["rstrip"] = Text(
            "rstrip",
            0,
            1,
            (text, arguments) =>
                new PythonTextValue(
                    arguments.Count == 0
                        ? text.TrimEnd()
                        : text.TrimEnd(RequireText("rstrip", arguments[0]).ToCharArray())
                )
        ),
        ["split"] = Text("split", 0, 2, SplitText)
            .WithSignature(["sep", "maxsplit"], [PythonNoneValue.Instance, null]),
        ["encode"] = Text(
                "encode",
                0,
                2,
                (text, arguments) =>
                    new PythonByteSequenceValue(
                        PythonTextCodecs.Encode(
                            text,
                            arguments.Count > 0 ? RequireText("encode", arguments[0]) : "utf-8",
                            arguments.Count > 1 ? RequireText("encode", arguments[1]) : "strict",
                            default
                        )
                    )
            )
            .WithSignature(["encoding", "errors"], [null, null]),
        ["join"] = Text("join", 1, 1, JoinText),
        ["format"] = Text(
            "format",
            0,
            int.MaxValue,
            (text, arguments) =>
                new PythonTextValue(
                    PythonTextFormatting.FormatTemplate(text, arguments, [], [], default)
                )
        ),
        ["format_map"] = Text(
            "format_map",
            1,
            1,
            (text, arguments) =>
            {
                if (arguments[0] is not PythonDictionaryValue mapping)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        "format_map() argument must be a mapping.",
                        default,
                        "TypeError"
                    );
                }

                var names = new List<string>();
                var values = new List<PythonValue>();
                foreach (var item in mapping.Items)
                {
                    if (item.Key is PythonTextValue keyText)
                    {
                        names.Add(keyText.Value);
                        values.Add(item.Value);
                    }
                }

                return new PythonTextValue(
                    PythonTextFormatting.FormatTemplate(text, [], names, values, default)
                );
            }
        ),
        ["replace"] = Text(
                "replace",
                2,
                3,
                (text, arguments) =>
                    new PythonTextValue(
                        ReplaceText(
                            text,
                            RequireText("replace", arguments[0]),
                            RequireText("replace", arguments[1]),
                            arguments.Count == 3 ? RequireCount("replace", arguments[2]) : -1
                        )
                    )
            )
            .WithSignature(["old", "new", "count"], [null, null, null], positionalOnly: 2),
        ["startswith"] = Text(
            "startswith",
            1,
            3,
            (text, arguments) => Truth(MatchesAffix("startswith", text, arguments, prefix: true))
        ),
        ["endswith"] = Text(
            "endswith",
            1,
            3,
            (text, arguments) => Truth(MatchesAffix("endswith", text, arguments, prefix: false))
        ),
        ["find"] = Text(
            "find",
            1,
            3,
            (text, arguments) => PythonWholeNumberValue.Create(FindInRange("find", text, arguments))
        ),
        ["index"] = Text(
            "index",
            1,
            3,
            (text, arguments) =>
            {
                var position = FindInRange("index", text, arguments);
                if (position < 0)
                {
                    throw Fault("substring not found", "ValueError");
                }

                return PythonWholeNumberValue.Create(position);
            }
        ),
        ["count"] = Text(
            "count",
            1,
            3,
            (text, arguments) =>
                PythonWholeNumberValue.Create(
                    CountText(
                        SliceRunes(text, arguments, 1, out _),
                        RequireText("count", arguments[0])
                    )
                )
        ),
        ["capitalize"] = Text(
            "capitalize",
            0,
            0,
            (text, _) => new PythonTextValue(Capitalize(text))
        ),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> ListMethods = new(
        StringComparer.Ordinal
    )
    {
        ["append"] = List(
            "append",
            1,
            1,
            (list, arguments) =>
            {
                list.Elements.Add(arguments[0]);
                return PythonNoneValue.Instance;
            }
        ),
        ["extend"] = List(
            "extend",
            1,
            1,
            (list, arguments) =>
            {
                ManagedObjectProtocols.ExtendList(list, arguments[0], default);
                return PythonNoneValue.Instance;
            }
        ),
        ["insert"] = List(
            "insert",
            2,
            2,
            (list, arguments) =>
            {
                var index = RequireInteger("insert", arguments[0]);
                if (index < 0)
                {
                    index += list.Elements.Count;
                }

                index = Math.Clamp(index, 0, list.Elements.Count);
                list.Elements.Insert(index, arguments[1]);
                return PythonNoneValue.Instance;
            }
        ),
        ["pop"] = List(
            "pop",
            0,
            1,
            (list, arguments) =>
            {
                if (list.Elements.Count == 0)
                {
                    throw Fault("Cannot pop from an empty list.", "IndexError");
                }

                var index =
                    arguments.Count == 0
                        ? list.Elements.Count - 1
                        : ManagedObjectProtocols.GetSequenceIndex(
                            arguments[0],
                            list.Elements.Count,
                            default
                        );
                var value = list.Elements[index];
                list.Elements.RemoveAt(index);
                return value;
            }
        ),
        ["remove"] = List(
            "remove",
            1,
            1,
            (list, arguments) =>
            {
                var index = FindElement(list.Elements, arguments[0]);
                if (index < 0)
                {
                    throw Fault("The value was not found in the list.", "ValueError");
                }

                list.Elements.RemoveAt(index);
                return PythonNoneValue.Instance;
            }
        ),
        ["clear"] = List(
            "clear",
            0,
            0,
            (list, _) =>
            {
                list.Elements.Clear();
                return PythonNoneValue.Instance;
            }
        ),
        ["index"] = List(
            "index",
            1,
            1,
            (list, arguments) =>
            {
                var index = FindElement(list.Elements, arguments[0]);
                if (index < 0)
                {
                    throw Fault("The value was not found in the list.", "ValueError");
                }

                return PythonWholeNumberValue.Create(index);
            }
        ),
        ["count"] = List(
            "count",
            1,
            1,
            (list, arguments) =>
                PythonWholeNumberValue.Create(
                    list.Elements.Count(element =>
                        ManagedObjectProtocols.AreEqual(element, arguments[0])
                    )
                )
        ),
        ["reverse"] = List(
            "reverse",
            0,
            0,
            (list, _) =>
            {
                list.Elements.Reverse();
                return PythonNoneValue.Instance;
            }
        ),
        ["sort"] = List(
                "sort",
                0,
                2,
                (list, arguments) =>
                {
                    var key = arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance;
                    var reverse =
                        arguments.Count > 1 && ManagedObjectProtocols.IsTrue(arguments[1]);
                    var keys = new PythonValue[list.Elements.Count];
                    for (var index = 0; index < keys.Length; index++)
                    {
                        keys[index] =
                            key is PythonNoneValue ? list.Elements[index]
                            : UserObjectProtocols.Dispatcher is { } dispatcher
                                ? dispatcher.Invoke(key, [list.Elements[index]], default)
                            : ManagedObjectProtocols.Call(key, [list.Elements[index]]);
                    }

                    List<PythonValue> sorted;
                    try
                    {
                        var indexed = list.Elements.Select((element, index) => (element, index));
                        sorted = (
                            reverse
                                ? indexed.OrderByDescending(
                                    pair => keys[pair.index],
                                    PythonOrderingComparer.Instance
                                )
                                : indexed.OrderBy(
                                    pair => keys[pair.index],
                                    PythonOrderingComparer.Instance
                                )
                        )
                            .Select(pair => pair.element)
                            .ToList();
                    }
                    catch (InvalidOperationException exception)
                        when (exception.InnerException
                                is PythonRuntimeException
                                    or PythonRaisedException
                        )
                    {
                        System
                            .Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                                exception.InnerException
                            )
                            .Throw();
                        throw;
                    }

                    list.Elements.Clear();
                    list.Elements.AddRange(sorted);
                    return PythonNoneValue.Instance;
                }
            )
            .WithSignature(["key", "reverse"], [PythonNoneValue.Instance, PythonTruthValue.False]),
        ["copy"] = List("copy", 0, 0, (list, _) => new PythonListValue([.. list.Elements])),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> DictionaryMethods = new(
        StringComparer.Ordinal
    )
    {
        ["get"] = Dictionary(
            "get",
            1,
            2,
            (dictionary, arguments) =>
                ManagedObjectProtocols.TryFindDictionaryItem(dictionary, arguments[0], out var item)
                    ? item.Value
                    : (arguments.Count == 2 ? arguments[1] : PythonNoneValue.Instance)
        ),
        ["keys"] = Dictionary(
            "keys",
            0,
            0,
            (dictionary, _) =>
                new PythonDictionaryViewValue(
                    "dict_keys",
                    new PythonListValue([.. dictionary.Items.Select(item => item.Key)])
                )
        ),
        ["values"] = Dictionary(
            "values",
            0,
            0,
            (dictionary, _) =>
                new PythonDictionaryViewValue(
                    "dict_values",
                    new PythonListValue([.. dictionary.Items.Select(item => item.Value)])
                )
        ),
        ["items"] = Dictionary(
            "items",
            0,
            0,
            (dictionary, _) =>
                new PythonDictionaryViewValue(
                    "dict_items",
                    new PythonListValue([
                        .. dictionary.Items.Select(item =>
                            (PythonValue)new PythonTupleValue([item.Key, item.Value])
                        ),
                    ])
                )
        ),
        ["pop"] = Dictionary(
            "pop",
            1,
            2,
            (dictionary, arguments) =>
            {
                if (
                    ManagedObjectProtocols.TryFindDictionaryItem(
                        dictionary,
                        arguments[0],
                        out var item
                    )
                )
                {
                    dictionary.Items.Remove(item);
                    dictionary.SizeVersion++;
                    return item.Value;
                }

                if (arguments.Count == 2)
                {
                    return arguments[1];
                }

                throw ManagedObjectProtocols.MissingKey(arguments[0]);
            }
        ),
        ["clear"] = Dictionary(
            "clear",
            0,
            0,
            (dictionary, _) =>
            {
                dictionary.Items.Clear();
                dictionary.SizeVersion++;
                return PythonNoneValue.Instance;
            }
        ),
        ["update"] = new PythonProtocolFunctionValue(
            "update",
            (target, arguments) =>
            {
                RequireArguments("update", arguments, 0, 1);
                if (arguments.Count == 1)
                {
                    MergeInto((PythonDictionaryValue)target!, arguments[0]);
                }

                return PythonNoneValue.Instance;
            },
            (target, positional, keywordNames, keywordValues) =>
            {
                RequireArguments("update", positional, 0, 1);
                var dictionary = (PythonDictionaryValue)target!;
                if (positional.Count == 1)
                {
                    MergeInto(dictionary, positional[0]);
                }

                for (var index = 0; index < keywordNames.Count; index++)
                {
                    ManagedObjectProtocols.SetDictionaryItem(
                        dictionary,
                        new PythonTextValue(keywordNames[index]),
                        keywordValues[index],
                        default
                    );
                }

                return PythonNoneValue.Instance;
            }
        ),
        ["popitem"] = Dictionary(
            "popitem",
            0,
            0,
            (dictionary, _) =>
            {
                if (dictionary.Items.Count == 0)
                {
                    throw ManagedObjectProtocols.MissingKey(
                        new PythonTextValue("popitem(): dictionary is empty")
                    );
                }

                var last = dictionary.Items[^1];
                dictionary.Items.RemoveAt(dictionary.Items.Count - 1);
                dictionary.SizeVersion++;
                return new PythonTupleValue([last.Key, last.Value]);
            }
        ),
        ["setdefault"] = Dictionary(
            "setdefault",
            1,
            2,
            (dictionary, arguments) =>
            {
                if (
                    ManagedObjectProtocols.TryFindDictionaryItem(
                        dictionary,
                        arguments[0],
                        out var item
                    )
                )
                {
                    return item.Value;
                }

                var value = arguments.Count == 2 ? arguments[1] : PythonNoneValue.Instance;
                ManagedObjectProtocols.SetDictionaryItem(dictionary, arguments[0], value, default);
                return value;
            }
        ),
        ["copy"] = Dictionary(
            "copy",
            0,
            0,
            (dictionary, _) =>
                new PythonDictionaryValue([
                    .. dictionary.Items.Select(item => new PythonDictionaryItemValue(
                        item.Key,
                        item.Value,
                        item.KeyHash
                    )),
                ])
        ),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> TupleMethods = new(
        StringComparer.Ordinal
    )
    {
        ["index"] = Tuple(
            "index",
            (tuple, arguments) =>
            {
                var index = FindElement(tuple.Elements, arguments[0]);
                if (index < 0)
                {
                    throw Fault("The value was not found in the tuple.", "ValueError");
                }

                return PythonWholeNumberValue.Create(index);
            }
        ),
        ["count"] = Tuple(
            "count",
            (tuple, arguments) =>
                PythonWholeNumberValue.Create(
                    tuple.Elements.Count(element =>
                        ManagedObjectProtocols.AreEqual(element, arguments[0])
                    )
                )
        ),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> SetMethods = new(
        StringComparer.Ordinal
    )
    {
        ["add"] = Set(
            "add",
            1,
            1,
            (set, arguments) =>
            {
                ManagedObjectProtocols.AddToSet(set, arguments[0], default);
                return PythonNoneValue.Instance;
            }
        ),
        ["remove"] = Set(
            "remove",
            1,
            1,
            (set, arguments) =>
            {
                var index = FindElement(set.Elements, arguments[0]);
                if (index < 0)
                {
                    throw ManagedObjectProtocols.MissingKey(arguments[0]);
                }

                set.Elements.RemoveAt(index);
                return PythonNoneValue.Instance;
            }
        ),
        ["discard"] = Set(
            "discard",
            1,
            1,
            (set, arguments) =>
            {
                var index = FindElement(set.Elements, arguments[0]);
                if (index >= 0)
                {
                    set.Elements.RemoveAt(index);
                }

                return PythonNoneValue.Instance;
            }
        ),
        ["clear"] = Set(
            "clear",
            0,
            0,
            (set, _) =>
            {
                set.Elements.Clear();
                return PythonNoneValue.Instance;
            }
        ),
        ["copy"] = Set("copy", 0, 0, (set, _) => new PythonSetValue([.. set.Elements])),
        ["pop"] = Set(
            "pop",
            0,
            0,
            (set, _) =>
            {
                if (set.Elements.Count == 0)
                {
                    throw ManagedObjectProtocols.MissingKey(
                        new PythonTextValue("pop from an empty set")
                    );
                }

                var first = set.Elements[0];
                set.Elements.RemoveAt(0);
                return first;
            }
        ),
        ["update"] = SetMutator(
            "update",
            (set, others) => Combine(set, others, SetOperation.Union)
        ),
        ["intersection_update"] = SetMutator(
            "intersection_update",
            (set, others) => Combine(set, others, SetOperation.Intersection)
        ),
        ["difference_update"] = SetMutator(
            "difference_update",
            (set, others) => Combine(set, others, SetOperation.Difference)
        ),
        ["symmetric_difference_update"] = SetMutator(
            "symmetric_difference_update",
            (set, others) => Combine(set, others, SetOperation.SymmetricDifference)
        ),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> FrozenSetMethods = new(
        StringComparer.Ordinal
    )
    {
        ["copy"] = Set(
            "copy",
            0,
            0,
            (set, _) => new PythonSetValue([.. set.Elements]) { IsFrozen = true }
        ),
    };

    internal static bool TryGet(
        PythonValue target,
        string name,
        out PythonProtocolFunctionValue method
    )
    {
        var table = target switch
        {
            PythonTextValue => TextMethods,
            PythonListValue => ListMethods,
            PythonDictionaryValue => DictionaryMethods,
            PythonTupleValue => TupleMethods,
            PythonSetValue { IsFrozen: true } => FrozenSetMethods,
            PythonSetValue => SetMethods,
            _ => null,
        };
        if (table is not null && table.TryGetValue(name, out var found))
        {
            method = found;
            return true;
        }

        if (target is PythonSetValue && SetAlgebraMethods.TryGetValue(name, out var algebra))
        {
            method = algebra;
            return true;
        }

        method = null!;
        return false;
    }

    private static readonly Dictionary<string, PythonProtocolFunctionValue> SetAlgebraMethods = new(
        StringComparer.Ordinal
    )
    {
        ["union"] = SetQuery("union", (set, others) => Combine(set, others, SetOperation.Union)),
        ["intersection"] = SetQuery(
            "intersection",
            (set, others) => Combine(set, others, SetOperation.Intersection)
        ),
        ["difference"] = SetQuery(
            "difference",
            (set, others) => Combine(set, others, SetOperation.Difference)
        ),
        ["symmetric_difference"] = SetQuery(
            "symmetric_difference",
            (set, others) => Combine(set, others, SetOperation.SymmetricDifference),
            exactlyOne: true
        ),
        ["issubset"] = Set(
            "issubset",
            1,
            1,
            (set, arguments) =>
                Truth(
                    set.Elements.All(element =>
                        ManagedObjectProtocols.Contains(AsSet(arguments[0]), element)
                    )
                )
        ),
        ["issuperset"] = Set(
            "issuperset",
            1,
            1,
            (set, arguments) =>
                Truth(
                    AsSet(arguments[0])
                        .Elements.All(element => ManagedObjectProtocols.Contains(set, element))
                )
        ),
        ["isdisjoint"] = Set(
            "isdisjoint",
            1,
            1,
            (set, arguments) =>
                Truth(
                    !AsSet(arguments[0])
                        .Elements.Any(element => ManagedObjectProtocols.Contains(set, element))
                )
        ),
    };

    private enum SetOperation
    {
        Union,
        Intersection,
        Difference,
        SymmetricDifference,
    }

    private static PythonSetValue AsSet(PythonValue value) =>
        value as PythonSetValue
        ?? ManagedObjectProtocols.CreateSet(
            ManagedObjectProtocols.MaterializeValues(value, default),
            default
        );

    /// <summary>Applies the operation with each operand in turn, returning a new set.</summary>
    private static PythonSetValue Combine(
        PythonSetValue set,
        IReadOnlyList<PythonValue> others,
        SetOperation operation
    )
    {
        var result = new PythonSetValue([.. set.Elements]);
        foreach (var operand in others)
        {
            var other = AsSet(operand);
            result = operation switch
            {
                SetOperation.Union => ManagedObjectProtocols.CreateSet(
                    [.. result.Elements, .. other.Elements],
                    default
                ),
                SetOperation.Intersection => new PythonSetValue([
                    .. result.Elements.Where(element =>
                        ManagedObjectProtocols.Contains(other, element)
                    ),
                ]),
                SetOperation.Difference => new PythonSetValue([
                    .. result.Elements.Where(element =>
                        !ManagedObjectProtocols.Contains(other, element)
                    ),
                ]),
                _ => ManagedObjectProtocols.CreateSet(
                    [
                        .. result.Elements.Where(element =>
                            !ManagedObjectProtocols.Contains(other, element)
                        ),
                        .. other.Elements.Where(element =>
                            !ManagedObjectProtocols.Contains(result, element)
                        ),
                    ],
                    default
                ),
            };
        }

        return result;
    }

    private static PythonProtocolFunctionValue SetQuery(
        string name,
        Func<PythonSetValue, IReadOnlyList<PythonValue>, PythonSetValue> combine,
        bool exactlyOne = false
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                if (exactlyOne)
                {
                    RequireArguments(name, arguments, 1, 1);
                }

                var set = (PythonSetValue)target!;
                var combined = combine(set, arguments);
                return set.IsFrozen
                    ? new PythonSetValue(combined.Elements) { IsFrozen = true }
                    : combined;
            }
        );

    private static PythonProtocolFunctionValue SetMutator(
        string name,
        Func<PythonSetValue, IReadOnlyList<PythonValue>, PythonSetValue> combine
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                var set = (PythonSetValue)target!;
                var combined = combine(set, arguments);
                set.Elements.Clear();
                set.Elements.AddRange(combined.Elements);
                return PythonNoneValue.Instance;
            }
        );

    private static void MergeInto(PythonDictionaryValue dictionary, PythonValue source)
    {
        if (source is PythonDictionaryValue other)
        {
            foreach (var item in other.Items.ToArray())
            {
                ManagedObjectProtocols.SetDictionaryItem(dictionary, item.Key, item.Value, default);
            }

            return;
        }

        foreach (var pair in ManagedObjectProtocols.MaterializeValues(source, default))
        {
            var elements = ManagedObjectProtocols.MaterializeValues(pair, default);
            if (elements.Count != 2)
            {
                throw Fault(
                    $"dictionary update sequence element has length {elements.Count}; 2 is required",
                    "ValueError"
                );
            }

            ManagedObjectProtocols.SetDictionaryItem(dictionary, elements[0], elements[1], default);
        }
    }

    internal static bool SupportsMethods(PythonValue target) =>
        target
            is PythonTextValue
                or PythonListValue
                or PythonDictionaryValue
                or PythonTupleValue
                or PythonSetValue;

    private static PythonProtocolFunctionValue Text(
        string name,
        int minimumArguments,
        int maximumArguments,
        Func<string, IReadOnlyList<PythonValue>, PythonValue> implementation
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, minimumArguments, maximumArguments);
                return implementation(((PythonTextValue)target!).Value, arguments);
            }
        );

    private static PythonProtocolFunctionValue List(
        string name,
        int minimumArguments,
        int maximumArguments,
        Func<PythonListValue, IReadOnlyList<PythonValue>, PythonValue> implementation
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, minimumArguments, maximumArguments);
                return implementation((PythonListValue)target!, arguments);
            }
        );

    private static PythonProtocolFunctionValue Dictionary(
        string name,
        int minimumArguments,
        int maximumArguments,
        Func<PythonDictionaryValue, IReadOnlyList<PythonValue>, PythonValue> implementation
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, minimumArguments, maximumArguments);
                return implementation((PythonDictionaryValue)target!, arguments);
            }
        );

    private static PythonProtocolFunctionValue Set(
        string name,
        int minimumArguments,
        int maximumArguments,
        Func<PythonSetValue, IReadOnlyList<PythonValue>, PythonValue> implementation
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, minimumArguments, maximumArguments);
                return implementation((PythonSetValue)target!, arguments);
            }
        );

    private static PythonProtocolFunctionValue Tuple(
        string name,
        Func<PythonTupleValue, IReadOnlyList<PythonValue>, PythonValue> implementation
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 1);
                return implementation((PythonTupleValue)target!, arguments);
            }
        );

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int minimum,
        int maximum
    )
    {
        if (arguments.Count >= minimum && arguments.Count <= maximum)
        {
            return;
        }

        var expectation = minimum == maximum ? $"{maximum}" : $"between {minimum} and {maximum}";
        throw Fault(
            $"Method '{name}' expected {expectation} argument(s), "
                + $"but received {arguments.Count}.",
            "TypeError"
        );
    }

    private static string RequireText(string name, PythonValue value) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                $"Method '{name}' expected a string argument, "
                    + $"but received {ManagedObjectProtocols.GetTypeName(value)}.",
                "TypeError"
            );

    private static int RequireInteger(string name, PythonValue value) =>
        value is PythonWholeNumberValue wholeNumber
            ? (int)BigIntegerClamp(wholeNumber.Value, int.MinValue, int.MaxValue)
            : throw Fault(
                $"Method '{name}' expected an integer argument, "
                    + $"but received {ManagedObjectProtocols.GetTypeName(value)}.",
                "TypeError"
            );

    private static System.Numerics.BigInteger BigIntegerClamp(
        System.Numerics.BigInteger value,
        System.Numerics.BigInteger minimum,
        System.Numerics.BigInteger maximum
    ) => value < minimum ? minimum : (value > maximum ? maximum : value);

    private static int FindElement(IReadOnlyList<PythonValue> elements, PythonValue value)
    {
        for (var index = 0; index < elements.Count; index++)
        {
            if (ManagedObjectProtocols.AreEqual(elements[index], value))
            {
                return index;
            }
        }

        return -1;
    }

    private static PythonValue SplitText(string text, IReadOnlyList<PythonValue> arguments)
    {
        var maxSplit = arguments.Count > 1 ? RequireCount("split", arguments[1]) : -1;
        var limit = maxSplit < 0 ? int.MaxValue : maxSplit + 1;
        string[] parts;
        if (arguments.Count == 0 || arguments[0] is PythonNoneValue)
        {
            parts = SplitOnWhitespace(text, limit);
        }
        else
        {
            var separator = RequireText("split", arguments[0]);
            if (separator.Length == 0)
            {
                throw Fault("empty separator", "ValueError");
            }

            parts = text.Split(separator, limit, StringSplitOptions.None);
        }

        return new PythonListValue([
            .. parts.Select(part => (PythonValue)new PythonTextValue(part)),
        ]);
    }

    /// <summary>CPython's whitespace split: runs of whitespace separate, `maxsplit` keeps the tail intact.</summary>
    private static string[] SplitOnWhitespace(string text, int limit)
    {
        var parts = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position >= text.Length)
            {
                break;
            }

            if (parts.Count == limit - 1)
            {
                // The remainder keeps its trailing whitespace, as CPython does.
                parts.Add(text[position..]);
                break;
            }

            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            parts.Add(text[start..position]);
        }

        return [.. parts];
    }

    private static int RequireCount(string name, PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole
                when whole.Value >= int.MinValue && whole.Value <= int.MaxValue => (int)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
                "TypeError"
            ),
        };

    /// <summary>Applies optional `start`/`end` slice bounds (as runes) to a text method's subject.</summary>
    private static string SliceRunes(
        string text,
        IReadOnlyList<PythonValue> arguments,
        int firstBoundIndex,
        out int offset
    )
    {
        var runes = text.EnumerateRunes().ToArray();
        var start = 0;
        var end = runes.Length;
        if (arguments.Count > firstBoundIndex && arguments[firstBoundIndex] is not PythonNoneValue)
        {
            start = ClampBound(RequireCount("index", arguments[firstBoundIndex]), runes.Length);
        }

        if (
            arguments.Count > firstBoundIndex + 1
            && arguments[firstBoundIndex + 1] is not PythonNoneValue
        )
        {
            end = ClampBound(RequireCount("index", arguments[firstBoundIndex + 1]), runes.Length);
        }

        offset = start;
        if (start >= end)
        {
            return string.Empty;
        }

        return string.Concat(runes[start..end].Select(rune => rune.ToString()));
    }

    private static int ClampBound(int bound, int length)
    {
        if (bound < 0)
        {
            bound += length;
        }

        return Math.Clamp(bound, 0, length);
    }

    private static bool MatchesAffix(
        string name,
        string text,
        IReadOnlyList<PythonValue> arguments,
        bool prefix
    )
    {
        var subject = SliceRunes(text, arguments, 1, out _);
        IEnumerable<PythonValue> candidates = arguments[0] switch
        {
            PythonTextValue single => [single],
            PythonTupleValue tuple => tuple.Elements,
            var other => throw Fault(
                $"{name} first arg must be str or a tuple of str, not {ManagedObjectProtocols.GetTypeName(other)}",
                "TypeError"
            ),
        };
        // Tuple elements are checked lazily: a match before a non-str element wins.
        foreach (var candidate in candidates)
        {
            if (candidate is not PythonTextValue candidateText)
            {
                throw Fault(
                    $"tuple for {name} must only contain str, not {ManagedObjectProtocols.GetTypeName(candidate)}",
                    "TypeError"
                );
            }

            if (
                prefix
                    ? subject.StartsWith(candidateText.Value, StringComparison.Ordinal)
                    : subject.EndsWith(candidateText.Value, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static int FindInRange(string name, string text, IReadOnlyList<PythonValue> arguments)
    {
        var needle = RequireText(name, arguments[0]);
        var subject = SliceRunes(text, arguments, 1, out var offset);
        var position = FindRuneIndex(subject, needle);
        return position < 0 ? -1 : position + offset;
    }

    private static PythonValue JoinText(string separator, IReadOnlyList<PythonValue> arguments)
    {
        var parts = new List<string>();
        var iterator = ManagedObjectProtocols.GetIterator(arguments[0]);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element))
        {
            if (element is not PythonTextValue part)
            {
                throw Fault(
                    $"Method 'join' expected string items, "
                        + $"but received {ManagedObjectProtocols.GetTypeName(element)}.",
                    "TypeError"
                );
            }

            parts.Add(part.Value);
        }

        return new PythonTextValue(string.Join(separator, parts));
    }

    private static string ReplaceText(string text, string oldValue, string newValue, int count)
    {
        if (count < 0)
        {
            return ReplaceText(text, oldValue, newValue);
        }

        var builder = new StringBuilder();
        var position = 0;
        var replaced = 0;
        while (replaced < count)
        {
            var next =
                oldValue.Length == 0
                    ? position
                    : text.IndexOf(oldValue, position, StringComparison.Ordinal);
            if (next < 0 || next > text.Length)
            {
                break;
            }

            builder.Append(text, position, next - position).Append(newValue);
            if (oldValue.Length == 0)
            {
                if (next < text.Length)
                {
                    builder.Append(text[next]);
                }

                position = next + 1;
            }
            else
            {
                position = next + oldValue.Length;
            }

            replaced++;
            if (position > text.Length)
            {
                return builder.ToString();
            }
        }

        if (position <= text.Length)
        {
            builder.Append(text, position, text.Length - position);
        }

        return builder.ToString();
    }

    private static string ReplaceText(string text, string oldValue, string newValue)
    {
        if (oldValue.Length != 0)
        {
            return text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        var builder = new StringBuilder();
        builder.Append(newValue);
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(rune.ToString());
            builder.Append(newValue);
        }

        return builder.ToString();
    }

    private static int FindRuneIndex(string text, string substring)
    {
        var position = text.IndexOf(substring, StringComparison.Ordinal);
        return position < 0 ? -1 : text[..position].EnumerateRunes().Count();
    }

    private static int CountText(string text, string substring)
    {
        if (substring.Length == 0)
        {
            return text.EnumerateRunes().Count() + 1;
        }

        var count = 0;
        var position = 0;
        while (true)
        {
            position = text.IndexOf(substring, position, StringComparison.Ordinal);
            if (position < 0)
            {
                return count;
            }

            count++;
            position += substring.Length;
        }
    }

    private static string Capitalize(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var runes = text.EnumerateRunes().ToArray();
        var builder = new StringBuilder();
        builder.Append(runes[0].ToString().ToUpperInvariant());
        for (var index = 1; index < runes.Length; index++)
        {
            builder.Append(runes[index].ToString().ToLowerInvariant());
        }

        return builder.ToString();
    }

    private static PythonTruthValue Truth(bool value) =>
        value ? PythonTruthValue.True : PythonTruthValue.False;

    private static PythonRuntimeException Fault(string message, string pythonType) =>
        ManagedObjectProtocols.Fault("DPY4009", message, default, pythonType);
}
