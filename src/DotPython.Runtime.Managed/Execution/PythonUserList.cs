// The `collections.UserList` surface follows CPython 3.14.7 Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.UserList</c> type: a mutable sequence that keeps an ordinary list in
/// its <c>data</c> attribute and forwards everything to it.
/// </summary>
/// <remarks>
/// The methods the Python source spells out replace the <c>MutableSequence</c> mixins they
/// shadow — <c>append</c>, <c>pop</c>, <c>remove</c>, <c>reverse</c>, <c>extend</c>,
/// <c>index</c> and <c>count</c> — while <c>__iter__</c>, <c>__reversed__</c>,
/// <c>__contains__</c> and the in-place addition come from the mixins, as in CPython.
/// </remarks>
internal static class PythonUserList
{
    private const string Owner = "UserList";

    /// <summary>The `collections.UserList` type object.</summary>
    internal static readonly PythonManagedTypeValue Type = CreateType();

    private static PythonManagedTypeValue CreateType()
    {
        var sequence = PythonCollectionsAbc.Class("MutableSequence");
        var type = new PythonManagedTypeValue("UserList")
        {
            Module = "collections",
            QualName = "UserList",
        };
        type.SetDeclaredBases(new PythonTupleValue([sequence]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, .. PythonBuiltinTypes.GetMro(sequence).Elements])
        );
        type.Attributes["__module__"] = new PythonTextValue("collections");
        PythonUserTypes.MarkConcrete(type);
        type.Attributes["__doc__"] = new PythonTextValue(
            "A more or less complete user-defined wrapper around list objects.\n"
        );
        type.Attributes["__init__"] = Method(
            "__init__",
            (receiver, arguments) => Initialize(receiver, arguments),
            parameters: ["initlist"],
            defaults: [PythonNoneValue.Instance]
        );
        type.Attributes["__repr__"] = Method("__repr__", Represent);
        type.Attributes["__lt__"] = Method(
            "__lt__",
            (r, a) => Compare(r, a, PythonRichComparison.LessThan)
        );
        type.Attributes["__le__"] = Method(
            "__le__",
            (r, a) => Compare(r, a, PythonRichComparison.LessThanOrEqual)
        );
        type.Attributes["__eq__"] = Method(
            "__eq__",
            (r, a) => Compare(r, a, PythonRichComparison.Equal)
        );
        type.Attributes["__gt__"] = Method(
            "__gt__",
            (r, a) => Compare(r, a, PythonRichComparison.GreaterThan)
        );
        type.Attributes["__ge__"] = Method(
            "__ge__",
            (r, a) => Compare(r, a, PythonRichComparison.GreaterThanOrEqual)
        );
        // The private helper the comparisons cast through, name-mangled as the source has it.
        type.Attributes["_UserList__cast"] = Method("_UserList__cast", Cast);
        type.Attributes["__contains__"] = Method("__contains__", Contains);
        type.Attributes["__len__"] = Method("__len__", Length);
        type.Attributes["__getitem__"] = Method("__getitem__", Get);
        type.Attributes["__setitem__"] = Method("__setitem__", Set);
        type.Attributes["__delitem__"] = Method("__delitem__", Delete);
        type.Attributes["__add__"] = Method("__add__", (r, a) => Combine(r, a, reflected: false));
        type.Attributes["__radd__"] = Method("__radd__", (r, a) => Combine(r, a, reflected: true));
        type.Attributes["__iadd__"] = Method("__iadd__", AddInPlace);
        type.Attributes["__mul__"] = Method("__mul__", Multiply);
        type.Attributes["__rmul__"] = Method("__mul__", Multiply);
        type.Attributes["__imul__"] = Method("__imul__", MultiplyInPlace);
        type.Attributes["__copy__"] = Method("__copy__", CopyInstance);
        type.Attributes["append"] = Method("append", Append, parameters: ["item"]);
        type.Attributes["insert"] = Method("insert", Insert, parameters: ["index", "item"]);
        type.Attributes["pop"] = Method(
            "pop",
            Pop,
            parameters: ["index"],
            defaults: [PythonWholeNumberValue.Create(-1)]
        );
        type.Attributes["remove"] = Method("remove", Remove, parameters: ["item"]);
        type.Attributes["clear"] = Method("clear", Clear);
        type.Attributes["copy"] = Method("copy", Copy);
        type.Attributes["count"] = Method("count", Count, parameters: ["item"]);
        type.Attributes["index"] = Method("index", Index, parameters: ["item"]);
        type.Attributes["reverse"] = Method("reverse", Reverse);
        type.Attributes["sort"] = Method("sort", Sort, SortWithKeywords);
        type.Attributes["extend"] = Method("extend", Extend, parameters: ["other"]);
        // A class that defines `__eq__` without `__hash__` is unhashable, which is the value
        // `None` sitting in the class dictionary.
        type.Attributes["__hash__"] = PythonNoneValue.Instance;
        return type;
    }

    private static PythonProtocolFunctionValue Method(
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    ) => PythonUserTypes.Method(Owner, name, body, keywords, parameters, defaults);

    /// <summary>
    /// `UserList(initlist=None)`: a list of its own, filled from a list in place and from
    /// anything else through `list()`.
    /// </summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var data = new PythonListValue([]);
        PythonUserTypes.SetData(self, data);
        var initial = rest.Count > 0 ? rest[0] : PythonNoneValue.Instance;
        switch (initial)
        {
            case PythonNoneValue:
                break;
            case PythonListValue list when list.GetType() == typeof(PythonListValue):
                data.Elements.AddRange(list.Elements);
                break;
            default:
                if (PythonUserTypes.IsInstance(initial, Type))
                {
                    var source = PythonUserTypes.Data(initial) as PythonListValue;
                    data.Elements.AddRange(source?.Elements ?? []);
                }
                else
                {
                    foreach (
                        var element in ManagedObjectProtocols.MaterializeValues(initial, default)
                    )
                        data.Elements.Add(element);
                }
                break;
        }
        return PythonNoneValue.Instance;
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

    /// <summary>One of the five comparisons, over the store and the cast operand.</summary>
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
                $"comparison takes exactly one argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        return ManagedObjectProtocols.RichCompareValue(
            PythonUserTypes.Data(self),
            CastValue(rest[0]),
            comparison
        );
    }

    /// <summary>`self.__cast(other)`: another wrapper is compared by its store.</summary>
    private static PythonValue Cast(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        return rest.Count == 0 ? PythonNoneValue.Instance : CastValue(rest[0]);
    }

    private static PythonValue CastValue(PythonValue other) =>
        PythonUserTypes.IsInstance(other, Type) ? PythonUserTypes.Data(other) : other;

    /// <summary>`item in self` asks the store.</summary>
    private static PythonValue Contains(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        return rest.Count == 1 && PythonUserTypes.Contains(PythonUserTypes.Data(self), rest[0])
            ? PythonTruthValue.True
            : PythonTruthValue.False;
    }

    /// <summary>`len(self)` is the store's length.</summary>
    private static PythonValue Length(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonUserTypes.Length(PythonUserTypes.Data(self));
    }

    /// <summary>`self[i]`: a slice answers a new instance of the receiver's own class.</summary>
    private static PythonValue Get(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__getitem__", rest, 1);
        var data = PythonUserTypes.Data(self);
        if (rest[0] is PythonSliceValue)
            return PythonUserTypes.Construct(
                PythonUserTypes.ClassOf(self),
                [ManagedObjectProtocols.GetItem(data, rest[0])]
            );
        return ManagedObjectProtocols.GetItem(data, rest[0]);
    }

    /// <summary>`self[i] = item`.</summary>
    private static PythonValue Set(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__setitem__", rest, 2);
        ManagedObjectProtocols.SetItem(PythonUserTypes.Data(self), rest[0], rest[1]);
        return PythonNoneValue.Instance;
    }

    /// <summary>`del self[i]`.</summary>
    private static PythonValue Delete(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__delitem__", rest, 1);
        ManagedObjectProtocols.DeleteItem(PythonUserTypes.Data(self), rest[0]);
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// `self + other` and its reflection: another wrapper contributes its store, a list its
    /// own contents, and anything else is walked through `list()`.
    /// </summary>
    private static PythonValue Combine(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool reflected
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__add__", rest, 1);
        var other = rest[0];
        var data = PythonUserTypes.Data(self);
        PythonValue theirs =
            PythonUserTypes.IsInstance(other, Type) ? PythonUserTypes.Data(other)
            : PythonBuiltinTypes.IsInstance(other, PythonBuiltinTypes.List) ? other
            : PythonUserTypes.Dispatched(PythonBuiltinTypes.List, [other]);
        var combined = reflected
            ? PythonUserTypes.InvokeOn(theirs, "__add__", [data])
            : PythonUserTypes.InvokeOn(data, "__add__", [theirs]);
        return PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [combined]);
    }

    /// <summary>`self += other` extends the store in place and keeps the receiver.</summary>
    private static PythonValue AddInPlace(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__iadd__", rest, 1);
        var data = PythonUserTypes.Data(self);
        var other = PythonUserTypes.IsInstance(rest[0], Type)
            ? PythonUserTypes.Data(rest[0])
            : rest[0];
        PythonUserTypes.InvokeOn(data, "__iadd__", [other]);
        return self;
    }

    /// <summary>`self * n` answers a new instance; the reflection multiplies the other way.</summary>
    private static PythonValue Multiply(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__mul__", rest, 1);
        var multiplied = PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "__mul__", [rest[0]]);
        return PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [multiplied]);
    }

    /// <summary>`self *= n` multiplies the store in place.</summary>
    private static PythonValue MultiplyInPlace(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__imul__", rest, 1);
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "__imul__", [rest[0]]);
        return self;
    }

    /// <summary>`__copy__`: a new instance with the store copied and the other attributes
    /// taken from the original.</summary>
    private static PythonValue CopyInstance(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var copy = PythonUserTypes.Allocate(PythonUserTypes.ClassOf(self));
        PythonUserTypes.CopyAttributes((PythonManagedObjectValue)self, copy);
        PythonUserTypes.SetData(
            copy,
            PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "copy", [])
        );
        return copy;
    }

    /// <summary>`copy()` answers a new instance of the receiver's class holding the store's
    /// contents.</summary>
    private static PythonValue Copy(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [self]);
    }

    private static PythonValue Append(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("append", rest, 1);
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "append", [rest[0]]);
        return PythonNoneValue.Instance;
    }

    private static PythonValue Insert(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("insert", rest, 2);
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "insert", rest);
        return PythonNoneValue.Instance;
    }

    private static PythonValue Pop(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var index = rest.Count > 0 ? rest[0] : PythonWholeNumberValue.Create(-1);
        return PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "pop", [index]);
    }

    private static PythonValue Remove(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("remove", rest, 1);
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "remove", [rest[0]]);
        return PythonNoneValue.Instance;
    }

    private static PythonValue Clear(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "clear", []);
        return PythonNoneValue.Instance;
    }

    private static PythonValue Count(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("count", rest, 1);
        return PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "count", [rest[0]]);
    }

    private static PythonValue Index(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "index() missing required argument 'item' (pos 1)",
                default,
                "TypeError"
            );
        return PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "index", rest);
    }

    private static PythonValue Reverse(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "reverse", []);
        return PythonNoneValue.Instance;
    }

    /// <summary>`sort(*args, **kwds)` sorts the store with the list's own ordering.</summary>
    private static PythonValue Sort(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "sort", rest);
        return PythonNoneValue.Instance;
    }

    private static PythonValue SortWithKeywords(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, positional);
        // The store's own sort takes the keys the keyword call carried, so the call is
        // handed to it whole rather than through the positional-only helper.
        UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
            ManagedObjectProtocols.GetAttribute(PythonUserTypes.Data(self), "sort"),
            rest,
            keywordNames,
            keywordValues,
            default
        );
        return PythonNoneValue.Instance;
    }

    private static PythonValue Extend(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("extend", rest, 1);
        var other = PythonUserTypes.IsInstance(rest[0], Type)
            ? PythonUserTypes.Data(rest[0])
            : rest[0];
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "extend", [other]);
        return PythonNoneValue.Instance;
    }

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int count
    )
    {
        if (arguments.Count != count)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() takes exactly {count} argument{(count == 1 ? "" : "s")} "
                    + $"({arguments.Count} given)",
                default,
                "TypeError"
            );
    }
}
