// The abstract base classes follow CPython 3.14.7 Lib/_collections_abc.py and Lib/abc.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// <c>collections.abc</c>: the abstract base classes the runtime answers <c>isinstance</c>
/// and <c>issubclass</c> for, their registrations, and the structural checks the classes
/// that duck-type carry.
/// </summary>
/// <remarks>
/// The classes are native types whose metaclass is <c>ABCMeta</c>. A query answers from the
/// type's own resolution order, from a class registered with <c>register</c>, or from the
/// structural check the class declares — <c>Iterable</c> for <c>__iter__</c>, <c>Buffer</c>
/// for <c>__buffer__</c> and so on — while the composite classes (<c>Collection</c>,
/// <c>Mapping</c>, <c>Sequence</c>, <c>Set</c>) duck-type nothing, as in 3.14. The builtin
/// value kinds the runtime models are registered the way CPython registers them.
/// </remarks>
internal static class PythonCollectionsAbc
{
    /// <summary>One abstract base class: its bases, its abstract methods, its duck check.</summary>
    /// <param name="AbstractMethods">The set `__abstractmethods__` reports, inherited names
    /// included.</param>
    /// <param name="Declared">The stubs the class itself carries — the abstract names CPython
    /// declares in its own class body. A name the class only inherits is answered by the class
    /// that declares it, so a later base implementing it still wins the lookup, which is the
    /// order a mixed MRO resolves in. Null means the class declares every name it reports.</param>
    private sealed record Abc(
        string Name,
        string[] Bases,
        string[] AbstractMethods,
        string[]? Declared = null,
        string[]? DuckMethods = null,
        bool Hashes = false
    );

    /// <summary>
    /// The classes, in dependency order so the bases exist when a class is built. A duck
    /// check lists the methods a class must carry; <c>Hashes</c> is `Hashable`'s own rule
    /// that the hash must be present and not None.
    /// </summary>
    private static readonly Abc[] Definitions =
    [
        new("Hashable", [], ["__hash__"], Hashes: true),
        new("Awaitable", [], ["__await__"], DuckMethods: ["__await__"]),
        new("AsyncIterable", [], ["__aiter__"], DuckMethods: ["__aiter__"]),
        new(
            "AsyncIterator",
            ["AsyncIterable"],
            ["__anext__"],
            Declared: ["__anext__"],
            DuckMethods: ["__aiter__", "__anext__"]
        ),
        new(
            "AsyncGenerator",
            ["AsyncIterator"],
            ["asend", "athrow"],
            Declared: ["asend", "athrow"],
            DuckMethods: ["__aiter__", "__anext__", "asend", "athrow", "aclose"]
        ),
        new("Iterable", [], ["__iter__"], DuckMethods: ["__iter__"]),
        new("Iterator", ["Iterable"], ["__next__"], DuckMethods: ["__iter__", "__next__"]),
        new("Reversible", ["Iterable"], ["__iter__", "__reversed__"], Declared: ["__reversed__"]),
        new("Sized", [], ["__len__"], DuckMethods: ["__len__"]),
        new("Container", [], ["__contains__"], DuckMethods: ["__contains__"]),
        new("Callable", [], ["__call__"], DuckMethods: ["__call__"]),
        new("Buffer", [], ["__buffer__"], DuckMethods: ["__buffer__"]),
        new(
            "Coroutine",
            ["Awaitable"],
            ["__await__", "send", "throw"],
            Declared: ["send", "throw"],
            DuckMethods: ["__await__", "send", "throw", "close"]
        ),
        new(
            "Generator",
            ["Iterator"],
            ["send", "throw"],
            Declared: ["send", "throw"],
            DuckMethods: ["send", "throw", "close"]
        ),
        new(
            "Collection",
            ["Sized", "Iterable", "Container"],
            ["__contains__", "__iter__", "__len__"],
            Declared: []
        ),
        new("Set", ["Collection"], ["__contains__", "__iter__", "__len__"], Declared: []),
        new(
            "MutableSet",
            ["Set"],
            ["__contains__", "__iter__", "__len__", "add", "discard"],
            Declared: ["add", "discard"]
        ),
        new(
            "Mapping",
            ["Collection"],
            ["__getitem__", "__iter__", "__len__"],
            Declared: ["__getitem__"]
        ),
        new(
            "MutableMapping",
            ["Mapping"],
            ["__delitem__", "__getitem__", "__iter__", "__len__", "__setitem__"],
            Declared: ["__setitem__", "__delitem__"]
        ),
        new("MappingView", ["Sized"], [], Declared: []),
        new("KeysView", ["MappingView", "Set"], [], Declared: []),
        new("ItemsView", ["MappingView", "Set"], [], Declared: []),
        new("ValuesView", ["MappingView", "Collection"], [], Declared: []),
        new(
            "Sequence",
            ["Reversible", "Collection"],
            ["__getitem__", "__len__"],
            Declared: ["__getitem__"]
        ),
        new(
            "MutableSequence",
            ["Sequence"],
            ["__delitem__", "__getitem__", "__len__", "__setitem__", "insert"],
            Declared: ["__setitem__", "__delitem__", "insert"]
        ),
        new("ByteString", ["Sequence"], ["__getitem__", "__len__"], Declared: []),
    ];

    /// <summary>
    /// The builtin kinds registered with a class, under the name the runtime reports for
    /// them — the same registrations CPython's `_collections_abc` performs.
    /// </summary>
    private static readonly (string Name, string[] Abcs)[] RegisteredKinds =
    [
        ("dict", ["MutableMapping", "Reversible"]),
        ("list", ["MutableSequence", "Reversible"]),
        ("bytearray", ["MutableSequence", "ByteString", "Reversible"]),
        ("set", ["MutableSet"]),
        ("frozenset", ["Set"]),
        ("tuple", ["Sequence", "Reversible"]),
        ("str", ["Sequence", "Reversible"]),
        ("bytes", ["Sequence", "ByteString", "Reversible"]),
        ("range", ["Sequence", "Reversible"]),
        ("memoryview", ["Sequence", "Reversible"]),
        ("deque", ["MutableSequence", "Reversible"]),
        ("dict_keys", ["MappingView", "KeysView", "Set", "Reversible"]),
        ("dict_items", ["MappingView", "ItemsView", "Set", "Reversible"]),
        ("dict_values", ["MappingView", "ValuesView", "Reversible"]),
        ("odict_keys", ["MappingView", "KeysView", "Set", "Reversible"]),
        ("odict_items", ["MappingView", "ItemsView", "Set", "Reversible"]),
        ("odict_values", ["MappingView", "ValuesView", "Reversible"]),
    ];

    private static readonly Dictionary<string, PythonManagedTypeValue> Classes = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// The classes are built once, when the type is first touched, so that a class built on
    /// one of them — `collections.UserDict` on `MutableMapping` — finds it whether or not the
    /// `collections.abc` module has been imported yet.
    /// </summary>
    static PythonCollectionsAbc()
    {
        foreach (var definition in Definitions)
            Build(definition);
    }

    /// <summary>The classes registered with each base class, per `register`.</summary>
    private static readonly Dictionary<PythonManagedTypeValue, HashSet<PythonValue>> Registered =
    [];

    /// <summary>The names the module publishes, in CPython's order.</summary>
    private static readonly string[] ExportedNames =
    [
        "AsyncGenerator",
        "AsyncIterable",
        "AsyncIterator",
        "Awaitable",
        "Buffer",
        "ByteString",
        "Callable",
        "Collection",
        "Container",
        "Coroutine",
        "Generator",
        "Hashable",
        "ItemsView",
        "Iterable",
        "Iterator",
        "KeysView",
        "Mapping",
        "MappingView",
        "MutableMapping",
        "MutableSequence",
        "MutableSet",
        "Reversible",
        "Sequence",
        "Set",
        "Sized",
        "ValuesView",
    ];

    /// <summary>One of the classes by name, for a type that is built on it.</summary>
    internal static PythonManagedTypeValue Class(string name) => Classes[name];

    /// <summary>`abc.ABCMeta`, the metaclass every class here is an instance of.</summary>
    internal static readonly PythonManagedTypeValue AbcMeta = CreateMetaClass();

    /// <summary>The `collections.abc` module's names.</summary>
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("ABCMeta", AbcMeta);
        foreach (var definition in Definitions)
            globals.SetValue(definition.Name, Classes[definition.Name]);
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. ExportedNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
    }

    /// <summary>
    /// The C3 linearization of a class with these bases, which is the order CPython installs:
    /// `MutableSequence` lists `Reversible` before `Collection` because its single base does.
    /// </summary>
    private static List<PythonValue> Linearize(
        PythonManagedTypeValue type,
        PythonManagedTypeValue[] bases
    )
    {
        var sequences = new List<List<PythonValue>>();
        sequences.Add([.. bases.Select(b => (PythonValue)b)]);
        sequences.AddRange(
            bases.Select(b => new List<PythonValue>(b.ResolutionOrder ?? [.. b.Mro]))
        );
        var order = new List<PythonValue> { type };
        while (sequences.Count > 0)
        {
            PythonValue? candidate = null;
            foreach (var sequence in sequences)
            {
                if (sequence.Count == 0)
                    continue;
                var head = sequence[0];
                if (sequences.Any(other => other.Skip(1).Contains(head)))
                    continue;
                candidate = head;
                break;
            }
            if (candidate is null)
                throw new InvalidOperationException(
                    $"The bases of {type.Name} have no consistent order."
                );
            order.Add(candidate);
            foreach (var sequence in sequences)
            {
                if (sequence.Count > 0 && ReferenceEquals(sequence[0], candidate))
                    sequence.RemoveAt(0);
            }
            sequences.RemoveAll(sequence => sequence.Count == 0);
        }
        return order;
    }

    private static PythonManagedTypeValue CreateMetaClass()
    {
        var type = new PythonManagedTypeValue("ABCMeta")
        {
            Module = "abc",
            QualName = "ABCMeta",
            IsMetaclass = true,
        };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        // A metaclass is a subclass of `type`, which is what lets a class with an ABC base be
        // created without a metaclass conflict.
        type.SetResolutionOrder(
            new PythonTupleValue([type, PythonBuiltinTypes.Type, PythonBuiltinFunctions.Object])
        );
        return type;
    }

    private static PythonManagedTypeValue Build(Abc definition)
    {
        var bases = definition.Bases.Select(name => Classes[name]).ToArray();
        var type = new PythonManagedTypeValue(
            definition.Name,
            bases,
            [.. bases.SelectMany(b => b.Mro)]
        )
        {
            Module = "collections.abc",
            QualName = definition.Name,
            Metaclass = AbcMeta,
        };
        if (bases.Length == 0)
        {
            // A class with no base of its own sits on `object` alone.
            type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
            type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        }
        else
        {
            type.SetDeclaredBases(new PythonTupleValue([.. bases.Cast<PythonValue>()]));
            type.SetResolutionOrder(new PythonTupleValue([.. Linearize(type, bases)]));
        }
        type.Attributes["__module__"] = new PythonTextValue("collections.abc");
        type.Attributes["__doc__"] = PythonNoneValue.Instance;
        type.Attributes["__slots__"] = new PythonTupleValue([]);
        // The abstract methods are stubs the class carries, so instantiating a class that
        // has not implemented them can name them. Only the names the class declares stand
        // here: one it inherits is answered by the class that declares it.
        foreach (var name in definition.Declared ?? definition.AbstractMethods)
            type.Attributes[name] = AbstractStub(name);
        type.Attributes["__abstractmethods__"] = new PythonSetValue(
            definition.AbstractMethods.Select(name => (PythonValue)new PythonTextValue(name))
        )
        {
            IsFrozen = true,
        };
        type.Attributes["register"] = new PythonBuiltinFunctionValue(
            "register",
            (arguments, span) => Register(type, arguments, span)
        );
        type.Attributes["__subclasshook__"] = new PythonBuiltinFunctionValue(
            "__subclasshook__",
            (arguments, span) => SubclassHook(type, arguments, span)
        );
        InstallMixins(type, definition.Name);
        Classes[definition.Name] = type;
        return type;
    }

    // -------------------------------------------------------------------------
    // Mixins
    // -------------------------------------------------------------------------

    /// <summary>
    /// The concrete methods each class provides over the abstract ones — CPython's mixins —
    /// installed after the stubs so a subclass that implements only the abstract methods
    /// inherits a working sequence, mapping, set or view.
    /// </summary>
    /// <remarks>
    /// Every body works through the object protocol rather than the storage: `self[i]` is
    /// `__getitem__`, `value in self` is `__contains__`, `len(self)` is `__len__`, so a
    /// subclass that overrides one of them is honoured the way CPython honours it. The
    /// methods are functions written in Python, so they report as functions, and the ones
    /// whose signature declares parameters accept the keyword calls that signature allows.
    /// </remarks>
    private static void InstallMixins(PythonManagedTypeValue type, string name)
    {
        void Mix(
            string method,
            Func<PythonValue, IReadOnlyList<PythonValue>, PythonValue> body,
            ProtocolKeywordInvoker? keywords = null,
            string[]? parameters = null,
            PythonValue?[]? defaults = null,
            bool classMethod = false,
            int positionalOnly = 0
        )
        {
            var function = new PythonProtocolFunctionValue(
                method,
                (receiver, arguments) =>
                {
                    var (self, rest) = Receiver(receiver, arguments);
                    return body(self, rest);
                },
                keywords
            )
            {
                DeclaringType = name,
                IsPythonMethod = true,
            };
            if (parameters is not null)
            {
                function = function.WithSignature(
                    parameters,
                    defaults ?? new PythonValue?[parameters.Length],
                    positionalOnly
                );
            }
            type.Attributes[method] = classMethod ? new PythonClassMethodValue(function) : function;
        }

        switch (name)
        {
            case "Iterator":
                // An iterator is its own iterable.
                Mix("__iter__", (self, _) => self);
                break;
            case "Generator":
                // A generator advances through `send`, and `close` throws the exit into it.
                Mix("__next__", (self, _) => InvokeOn(self, "send", [PythonNoneValue.Instance]));
                Mix("close", (self, _) => Closed(self, "generator ignored GeneratorExit"));
                break;
            case "Coroutine":
                Mix("close", (self, _) => Closed(self, "coroutine ignored GeneratorExit"));
                break;
            case "AsyncIterator":
                Mix("__aiter__", (self, _) => self);
                break;
            case "Sequence":
                // The walk is lazy: every step reads the element afresh and ends where the
                // sequence raises IndexError. `__reversed__` knows its own bound, because
                // `reversed(range(len(self)))` never reaches an out-of-range index.
                Mix("__iter__", (self, _) => Walk(self, start: 0, step: 1, stop: null));
                Mix(
                    "__reversed__",
                    (self, _) => Walk(self, start: Length(self) - 1, step: -1, stop: -1)
                );
                Mix("__contains__", SequenceContains, parameters: ["value"]);
                Mix(
                    "index",
                    SequenceIndex,
                    parameters: ["value", "start", "stop"],
                    defaults: [null, PythonWholeNumberValue.Create(0), PythonNoneValue.Instance]
                );
                Mix("count", SequenceCount, parameters: ["value"]);
                break;
            case "MutableSequence":
                Mix(
                    "append",
                    (self, arguments) =>
                        InvokeOn(self, "insert", [LengthValue(self), arguments[0]]),
                    parameters: ["value"]
                );
                Mix(
                    "clear",
                    (self, _) =>
                    {
                        while (true)
                        {
                            try
                            {
                                Delete(self, PythonWholeNumberValue.Create(-1));
                            }
                            catch (Exception error) when (IsFault(error, "IndexError"))
                            {
                                return PythonNoneValue.Instance;
                            }
                        }
                    }
                );
                Mix(
                    "reverse",
                    (self, _) =>
                    {
                        // The two ends move inwards, as CPython's own swap loop does.
                        var count = Length(self);
                        for (var offset = 0; offset < count / 2; offset++)
                        {
                            var front = PythonWholeNumberValue.Create(offset);
                            var back = PythonWholeNumberValue.Create(count - offset - 1);
                            var head = Get(self, front);
                            var tail = Get(self, back);
                            Set(self, front, tail);
                            Set(self, back, head);
                        }
                        return PythonNoneValue.Instance;
                    }
                );
                Mix(
                    "extend",
                    (self, arguments) =>
                    {
                        // A sequence extending itself walks a snapshot, which is the copy the
                        // mixin's `list(values)` takes.
                        var values = ReferenceEquals(arguments[0], self)
                            ? new PythonListValue(
                                ManagedObjectProtocols.MaterializeValues(arguments[0], default)
                            )
                            : arguments[0];
                        var iterator = ManagedObjectProtocols.GetIterator(values);
                        while (
                            ManagedObjectProtocols.TryGetNext(iterator, out var element, default)
                        )
                        {
                            InvokeOn(self, "append", [element]);
                        }
                        return PythonNoneValue.Instance;
                    },
                    parameters: ["values"]
                );
                Mix(
                    "pop",
                    (self, arguments) =>
                    {
                        var index =
                            arguments.Count == 1 ? arguments[0] : PythonWholeNumberValue.Create(-1);
                        var value = Get(self, index);
                        Delete(self, index);
                        return value;
                    },
                    parameters: ["index"],
                    defaults: [PythonWholeNumberValue.Create(-1)]
                );
                Mix(
                    "remove",
                    (self, arguments) =>
                    {
                        Delete(self, InvokeOn(self, "index", [arguments[0]]));
                        return PythonNoneValue.Instance;
                    },
                    parameters: ["value"]
                );
                Mix(
                    "__iadd__",
                    (self, arguments) =>
                    {
                        InvokeOn(self, "extend", [arguments[0]]);
                        return self;
                    }
                );
                break;
            case "Set":
                Mix("__le__", (self, arguments) => SetOrdered(self, arguments, "le"));
                Mix("__lt__", (self, arguments) => SetOrdered(self, arguments, "lt"));
                Mix("__gt__", (self, arguments) => SetOrdered(self, arguments, "gt"));
                Mix("__ge__", (self, arguments) => SetOrdered(self, arguments, "ge"));
                Mix("__eq__", (self, arguments) => SetOrdered(self, arguments, "eq"));
                Mix(
                    "_from_iterable",
                    (self, arguments) => Construct(self, arguments[0]),
                    parameters: ["it"],
                    classMethod: true
                );
                Mix("isdisjoint", SetDisjoint, parameters: ["other"]);
                foreach (
                    var (method, operation) in new[]
                    {
                        ("__and__", SetOperator.Intersection),
                        ("__rand__", SetOperator.Intersection),
                        ("__or__", SetOperator.Union),
                        ("__ror__", SetOperator.Union),
                        ("__sub__", SetOperator.Difference),
                        ("__rsub__", SetOperator.ReflectedDifference),
                        ("__xor__", SetOperator.SymmetricDifference),
                        ("__rxor__", SetOperator.SymmetricDifference),
                    }
                )
                {
                    Mix(method, (self, arguments) => SetCombined(self, arguments, operation));
                }
                Mix("_hash", SetHash);
                // A class that defines `__eq__` without `__hash__` is unhashable, which is
                // the value `None` sitting in the class dictionary.
                type.Attributes["__hash__"] = PythonNoneValue.Instance;
                break;
            case "MutableSet":
                Mix(
                    "remove",
                    (self, arguments) =>
                    {
                        if (!Contains(self, arguments[0]))
                            throw ManagedObjectProtocols.MissingKey(arguments[0]);
                        InvokeOn(self, "discard", [arguments[0]]);
                        return PythonNoneValue.Instance;
                    },
                    parameters: ["value"]
                );
                Mix(
                    "pop",
                    (self, _) =>
                    {
                        if (First(self) is not { } value)
                            throw Bare("KeyError");
                        InvokeOn(self, "discard", [value]);
                        return value;
                    }
                );
                Mix(
                    "clear",
                    (self, _) =>
                    {
                        while (First(self) is { } value)
                            InvokeOn(self, "discard", [value]);
                        return PythonNoneValue.Instance;
                    }
                );
                foreach (
                    var (method, operation) in new[]
                    {
                        ("__ior__", SetOperator.UnionInPlace),
                        ("__iand__", SetOperator.IntersectionInPlace),
                        ("__ixor__", SetOperator.SymmetricDifferenceInPlace),
                        ("__isub__", SetOperator.DifferenceInPlace),
                    }
                )
                {
                    Mix(method, (self, arguments) => SetUpdated(self, arguments, operation));
                }
                break;
            case "Mapping":
                Mix(
                    "get",
                    (self, arguments) =>
                    {
                        try
                        {
                            return Get(self, arguments[0]);
                        }
                        catch (Exception error) when (IsFault(error, "KeyError"))
                        {
                            return arguments.Count == 2 ? arguments[1] : PythonNoneValue.Instance;
                        }
                    },
                    parameters: ["key", "default"],
                    defaults: [null, PythonNoneValue.Instance]
                );
                Mix(
                    "__contains__",
                    (self, arguments) =>
                    {
                        try
                        {
                            Get(self, arguments[0]);
                        }
                        catch (Exception error) when (IsFault(error, "KeyError"))
                        {
                            return PythonTruthValue.False;
                        }
                        return PythonTruthValue.True;
                    },
                    parameters: ["key"]
                );
                Mix("keys", (self, _) => Construct(Classes["KeysView"], self));
                Mix("items", (self, _) => Construct(Classes["ItemsView"], self));
                Mix("values", (self, _) => Construct(Classes["ValuesView"], self));
                Mix("__eq__", MappingEqual);
                // `__reversed__ = None` is how the mixin refuses reversing a mapping whose
                // subclass has not said how its keys reverse.
                type.Attributes["__reversed__"] = PythonNoneValue.Instance;
                type.Attributes["__hash__"] = PythonNoneValue.Instance;
                break;
            case "MutableMapping":
                Mix(
                    "pop",
                    (self, arguments) =>
                    {
                        var fallback = arguments.Count == 2 ? arguments[1] : PopMarker;
                        try
                        {
                            var value = Get(self, arguments[0]);
                            Delete(self, arguments[0]);
                            return value;
                        }
                        catch (Exception error) when (IsFault(error, "KeyError"))
                        {
                            if (ReferenceEquals(fallback, PopMarker))
                                throw;
                            return fallback;
                        }
                    },
                    parameters: ["key", "default"],
                    defaults: [null, PopMarker]
                );
                Mix("popitem", (self, _) => MappingPopItem(self));
                Mix(
                    "clear",
                    (self, _) =>
                    {
                        while (true)
                        {
                            try
                            {
                                MappingPopItem(self);
                            }
                            catch (Exception error) when (IsFault(error, "KeyError"))
                            {
                                return PythonNoneValue.Instance;
                            }
                        }
                    }
                );
                Mix(
                    "update",
                    // The positional form; a keyword call lands on the same body, which is
                    // all the mixin's `**kwds` needs.
                    (self, arguments) =>
                        MappingUpdate(self, arguments.Count == 1 ? arguments[0] : null, [], []),
                    (target, positional, names, values) =>
                    {
                        var (self, rest) = Receiver(target, positional);
                        if (rest.Count > 1)
                            throw ManagedObjectProtocols.Fault(
                                "DPY4003",
                                $"update expected at most 1 argument, got {rest.Count}",
                                default,
                                "TypeError"
                            );
                        return MappingUpdate(self, rest.Count == 1 ? rest[0] : null, names, values);
                    }
                );
                Mix(
                    "setdefault",
                    (self, arguments) =>
                    {
                        try
                        {
                            return Get(self, arguments[0]);
                        }
                        catch (Exception error) when (IsFault(error, "KeyError"))
                        {
                            var fallback =
                                arguments.Count == 2 ? arguments[1] : PythonNoneValue.Instance;
                            Set(self, arguments[0], fallback);
                            return fallback;
                        }
                    },
                    parameters: ["key", "default"],
                    defaults: [null, PythonNoneValue.Instance]
                );
                break;
            case "MappingView":
                Mix(
                    "__init__",
                    (self, arguments) =>
                    {
                        ManagedObjectProtocols.SetAttribute(self, MappingMember, arguments[0]);
                        return PythonNoneValue.Instance;
                    },
                    parameters: ["mapping"]
                );
                Mix("__len__", (self, _) => LengthValue(Mapping(self)));
                Mix(
                    "__repr__",
                    (self, _) =>
                        new PythonTextValue(
                            $"{TypeName(self)}({Mapping(self).ToRepresentationString()})"
                        )
                );
                // The single slot the view keeps its mapping in.
                type.Attributes["__slots__"] = new PythonTupleValue([
                    new PythonTextValue(MappingMember),
                ]);
                break;
            case "KeysView":
                Mix(
                    "_from_iterable",
                    (self, arguments) => Dispatched(PythonBuiltinTypes.Set, [arguments[0]]),
                    parameters: ["it"],
                    classMethod: true
                );
                Mix(
                    "__contains__",
                    (self, arguments) =>
                        Contains(Mapping(self), arguments[0])
                            ? PythonTruthValue.True
                            : PythonTruthValue.False,
                    parameters: ["key"]
                );
                Mix("__iter__", (self, _) => ManagedObjectProtocols.GetIterator(Mapping(self)));
                break;
            case "ItemsView":
                Mix(
                    "_from_iterable",
                    (self, arguments) => Dispatched(PythonBuiltinTypes.Set, [arguments[0]]),
                    parameters: ["it"],
                    classMethod: true
                );
                Mix("__contains__", ItemsContains, parameters: ["item"]);
                Mix("__iter__", (self, _) => ViewWalk(Mapping(self), items: true));
                break;
            case "ValuesView":
                Mix("__contains__", ValuesContains, parameters: ["value"]);
                Mix("__iter__", (self, _) => ViewWalk(Mapping(self), items: false));
                break;
        }
    }

    // -------------------------------------------------------------------------
    // What the mixin bodies work through
    // -------------------------------------------------------------------------

    /// <summary>The slot a mapping view keeps its mapping in.</summary>
    private const string MappingMember = "_mapping";

    /// <summary>The value `MutableMapping.pop` reads for "no default was given".</summary>
    private static readonly PythonValue PopMarker = new PythonManagedObjectValue(
        PythonBuiltinFunctions.ObjectType
    );

    private enum SetOperator
    {
        Intersection,
        Union,
        Difference,
        ReflectedDifference,
        SymmetricDifference,
        UnionInPlace,
        IntersectionInPlace,
        DifferenceInPlace,
        SymmetricDifferenceInPlace,
    }

    /// <summary>
    /// The receiver of a mixin call: a bound call hands the instance over beside its
    /// arguments, an unbound one — `Sequence.__iter__(c)` — passes it first, the way any
    /// function object does.
    /// </summary>
    private static (PythonValue Self, IReadOnlyList<PythonValue> RestArguments) Receiver(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    ) =>
        receiver is not null ? (receiver, arguments)
        : arguments.Count != 0 ? (arguments[0], [.. arguments.Skip(1)])
        : throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "unbound method call needs a receiver",
            default,
            "TypeError"
        );

    /// <summary>
    /// A method of the receiver, called with these arguments: the attribute is looked up
    /// first — the subclass's own method, or the mixin it inherits — and the call itself goes
    /// through the dispatcher, which is what a call of a method written in Python needs.
    /// </summary>
    private static PythonValue InvokeOn(
        PythonValue self,
        string name,
        IReadOnlyList<PythonValue> arguments
    ) => Dispatched(ManagedObjectProtocols.GetAttribute(self, name), arguments);

    /// <summary>A callable the mixins reach, called the way a Python call reaches it.</summary>
    private static PythonValue Dispatched(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments
    ) => UserObjectProtocols.Dispatcher!.Invoke(callable, [.. arguments], default);

    /// <summary>`self[index]` and its assignment and deletion, through the object protocol.</summary>
    private static PythonValue Get(PythonValue self, PythonValue index) =>
        ManagedObjectProtocols.GetItem(self, index);

    private static void Set(PythonValue self, PythonValue index, PythonValue value) =>
        ManagedObjectProtocols.SetItem(self, index, value);

    private static void Delete(PythonValue self, PythonValue index) =>
        ManagedObjectProtocols.DeleteItem(self, index);

    /// <summary>`len(self)`, which a sequence mixin asks for as a count.</summary>
    private static int Length(PythonValue self) => ManagedObjectProtocols.GetLength(self);

    /// <summary>`len(self)` in the value form a Python call hands back.</summary>
    private static PythonWholeNumberValue LengthValue(PythonValue self) =>
        PythonWholeNumberValue.Create(Length(self));

    /// <summary>`value in self`, through `__contains__` and its fallbacks.</summary>
    private static bool Contains(PythonValue container, PythonValue item) =>
        ManagedObjectProtocols.Contains(container, item);

    /// <summary>`v is value or v == value`, which is how every mixin compares an element.</summary>
    private static bool ElementMatches(PythonValue element, PythonValue value) =>
        ReferenceEquals(element, value) || ManagedObjectProtocols.AreEqual(element, value);

    /// <summary>`next(iter(self))`, or null when the walk is already over.</summary>
    private static PythonValue? First(PythonValue self)
    {
        var iterator = ManagedObjectProtocols.GetIterator(self);
        return ManagedObjectProtocols.TryGetNext(iterator, out var value, default) ? value : null;
    }

    /// <summary>Whether an error is the Python exception with this name.</summary>
    private static bool IsFault(Exception error, string name) =>
        PythonNamespaceMapping.IsPythonException(error, name);

    /// <summary>An exception raised with no arguments at all, as a bare `raise X` is.</summary>
    private static PythonRaisedException Bare(string typeName) =>
        new(new PythonExceptionValue(typeName, string.Empty) { SuppressContext = true });

    /// <summary>`isinstance(value, name)` for one of the classes here.</summary>
    private static bool IsAncestor(PythonValue value, string name) =>
        Classes.TryGetValue(name, out var abc) && Matches(value, abc);

    /// <summary>
    /// `type(argument)`, for a value a mixin builds: a class answers its own type object and
    /// anything else is constructed through the class-call path.
    /// </summary>
    private static PythonValue Construct(PythonValue type, PythonValue argument) =>
        UserObjectProtocols.Dispatcher!.CallType(type, [argument], [], [], default);

    /// <summary>`self._mapping`, the mapping a view was built over.</summary>
    private static PythonValue Mapping(PythonValue self) =>
        ManagedObjectProtocols.GetAttribute(self, MappingMember);

    /// <summary>The name a view reports itself as in a representation.</summary>
    private static string TypeName(PythonValue self) =>
        self is PythonManagedObjectValue instance
            ? instance.Type.Name
            : ManagedObjectProtocols.GetTypeName(self);

    /// <summary>
    /// The walk `Sequence.__iter__` and `Sequence.__reversed__` hand out: a lazy cursor that
    /// reads `self[index]` on every step and ends where the sequence raises IndexError, or at
    /// the bound `__reversed__` already knows.
    /// </summary>
    private static PythonIteratorValue Walk(PythonValue self, long start, long step, long? stop) =>
        new PythonIteratorValue(
            new PythonSequenceIteratorSourceValue
            {
                Sequence = self,
                NextIndex = start,
                Step = step,
                Stop = stop,
            },
            -1
        );

    /// <summary>
    /// The walk a mapping view's `__iter__` hands out: the mapping's keys one at a time, with
    /// each value read as its element is produced.
    /// </summary>
    private static PythonIteratorValue ViewWalk(PythonValue mapping, bool items) =>
        new PythonIteratorValue(
            new PythonMappingViewSourceValue(mapping, items)
            {
                Inner = ManagedObjectProtocols.GetIterator(mapping),
            },
            -1
        );

    /// <summary>`Sequence.value in self`, over the mixin's own walk.</summary>
    private static PythonValue SequenceContains(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var iterator = ManagedObjectProtocols.GetIterator(self);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, default))
        {
            if (ElementMatches(element, arguments[0]))
                return PythonTruthValue.True;
        }
        return PythonTruthValue.False;
    }

    /// <summary>`self.index(value, start=0, stop=None)`, with the negative-bound handling of
    /// the mixin, and the bare `ValueError` it raises on a miss.</summary>
    private static PythonValue SequenceIndex(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        var length = Length(self);
        var start = arguments.Count > 1 ? SearchBound(arguments[1]) : BigInteger.Zero;
        var stop =
            arguments.Count > 2 && arguments[2] is not PythonNoneValue
                ? SearchBound(arguments[2])
                : (BigInteger?)null;
        if (start < 0)
            start = BigInteger.Max(length + start, BigInteger.Zero);
        if (stop is { } bound && bound < 0)
            stop = bound + length;
        for (var index = start; stop is null || index < stop; index++)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(default);
            PythonValue element;
            try
            {
                element = Get(self, PythonWholeNumberValue.Create(index));
            }
            catch (Exception error) when (IsFault(error, "IndexError"))
            {
                break;
            }
            if (ElementMatches(element, arguments[0]))
                return PythonWholeNumberValue.Create(index);
        }
        throw Bare("ValueError");
    }

    /// <summary>`self.count(value)`: how many elements the value matches.</summary>
    private static PythonValue SequenceCount(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        var count = 0;
        var iterator = ManagedObjectProtocols.GetIterator(self);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, default))
        {
            if (ElementMatches(element, arguments[0]))
                count++;
        }
        return PythonWholeNumberValue.Create(count);
    }

    /// <summary>A `start` or `stop` bound, in the runtime's usual index words.</summary>
    private static BigInteger SearchBound(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => UserObjectProtocols.TryConvertToIndex(value, default, out var index)
                ? index
                : throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "slice indices must be integers or have an __index__ method",
                    default,
                    "TypeError"
                ),
        };

    /// <summary>
    /// The five comparisons `Set` defines: `NotImplemented` against anything that is not a
    /// set, and otherwise CPython's own short-circuiting.
    /// </summary>
    private static PythonValue SetOrdered(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        string comparison
    )
    {
        var other = arguments[0];
        if (!IsAncestor(other, "Set"))
            return PythonNotImplementedValue.Instance;
        var selfLength = Length(self);
        var otherLength = Length(other);
        switch (comparison)
        {
            case "le":
                return selfLength <= otherLength && EveryMember(self, other)
                    ? PythonTruthValue.True
                    : PythonTruthValue.False;
            case "ge":
                return selfLength >= otherLength && EveryMember(other, self)
                    ? PythonTruthValue.True
                    : PythonTruthValue.False;
            case "eq":
                // `len(self) == len(other) and self.__le__(other)`: through the class's own
                // `__le__`, so an overriding subclass decides the answer.
                return selfLength == otherLength
                    ? InvokeOn(self, "__le__", [other])
                    : PythonTruthValue.False;
            case "lt":
                return selfLength < otherLength
                    ? InvokeOn(self, "__le__", [other])
                    : PythonTruthValue.False;
            default:
                return selfLength > otherLength
                    ? InvokeOn(self, "__ge__", [other])
                    : PythonTruthValue.False;
        }
    }

    /// <summary>Whether every element of `self` is a member of `other`.</summary>
    private static bool EveryMember(PythonValue self, PythonValue other)
    {
        var iterator = ManagedObjectProtocols.GetIterator(self);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, default))
        {
            if (!Contains(other, element))
                return false;
        }
        return true;
    }

    /// <summary>`self.isdisjoint(other)`: no element of `other` is a member of `self`.</summary>
    private static PythonValue SetDisjoint(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        var iterator = ManagedObjectProtocols.GetIterator(arguments[0]);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, default))
        {
            if (Contains(self, element))
                return PythonTruthValue.False;
        }
        return PythonTruthValue.True;
    }

    /// <summary>
    /// The set algebra, with the mixin's own admission rule: the operators that need a set
    /// take any iterable and build one through `_from_iterable`, and anything else is
    /// `NotImplemented`.
    /// </summary>
    private static PythonValue SetCombined(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        SetOperator operation
    )
    {
        var other = arguments[0];
        switch (operation)
        {
            case SetOperator.Intersection:
                // `value for value in other if value in self`: the right operand is walked
                // and the left one is only asked for membership.
                if (!IsAncestor(other, "Iterable"))
                    return PythonNotImplementedValue.Instance;
                return FromIterable(
                    self,
                    new PythonListValue([
                        .. Materialize(other).Where(value => Contains(self, value)),
                    ])
                );
            case SetOperator.Union:
                if (!IsAncestor(other, "Iterable"))
                    return PythonNotImplementedValue.Instance;
                return FromIterable(
                    self,
                    new PythonListValue([.. Materialize(self), .. Materialize(other)])
                );
            case SetOperator.SymmetricDifference:
                if (!IsAncestor(other, "Set") && !IsAncestor(other, "Iterable"))
                    return PythonNotImplementedValue.Instance;
                return FromIterable(self, new PythonListValue(Symmetric(self, other)));
            default:
                if (!IsAncestor(other, "Set") && !IsAncestor(other, "Iterable"))
                    return PythonNotImplementedValue.Instance;
                var (walked, filtered) =
                    operation == SetOperator.Difference ? (self, other) : (other, self);
                return FromIterable(
                    self,
                    new PythonListValue([
                        .. Materialize(walked).Where(value => !Contains(filtered, value)),
                    ])
                );
        }
    }

    /// <summary>`(self - other) | (other - self)`, which is what the mixin's `__xor__` is.</summary>
    private static List<PythonValue> Symmetric(PythonValue self, PythonValue other)
    {
        var left = Materialize(self);
        var right = Materialize(other);
        return
        [
            .. left.Where(value => !Contains(other, value)),
            .. right.Where(value => !Contains(self, value)),
        ];
    }

    /// <summary>
    /// The in-place operators: `__ior__` adds what the operand walks, the other three discard
    /// or add member by member, and an operand that is the receiver itself is emptied first
    /// where the mixin says so.
    /// </summary>
    private static PythonValue SetUpdated(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        SetOperator operation
    )
    {
        var other = arguments[0];
        switch (operation)
        {
            case SetOperator.UnionInPlace:
                var additions = ManagedObjectProtocols.GetIterator(other);
                while (ManagedObjectProtocols.TryGetNext(additions, out var element, default))
                    InvokeOn(self, "add", [element]);
                return self;
            case SetOperator.IntersectionInPlace:
                // `self - it` is built first, so discarding while walking it is safe.
                foreach (var value in Difference(self, other))
                    InvokeOn(self, "discard", [value]);
                return self;
            case SetOperator.DifferenceInPlace:
                if (ReferenceEquals(other, self))
                {
                    InvokeOn(self, "clear", []);
                    return self;
                }
                var removals = ManagedObjectProtocols.GetIterator(other);
                while (ManagedObjectProtocols.TryGetNext(removals, out var element, default))
                    InvokeOn(self, "discard", [element]);
                return self;
            default:
                if (ReferenceEquals(other, self))
                {
                    InvokeOn(self, "clear", []);
                    return self;
                }
                var converted = IsAncestor(other, "Set") ? other : FromIterable(self, other);
                var iterator = ManagedObjectProtocols.GetIterator(converted);
                while (ManagedObjectProtocols.TryGetNext(iterator, out var element, default))
                {
                    if (Contains(self, element))
                        InvokeOn(self, "discard", [element]);
                    else
                        InvokeOn(self, "add", [element]);
                }
                return self;
        }
    }

    /// <summary>The elements of `self` that are not in `other`.</summary>
    private static List<PythonValue> Difference(PythonValue self, PythonValue other) =>
        [.. Materialize(self).Where(value => !Contains(other, value))];

    /// <summary>`self._from_iterable(values)`: the class the receiver belongs to builds it.</summary>
    private static PythonValue FromIterable(PythonValue self, PythonValue values) =>
        InvokeOn(self, "_from_iterable", [values]);

    /// <summary>
    /// `_hash(self)`: the algorithm the builtin sets use, so a set-like class that defines
    /// `__hash__` this way compares equal to a `frozenset` holding the same elements.
    /// </summary>
    private static PythonValue SetHash(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        var mask = (BigInteger.One << 64) - 1;
        var maximum = (BigInteger.One << 63) - 1;
        var hash = (BigInteger)1927868237 * (Length(self) + 1) & mask;
        var iterator = ManagedObjectProtocols.GetIterator(self);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, default))
        {
            BigInteger member = ManagedObjectProtocols.GetPythonHash(element);
            hash ^= (member ^ (member << 16) ^ 89869747) * 3644798167;
            hash &= mask;
        }
        hash ^= (hash >> 11) ^ (hash >> 25);
        hash = (hash * 69069 + 907133923) & mask;
        if (hash > maximum)
            hash -= mask + 1;
        if (hash == -1)
            hash = 590923713;
        return PythonWholeNumberValue.Create(hash);
    }

    /// <summary>`self._mapping[key]`, in a `try` whose miss is the caller's to answer.</summary>
    private static PythonValue MappingGet(PythonValue self, PythonValue key) =>
        Get(Mapping(self), key);

    /// <summary>`ItemsView.value in self`: `key, value = item`, then the mapping's own answer.</summary>
    private static PythonValue ItemsContains(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        var (key, value) = UnpackPair(arguments[0]);
        PythonValue found;
        try
        {
            found = MappingGet(self, key);
        }
        catch (Exception error) when (IsFault(error, "KeyError"))
        {
            return PythonTruthValue.False;
        }
        return ElementMatches(found, value) ? PythonTruthValue.True : PythonTruthValue.False;
    }

    /// <summary>`ValuesView.value in self`: the mapping's values, compared one by one.</summary>
    private static PythonValue ValuesContains(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var mapping = Mapping(self);
        var iterator = ManagedObjectProtocols.GetIterator(mapping);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var key, default))
        {
            if (ElementMatches(Get(mapping, key), arguments[0]))
                return PythonTruthValue.True;
        }
        return PythonTruthValue.False;
    }

    /// <summary>`Mapping.__eq__`: both sides reduced to a dictionary, which is what
    /// `dict(self.items()) == dict(other.items())` compares.</summary>
    private static PythonValue MappingEqual(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        if (!IsAncestor(arguments[0], "Mapping"))
            return PythonNotImplementedValue.Instance;
        return ManagedObjectProtocols.AreEqual(AsDictionary(self), AsDictionary(arguments[0]))
            ? PythonTruthValue.True
            : PythonTruthValue.False;
    }

    /// <summary>`dict(mapping.items())`.</summary>
    private static PythonValue AsDictionary(PythonValue mapping) =>
        Dispatched(PythonBuiltinTypes.Dict, [InvokeOn(mapping, "items", [])]);

    /// <summary>`popitem()`: the first key's pair, removed, or the bare `KeyError` of an
    /// empty mapping.</summary>
    private static PythonTupleValue MappingPopItem(PythonValue self)
    {
        if (First(self) is not { } key)
            throw Bare("KeyError");
        var value = Get(self, key);
        Delete(self, key);
        return new PythonTupleValue([key, value]);
    }

    /// <summary>
    /// `update(other=(), /, **kwds)`: a mapping, anything else with `keys`, or a walk of pairs,
    /// and then the keywords as items of their own.
    /// </summary>
    private static PythonNoneValue MappingUpdate(
        PythonValue self,
        PythonValue? other,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        if (other is not null)
        {
            if (IsAncestor(other, "Mapping"))
            {
                foreach (var key in Materialize(other))
                    Set(self, key, Get(other, key));
            }
            else if (HasKeys(other))
            {
                foreach (var key in Materialize(InvokeOn(other, "keys", [])))
                    Set(self, key, Get(other, key));
            }
            else
            {
                foreach (var item in Materialize(other))
                {
                    var (key, value) = UnpackPair(item);
                    Set(self, key, value);
                }
            }
        }
        for (var index = 0; index < keywordNames.Count; index++)
            Set(self, new PythonTextValue(keywordNames[index]), keywordValues[index]);
        return PythonNoneValue.Instance;
    }

    /// <summary>`hasattr(other, "keys")`, which is how `update` recognizes a mapping-like.</summary>
    private static bool HasKeys(PythonValue other)
    {
        try
        {
            ManagedObjectProtocols.GetAttribute(other, "keys");
        }
        catch (Exception error) when (IsFault(error, "AttributeError"))
        {
            return false;
        }
        return true;
    }

    /// <summary>`key, value = item`, in CPython's own unpacking words.</summary>
    private static (PythonValue Key, PythonValue Value) UnpackPair(PythonValue item)
    {
        IReadOnlyList<PythonValue> elements;
        if (item is PythonTupleValue tuple)
            elements = tuple.Elements;
        else if (item is PythonListValue list)
            elements = list.Elements;
        else if (TryMaterializePair(item) is { } materialized)
            elements = materialized;
        else
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"cannot unpack non-iterable {ManagedObjectProtocols.GetTypeName(item)} object",
                default,
                "TypeError"
            );
        if (elements.Count != 2)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                elements.Count < 2
                    ? $"not enough values to unpack (expected 2, got {elements.Count})"
                    : $"too many values to unpack (expected 2, got {elements.Count})",
                default,
                "ValueError"
            );
        return (elements[0], elements[1]);
    }

    /// <summary>
    /// Iterates a pair candidate, or reports "not iterable" the way `iter()` does before
    /// CPython's unpacking raises its own wording. Errors raised while the iteration itself
    /// runs are passed through untouched.
    /// </summary>
    private static List<PythonValue>? TryMaterializePair(PythonValue item)
    {
        try
        {
            return ManagedObjectProtocols.MaterializeValues(item, default);
        }
        catch (PythonRuntimeException error) when (error.Code == "DPY4015")
        {
            return null;
        }
    }

    /// <summary>`close()`: the exit thrown into the generator or coroutine, which is over
    /// when the throw returns nothing and refused when the callee swallows it.</summary>
    private static PythonNoneValue Closed(PythonValue self, string message)
    {
        try
        {
            InvokeOn(self, "throw", [new PythonExceptionValue("GeneratorExit", string.Empty)]);
        }
        catch (Exception error)
            when (IsFault(error, "GeneratorExit") || IsFault(error, "StopIteration"))
        {
            return PythonNoneValue.Instance;
        }
        throw ManagedObjectProtocols.Fault("DPY4003", message, default, "RuntimeError");
    }

    /// <summary>The elements of the sequence a walk yields, in one list.</summary>
    private static List<PythonValue> Materialize(PythonValue value) =>
        ManagedObjectProtocols.MaterializeValues(value, default);

    /// <summary>The abstract stub `Mapping.__getitem__` is: a method that answers NotImplemented.</summary>
    private static PythonProtocolFunctionValue AbstractStub(string name) =>
        new(name, (_, _) => PythonNotImplementedValue.Instance)
        {
            DeclaringType = "collections.abc",
        };

    /// <summary>
    /// `register(cls)`: a class is added to the registry, after which `isinstance` and
    /// `issubclass` answer for it and everything that descends from it.
    /// </summary>
    private static PythonValue Register(
        PythonManagedTypeValue abc,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"register() takes exactly one argument ({arguments.Count} given)",
                span,
                "TypeError"
            );
        if (arguments[0] is not (PythonManagedTypeValue or PythonBuiltinTypeValue))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "Can only register classes",
                span,
                "TypeError"
            );
        if (!Registered.TryGetValue(abc, out var classes))
            Registered[abc] = classes = [];
        classes.Add(arguments[0]);
        return arguments[0];
    }

    /// <summary>
    /// `__subclasshook__(cls)`: `True` when the class satisfies the structural check this
    /// base class declares, and `NotImplemented` when it declares none or the class fails
    /// it — the answer CPython's hooks give.
    /// </summary>
    private static PythonValue SubclassHook(
        PythonManagedTypeValue abc,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__subclasshook__() takes exactly one argument ({arguments.Count} given)",
                span,
                "TypeError"
            );
        return DuckTypes(abc, arguments[0]) is true
            ? PythonTruthValue.True
            : PythonNotImplementedValue.Instance;
    }

    /// <summary>
    /// Whether a value is an instance of an abstract base class: the classes it is
    /// registered with, the builtin kind the runtime registers, or the structural check.
    /// </summary>
    internal static bool Matches(PythonValue value, PythonManagedTypeValue abc)
    {
        if (!IsAbc(abc))
            return false;
        if (value is PythonManagedObjectValue instance)
        {
            if (
                PythonSubclassStorage.StorageKindOf(instance) is { } kind
                && IsKindRegistered(abc, kind)
            )
                return true;
            if (IsSubclass(instance.Type, abc))
                return true;
        }
        else if (
            value is PythonManagedTypeValue or PythonBuiltinTypeValue or PythonExceptionTypeValue
        )
        {
            // A type object is an instance of its metaclass, which is what makes
            // `isinstance(int, Callable)` true.
            if (IsSubclass(PythonBuiltinTypes.GetRuntimeType(value), abc))
                return true;
        }
        else if (IsKindRegistered(abc, PythonBuiltinTypes.GetRuntimeTypeName(value)))
            return true;
        return DuckTypes(abc, value) is true;
    }

    /// <summary>
    /// Whether a class is a subclass of an abstract base class — the class itself, its
    /// resolution order, its registrations, or the structural check.
    /// </summary>
    internal static bool IsSubclass(PythonValue cls, PythonManagedTypeValue abc)
    {
        if (!IsAbc(abc))
            return false;
        if (ReferenceEquals(cls, abc) || IsRegistered(abc, cls))
            return true;
        if (cls is PythonManagedTypeValue managed)
        {
            if (
                managed.Mro.Any(entry => ReferenceEquals(entry, abc))
                || IsRegistered(abc, cls)
                || IsKindBase(abc, managed)
                || KindCarries(managed.Name, "__iter__") && managed.Name == "generator"
            )
                return true;
        }
        if (cls is PythonBuiltinTypeValue builtin && IsKindRegistered(abc, builtin.Name))
            return true;
        return DuckTypes(abc, cls) is true;
    }

    /// <summary>
    /// Whether a managed class descends from a builtin kind the runtime registers — the
    /// `dict` or `list` a native subclass or a user class is built on.
    /// </summary>
    private static bool IsKindBase(PythonManagedTypeValue abc, PythonManagedTypeValue type) =>
        PythonTypeLayout.GetSolidBase(type) is PythonBuiltinTypeValue { Name: var name }
        && IsKindRegistered(abc, name);

    /// <summary>
    /// The structural check of a class: whether the queried value or class carries every
    /// method the check names, with `Hashable`'s own rule that the hash is not None.
    /// </summary>
    private static bool? DuckTypes(PythonManagedTypeValue abc, PythonValue candidate)
    {
        var definition = Array.Find(Definitions, entry => entry.Name == abc.Name);
        if (definition is null)
            return null;
        if (definition.Hashes)
            return IsHashable(candidate) ? true : null;
        if (definition.DuckMethods is not { } names)
            return null;
        foreach (var name in names)
        {
            if (!Carries(candidate, name))
                return null;
        }
        return true;
    }

    private static bool IsHashable(PythonValue candidate)
    {
        if (candidate is PythonManagedObjectValue instance)
            return UserObjectProtocols.IsHashable(instance);
        if (candidate is PythonManagedTypeValue managed)
            return !ManagedObjectProtocols.TryGetTypeAttribute(managed, "__hash__", out var hash)
                || hash is not PythonNoneValue;
        var name = PythonBuiltinTypes.GetRuntimeTypeName(candidate);
        return !PythonSlotMethods.HasNoneHash(name);
    }

    /// <summary>
    /// The methods a kind carries by name, which is how the type object of an iterator or a
    /// generator answers `issubclass` — the same answer CPython's own hook gives.
    /// </summary>
    private static bool KindCarries(string kind, string name) =>
        name switch
        {
            "__iter__" or "__next__" => kind
                is "iterator"
                    or "generator"
                    or "map"
                    or "zip"
                    or "filter"
                    or "enumerate"
                || kind.EndsWith("_iterator", StringComparison.Ordinal),
            "send" or "throw" or "close" => kind == "generator",
            "__aiter__" or "__anext__" or "asend" or "athrow" or "aclose" => kind
                == "async_generator",
            "__call__" => kind == "type",
            _ => false,
        };

    /// <summary>
    /// The methods the runtime's iterator kinds carry: an iterator is its own `__iter__` and
    /// answers `__next__`, and a generator adds the four a generator exposes.
    /// </summary>
    private static bool CarriesIteratorMethods(PythonValue candidate, string name) =>
        candidate switch
        {
            PythonGeneratorValue { IsAsyncGenerator: true } => AsyncGenerated(name),
            PythonGeneratorValue generator => name is "__iter__" or "__next__"
                || name is "send" or "throw" or "close" && !generator.IsCoroutine,
            PythonIteratorValue => name is "__iter__" or "__next__",
            PythonMapSourceValue
            or PythonZipSourceValue
            or PythonFilterSourceValue
            or PythonEnumerateSourceValue => name is "__iter__" or "__next__",
            _ => false,
        };

    /// <summary>Whether an async generator carries a method of the `async_generator` protocol.</summary>
    private static bool AsyncGenerated(string name) =>
        name is "__aiter__" or "__anext__" or "asend" or "athrow" or "aclose";

    /// <summary>Whether a value — or the class it is — carries a method under that name.</summary>
    private static bool Carries(PythonValue candidate, string name)
    {
        // A class answers `__call__` for its instances, and anything else for itself:
        // `issubclass(dict, Callable)` is False while `isinstance(dict, Callable)` is True,
        // because the second asks about the type object, which `type` makes callable.
        if (name == "__call__")
        {
            return candidate switch
            {
                PythonManagedTypeValue classCandidate => classCandidate.Mro.Any(entry =>
                    entry.Attributes.TryGetValue(name, out _)
                ),
                PythonBuiltinTypeValue kindCandidate => KindCarries(kindCandidate.Name, name)
                    || PythonSlotMethods.GetDescriptor(kindCandidate.Name, name) is not null,
                _ => ManagedObjectProtocols.IsCallable(candidate),
            };
        }
        if (candidate is PythonManagedTypeValue managed)
            return managed.Mro.Any(entry => entry.Attributes.TryGetValue(name, out _))
                // A managed type that stands for one of the runtime's own kinds — the
                // `generator` or `list_iterator` a value reports — carries what that kind
                // carries.
                || (managed.Module is null && KindCarries(managed.Name, name));
        if (candidate is PythonManagedObjectValue instance)
            return instance.Type.Mro.Any(entry => entry.Attributes.TryGetValue(name, out _));
        if (candidate is PythonBuiltinTypeValue builtinType)
            return KindCarries(builtinType.Name, name)
                || PythonSlotMethods.GetDescriptor(builtinType.Name, name) is not null;
        // The runtime's own iterator kinds answer the two methods the iterator protocol
        // gives them, and a generator the four a generator adds.
        if (
            CarriesIteratorMethods(candidate, name)
            || KindCarries(PythonBuiltinTypes.GetRuntimeTypeName(candidate), name)
        )
            return true;
        // A builtin value answers from the method tables its kind publishes, which is where
        // `__iter__`, `__len__` and the rest are defined.
        var kind = PythonBuiltinTypes.GetRuntimeTypeName(candidate);
        return PythonSlotMethods.GetDescriptor(kind, name) is not null
            || PythonBuiltinMethods.TryGet(candidate, name, out _);
    }

    private static bool IsAbc(PythonManagedTypeValue abc) =>
        ReferenceEquals(abc.Metaclass, AbcMeta);

    private static IEnumerable<PythonValue> CandidateTypes(PythonValue value)
    {
        if (value is PythonManagedObjectValue instance)
        {
            yield return instance.Type;
            yield break;
        }
        if (value is PythonManagedTypeValue or PythonBuiltinTypeValue or PythonExceptionTypeValue)
            yield return value;
    }

    private static bool IsRegistered(PythonManagedTypeValue abc, PythonValue cls) =>
        Registered.TryGetValue(abc, out var classes) && classes.Contains(cls);

    /// <summary>
    /// Whether a builtin kind is registered with this class or with one of its subclasses:
    /// CPython registers `dict` with `MutableMapping`, and that registration answers for
    /// `Mapping` too, never the other way round.
    /// </summary>
    private static bool IsKindRegistered(PythonManagedTypeValue abc, string kind) =>
        Array.Find(RegisteredKinds, entry => entry.Name == kind) is { Name: not null } registration
        && registration.Abcs.Any(name =>
            Classes[name].Mro.Any(entry => ReferenceEquals(entry, abc))
        );

    /// <summary>
    /// The refusal an abstract class reports when it is instantiated without every abstract
    /// method implemented, in CPython's wording and with the names sorted.
    /// </summary>
    internal static PythonRuntimeException? RefuseInstantiation(
        PythonManagedTypeValue type,
        TextSpan span
    )
    {
        // The nearest abstract base class declares the set this class inherits, which is the
        // set CPython computes for it when the class is created.
        PythonSetValue? declared = null;
        foreach (var entry in type.Mro)
        {
            if (
                entry.Attributes.TryGetValue("__abstractmethods__", out var value)
                && value is PythonSetValue names
            )
            {
                declared = names;
                break;
            }
        }
        if (declared is null)
            return null;
        var missing = new List<string>();
        foreach (var name in declared.Elements)
        {
            var text = ((PythonTextValue)name).Value;
            // A name still answers with the stub when the class and its bases below the one
            // that declares it implement nothing.
            if (ResolvesToAbstractStub(type, text) && !missing.Contains(text))
                missing.Add(text);
        }
        if (missing.Count == 0)
            return null;
        missing.Sort(StringComparer.Ordinal);
        var listed = string.Join(", ", missing.Select(name => $"'{name}'"));
        return ManagedObjectProtocols.Fault(
            "DPY4009",
            $"Can't instantiate abstract class {type.Name} without an implementation for "
                + (missing.Count == 1 ? "abstract method " : "abstract methods ")
                + listed,
            span,
            "TypeError"
        );
    }

    /// <summary>Whether the class still answers an abstract name with the stub itself.</summary>
    internal static bool ResolvesToAbstractStub(PythonManagedTypeValue type, string name)
    {
        foreach (var entry in type.Mro)
        {
            if (entry.Attributes.TryGetValue(name, out var value))
                return value is PythonProtocolFunctionValue { DeclaringType: "collections.abc" };
        }
        return false;
    }
}
