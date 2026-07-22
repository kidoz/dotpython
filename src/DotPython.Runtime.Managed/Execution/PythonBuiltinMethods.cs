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
        ["split"] = Text("split", 0, 1, SplitText),
        ["join"] = Text("join", 1, 1, JoinText),
        ["replace"] = Text(
            "replace",
            2,
            2,
            (text, arguments) =>
                new PythonTextValue(
                    ReplaceText(
                        text,
                        RequireText("replace", arguments[0]),
                        RequireText("replace", arguments[1])
                    )
                )
        ),
        ["startswith"] = Text(
            "startswith",
            1,
            1,
            (text, arguments) =>
                Truth(
                    text.StartsWith(
                        RequireText("startswith", arguments[0]),
                        StringComparison.Ordinal
                    )
                )
        ),
        ["endswith"] = Text(
            "endswith",
            1,
            1,
            (text, arguments) =>
                Truth(
                    text.EndsWith(RequireText("endswith", arguments[0]), StringComparison.Ordinal)
                )
        ),
        ["find"] = Text(
            "find",
            1,
            1,
            (text, arguments) =>
                PythonWholeNumberValue.Create(
                    FindRuneIndex(text, RequireText("find", arguments[0]))
                )
        ),
        ["index"] = Text(
            "index",
            1,
            1,
            (text, arguments) =>
            {
                var position = FindRuneIndex(text, RequireText("index", arguments[0]));
                if (position < 0)
                {
                    throw Fault("The substring was not found.", "ValueError");
                }

                return PythonWholeNumberValue.Create(position);
            }
        ),
        ["count"] = Text(
            "count",
            1,
            1,
            (text, arguments) =>
                PythonWholeNumberValue.Create(CountText(text, RequireText("count", arguments[0])))
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
            0,
            (list, _) =>
            {
                List<PythonValue> sorted;
                try
                {
                    sorted = list
                        .Elements.OrderBy(element => element, PythonOrderingComparer.Instance)
                        .ToList();
                }
                catch (InvalidOperationException exception)
                    when (exception.InnerException is PythonRuntimeException fault)
                {
                    throw fault;
                }

                list.Elements.Clear();
                list.Elements.AddRange(sorted);
                return PythonNoneValue.Instance;
            }
        ),
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

                throw Fault("The dictionary key was not found.", "KeyError");
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
        ["update"] = Dictionary(
            "update",
            1,
            1,
            (dictionary, arguments) =>
            {
                if (arguments[0] is not PythonDictionaryValue other)
                {
                    throw Fault(
                        "Method 'update' requires a dictionary argument in this runtime slice.",
                        "TypeError"
                    );
                }

                foreach (var item in other.Items.ToArray())
                {
                    ManagedObjectProtocols.SetDictionaryItem(
                        dictionary,
                        item.Key,
                        item.Value,
                        default
                    );
                }

                return PythonNoneValue.Instance;
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
                        item.Value
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
                    throw Fault("The set element was not found.", "KeyError");
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
            PythonSetValue => SetMethods,
            _ => null,
        };
        if (table is not null && table.TryGetValue(name, out var found))
        {
            method = found;
            return true;
        }

        method = null!;
        return false;
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
        string[] parts;
        if (arguments.Count == 0)
        {
            parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            var separator = RequireText("split", arguments[0]);
            if (separator.Length == 0)
            {
                throw Fault("The separator cannot be empty.", "ValueError");
            }

            parts = text.Split(separator, StringSplitOptions.None);
        }

        return new PythonListValue([
            .. parts.Select(part => (PythonValue)new PythonTextValue(part)),
        ]);
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
