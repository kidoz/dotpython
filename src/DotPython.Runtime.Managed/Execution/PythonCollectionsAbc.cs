// The abstract base classes follow CPython 3.14.7 Lib/_collections_abc.py and Lib/abc.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

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
    private sealed record Abc(
        string Name,
        string[] Bases,
        string[] AbstractMethods,
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
        new("Awaitable", [], ["__await__"], ["__await__"]),
        new("AsyncIterable", [], ["__aiter__"], ["__aiter__"]),
        new("AsyncIterator", ["AsyncIterable"], ["__anext__"], ["__aiter__", "__anext__"]),
        new(
            "AsyncGenerator",
            ["AsyncIterator"],
            ["asend", "athrow"],
            ["__aiter__", "__anext__", "asend", "athrow", "aclose"]
        ),
        new("Iterable", [], ["__iter__"], ["__iter__"]),
        new("Iterator", ["Iterable"], ["__next__"], ["__iter__", "__next__"]),
        new("Reversible", ["Iterable"], ["__iter__", "__reversed__"]),
        new("Sized", [], ["__len__"], ["__len__"]),
        new("Container", [], ["__contains__"], ["__contains__"]),
        new("Callable", [], ["__call__"], ["__call__"]),
        new("Buffer", [], ["__buffer__"], ["__buffer__"]),
        new(
            "Coroutine",
            ["Awaitable"],
            ["__await__", "send", "throw"],
            ["__await__", "send", "throw", "close"]
        ),
        new("Generator", ["Iterator"], ["send", "throw"], ["send", "throw", "close"]),
        new(
            "Collection",
            ["Sized", "Iterable", "Container"],
            ["__contains__", "__iter__", "__len__"]
        ),
        new("Set", ["Collection"], ["__contains__", "__iter__", "__len__"]),
        new("MutableSet", ["Set"], ["__contains__", "__iter__", "__len__", "add", "discard"]),
        new("Mapping", ["Collection"], ["__getitem__", "__iter__", "__len__"]),
        new(
            "MutableMapping",
            ["Mapping"],
            ["__delitem__", "__getitem__", "__iter__", "__len__", "__setitem__"]
        ),
        new("MappingView", ["Sized"], []),
        new("KeysView", ["MappingView", "Set"], []),
        new("ItemsView", ["MappingView", "Set"], []),
        new("ValuesView", ["MappingView", "Collection"], []),
        new("Sequence", ["Reversible", "Collection"], ["__getitem__", "__len__"]),
        new(
            "MutableSequence",
            ["Sequence"],
            ["__delitem__", "__getitem__", "__len__", "__setitem__", "insert"]
        ),
        new("ByteString", ["Sequence"], ["__getitem__", "__len__"]),
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

    /// <summary>`abc.ABCMeta`, the metaclass every class here is an instance of.</summary>
    internal static readonly PythonManagedTypeValue AbcMeta = CreateMetaClass();

    /// <summary>The `collections.abc` module's names.</summary>
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("ABCMeta", AbcMeta);
        foreach (var definition in Definitions)
            globals.SetValue(definition.Name, Build(definition));
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
        // has not implemented them can name them.
        foreach (var name in definition.AbstractMethods)
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
        Classes[definition.Name] = type;
        return type;
    }

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
            if (ResolvesToStub(type, text) && !missing.Contains(text))
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
    private static bool ResolvesToStub(PythonManagedTypeValue type, string name)
    {
        foreach (var entry in type.Mro)
        {
            if (entry.Attributes.TryGetValue(name, out var value))
                return value is PythonProtocolFunctionValue { DeclaringType: "collections.abc" };
        }
        return false;
    }
}
