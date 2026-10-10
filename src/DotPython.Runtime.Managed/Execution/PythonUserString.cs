// The `collections.UserString` surface follows CPython 3.14.7 Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.UserString</c> type: an immutable sequence that keeps a string in its
/// <c>data</c> attribute and answers the string methods over it.
/// </summary>
/// <remarks>
/// The source spells out the whole surface, so this does too: every method asks the store for
/// the string method of the same name — the runtime's own `str` implementation — and wraps the
/// answer in the receiver's class where the source does. A `UserString` argument is unwrapped
/// to its store only in the methods the source unwraps it in, which is why `"ab".endswith`
/// sees the wrapper itself.
/// </remarks>
internal static class PythonUserString
{
    private const string Owner = "UserString";

    /// <summary>One method the string provides, in the shape the source declares it.</summary>
    /// <param name="Wraps">Whether the answer is wrapped in the receiver's own class.</param>
    /// <param name="Unwraps">The parameters whose `UserString` argument is replaced by its
    /// store.</param>
    private sealed record Delegation(
        string Name,
        string[] Parameters,
        PythonValue?[] Defaults,
        bool Wraps,
        string[] Unwraps
    );

    /// <summary>The given defaults, shared by the table below.</summary>
    private static readonly PythonValue Zero = PythonWholeNumberValue.Create(0);
    private static readonly PythonValue MinusOne = PythonWholeNumberValue.Create(-1);
    private static readonly PythonValue Eight = PythonWholeNumberValue.Create(8);
    private static readonly PythonValue MaxSize = PythonWholeNumberValue.Create(long.MaxValue);

    private static readonly Delegation[] Delegations =
    [
        new("capitalize", [], [], true, []),
        new("casefold", [], [], true, []),
        new("count", ["sub", "start", "end"], [null, Zero, MaxSize], false, ["sub"]),
        new("endswith", ["suffix", "start", "end"], [null, Zero, MaxSize], false, []),
        new("expandtabs", ["tabsize"], [Eight], true, []),
        new("find", ["sub", "start", "end"], [null, Zero, MaxSize], false, ["sub"]),
        new("index", ["sub", "start", "end"], [null, Zero, MaxSize], false, ["sub"]),
        new("isalnum", [], [], false, []),
        new("isalpha", [], [], false, []),
        new("isascii", [], [], false, []),
        new("isdecimal", [], [], false, []),
        new("isdigit", [], [], false, []),
        new("isidentifier", [], [], false, []),
        new("islower", [], [], false, []),
        new("isnumeric", [], [], false, []),
        new("isprintable", [], [], false, []),
        new("isspace", [], [], false, []),
        new("istitle", [], [], false, []),
        new("isupper", [], [], false, []),
        new("join", ["seq"], [null], false, []),
        new("lower", [], [], true, []),
        new("lstrip", ["chars"], [PythonNoneValue.Instance], true, []),
        new("partition", ["sep"], [null], false, []),
        new("removeprefix", ["prefix"], [null], true, ["prefix"]),
        new("removesuffix", ["suffix"], [null], true, ["suffix"]),
        new("replace", ["old", "new", "maxsplit"], [null, null, MinusOne], true, ["old", "new"]),
        new("rfind", ["sub", "start", "end"], [null, Zero, MaxSize], false, ["sub"]),
        new("rindex", ["sub", "start", "end"], [null, Zero, MaxSize], false, ["sub"]),
        new("rpartition", ["sep"], [null], false, []),
        new("rsplit", ["sep", "maxsplit"], [PythonNoneValue.Instance, MinusOne], false, []),
        new("rstrip", ["chars"], [PythonNoneValue.Instance], true, []),
        new("split", ["sep", "maxsplit"], [PythonNoneValue.Instance, MinusOne], false, []),
        new("splitlines", ["keepends"], [PythonTruthValue.False], false, []),
        new("startswith", ["prefix", "start", "end"], [null, Zero, MaxSize], false, []),
        new("strip", ["chars"], [PythonNoneValue.Instance], true, []),
        new("swapcase", [], [], true, []),
        new("title", [], [], true, []),
        new("upper", [], [], true, []),
        new("zfill", ["width"], [null], true, []),
    ];

    /// <summary>The `collections.UserString` type object, built after the tables above it —
    /// a static field initializer runs in the order it is written.</summary>
    internal static readonly PythonManagedTypeValue Type = CreateType();

    private static PythonManagedTypeValue CreateType()
    {
        var sequence = PythonCollectionsAbc.Class("Sequence");
        var type = new PythonManagedTypeValue("UserString")
        {
            Module = "collections",
            QualName = "UserString",
        };
        type.SetDeclaredBases(new PythonTupleValue([sequence]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, .. PythonBuiltinTypes.GetMro(sequence).Elements])
        );
        type.Attributes["__module__"] = new PythonTextValue("collections");
        PythonUserTypes.MarkConcrete(type);
        type.Attributes["__doc__"] = new PythonTextValue(
            "A more or less complete user-defined wrapper around string objects.\n"
        );
        type.Attributes["__init__"] = Method("__init__", Initialize, parameters: ["seq"]);
        type.Attributes["__str__"] = Method(
            "__str__",
            (r, a) => Convert(r, a, PythonBuiltinTypes.Str)
        );
        type.Attributes["__repr__"] = Method("__repr__", Represent);
        type.Attributes["__int__"] = Method(
            "__int__",
            (r, a) => Convert(r, a, PythonBuiltinTypes.Int)
        );
        type.Attributes["__float__"] = Method(
            "__float__",
            (r, a) => Convert(r, a, PythonBuiltinTypes.Float)
        );
        type.Attributes["__complex__"] = Method(
            "__complex__",
            (r, a) => Convert(r, a, PythonBuiltinFunctions.Complex)
        );
        type.Attributes["__hash__"] = Method("__hash__", Hash);
        type.Attributes["__getnewargs__"] = Method("__getnewargs__", GetNewArguments);
        type.Attributes["__eq__"] = Method(
            "__eq__",
            (r, a) => Compare(r, a, PythonRichComparison.Equal)
        );
        type.Attributes["__lt__"] = Method(
            "__lt__",
            (r, a) => Compare(r, a, PythonRichComparison.LessThan)
        );
        type.Attributes["__le__"] = Method(
            "__le__",
            (r, a) => Compare(r, a, PythonRichComparison.LessThanOrEqual)
        );
        type.Attributes["__gt__"] = Method(
            "__gt__",
            (r, a) => Compare(r, a, PythonRichComparison.GreaterThan)
        );
        type.Attributes["__ge__"] = Method(
            "__ge__",
            (r, a) => Compare(r, a, PythonRichComparison.GreaterThanOrEqual)
        );
        type.Attributes["__contains__"] = Method("__contains__", Contains, parameters: ["char"]);
        type.Attributes["__len__"] = Method("__len__", Length);
        type.Attributes["__getitem__"] = Method("__getitem__", Get, parameters: ["index"]);
        type.Attributes["__add__"] = Method(
            "__add__",
            (r, a) => Concatenate(r, a, reflected: false),
            parameters: ["other"]
        );
        type.Attributes["__radd__"] = Method(
            "__radd__",
            (r, a) => Concatenate(r, a, reflected: true),
            parameters: ["other"]
        );
        var multiply = Method("__mul__", Multiply, parameters: ["n"]);
        type.Attributes["__mul__"] = multiply;
        // `__rmul__ = __mul__` is the same function object under both names.
        type.Attributes["__rmul__"] = multiply;
        type.Attributes["__mod__"] = Method("__mod__", Modulate, parameters: ["args"]);
        type.Attributes["__rmod__"] = Method(
            "__rmod__",
            ModulateReflected,
            parameters: ["template"]
        );
        // These four declare a variadic part (`center(width, *args)`), so their extra
        // arguments pass through untouched and their answer is wrapped.
        type.Attributes["center"] = Method("center", (r, a) => Wrapped(r, a, "center"));
        type.Attributes["ljust"] = Method("ljust", (r, a) => Wrapped(r, a, "ljust"));
        type.Attributes["rjust"] = Method("rjust", (r, a) => Wrapped(r, a, "rjust"));
        type.Attributes["translate"] = Method("translate", (r, a) => Wrapped(r, a, "translate"));
        type.Attributes["encode"] = Method(
            "encode",
            Encode,
            parameters: ["encoding", "errors"],
            defaults: [new PythonTextValue("utf-8"), new PythonTextValue("strict")]
        );
        type.Attributes["format_map"] = Method("format_map", FormatMap, parameters: ["mapping"]);
        type.Attributes["format"] = Method(
            "format",
            (receiver, arguments) => Format(receiver, arguments, [], []),
            FormatWithKeywords
        );
        // `maketrans = str.maketrans` is the string's own function object.
        type.Attributes["maketrans"] = ManagedObjectProtocols.GetAttribute(
            PythonBuiltinTypes.Str,
            "maketrans"
        );
        foreach (var delegation in Delegations)
        {
            var method = delegation;
            type.Attributes[method.Name] = Method(
                method.Name,
                (receiver, arguments) => Delegate(receiver, arguments, method),
                parameters: method.Parameters,
                defaults: method.Defaults
            );
        }
        return type;
    }

    private static PythonProtocolFunctionValue Method(
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    ) => PythonUserTypes.Method(Owner, name, body, keywords, parameters, defaults);

    /// <summary>`UserString(seq)`: a string keeps itself, another wrapper contributes its
    /// store, and anything else is converted with `str()`.</summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"UserString() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        PythonValue sequence = rest[0];
        PythonUserTypes.SetData(
            self,
            sequence switch
            {
                PythonTextValue text => text,
                _ when PythonUserTypes.IsInstance(sequence, Type) => PythonUserTypes.Data(sequence),
                _ => PythonUserTypes.Dispatched(PythonBuiltinTypes.Str, [sequence]),
            }
        );
        return PythonNoneValue.Instance;
    }

    /// <summary>`str(self)` and the three numeric conversions, over the store.</summary>
    private static PythonValue Convert(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        PythonValue type
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonUserTypes.Dispatched(type, [PythonUserTypes.Data(self)]);
    }

    /// <summary>`repr(self)` is the store's own representation.</summary>
    private static PythonValue Represent(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return new PythonTextValue(PythonUserTypes.Data(self).ToRepresentationString());
    }

    /// <summary>`hash(self)` is the store's hash.</summary>
    private static PythonValue Hash(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonWholeNumberValue.Create(
            ManagedObjectProtocols.GetPythonHash(PythonUserTypes.Data(self))
        );
    }

    /// <summary>`__getnewargs__`: the store as a full slice of itself, which is what the copy
    /// and pickle protocols carry.</summary>
    private static PythonValue GetNewArguments(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var data = PythonUserTypes.Data(self);
        return new PythonTupleValue([ManagedObjectProtocols.GetItem(data, FullSlice())]);
    }

    private static PythonSliceValue FullSlice() =>
        new(PythonNoneValue.Instance, PythonNoneValue.Instance, PythonNoneValue.Instance);

    /// <summary>One of the comparisons: another wrapper is compared by its store, and
    /// anything else as it stands.</summary>
    private static PythonValue Compare(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        PythonRichComparison comparison
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"comparison takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        return ManagedObjectProtocols.RichCompareValue(
            PythonUserTypes.Data(self),
            CastValue(rest[0]),
            comparison
        );
    }

    /// <summary>A `UserString` operand compares as its store.</summary>
    private static PythonValue CastValue(PythonValue other) =>
        PythonUserTypes.IsInstance(other, Type) ? PythonUserTypes.Data(other) : other;

    /// <summary>`char in self`, with a wrapper unwrapped first.</summary>
    private static PythonValue Contains(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__contains__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        return PythonUserTypes.Contains(PythonUserTypes.Data(self), CastValue(rest[0]))
            ? PythonTruthValue.True
            : PythonTruthValue.False;
    }

    private static PythonValue Length(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonUserTypes.Length(PythonUserTypes.Data(self));
    }

    /// <summary>`self[index]` answers a new instance of the receiver's own class, over the
    /// string the store answered — which is why a slice comes back wrapped and a character
    /// does too.</summary>
    private static PythonValue Get(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__getitem__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        return PythonUserTypes.Construct(
            PythonUserTypes.ClassOf(self),
            [ManagedObjectProtocols.GetItem(PythonUserTypes.Data(self), rest[0])]
        );
    }

    /// <summary>`self + other`: another wrapper contributes its store, a string stands as it
    /// is, and anything else is converted with `str()`.</summary>
    private static PythonValue Concatenate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool reflected
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__add__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        var data = PythonUserTypes.Data(self);
        var other = rest[0];
        PythonValue theirs =
            PythonUserTypes.IsInstance(other, Type) ? PythonUserTypes.Data(other)
            : other is PythonTextValue ? other
            : PythonUserTypes.Dispatched(PythonBuiltinTypes.Str, [other]);
        var joined = reflected
            ? PythonUserTypes.InvokeOn(theirs, "__add__", [data])
            : PythonUserTypes.InvokeOn(data, "__add__", [theirs]);
        return PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [joined]);
    }

    /// <summary>`self * n` answers a new instance, whichever side the count is on.</summary>
    private static PythonValue Multiply(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__mul__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        var multiplied = PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "__mul__", [rest[0]]);
        return PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [multiplied]);
    }

    /// <summary>`self % args` formats the store and wraps the answer.</summary>
    private static PythonValue Modulate(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__mod__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        var formatted = PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "__mod__", [rest[0]]);
        return PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [formatted]);
    }

    /// <summary>`template % self`: the format is converted with `str()` and the wrapper is
    /// left for the format itself to see.</summary>
    private static PythonValue ModulateReflected(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__rmod__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        var template = (PythonTextValue)
            PythonUserTypes.Dispatched(PythonBuiltinTypes.Str, [rest[0]]);
        // The format runs over the wrapper itself rather than through `%`, which would reach
        // this method again before the string's own formatting had a chance to answer. A
        // string template answers with a plain string, which is the answer `str.__mod__`
        // would have given had the reflected method not been reached first.
        var formatted = new PythonTextValue(
            PythonTextFormatting.FormatPercent(template.Value, self, default)
        );
        return rest[0] is PythonTextValue
            ? formatted
            : PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [formatted]);
    }

    /// <summary>One of the declared string methods.</summary>
    private static PythonValue Delegate(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        Delegation delegation
    ) =>
        Delegated(
            receiver,
            arguments,
            delegation.Name,
            delegation.Wraps,
            delegation.Parameters,
            delegation.Unwraps
        );

    /// <summary>One of the variadic string methods.</summary>
    private static PythonValue Wrapped(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        string name
    ) => Delegated(receiver, arguments, name, Wraps: true, [], []);

    /// <summary>
    /// The store answers its own method of that name with the same arguments — a
    /// `UserString` argument replaced by its store in the parameters the source unwraps —
    /// and the answer is wrapped in the receiver's class where the source wraps it.
    /// </summary>
    private static PythonValue Delegated(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        string name,
        bool Wraps,
        string[] parameters,
        string[] unwraps
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var forwarded = new PythonValue[rest.Count];
        for (var index = 0; index < rest.Count; index++)
            forwarded[index] = rest[index];
        foreach (var parameter in unwraps)
        {
            var position = Array.IndexOf(parameters, parameter);
            if (position >= 0 && position < forwarded.Length)
                forwarded[position] = CastValue(forwarded[position]);
        }
        var answered = PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), name, forwarded);
        return Wraps
            ? PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [answered])
            : answered;
    }

    /// <summary>`encode(encoding='utf-8', errors='strict')`, with `None` taken as the
    /// default — the answers are bytes, so nothing is wrapped.</summary>
    private static PythonValue Encode(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var encoding =
            rest.Count > 0 && rest[0] is not PythonNoneValue
                ? rest[0]
                : new PythonTextValue("utf-8");
        var errors =
            rest.Count > 1 && rest[1] is not PythonNoneValue
                ? rest[1]
                : new PythonTextValue("strict");
        return PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "encode", [encoding, errors]);
    }

    /// <summary>`format_map(mapping)` formats the store.</summary>
    private static PythonValue FormatMap(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"format_map() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        return PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "format_map", [rest[0]]);
    }

    /// <summary>`format(*args, **kwds)` formats the store, answering a plain string — the
    /// source wraps nothing here.</summary>
    private static PythonValue Format(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, positional);
        var data = PythonUserTypes.Data(self);
        if (keywordNames.Count == 0)
            return PythonUserTypes.InvokeOn(data, "format", rest);
        return UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
            ManagedObjectProtocols.GetAttribute(data, "format"),
            rest,
            keywordNames,
            keywordValues,
            default
        );
    }

    private static PythonValue FormatWithKeywords(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    ) => Format(receiver, positional, keywordNames, keywordValues);
}
