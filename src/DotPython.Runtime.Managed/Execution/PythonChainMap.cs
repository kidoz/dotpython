// The `collections.ChainMap` surface follows CPython 3.14.7 Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.ChainMap</c> type: a mapping that searches a list of mappings in turn,
/// keeping that list in its <c>maps</c> attribute, and writes only to the first of them.
/// </summary>
/// <remarks>
/// What the Python source does not spell out comes from the <c>MutableMapping</c> mixins —
/// <c>keys</c>, <c>items</c>, <c>values</c>, <c>update</c>, <c>setdefault</c> and the
/// equality — and every lookup goes through the mappings themselves, so a mapping that
/// raises something other than `KeyError` stops the walk, as it does in CPython.
/// </remarks>
internal static class PythonChainMap
{
    private const string Owner = "ChainMap";

    /// <summary>The `collections.ChainMap` type object.</summary>
    internal static readonly PythonManagedTypeValue Type = CreateType();

    private static PythonManagedTypeValue CreateType()
    {
        var mapping = PythonCollectionsAbc.Class("MutableMapping");
        var type = new PythonManagedTypeValue("ChainMap")
        {
            Module = "collections",
            QualName = "ChainMap",
        };
        type.SetDeclaredBases(new PythonTupleValue([mapping]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, .. PythonBuiltinTypes.GetMro(mapping).Elements])
        );
        type.Attributes["__module__"] = new PythonTextValue("collections");
        PythonUserTypes.MarkConcrete(type);
        type.Attributes["__doc__"] = new PythonTextValue(
            "A ChainMap groups multiple dicts (or other mappings) together to create a single,\n"
                + "updateable view.\n"
        );
        type.Attributes["__init__"] = Method(
            "__init__",
            (receiver, arguments) => Initialize(receiver, arguments),
            (receiver, positional, names, _) =>
            {
                if (names.Count != 0)
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"__init__() got an unexpected keyword argument '{names[0]}'",
                        default,
                        "TypeError"
                    );
                return Initialize(PythonUserTypes.Bound(receiver, positional).Self, positional);
            }
        );
        type.Attributes["__missing__"] = Method("__missing__", Missing);
        type.Attributes["__getitem__"] = Method("__getitem__", Get);
        type.Attributes["get"] = Method(
            "get",
            GetOrDefault,
            parameters: ["key", "default"],
            defaults: [null, PythonNoneValue.Instance]
        );
        type.Attributes["__len__"] = Method("__len__", Length);
        type.Attributes["__iter__"] = Method("__iter__", Iterate);
        type.Attributes["__contains__"] = Method("__contains__", ContainsKey);
        type.Attributes["__bool__"] = Method("__bool__", IsTrue);
        type.Attributes["__repr__"] = Method("__repr__", Represent);
        type.Attributes["fromkeys"] = new PythonClassMethodValue(
            Method(
                "fromkeys",
                FromKeys,
                parameters: ["iterable", "value"],
                defaults: [null, PythonNoneValue.Instance]
            )
        );
        var copy = Method("copy", Copy);
        type.Attributes["copy"] = copy;
        // `__copy__ = copy` is the same function object under both names.
        type.Attributes["__copy__"] = copy;
        type.Attributes["new_child"] = Method("new_child", NewChild, NewChildWithKeywords);
        type.Attributes["parents"] = new PythonPropertyValue(
            Method("parents", Parents),
            null,
            null
        );
        type.Attributes["__setitem__"] = Method("__setitem__", Set);
        type.Attributes["__delitem__"] = Method("__delitem__", Delete);
        type.Attributes["popitem"] = Method("popitem", PopItem);
        type.Attributes["pop"] = Method("pop", Pop);
        type.Attributes["clear"] = Method("clear", Clear);
        type.Attributes["__ior__"] = Method("__ior__", MergeInPlace);
        type.Attributes["__or__"] = Method(
            "__or__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: false)
        );
        type.Attributes["__ror__"] = Method(
            "__ror__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: true)
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

    /// <summary>`ChainMap(*maps)`: the mappings as they were given, and an empty one when
    /// none was — the list is never empty.</summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var maps = new PythonListValue([.. rest]);
        if (rest.Count == 0)
            maps.Elements.Add(new PythonDictionaryValue([]));
        PythonUserTypes.SetMaps(self, maps);
        return PythonNoneValue.Instance;
    }

    /// <summary>`__missing__(key)`: the chain itself has no answer for a key no mapping
    /// carries, and a subclass may give one.</summary>
    private static PythonValue Missing(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (_, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__missing__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        throw ManagedObjectProtocols.MissingKey(rest[0]);
    }

    /// <summary>
    /// `self[key]`: each mapping is asked in turn — a `KeyError` moves on, anything else it
    /// raises stops the walk — and a key no mapping carries reaches `__missing__`.
    /// </summary>
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
        var iterator = PythonUserTypes.Iterate(PythonUserTypes.Maps(self));
        while (ManagedObjectProtocols.TryGetNext(iterator, out var mapping, default))
        {
            try
            {
                return ManagedObjectProtocols.GetItem(mapping, rest[0]);
            }
            catch (Exception error)
                when (PythonNamespaceMapping.IsPythonException(error, "KeyError")) { }
        }
        return PythonUserTypes.InvokeOn(self, "__missing__", [rest[0]]);
    }

    /// <summary>`get(key, default=None)`: the chain's answer when the key is anywhere in it,
    /// which is what makes it use `__contains__` rather than a miss.</summary>
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
        if (PythonUserTypes.Contains(self, rest[0]))
            return ManagedObjectProtocols.GetItem(self, rest[0]);
        return rest.Count > 1 ? rest[1] : PythonNoneValue.Instance;
    }

    /// <summary>`len(self)`: the keys the mappings hold between them, counted once each.</summary>
    private static PythonValue Length(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonUserTypes.Length(
            PythonUserTypes.Dispatched(PythonBuiltinTypes.Set, [Keys(self)])
        );
    }

    /// <summary>
    /// `iter(self)`: the mappings are walked last first, so the keys an earlier mapping adds
    /// to the chain come after the ones the later mappings already named.
    /// </summary>
    private static PythonValue Iterate(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var keys = new PythonDictionaryValue([]);
        foreach (var mapping in Reversed(PythonUserTypes.Maps(self)))
        {
            var iterator = ManagedObjectProtocols.GetIterator(mapping);
            while (ManagedObjectProtocols.TryGetNext(iterator, out var key, default))
            {
                if (!PythonUserTypes.Contains(keys, key))
                    ManagedObjectProtocols.SetItem(keys, key, PythonNoneValue.Instance);
            }
        }
        return ManagedObjectProtocols.GetIterator(keys);
    }

    /// <summary>`key in self`: the mappings are asked in order, through their own
    /// `__contains__`.</summary>
    private static PythonValue ContainsKey(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__contains__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        var iterator = PythonUserTypes.Iterate(PythonUserTypes.Maps(self));
        while (ManagedObjectProtocols.TryGetNext(iterator, out var mapping, default))
        {
            if (PythonUserTypes.Contains(mapping, rest[0]))
                return PythonTruthValue.True;
        }
        return PythonTruthValue.False;
    }

    /// <summary>`bool(self)`: a mapping that is not empty makes the chain true.</summary>
    private static PythonValue IsTrue(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var iterator = PythonUserTypes.Iterate(PythonUserTypes.Maps(self));
        while (ManagedObjectProtocols.TryGetNext(iterator, out var mapping, default))
        {
            if (ManagedObjectProtocols.IsTrue(mapping))
                return PythonTruthValue.True;
        }
        return PythonTruthValue.False;
    }

    /// <summary>`repr(self)` names the class the receiver belongs to, then the mappings.</summary>
    private static PythonValue Represent(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var name = PythonUserTypes.ClassOf(self) is PythonManagedTypeValue type
            ? type.Name
            : ManagedObjectProtocols.GetTypeName(self);
        var rendered = string.Join(
            ", ",
            Elements(PythonUserTypes.Maps(self)).Select(mapping => mapping.ToRepresentationString())
        );
        return new PythonTextValue($"{name}({rendered})");
    }

    /// <summary>`ChainMap.fromkeys(iterable, value=None, /)`: one mapping holding the keys.</summary>
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
        return PythonUserTypes.Construct(
            cls,
            [PythonUserTypes.InvokeOn(PythonBuiltinTypes.Dict, "fromkeys", [rest[0], value])]
        );
    }

    /// <summary>`copy()`: the first mapping is copied, the rest are shared, and the receiver's
    /// own class is what the copy is built as.</summary>
    private static PythonValue Copy(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var maps = Elements(PythonUserTypes.Maps(self));
        return PythonUserTypes.Construct(
            PythonUserTypes.ClassOf(self),
            [
                maps.Count == 0
                    ? new PythonDictionaryValue([])
                    : PythonUserTypes.InvokeOn(maps[0], "copy", []),
                .. maps.Skip(1),
            ]
        );
    }

    /// <summary>
    /// `new_child(m=None, **kwargs)`: a new chain with a mapping in front of all the old
    /// ones — the keywords become that mapping when none was given, and update it when one
    /// was.
    /// </summary>
    private static PythonValue NewChild(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var child =
            rest.Count > 0 && rest[0] is not PythonNoneValue
                ? rest[0]
                : new PythonDictionaryValue([]);
        return PythonUserTypes.Construct(
            PythonUserTypes.ClassOf(self),
            [child, .. Elements(PythonUserTypes.Maps(self))]
        );
    }

    private static PythonValue NewChildWithKeywords(
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
                $"new_child() takes at most 1 positional argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        var keywords = PythonUserTypes.Keywords(keywordNames, keywordValues);
        PythonValue? child = rest.Count == 0 || rest[0] is PythonNoneValue ? null : rest[0];
        if (child is null)
            return NewChild(self, keywordNames.Count == 0 ? [] : [keywords]);
        if (keywordNames.Count != 0)
            PythonUserTypes.InvokeOn(child, "update", [keywords]);
        return NewChild(self, [child]);
    }

    /// <summary>`parents`: the same chain without its first mapping.</summary>
    private static PythonValue Parents(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        return PythonUserTypes.Construct(
            PythonUserTypes.ClassOf(self),
            [.. Elements(PythonUserTypes.Maps(self)).Skip(1)]
        );
    }

    /// <summary>`self[key] = value` writes to the first mapping, and to no other.</summary>
    private static PythonValue Set(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 2)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__setitem__() takes exactly 2 arguments ({rest.Count} given)",
                default,
                "TypeError"
            );
        ManagedObjectProtocols.SetItem(FirstMap(self), rest[0], rest[1]);
        return PythonNoneValue.Instance;
    }

    /// <summary>`del self[key]`: only the first mapping is asked, and its refusal is replaced
    /// by the chain's own.</summary>
    private static PythonValue Delete(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__delitem__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        try
        {
            ManagedObjectProtocols.DeleteItem(FirstMap(self), rest[0]);
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "KeyError"))
        {
            throw FirstMappingMiss("Key not found in the first mapping", rest[0]);
        }
        return PythonNoneValue.Instance;
    }

    /// <summary>`popitem()`: through the first mapping, or the chain's own `KeyError`.</summary>
    private static PythonValue PopItem(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        try
        {
            return PythonUserTypes.InvokeOn(FirstMap(self), "popitem", []);
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "KeyError"))
        {
            throw BareKeyError("No keys found in the first mapping.");
        }
    }

    /// <summary>`pop(key, *args)`: through the first mapping when it carries the key.</summary>
    private static PythonValue Pop(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "pop() missing required argument 'key' (pos 1)",
                default,
                "TypeError"
            );
        try
        {
            return PythonUserTypes.InvokeOn(FirstMap(self), "pop", rest);
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "KeyError"))
        {
            throw FirstMappingMiss("Key not found in the first mapping", rest[0]);
        }
    }

    /// <summary>`clear()` empties the first mapping and leaves the rest alone.</summary>
    private static PythonValue Clear(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        PythonUserTypes.InvokeOn(FirstMap(self), "clear", []);
        return PythonNoneValue.Instance;
    }

    /// <summary>`self |= other` updates the first mapping in place.</summary>
    private static PythonValue MergeInPlace(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__ior__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        PythonUserTypes.InvokeOn(FirstMap(self), "update", [rest[0]]);
        return self;
    }

    /// <summary>
    /// `self | other` copies the chain and updates the copy's first mapping; the reflection
    /// starts from the operand and layers the chain's mappings on top of it, the later ones
    /// first.
    /// </summary>
    private static PythonValue Merge(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool reflected
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__or__() takes exactly 1 argument ({rest.Count} given)",
                default,
                "TypeError"
            );
        if (!PythonCollectionsAbc.Matches(rest[0], PythonCollectionsAbc.Class("Mapping")))
            return PythonNotImplementedValue.Instance;
        if (!reflected)
        {
            var copy = (PythonManagedObjectValue)Copy(self, []);
            PythonUserTypes.InvokeOn(FirstMap(copy), "update", [rest[0]]);
            return copy;
        }
        var merged = PythonUserTypes.Dispatched(PythonBuiltinTypes.Dict, [rest[0]]);
        var maps = Elements(PythonUserTypes.Maps(self));
        for (var index = maps.Count - 1; index >= 0; index--)
            PythonUserTypes.InvokeOn(merged, "update", [maps[index]]);
        return PythonUserTypes.Construct(PythonUserTypes.ClassOf(self), [merged]);
    }

    /// <summary>The first mapping, which is the only one writes reach.</summary>
    private static PythonValue FirstMap(PythonValue self) =>
        ManagedObjectProtocols.GetItem(
            PythonUserTypes.Maps(self),
            PythonWholeNumberValue.Create(0)
        );

    /// <summary>`self.maps[key]`'s refusal, in the chain's own words.</summary>
    private static PythonRaisedException FirstMappingMiss(string message, PythonValue key)
    {
        var text = $"{message}: {key.ToRepresentationString()}";
        return new PythonRaisedException(
            new PythonExceptionValue("KeyError", new PythonTextValue(text).ToRepresentationString())
            {
                Arguments = [new PythonTextValue(text)],
            }
        );
    }

    /// <summary>A `KeyError` carrying a message and nothing else.</summary>
    private static PythonRaisedException BareKeyError(string message) =>
        new(
            new PythonExceptionValue(
                "KeyError",
                new PythonTextValue(message).ToRepresentationString()
            )
            {
                Arguments = [new PythonTextValue(message)],
            }
        );

    /// <summary>All the keys the mappings hold, as one list.</summary>
    private static PythonListValue Keys(PythonValue self)
    {
        var keys = new PythonListValue([]);
        foreach (var mapping in Elements(PythonUserTypes.Maps(self)))
        {
            var iterator = ManagedObjectProtocols.GetIterator(mapping);
            while (ManagedObjectProtocols.TryGetNext(iterator, out var key, default))
                keys.Elements.Add(key);
        }
        return keys;
    }

    /// <summary>The mappings the chain was built from, in the order its list holds them.</summary>
    private static List<PythonValue> Elements(PythonValue maps) => PythonUserTypes.Elements(maps);

    /// <summary>The mappings in reverse, which is the order `iter` and `|` reflected walk
    /// them in.</summary>
    private static List<PythonValue> Reversed(PythonValue maps)
    {
        var elements = PythonUserTypes.Elements(maps);
        elements.Reverse();
        return elements;
    }
}
