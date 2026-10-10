// The `collections.UserDict` surface follows CPython 3.14.7 Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.UserDict</c> type: a mapping that keeps an ordinary dictionary in its
/// <c>data</c> attribute, so a subclass has a dictionary to build on without being one.
/// </summary>
/// <remarks>
/// Everything the Python source does not spell out — <c>update</c>, <c>pop</c>,
/// <c>setdefault</c>, <c>keys</c>, <c>items</c>, <c>values</c> and equality — comes from the
/// <c>MutableMapping</c> mixins, which is exactly what CPython's own class relies on.
/// </remarks>
internal static class PythonUserDict
{
    private const string Owner = "UserDict";

    /// <summary>The `collections.UserDict` type object.</summary>
    internal static readonly PythonManagedTypeValue Type = CreateType();

    private static PythonManagedTypeValue CreateType()
    {
        var mapping = PythonCollectionsAbc.Class("MutableMapping");
        var type = new PythonManagedTypeValue("UserDict")
        {
            Module = "collections",
            QualName = "UserDict",
        };
        type.SetDeclaredBases(new PythonTupleValue([mapping]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, .. PythonBuiltinTypes.GetMro(mapping).Elements])
        );
        type.Attributes["__module__"] = new PythonTextValue("collections");
        PythonUserTypes.MarkConcrete(type);
        type.Attributes["__doc__"] = new PythonTextValue(
            "Dictionary wrapper with a store of its own.\n"
        );
        type.Attributes["__init__"] = Method(
            "__init__",
            (receiver, arguments) => Initialize(receiver, arguments, [], []),
            InitializeWithKeywords
        );
        type.Attributes["__len__"] = Method("__len__", Length);
        type.Attributes["__getitem__"] = Method("__getitem__", Get);
        type.Attributes["__setitem__"] = Method("__setitem__", Set);
        type.Attributes["__delitem__"] = Method("__delitem__", Delete);
        type.Attributes["__iter__"] = Method("__iter__", Iterate);
        type.Attributes["__contains__"] = Method("__contains__", ContainsKey);
        type.Attributes["get"] = Method(
            "get",
            GetOrDefault,
            parameters: ["key", "default"],
            defaults: [null, PythonNoneValue.Instance]
        );
        type.Attributes["__repr__"] = Method("__repr__", Represent);
        type.Attributes["__or__"] = Method(
            "__or__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: false)
        );
        type.Attributes["__ror__"] = Method(
            "__ror__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: true)
        );
        type.Attributes["__ior__"] = Method("__ior__", UpdateInPlace);
        type.Attributes["__copy__"] = Method("__copy__", CopyInstance);
        type.Attributes["copy"] = Method("copy", Copy);
        type.Attributes["fromkeys"] = new PythonClassMethodValue(
            Method(
                "fromkeys",
                FromKeys,
                parameters: ["iterable", "value"],
                defaults: [null, PythonNoneValue.Instance]
            )
        );
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
    /// `UserDict(dict=None, /, **kwargs)`: an empty store, then whatever was given — the
    /// positional-only argument through `update`, the keywords as the mapping they are.
    /// </summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        PythonUserTypes.SetData(self, new PythonDictionaryValue([]));
        if (rest.Count > 0 && rest[0] is not PythonNoneValue)
            PythonUserTypes.InvokeOn(self, "update", [rest[0]]);
        if (keywordNames.Count != 0)
            PythonUserTypes.InvokeOn(
                self,
                "update",
                [PythonUserTypes.Keywords(keywordNames, keywordValues)]
            );
        return PythonNoneValue.Instance;
    }

    /// <summary>The keyword form: `dict` stays positional-only and everything else is
    /// collected by `**kwargs`.</summary>
    private static PythonValue InitializeWithKeywords(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, positional);
        if (rest.Count > 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"UserDict() takes at most 1 positional argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        return Initialize(self, rest, keywordNames, keywordValues);
    }

    /// <summary>`len(self)` is the length of the store.</summary>
    private static PythonValue Length(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonUserTypes.Length(PythonUserTypes.Data(self));
    }

    /// <summary>
    /// `self[key]`: a miss asks the class's own `__missing__` when it declares one, and is a
    /// `KeyError` carrying the key otherwise.
    /// </summary>
    private static PythonValue Get(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__getitem__", rest, 1);
        var data = PythonUserTypes.Data(self);
        if (PythonUserTypes.Contains(data, rest[0]))
            return ManagedObjectProtocols.GetItem(data, rest[0]);
        var type = PythonUserTypes.ClassOf(self);
        if (PythonUserTypes.HasAttribute(type, "__missing__"))
            return PythonUserTypes.Dispatched(
                ManagedObjectProtocols.GetAttribute(type, "__missing__"),
                [self, rest[0]]
            );
        throw ManagedObjectProtocols.MissingKey(rest[0]);
    }

    /// <summary>`self[key] = item`.</summary>
    private static PythonValue Set(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__setitem__", rest, 2);
        ManagedObjectProtocols.SetItem(PythonUserTypes.Data(self), rest[0], rest[1]);
        return PythonNoneValue.Instance;
    }

    /// <summary>`del self[key]`.</summary>
    private static PythonValue Delete(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__delitem__", rest, 1);
        ManagedObjectProtocols.DeleteItem(PythonUserTypes.Data(self), rest[0]);
        return PythonNoneValue.Instance;
    }

    /// <summary>`iter(self)` walks the store, which is what makes the mixin's `update`,
    /// `pop` and `popitem` see the same keys the dictionary does.</summary>
    private static PythonValue Iterate(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return ManagedObjectProtocols.GetIterator(PythonUserTypes.Data(self));
    }

    /// <summary>`key in self` asks the store, so a `__missing__` a subclass declares does
    /// not turn every miss into a hit — the rule `dict` itself follows.</summary>
    private static PythonValue ContainsKey(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__contains__", rest, 1);
        return PythonUserTypes.Contains(PythonUserTypes.Data(self), rest[0])
            ? PythonTruthValue.True
            : PythonTruthValue.False;
    }

    /// <summary>`get(key, default=None)`: the store's answer, never the `__missing__` one.
    /// </summary>
    private static PythonValue GetOrDefault(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "get() missing required argument 'key' (pos 1)",
                default,
                "TypeError"
            );
        var data = PythonUserTypes.Data(self);
        if (PythonUserTypes.Contains(data, rest[0]))
            return ManagedObjectProtocols.GetItem(data, rest[0]);
        return rest.Count > 1 ? rest[1] : PythonNoneValue.Instance;
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

    /// <summary>
    /// `self | other` and its reflection: a `UserDict` or a `dict` merges the two stores —
    /// the receiver's own order in front for `|`, the operand's for `|` reflected — and
    /// anything else is `NotImplemented`.
    /// </summary>
    private static PythonValue Merge(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool reflected
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__or__", rest, 1);
        var other = rest[0];
        var own = PythonUserTypes.Data(self);
        var theirs =
            PythonUserTypes.IsInstance(other, Type) ? PythonUserTypes.Data(other)
            : PythonUserTypes.IsDictionary(other) ? other
            : null;
        if (theirs is null)
            return PythonNotImplementedValue.Instance;
        var merged = reflected
            ? PythonUserTypes.DictionaryUnion(theirs, own)
            : PythonUserTypes.DictionaryUnion(own, theirs);
        PythonValue type = PythonUserTypes.ClassOf(self);
        return PythonUserTypes.Construct(type, [merged]);
    }

    /// <summary>`self |= other` merges into the store in place, keeping the receiver.</summary>
    private static PythonValue UpdateInPlace(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        RequireArguments("__ior__", rest, 1);
        var other = PythonUserTypes.IsInstance(rest[0], Type)
            ? PythonUserTypes.Data(rest[0])
            : rest[0];
        PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "__ior__", [other]);
        return self;
    }

    /// <summary>
    /// `__copy__`: a new instance that takes the copy's attributes from the original, with the
    /// store replaced by a copy of its own — the source's `data` is left alone.
    /// </summary>
    private static PythonValue CopyInstance(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var source = (PythonManagedObjectValue)self;
        var copy = PythonUserTypes.Allocate(PythonUserTypes.ClassOf(self));
        PythonUserTypes.CopyAttributes(source, copy);
        PythonUserTypes.SetData(
            copy,
            PythonUserTypes.InvokeOn(PythonUserTypes.Data(self), "copy", [])
        );
        return copy;
    }

    /// <summary>
    /// `copy()`: a plain `UserDict` copies its store, while a subclass is copied with an empty
    /// store and then refilled through its own `__setitem__` — which is what the source's
    /// dance around `copy.copy` is for.
    /// </summary>
    private static PythonValue Copy(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var data = PythonUserTypes.Data(self);
        if (ReferenceEquals(PythonUserTypes.ClassOf(self), Type))
            return PythonUserTypes.Construct(Type, [PythonUserTypes.InvokeOn(data, "copy", [])]);
        PythonUserTypes.SetData(self, new PythonDictionaryValue([]));
        PythonValue copy;
        try
        {
            copy = PythonUserTypes.InvokeOn(self, "__copy__", []);
        }
        finally
        {
            PythonUserTypes.SetData(self, data);
        }
        PythonUserTypes.InvokeOn(copy, "update", [self]);
        return copy;
    }

    /// <summary>`UserDict.fromkeys(iterable, value=None)`: a new instance filled key by key,
    /// through the class's own assignment.</summary>
    private static PythonValue FromKeys(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (cls, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "fromkeys() missing required argument 'iterable' (pos 1)",
                default,
                "TypeError"
            );
        var value = rest.Count > 1 ? rest[1] : PythonNoneValue.Instance;
        var created = PythonUserTypes.Construct(cls, []);
        var iterator = ManagedObjectProtocols.GetIterator(rest[0]);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var key, default))
            ManagedObjectProtocols.SetItem(created, key, value);
        return created;
    }

    /// <summary>A method whose source body indexes its arguments checks their count first.</summary>
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
