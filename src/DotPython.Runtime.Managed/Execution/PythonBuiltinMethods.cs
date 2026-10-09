using System.Text;
using DotPython.Language.Text;

// CA1308 targets case-normalization before comparison; str.lower() and capitalize()
// produce lowercase text as their Python-visible result, not a comparison key.
#pragma warning disable CA1308

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Bound-method tables for the built-in str, list, dict, and tuple values.</summary>
internal static class PythonBuiltinMethods
{
    private static void AddName(List<string> names, string name)
    {
        if (!names.Contains(name))
            names.Add(name);
    }

    private static string CodecArgument(PythonValue value, string parameter)
    {
        _ = RequireText("encode", value, $"encode() argument '{parameter}' must be str, not {{1}}");
        return PythonCodecs.ConvertName(
            (PythonTextValue)value,
            UserObjectProtocols.Dispatcher?.CurrentSpan ?? default
        );
    }

    private static readonly Dictionary<string, PythonProtocolFunctionValue> TextMethods = new(
        PythonTextMethods.CreateTable(),
        StringComparer.Ordinal
    )
    {
        ["upper"] = Text(
            "upper",
            0,
            0,
            (text, _) => new PythonTextValue(PythonUnicodeCase.ToUpper(text))
        ),
        ["lower"] = Text(
            "lower",
            0,
            0,
            (text, _) => new PythonTextValue(PythonUnicodeCase.ToLower(text))
        ),
        ["casefold"] = Text(
            "casefold",
            0,
            0,
            (text, _) => new PythonTextValue(PythonUnicodeCase.ToCaseFold(text))
        ),
        ["title"] = Text(
            "title",
            0,
            0,
            (text, _) => new PythonTextValue(PythonUnicodeCase.ToTitle(text))
        ),
        ["swapcase"] = Text(
            "swapcase",
            0,
            0,
            (text, _) => new PythonTextValue(PythonUnicodeCase.SwapCase(text))
        ),
        ["strip"] = Text(
            "strip",
            0,
            1,
            (text, arguments) =>
                new PythonTextValue(
                    arguments.Count == 0
                        ? TrimSpace(text, leading: true, trailing: true)
                        : text.Trim(
                            RequireText("strip", arguments[0], "strip arg must be None or str")
                                .ToCharArray()
                        )
                )
        ),
        ["lstrip"] = Text(
            "lstrip",
            0,
            1,
            (text, arguments) =>
                new PythonTextValue(
                    arguments.Count == 0
                        ? TrimSpace(text, leading: true, trailing: false)
                        : text.TrimStart(
                            RequireText("lstrip", arguments[0], "lstrip arg must be None or str")
                                .ToCharArray()
                        )
                )
        ),
        ["rstrip"] = Text(
            "rstrip",
            0,
            1,
            (text, arguments) =>
                new PythonTextValue(
                    arguments.Count == 0
                        ? TrimSpace(text, leading: false, trailing: true)
                        : text.TrimEnd(
                            RequireText("rstrip", arguments[0], "rstrip arg must be None or str")
                                .ToCharArray()
                        )
                )
        ),
        ["split"] = Text("split", 0, 2, SplitText)
            .WithSignature(["sep", "maxsplit"], [PythonNoneValue.Instance, null]),
        ["encode"] = new PythonProtocolFunctionValue(
            "encode",
            (target, arguments) =>
            {
                RequireArguments("str", "encode", arguments, 0, 2);
                return PythonByteSequenceValue.Create(
                    PythonTextCodecs.Encode(
                        (PythonTextValue)target!,
                        arguments.Count > 0 ? CodecArgument(arguments[0], "encoding") : "utf-8",
                        arguments.Count > 1 ? CodecArgument(arguments[1], "errors") : "strict",
                        UserObjectProtocols.Dispatcher?.CurrentSpan ?? default
                    )
                );
            }
        ).WithSignature(
            ["encoding", "errors"],
            [new PythonTextValue("utf-8"), new PythonTextValue("strict")]
        ),
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
                        AddName(names, keyText.Value);
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
                            RequireText(
                                "replace",
                                arguments[0],
                                "replace() argument 1 must be str, not {1}"
                            ),
                            RequireText(
                                "replace",
                                arguments[1],
                                "replace() argument 2 must be str, not {1}"
                            ),
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
                        SliceCharacters(text, arguments, 1, out _),
                        RequireText("count", arguments[0])
                    )
                )
        ),
        ["capitalize"] = Text(
            "capitalize",
            0,
            0,
            (text, _) => new PythonTextValue(PythonUnicodeCase.Capitalize(text))
        ),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> ByteArrayMethods =
        PythonByteArrayMethods.CreateTable();

    private static readonly Dictionary<string, PythonProtocolFunctionValue> BytesMethods = new(
        PythonBytesMethods.CreateTable(),
        StringComparer.Ordinal
    )
    {
        ["decode"] = PythonBytesText.DecodeMethod,
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
                var index = GetListMethodIndex(arguments[0]);
                var count = list.Elements.Count;
                if (index < 0)
                {
                    index += count;
                }

                index = BigIntegerClamp(index, 0, count);
                list.Elements.Insert((int)index, arguments[1]);
                return PythonNoneValue.Instance;
            }
        ),
        ["pop"] = List(
            "pop",
            0,
            1,
            (list, arguments) =>
            {
                // Index conversion can resize even an initially empty list.
                var index = arguments.Count == 0 ? -1 : GetListMethodIndex(arguments[0]);
                var count = list.Elements.Count;
                if (count == 0)
                {
                    throw Fault("pop from empty list", "IndexError");
                }

                if (index < 0)
                    index += count;
                if (index < 0 || index >= count)
                    throw Fault("pop index out of range", "IndexError");
                var value = list.Elements[(int)index];
                list.Elements.RemoveAt((int)index);
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
                    throw Fault("list.remove(x): x not in list", "ValueError");
                }

                // Equality may replace or remove the matched position. Delete the
                // current item there, or succeed without deletion if it disappeared.
                if (index < list.Elements.Count)
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
            3,
            (list, arguments) => FindSequenceIndex(list.Elements, arguments, "list")
        ),
        ["count"] = List(
            "count",
            1,
            1,
            (list, arguments) => CountSequenceElements(list.Elements, arguments[0])
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
        ["sort"] = PythonListSorting.Method,
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
            (dictionary, _) => new PythonDictionaryViewValue("dict_keys", dictionary)
        ),
        ["values"] = Dictionary(
            "values",
            0,
            0,
            (dictionary, _) => new PythonDictionaryViewValue("dict_values", dictionary)
        ),
        ["items"] = Dictionary(
            "items",
            0,
            0,
            (dictionary, _) => new PythonDictionaryViewValue("dict_items", dictionary)
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
                    dictionary.RemoveItem(item);
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
                dictionary.ClearItems();
                return PythonNoneValue.Instance;
            }
        ),
        ["update"] = new PythonProtocolFunctionValue(
            "update",
            (target, arguments) =>
            {
                RequireArguments("dict", "update", arguments, 0, 1);
                if (arguments.Count == 1)
                {
                    MergeInto((PythonDictionaryValue)target!, arguments[0]);
                }

                return PythonNoneValue.Instance;
            },
            (target, positional, keywordNames, keywordValues) =>
            {
                RequireArguments("dict", "update", positional, 0, 1);
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
                dictionary.RemoveItem(last, trimTail: true);
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
        ["copy"] = Dictionary("copy", 0, 0, (dictionary, _) => dictionary.ShallowCopy()),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> TupleMethods = new(
        StringComparer.Ordinal
    )
    {
        ["index"] = Tuple(
            "index",
            (tuple, arguments) => FindSequenceIndex(tuple.Elements, arguments, "tuple"),
            maximumArguments: 3
        ),
        ["count"] = Tuple(
            "count",
            (tuple, arguments) => CountSequenceElements(tuple.Elements, arguments[0])
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
                var index = ManagedObjectProtocols.FindSetEntry(set, arguments[0]);
                if (index < 0)
                {
                    throw ManagedObjectProtocols.MissingKey(arguments[0]);
                }

                set.RemoveEntry(index);
                return PythonNoneValue.Instance;
            }
        ),
        ["discard"] = Set(
            "discard",
            1,
            1,
            (set, arguments) =>
            {
                var index = ManagedObjectProtocols.FindSetEntry(set, arguments[0]);
                if (index >= 0)
                {
                    set.RemoveEntry(index);
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
                set.ClearEntries();
                return PythonNoneValue.Instance;
            }
        ),
        ["copy"] = Set("copy", 0, 0, (set, _) => set.Copy()),
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
                set.RemoveEntry(0);
                return first;
            }
        ),
        ["update"] = SetMutator("update", PythonSetOperations.Operation.Union),
        ["intersection_update"] = SetMutator(
            "intersection_update",
            PythonSetOperations.Operation.Intersection
        ),
        ["difference_update"] = SetMutator(
            "difference_update",
            PythonSetOperations.Operation.Difference
        ),
        ["symmetric_difference_update"] = SetMutator(
            "symmetric_difference_update",
            PythonSetOperations.Operation.SymmetricDifference
        ),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> FrozenSetMethods = new(
        StringComparer.Ordinal
    )
    {
        ["copy"] = Set("copy", 0, 0, (set, _) => set),
    };

    private static readonly Dictionary<string, PythonProtocolFunctionValue> NumberMethods =
        PythonIntMethods.CreateTable();

    private static readonly Dictionary<string, PythonProtocolFunctionValue> FloatMethods =
        PythonFloatMethods.CreateTable();

    internal static bool TryGet(
        PythonValue target,
        string name,
        out PythonProtocolFunctionValue method
    )
    {
        var typeName = target switch
        {
            PythonTextValue => "str",
            PythonByteSequenceValue => "bytes",
            PythonByteArrayValue => "bytearray",
            PythonListValue => "list",
            PythonDictionaryValue => "dict",
            PythonTupleValue => "tuple",
            PythonSetValue { IsFrozen: true } => "frozenset",
            PythonSetValue => "set",
            PythonWholeNumberValue or PythonTruthValue => "int",
            PythonFloatingPointValue => "float",
            _ => null,
        };
        if (typeName is null)
        {
            method = null!;
            return false;
        }
        return TryGetTypeMember(typeName, name, out method);
    }

    /// <summary>
    /// The methods the type object itself answers. Reaching one through the type gives a
    /// method descriptor, so `list.append` is callable with an explicit receiver.
    /// </summary>
    internal static bool TryGetTypeMember(
        string typeName,
        string name,
        out PythonProtocolFunctionValue method
    )
    {
        var table = TableFor(typeName);
        if (table is not null && table.TryGetValue(name, out var found))
        {
            method = found;
            return true;
        }

        // Both set types share the query algebra; only `set` carries the mutators.
        if (
            typeName is "set" or "frozenset"
            && SetAlgebraMethods.TryGetValue(name, out var algebra)
        )
        {
            method = algebra;
            return true;
        }

        // A bool is an int, so it answers the int methods, under int's name.
        if (typeName == "bool")
            return TryGetTypeMember("int", name, out method);

        method = null!;
        return false;
    }

    /// <summary>The method table a type name belongs to, or null for a type without one.</summary>
    private static Dictionary<string, PythonProtocolFunctionValue>? TableFor(string name) =>
        name switch
        {
            "str" => TextMethods,
            "bytes" => BytesMethods,
            "bytearray" => ByteArrayMethods,
            "list" => ListMethods,
            "dict" => DictionaryMethods,
            "tuple" => TupleMethods,
            "set" => SetMethods,
            "frozenset" => FrozenSetMethods,
            "int" => NumberMethods,
            "float" => FloatMethods,
            _ => null,
        };

    /// <summary>Every method name a type exposes, for `dir` and `__dir__`.</summary>
    internal static void AddMethodNames(string typeName, List<string> names)
    {
        if (TableFor(typeName) is { } table)
        {
            foreach (var name in table.Keys)
                AddName(names, name);
        }
        if (typeName is "set" or "frozenset")
        {
            foreach (var name in SetAlgebraMethods.Keys)
                AddName(names, name);
        }
    }

    /// <summary>
    /// The interned descriptor for a type's method, or null when the type has no such
    /// method. One descriptor per type and name keeps `list.append is list.append`.
    /// </summary>
    internal static PythonMethodDescriptorValue? GetTypeMemberDescriptor(
        string typeName,
        string name
    )
    {
        if (!TryGetTypeMember(typeName, name, out var function))
            return null;
        // A bool reports the type it borrowed from, so `bool.bit_length` is int's descriptor.
        var ownerName = typeName == "bool" ? "int" : typeName;
        lock (TypeMemberDescriptors)
        {
            if (!TypeMemberDescriptors.TryGetValue((ownerName, name), out var descriptor))
            {
                descriptor = new PythonMethodDescriptorValue(
                    PythonBuiltinTypes.ForName(ownerName),
                    name,
                    function
                );
                TypeMemberDescriptors[(ownerName, name)] = descriptor;
            }
            return descriptor;
        }
    }

    private static readonly Dictionary<
        (string Owner, string Name),
        PythonMethodDescriptorValue
    > TypeMemberDescriptors = new();

    private static readonly Dictionary<string, PythonProtocolFunctionValue> SetAlgebraMethods = new(
        StringComparer.Ordinal
    )
    {
        ["union"] = SetQuery(
            "union",
            (set, others) => Combine(set, others, PythonSetOperations.Operation.Union)
        ),
        ["intersection"] = SetQuery(
            "intersection",
            (set, others) => Combine(set, others, PythonSetOperations.Operation.Intersection)
        ),
        ["difference"] = SetQuery(
            "difference",
            (set, others) => Combine(set, others, PythonSetOperations.Operation.Difference)
        ),
        ["symmetric_difference"] = SetQuery(
            "symmetric_difference",
            (set, others) =>
                Combine(set, others, PythonSetOperations.Operation.SymmetricDifference),
            exactlyOne: true
        ),
        ["issubset"] = Set(
            "issubset",
            1,
            1,
            (set, arguments) => Truth(PythonSetOperations.IsSubset(set, arguments[0]))
        ),
        ["issuperset"] = Set(
            "issuperset",
            1,
            1,
            (set, arguments) => Truth(PythonSetOperations.IsSuperset(set, arguments[0]))
        ),
        ["isdisjoint"] = Set(
            "isdisjoint",
            1,
            1,
            (set, arguments) => Truth(PythonSetOperations.IsDisjoint(set, arguments[0]))
        ),
    };

    private static PythonSetValue Combine(
        PythonSetValue set,
        IReadOnlyList<PythonValue> others,
        PythonSetOperations.Operation operation
    )
    {
        if (others.Count == 0)
            return set.Copy();
        if (operation == PythonSetOperations.Operation.Union)
        {
            var union = set.Copy();
            foreach (var operand in others)
                if (!ReferenceEquals(set, operand))
                    PythonSetOperations.Update(union, operand);
            return union;
        }
        var result = PythonSetOperations.Combine(set, others[0], operation);
        for (var index = 1; index < others.Count; index++)
        {
            if (operation == PythonSetOperations.Operation.Difference)
                PythonSetOperations.DifferenceUpdate(result, others[index]);
            else
                result = PythonSetOperations.Combine(result, others[index], operation);
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
                    RequireArguments("set", name, arguments, 1, 1);
                return combine((PythonSetValue)target!, arguments);
            }
        );

    private static PythonProtocolFunctionValue SetMutator(
        string name,
        PythonSetOperations.Operation operation
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                if (operation == PythonSetOperations.Operation.SymmetricDifference)
                    RequireArguments("set", name, arguments, 1, 1);
                var set = (PythonSetValue)target!;
                if (operation == PythonSetOperations.Operation.Intersection)
                {
                    set.ReplaceEntries(Combine(set, arguments, operation));
                    return PythonNoneValue.Instance;
                }
                foreach (var source in arguments)
                {
                    switch (operation)
                    {
                        case PythonSetOperations.Operation.Union:
                            PythonSetOperations.Update(set, source);
                            break;
                        case PythonSetOperations.Operation.Difference:
                            PythonSetOperations.DifferenceUpdate(set, source);
                            break;
                        default:
                            PythonSetOperations.SymmetricDifferenceUpdate(set, source);
                            break;
                    }
                }
                return PythonNoneValue.Instance;
            }
        );

    internal static void MergeInto(
        PythonDictionaryValue dictionary,
        PythonValue source,
        TextSpan span = default,
        bool mappingOnly = false
    )
    {
        if (source is PythonDictionaryValue other)
        {
            ManagedObjectProtocols.MergeDictionary(dictionary, other, span);
            return;
        }
        PythonValue? keysMethod;
        try
        {
            keysMethod = ManagedObjectProtocols.GetAttribute(source, "keys", span);
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError"))
        {
            keysMethod = null;
        }
        if (keysMethod is not null)
        {
            var keys = UserObjectProtocols.Dispatcher is { } dispatcher
                ? dispatcher.Invoke(keysMethod, [], span)
                : ManagedObjectProtocols.Call(keysMethod, [], span);
            foreach (var key in ManagedObjectProtocols.MaterializeValues(keys, span))
                ManagedObjectProtocols.SetDictionaryItem(
                    dictionary,
                    key,
                    PythonNamespaceMapping.GetItem(source, key, span),
                    span
                );
            return;
        }
        if (mappingOnly)
            throw new PythonRuntimeException(
                "DPY4009",
                $"'{ManagedObjectProtocols.GetTypeName(source)}' object is not a mapping",
                span,
                "TypeError"
            );

        foreach (var pair in ManagedObjectProtocols.MaterializeValues(source, span))
        {
            var elements = ManagedObjectProtocols.MaterializeValues(pair, span);
            if (elements.Count != 2)
            {
                throw Fault(
                    $"dictionary update sequence element has length {elements.Count}; 2 is required",
                    "ValueError"
                );
            }

            ManagedObjectProtocols.SetDictionaryItem(dictionary, elements[0], elements[1], span);
        }
    }

    internal static bool SupportsMethods(PythonValue target) =>
        target
            is PythonTextValue
                or PythonByteSequenceValue
                or PythonByteArrayValue
                or PythonListValue
                or PythonDictionaryValue
                or PythonTupleValue
                or PythonSetValue
                or PythonWholeNumberValue
                or PythonTruthValue
                or PythonFloatingPointValue;

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
                RequireArguments("str", name, arguments, minimumArguments, maximumArguments);
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
                RequireArguments("list", name, arguments, minimumArguments, maximumArguments);
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
                RequireArguments("dict", name, arguments, minimumArguments, maximumArguments);
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
                RequireArguments("set", name, arguments, minimumArguments, maximumArguments);
                return implementation((PythonSetValue)target!, arguments);
            }
        );

    private static PythonProtocolFunctionValue Tuple(
        string name,
        Func<PythonTupleValue, IReadOnlyList<PythonValue>, PythonValue> implementation,
        int maximumArguments = 1
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments("tuple", name, arguments, 1, maximumArguments);
                return implementation((PythonTupleValue)target!, arguments);
            }
        );

    private static void RequireArguments(
        string owner,
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

        throw Fault(PythonMethodWording.Arity(owner, name, arguments.Count), "TypeError");
    }

    /// <summary>
    /// A string argument, refused with the wording the calling method uses. CPython names the
    /// method and the argument position for some methods and only the expected type for others.
    /// </summary>
    private static string RequireText(string name, PythonValue value, string? wording = null) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                wording is null
                    ? $"{name}() argument 1 must be str, "
                        + $"not {ManagedObjectProtocols.GetTypeName(value)}"
                    : wording
                        .Replace("{0}", name, StringComparison.Ordinal)
                        .Replace(
                            "{1}",
                            ManagedObjectProtocols.GetTypeName(value),
                            StringComparison.Ordinal
                        ),
                "TypeError"
            );

    private static System.Numerics.BigInteger GetListMethodIndex(PythonValue value)
    {
        System.Numerics.BigInteger index;
        if (value is PythonWholeNumberValue whole)
            index = whole.Value;
        else if (value is PythonTruthValue truth)
            index = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(value, default, out index))
            throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
                "TypeError"
            );

        var minimum =
            IntPtr.Size == sizeof(long)
                ? new System.Numerics.BigInteger(long.MinValue)
                : int.MinValue;
        var maximum =
            IntPtr.Size == sizeof(long)
                ? new System.Numerics.BigInteger(long.MaxValue)
                : int.MaxValue;
        if (index < minimum || index > maximum)
            throw Fault("Python int too large to convert to C ssize_t", "OverflowError");
        return index;
    }

    private static System.Numerics.BigInteger BigIntegerClamp(
        System.Numerics.BigInteger value,
        System.Numerics.BigInteger minimum,
        System.Numerics.BigInteger maximum
    ) => value < minimum ? minimum : (value > maximum ? maximum : value);

    private static PythonWholeNumberValue FindSequenceIndex(
        IReadOnlyList<PythonValue> elements,
        IReadOnlyList<PythonValue> arguments,
        string typeName
    )
    {
        // Both conversions may mutate the list; normalize only after both finish.
        var start = arguments.Count > 1 ? GetSearchBound(arguments[1]) : 0;
        var stop = arguments.Count > 2 ? GetSearchBound(arguments[2]) : long.MaxValue;
        var count = elements.Count;
        if (start < 0)
            start = Math.Max(0, start + count);
        if (stop < 0)
            stop = Math.Max(0, stop + count);

        // Do not cap stop at the initial list length: comparisons may append items.
        for (var index = start; index < stop && index < elements.Count; index++)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(default);
            var element = elements[(int)index];
            if (SequenceElementMatches(element, arguments[0]))
                return PythonWholeNumberValue.Create(index);
        }
        throw Fault($"{typeName}.index(x): x not in {typeName}", "ValueError");
    }

    private static long GetSearchBound(PythonValue value)
    {
        System.Numerics.BigInteger bound;
        if (value is PythonWholeNumberValue whole)
            bound = whole.Value;
        else if (value is PythonTruthValue truth)
            bound = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(value, default, out bound))
            throw Fault("slice indices must be integers or have an __index__ method", "TypeError");

        var minimum = IntPtr.Size == sizeof(long) ? long.MinValue : int.MinValue;
        var maximum = IntPtr.Size == sizeof(long) ? long.MaxValue : int.MaxValue;
        return (long)BigIntegerClamp(bound, minimum, maximum);
    }

    private static int FindElement(List<PythonValue> elements, PythonValue value)
    {
        for (var index = 0; index < elements.Count; index++)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(default);
            if (SequenceElementMatches(elements[index], value))
            {
                return index;
            }
        }

        return -1;
    }

    private static PythonWholeNumberValue CountSequenceElements(
        IReadOnlyList<PythonValue> elements,
        PythonValue value
    )
    {
        var count = 0;
        // Each comparison may resize a list; inspect its live positions rather
        // than an enumerator or a span captured before the first callback.
        for (var index = 0; index < elements.Count; index++)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(default);
            if (SequenceElementMatches(elements[index], value))
                count++;
        }
        return PythonWholeNumberValue.Create(count);
    }

    private static bool SequenceElementMatches(PythonValue element, PythonValue value) =>
        ReferenceEquals(element, value) || ManagedObjectProtocols.AreEqual(element, value);

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
            var separator = RequireText("split", arguments[0], "must be str or None, not {1}");
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

    /// <summary>
    /// Trimming by CPython's whitespace set rather than the platform's: the two differ on
    /// the file, group, record and unit separators, which `str.isspace` accepts.
    /// </summary>
    private static string TrimSpace(string text, bool leading, bool trailing)
    {
        var start = 0;
        var end = text.Length;
        if (leading)
        {
            while (start < end && PythonTextPredicates.IsSpace(text[start]))
                start++;
        }
        if (trailing)
        {
            while (end > start && PythonTextPredicates.IsSpace(text[end - 1]))
                end--;
        }
        return text[start..end];
    }

    /// <summary>CPython's whitespace split: runs of whitespace separate, `maxsplit` keeps the tail intact.</summary>
    private static string[] SplitOnWhitespace(string text, int limit)
    {
        var parts = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && PythonTextPredicates.IsSpace(text[position]))
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
            while (position < text.Length && !PythonTextPredicates.IsSpace(text[position]))
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

    /// <summary>Applies optional `start`/`end` slice bounds (as Python characters) to a text method's subject.</summary>
    private static string SliceCharacters(
        string text,
        IReadOnlyList<PythonValue> arguments,
        int firstBoundIndex,
        out int offset
    )
    {
        var length = PythonTextTraversal.Count(text);
        var start = 0;
        var end = length;
        if (arguments.Count > firstBoundIndex && arguments[firstBoundIndex] is not PythonNoneValue)
        {
            start = ClampBound(RequireBound(arguments[firstBoundIndex]), length);
        }

        if (
            arguments.Count > firstBoundIndex + 1
            && arguments[firstBoundIndex + 1] is not PythonNoneValue
        )
        {
            end = ClampBound(RequireBound(arguments[firstBoundIndex + 1]), length);
        }

        offset = start;
        if (start >= end)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var index = 0;
        foreach (var character in PythonTextTraversal.Enumerate(text))
        {
            if (index >= end)
                break;
            if (index >= start)
                builder.Append(character.ToString());
            index++;
        }
        return builder.ToString();
    }

    /// <summary>
    /// A search bound, which CPython words as a slice index rather than as a count.
    /// </summary>
    private static int RequireBound(PythonValue value)
    {
        if (value is PythonWholeNumberValue whole)
            return whole.Value > int.MaxValue ? int.MaxValue
                : whole.Value < int.MinValue ? int.MinValue
                : (int)whole.Value;
        if (value is PythonTruthValue truth)
            return truth.Value ? 1 : 0;
        throw Fault(
            "slice indices must be integers or None or have an __index__ method",
            "TypeError"
        );
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
        var subject = SliceCharacters(text, arguments, 1, out _);
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
        var needle = RequireText(name, arguments[0], "{0}() argument 1 must be str, not {1}");
        var length = PythonTextTraversal.Count(text);
        if (needle.Length == 0)
        {
            // An empty needle sits at the start bound, but a start past the end finds
            // nothing — CPython does not slide it back onto the last character.
            var start = 0;
            if (arguments.Count > 1 && arguments[1] is not PythonNoneValue)
            {
                start = RequireBound(arguments[1]);
                if (start < 0)
                    start += length;
                // A start still negative after that settles on the beginning.
                start = Math.Max(start, 0);
            }
            var end = length;
            if (arguments.Count > 2 && arguments[2] is not PythonNoneValue)
            {
                end = RequireBound(arguments[2]);
                if (end < 0)
                    end += length;
            }
            return start > length || start > end ? -1 : start;
        }
        var subject = SliceCharacters(text, arguments, 1, out var offset);
        var position = FindCharacterIndex(subject, needle);
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
            if ((replaced & 255) == 0)
                UserObjectProtocols.Dispatcher?.CheckIterationWork(default);
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
                    var width = PythonTextTraversal.Width(text, next);
                    builder.Append(text, next, width);
                    position = next + width;
                }
                else
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
        foreach (var rune in PythonTextTraversal.Enumerate(text))
        {
            builder.Append(rune.ToString());
            builder.Append(newValue);
        }

        return builder.ToString();
    }

    private static int FindCharacterIndex(string text, string substring)
    {
        var position = text.IndexOf(substring, StringComparison.Ordinal);
        return position < 0 ? -1 : PythonTextTraversal.Enumerate(text[..position]).Count();
    }

    private static int CountText(string text, string substring)
    {
        if (substring.Length == 0)
        {
            return PythonTextTraversal.Enumerate(text).Count() + 1;
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

        var builder = new StringBuilder();
        var first = true;
        foreach (var character in PythonTextTraversal.Enumerate(text))
        {
            builder.Append(
                first
                    ? character.ToString().ToUpperInvariant()
                    : character.ToString().ToLowerInvariant()
            );
            first = false;
        }

        return builder.ToString();
    }

    private static PythonTruthValue Truth(bool value) =>
        value ? PythonTruthValue.True : PythonTruthValue.False;

    private static PythonRuntimeException Fault(string message, string pythonType) =>
        ManagedObjectProtocols.Fault("DPY4009", message, default, pythonType);
}
