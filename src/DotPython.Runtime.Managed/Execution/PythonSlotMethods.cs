// The slot tables follow CPython 3.14.7 Objects/descrobject.c (slot wrappers and their
// diagnostics) and the per-type slot definitions in Objects/*.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The dunder methods a type object answers, as <c>__len__</c> and its family. They are the
/// same operations the interpreter runs for <c>len()</c>, indexing, iteration and the
/// operators, reached through the type instead of the value.
/// </summary>
/// <remarks>
/// A slot wrapper reports its own argument errors — `expected 1 argument, got 2` without
/// naming anything — and takes no keywords, where a method descriptor names its owner the
/// way `list.__getitem__()` does.
/// </remarks>
internal static class PythonSlotMethods
{
    private static void AddName(List<string> names, string name)
    {
        if (!names.Contains(name))
            names.Add(name);
    }

    internal readonly record struct Slot(bool IsWrapper, PythonProtocolFunctionValue Function);

    /// <summary>
    /// The types whose `__hash__` is the value `None` rather than a descriptor, which is how
    /// CPython marks a type unhashable.
    /// </summary>
    internal static bool HasNoneHash(string typeName) =>
        typeName is "list" or "bytearray" or "dict" or "set" or "deque";

    internal static bool TryGet(string typeName, string name, out Slot slot) =>
        Slots.TryGetValue((typeName, name), out slot);

    /// <summary>Every slot name a type exposes, for `dir` and `__dir__`.</summary>
    internal static void AddSlotNames(string typeName, List<string> names)
    {
        // A bool answers the int slots.
        var ownerName = typeName == "bool" ? "int" : typeName;
        foreach (var (owner, name) in Slots.Keys)
        {
            if (owner == ownerName)
                AddName(names, name);
        }
        if (HasNoneHash(typeName))
            AddName(names, "__hash__");
        if (PythonIntrospection.HasSize(typeName))
            AddName(names, "__sizeof__");
    }

    /// <summary>
    /// The slot a *value* of this type answers, which is the same function the type object
    /// publishes. A wrapper refuses keywords with its own wording, as it does when unbound.
    /// </summary>
    internal static bool TryGetForValue(
        string typeName,
        string name,
        out PythonProtocolFunctionValue function,
        out bool isWrapper
    )
    {
        // A bool answers the int slots.
        var ownerName = typeName == "bool" ? "int" : typeName;
        if (!Slots.TryGetValue((ownerName, name), out var slot))
        {
            function = null!;
            isWrapper = false;
            return false;
        }
        isWrapper = slot.IsWrapper;
        function = slot.IsWrapper
            ? slot.Function with
            {
                InvokeWithKeywords = (_, _, _, _) =>
                    throw Fault($"wrapper {name}() takes no keyword arguments"),
            }
            : slot.Function;
        return true;
    }

    /// <summary>Whether the member is `object`'s, inherited by a type that has no own one.</summary>
    internal static bool IsObjectSlot(string typeName, string name) =>
        ObjectSlots.Contains((typeName, name));

    /// <summary>
    /// The interned descriptor for a slot, or null when the type has no such slot. A bool
    /// reports the type it borrowed from, so `bool.__eq__` is int's slot.
    /// </summary>
    internal static PythonMethodDescriptorValue? GetDescriptor(string typeName, string name)
    {
        var ownerName = typeName == "bool" ? "int" : typeName;
        if (!TryGet(ownerName, name, out var slot))
            return null;
        lock (Descriptors)
        {
            var descriptorOwner = IsObjectSlot(ownerName, name) ? "object" : ownerName;
            if (!Descriptors.TryGetValue((descriptorOwner, name), out var descriptor))
            {
                descriptor = new PythonMethodDescriptorValue(
                    PythonBuiltinTypes.ForName(descriptorOwner),
                    name,
                    slot.Function
                )
                {
                    IsWrapper = slot.IsWrapper,
                };
                Descriptors[(descriptorOwner, name)] = descriptor;
            }
            return descriptor;
        }
    }

    private static readonly Dictionary<
        (string Owner, string Name),
        PythonMethodDescriptorValue
    > Descriptors = new();

    /// <summary>
    /// The members inherited from `object`, which answer for any receiver. It is declared
    /// before the table because the table's initializer fills it.
    /// </summary>
    private static readonly HashSet<(string Type, string Name)> ObjectSlots = [];

    private static readonly Dictionary<(string Type, string Name), Slot> Slots = Create();

    private static Dictionary<(string Type, string Name), Slot> Create()
    {
        var slots = new Dictionary<(string Type, string Name), Slot>();

        // A slot wrapper of N parameters, reported without a name.
        void Wrapper(
            string type,
            string name,
            int parameters,
            Func<PythonValue, IReadOnlyList<PythonValue>, PythonValue> body
        ) =>
            slots[(type, name)] = new Slot(
                true,
                new PythonProtocolFunctionValue(
                    name,
                    (receiver, arguments) =>
                    {
                        if (arguments.Count != parameters)
                            throw Fault(
                                $"expected {parameters} argument{(parameters == 1 ? "" : "s")}, "
                                    + $"got {arguments.Count}"
                            );
                        return body(receiver!, arguments);
                    }
                )
            );

        // A slot whose wrapper names itself, as the subscript family does.
        void Named(
            string type,
            string name,
            int parameters,
            Func<PythonValue, IReadOnlyList<PythonValue>, PythonValue> body
        ) =>
            slots[(type, name)] = new Slot(
                true,
                new PythonProtocolFunctionValue(
                    name,
                    (receiver, arguments) =>
                    {
                        if (arguments.Count != parameters)
                            throw Fault(
                                $"{name} expected {parameters} arguments, got {arguments.Count}"
                            );
                        return body(receiver!, arguments);
                    }
                )
            );

        // A method descriptor: the owner-qualified family, as `list.__getitem__` uses.
        void Method(
            string type,
            string name,
            Func<PythonValue, IReadOnlyList<PythonValue>, PythonValue> body
        ) =>
            slots[(type, name)] = new Slot(
                false,
                new PythonProtocolFunctionValue(
                    name,
                    (receiver, arguments) => body(receiver!, arguments)
                )
            );

        Method(
            "list",
            "__getitem__",
            (receiver, arguments) => GetItem("list", receiver, arguments)
        );
        Method(
            "dict",
            "__getitem__",
            (receiver, arguments) => GetItem("dict", receiver, arguments)
        );
        Method(
            "list",
            "__reversed__",
            (receiver, arguments) => Reversed("list", receiver, arguments)
        );
        Method(
            "dict",
            "__reversed__",
            (receiver, arguments) => Reversed("dict", receiver, arguments)
        );
        Method(
            "tuple",
            "__getnewargs__",
            (receiver, arguments) => NewArguments("tuple", receiver, arguments)
        );
        Method(
            "str",
            "__getnewargs__",
            (receiver, arguments) => NewArguments("str", receiver, arguments)
        );
        Method(
            "bytes",
            "__getnewargs__",
            (receiver, arguments) => NewArguments("bytes", receiver, arguments)
        );
        Method(
            "int",
            "__getnewargs__",
            (receiver, arguments) => NewArguments("int", receiver, arguments)
        );
        Method(
            "float",
            "__getnewargs__",
            (receiver, arguments) => NewArguments("float", receiver, arguments)
        );
        Method("str", "__format__", (receiver, arguments) => Format("str", receiver, arguments));
        Method("int", "__format__", (receiver, arguments) => Format("int", receiver, arguments));
        Method(
            "float",
            "__format__",
            (receiver, arguments) => Format("float", receiver, arguments)
        );

        foreach (
            var type in new[]
            {
                "list",
                "tuple",
                "str",
                "bytes",
                "bytearray",
                "dict",
                "set",
                "frozenset",
            }
        )
        {
            Wrapper(
                type,
                "__len__",
                0,
                (receiver, _) =>
                    PythonWholeNumberValue.Create(ManagedObjectProtocols.GetLength(receiver))
            );
            Wrapper(
                type,
                "__iter__",
                0,
                (receiver, _) => ManagedObjectProtocols.GetIterator(receiver)
            );
            Wrapper(
                type,
                "__repr__",
                0,
                (receiver, _) => new PythonTextValue(receiver.ToRepresentationString())
            );
        }

        // These answers are method descriptors rather than wrappers, so they report through
        // the owner-qualified family; each one checks its own argument count.
        foreach (
            var (type, name, body) in new (
                string,
                string,
                Func<string, PythonValue, IReadOnlyList<PythonValue>, PythonValue>
            )[]
            {
                ("list", "__getitem__", GetItem),
                ("dict", "__getitem__", GetItem),
                ("dict", "__contains__", Contains),
                ("set", "__contains__", Contains),
                ("frozenset", "__contains__", Contains),
            }
        )
        {
            slots[(type, name)] = new Slot(
                false,
                new PythonProtocolFunctionValue(
                    name,
                    (receiver, arguments) => body(type, receiver!, arguments)
                )
            );
        }

        foreach (var type in new[] { "list", "tuple", "str", "bytes", "bytearray" })
        {
            Wrapper(
                type,
                "__contains__",
                1,
                (receiver, arguments) =>
                    ManagedObjectProtocols.Contains(receiver, arguments[0])
                        ? PythonTruthValue.True
                        : PythonTruthValue.False
            );
            // list's and dict's `__getitem__` are method descriptors, registered above.
            if (type is not "list")
                Wrapper(
                    type,
                    "__getitem__",
                    1,
                    (receiver, arguments) => ManagedObjectProtocols.GetItem(receiver, arguments[0])
                );
        }

        foreach (var type in new[] { "int", "float" })
        {
            Wrapper(
                type,
                "__repr__",
                0,
                (receiver, _) => new PythonTextValue(receiver.ToRepresentationString())
            );
        }

        // A view answers the sequence protocol, the two comparisons it has — equality, which
        // asks the bytes and the shape — and the hash its own contents allow.
        Wrapper(
            "memoryview",
            "__len__",
            0,
            (receiver, _) =>
                PythonWholeNumberValue.Create(
                    ManagedObjectProtocols.GetViewLength((PythonMemoryViewValue)receiver)
                )
        );
        Wrapper(
            "memoryview",
            "__iter__",
            0,
            (receiver, _) => ManagedObjectProtocols.GetIterator(receiver)
        );
        Wrapper(
            "memoryview",
            "__repr__",
            0,
            (receiver, _) => new PythonTextValue(receiver.ToRepresentationString())
        );
        Wrapper(
            "memoryview",
            "__contains__",
            1,
            (receiver, arguments) =>
                ManagedObjectProtocols.Contains(receiver, arguments[0])
                    ? PythonTruthValue.True
                    : PythonTruthValue.False
        );
        Wrapper(
            "memoryview",
            "__getitem__",
            1,
            (receiver, arguments) => ManagedObjectProtocols.GetItem(receiver, arguments[0])
        );
        Named(
            "memoryview",
            "__setitem__",
            2,
            (receiver, arguments) =>
            {
                ManagedObjectProtocols.SetItem(receiver, arguments[0], arguments[1]);
                return PythonNoneValue.Instance;
            }
        );
        Wrapper(
            "memoryview",
            "__delitem__",
            1,
            (receiver, arguments) =>
            {
                ManagedObjectProtocols.DeleteItem(receiver, arguments[0]);
                return PythonNoneValue.Instance;
            }
        );
        foreach (
            var (name, operation) in new[]
            {
                ("__eq__", 0),
                ("__ne__", 4),
                ("__lt__", 1),
                ("__le__", 2),
                ("__gt__", 3),
                ("__ge__", 5),
            }
        )
        {
            var comparison = operation;
            Wrapper(
                "memoryview",
                name,
                1,
                (receiver, arguments) => Compare("memoryview", receiver, arguments[0], comparison)
            );
        }
        Wrapper(
            "memoryview",
            "__hash__",
            0,
            (receiver, _) =>
                PythonWholeNumberValue.Create(ManagedObjectProtocols.ComputePythonHash(receiver))
        );

        // A range answers the sequence protocol through slots of its own, and its reversal
        // as a method rather than a slot.
        Wrapper(
            "range",
            "__len__",
            0,
            (receiver, _) =>
                PythonWholeNumberValue.Create(ManagedObjectProtocols.GetLength(receiver))
        );
        Wrapper(
            "range",
            "__iter__",
            0,
            (receiver, _) => ManagedObjectProtocols.GetIterator(receiver)
        );
        Wrapper(
            "range",
            "__getitem__",
            1,
            (receiver, arguments) => ManagedObjectProtocols.GetItem(receiver, arguments[0])
        );
        Wrapper(
            "range",
            "__contains__",
            1,
            (receiver, arguments) =>
                ManagedObjectProtocols.Contains(receiver, arguments[0])
                    ? PythonTruthValue.True
                    : PythonTruthValue.False
        );
        Method(
            "range",
            "__reversed__",
            (receiver, _) => PythonReverseIterators.Create(receiver!, default)
        );
        foreach (
            var (name, operation) in new[]
            {
                ("__eq__", 0),
                ("__ne__", 4),
                ("__lt__", 1),
                ("__le__", 2),
                ("__gt__", 3),
                ("__ge__", 5),
            }
        )
        {
            var comparison = operation;
            Wrapper(
                "range",
                name,
                1,
                (receiver, arguments) => Compare("range", receiver, arguments[0], comparison)
            );
        }
        Wrapper(
            "range",
            "__hash__",
            0,
            (receiver, _) =>
                PythonWholeNumberValue.Create(ManagedObjectProtocols.ComputePythonHash(receiver))
        );

        // A deque answers the sequence protocol, the two ends of it, concatenation and
        // repetition, and the comparisons — its contents decide equality and ordering.
        Wrapper(
            "deque",
            "__len__",
            0,
            (receiver, _) =>
                PythonWholeNumberValue.Create(ManagedObjectProtocols.GetLength(receiver))
        );
        Wrapper(
            "deque",
            "__iter__",
            0,
            (receiver, _) => ManagedObjectProtocols.GetIterator(receiver)
        );
        Method(
            "deque",
            "__reversed__",
            (receiver, _) => PythonReverseIterators.Create(receiver!, default)
        );
        Wrapper(
            "deque",
            "__getitem__",
            1,
            (receiver, arguments) => ManagedObjectProtocols.GetItem(receiver, arguments[0])
        );
        Named(
            "deque",
            "__setitem__",
            2,
            (receiver, arguments) =>
            {
                ManagedObjectProtocols.SetItem(receiver, arguments[0], arguments[1]);
                return PythonNoneValue.Instance;
            }
        );
        Wrapper(
            "deque",
            "__delitem__",
            1,
            (receiver, arguments) =>
            {
                ManagedObjectProtocols.DeleteItem(receiver, arguments[0]);
                return PythonNoneValue.Instance;
            }
        );
        slots[("deque", "__init__")] = new Slot(
            true,
            new PythonProtocolFunctionValue(
                "__init__",
                (receiver, arguments) =>
                    PythonDequeMethods.Reinitialize((PythonDequeValue)receiver!, arguments, default)
            )
        );
        Wrapper(
            "deque",
            "__contains__",
            1,
            (receiver, arguments) =>
                ManagedObjectProtocols.Contains(receiver, arguments[0])
                    ? PythonTruthValue.True
                    : PythonTruthValue.False
        );
        Wrapper(
            "deque",
            "__repr__",
            0,
            (receiver, _) => new PythonTextValue(receiver.ToRepresentationString())
        );
        Wrapper(
            "deque",
            "__add__",
            1,
            (receiver, arguments) =>
                Binary("deque", receiver, arguments[0], PythonOpCode.BinaryAdd, "add")
        );
        Wrapper(
            "deque",
            "__iadd__",
            1,
            (receiver, arguments) =>
                PythonDequeMethods.ExtendInPlace((PythonDequeValue)receiver!, arguments[0], default)
        );
        Wrapper(
            "deque",
            "__mul__",
            1,
            (receiver, arguments) =>
                Binary("deque", receiver, arguments[0], PythonOpCode.BinaryMultiply, "mul")
        );
        Wrapper(
            "deque",
            "__rmul__",
            1,
            (receiver, arguments) =>
                Binary("deque", receiver, arguments[0], PythonOpCode.BinaryMultiply, "mul")
        );
        Wrapper(
            "deque",
            "__imul__",
            1,
            (receiver, arguments) =>
                PythonDequeMethods.RepeatInPlace((PythonDequeValue)receiver!, arguments[0], default)
        );
        foreach (
            var (name, operation) in new[]
            {
                ("__eq__", 0),
                ("__ne__", 4),
                ("__lt__", 1),
                ("__le__", 2),
                ("__gt__", 3),
                ("__ge__", 5),
            }
        )
        {
            var comparison = operation;
            Wrapper(
                "deque",
                name,
                1,
                (receiver, arguments) => Compare("deque", receiver, arguments[0], comparison)
            );
        }

        // The PEP 688 exporters: every bytes-like builtin hands out a view of itself, and
        // the mutable one and a view also take that export back again. They are methods of
        // one argument, which report a wrong count in `METH_O`'s own words rather than in
        // the wrapper family's.
        void Exporter(string type, string name, Func<PythonValue, PythonValue, PythonValue> body) =>
            slots[(type, name)] = new Slot(
                true,
                new PythonProtocolFunctionValue(
                    name,
                    (receiver, arguments) =>
                        arguments.Count == 1
                            ? body(receiver!, arguments[0])
                            : throw Fault($"{name} expected 1 argument, got {arguments.Count}")
                )
            );

        foreach (var type in new[] { "bytes", "bytearray", "memoryview" })
        {
            Exporter(
                type,
                "__buffer__",
                (receiver, flags) => PythonBufferProtocol.Export(receiver, flags, default)
            );
        }
        foreach (var type in new[] { "bytearray", "memoryview" })
        {
            Exporter(
                type,
                "__release_buffer__",
                (receiver, export) => PythonBufferProtocol.ReleaseExport(receiver, export, default)
            );
        }

        foreach (var type in new[] { "list", "bytearray", "dict" })
        {
            Named(
                type,
                "__setitem__",
                2,
                (receiver, arguments) =>
                {
                    ManagedObjectProtocols.SetItem(receiver, arguments[0], arguments[1]);
                    return PythonNoneValue.Instance;
                }
            );
            // `__delitem__` reports through the plain wrapper family, unlike `__setitem__`.
            Wrapper(
                type,
                "__delitem__",
                1,
                (receiver, arguments) =>
                {
                    ManagedObjectProtocols.DeleteItem(receiver, arguments[0]);
                    return PythonNoneValue.Instance;
                }
            );
        }

        foreach (
            var type in new[]
            {
                "list",
                "tuple",
                "str",
                "bytes",
                "bytearray",
                "dict",
                "set",
                "frozenset",
                "int",
                "float",
            }
        )
        {
            Wrapper(
                type,
                "__eq__",
                1,
                (receiver, arguments) => Compare(type, receiver, arguments[0], 0)
            );
            Wrapper(
                type,
                "__ne__",
                1,
                (receiver, arguments) => Compare(type, receiver, arguments[0], 4)
            );
            Wrapper(
                type,
                "__lt__",
                1,
                (receiver, arguments) => Compare(type, receiver, arguments[0], 1)
            );
            Wrapper(
                type,
                "__le__",
                1,
                (receiver, arguments) => Compare(type, receiver, arguments[0], 2)
            );
            Wrapper(
                type,
                "__gt__",
                1,
                (receiver, arguments) => Compare(type, receiver, arguments[0], 3)
            );
            Wrapper(
                type,
                "__ge__",
                1,
                (receiver, arguments) => Compare(type, receiver, arguments[0], 5)
            );
        }

        foreach (var type in new[] { "tuple", "str", "bytes", "frozenset", "int", "float" })
        {
            Wrapper(
                type,
                "__hash__",
                0,
                (receiver, _) =>
                    PythonWholeNumberValue.Create(
                        ManagedObjectProtocols.ComputePythonHash(receiver)
                    )
            );
        }

        foreach (var type in new[] { "list", "tuple", "str", "bytes", "bytearray" })
        {
            Wrapper(
                type,
                "__add__",
                1,
                (receiver, arguments) =>
                    Binary(type, receiver, arguments[0], PythonOpCode.BinaryAdd, "add")
            );
            Wrapper(
                type,
                "__mul__",
                1,
                (receiver, arguments) =>
                    Binary(type, receiver, arguments[0], PythonOpCode.BinaryMultiply, "mul")
            );
            Wrapper(
                type,
                "__rmul__",
                1,
                (receiver, arguments) =>
                    Binary(type, receiver, arguments[0], PythonOpCode.BinaryMultiply, "mul")
            );
        }

        foreach (var type in new[] { "list", "bytearray" })
        {
            Wrapper(
                type,
                "__iadd__",
                1,
                (receiver, arguments) => InPlaceAdd(receiver, arguments[0])
            );
            Wrapper(
                type,
                "__imul__",
                1,
                (receiver, arguments) => InPlaceMultiply(receiver, arguments[0])
            );
        }

        // `object.__str__` is inherited by every type that does not define its own, and it
        // accepts any receiver, so `list.__str__(1)` is `'1'`.
        foreach (var type in new[] { "list", "tuple", "dict", "set", "frozenset", "int", "float" })
        {
            slots[(type, "__str__")] = new Slot(
                true,
                new PythonProtocolFunctionValue(
                    "__str__",
                    (receiver, arguments) =>
                    {
                        if (arguments.Count != 0)
                            throw Fault($"expected 0 arguments, got {arguments.Count}");
                        return new PythonTextValue(receiver!.ToDisplayString());
                    }
                )
            );
            ObjectSlots.Add((type, "__str__"));
        }

        Wrapper("str", "__str__", 0, (receiver, _) => receiver);
        Wrapper(
            "bytes",
            "__str__",
            0,
            (receiver, _) => new PythonTextValue(receiver.ToRepresentationString())
        );
        Wrapper(
            "bytearray",
            "__str__",
            0,
            (receiver, _) => new PythonTextValue(receiver.ToRepresentationString())
        );

        foreach (var type in new[] { "str", "bytes", "bytearray", "int", "float" })
        {
            Wrapper(
                type,
                "__mod__",
                1,
                (receiver, arguments) =>
                    Binary(type, receiver, arguments[0], PythonOpCode.BinaryModulo, "mod")
            );
        }

        foreach (var type in new[] { "list", "bytearray", "dict", "set" })
        {
            slots[(type, "__init__")] = new Slot(
                true,
                new PythonProtocolFunctionValue(
                    "__init__",
                    (receiver, arguments) =>
                    {
                        if (arguments.Count > 1)
                            throw Fault(
                                $"{type} expected at most 1 argument, got {arguments.Count}"
                            );
                        Initialize(type, receiver!, arguments);
                        return PythonNoneValue.Instance;
                    }
                )
            );
        }

        foreach (var type in new[] { "set", "frozenset" })
        {
            foreach (
                var (name, opCode) in new[]
                {
                    ("__or__", PythonOpCode.BinaryOr),
                    ("__and__", PythonOpCode.BinaryAnd),
                    ("__xor__", PythonOpCode.BinaryXor),
                    ("__sub__", PythonOpCode.BinarySubtract),
                }
            )
            {
                Wrapper(
                    type,
                    name,
                    1,
                    (receiver, arguments) => Binary(type, receiver, arguments[0], opCode, name)
                );
            }

            foreach (
                var (name, opCode) in new[]
                {
                    ("__ror__", PythonOpCode.BinaryOr),
                    ("__rand__", PythonOpCode.BinaryAnd),
                    ("__rxor__", PythonOpCode.BinaryXor),
                    ("__rsub__", PythonOpCode.BinarySubtract),
                }
            )
            {
                Wrapper(
                    type,
                    name,
                    1,
                    (receiver, arguments) => Reflected(type, receiver, arguments[0], opCode, name)
                );
            }
        }

        foreach (var name in new[] { "__ior__", "__iand__", "__ixor__", "__isub__" })
        {
            Wrapper(
                "set",
                name,
                1,
                (receiver, arguments) =>
                    InPlaceOperation("set", receiver, arguments[0], SetInPlaceMethod(name))
            );
        }

        Wrapper(
            "dict",
            "__or__",
            1,
            (receiver, arguments) =>
                Binary("dict", receiver, arguments[0], PythonOpCode.BinaryOr, "__or__")
        );
        Wrapper(
            "dict",
            "__ror__",
            1,
            (receiver, arguments) =>
                Reflected("dict", receiver, arguments[0], PythonOpCode.BinaryOr, "__ror__")
        );
        Wrapper(
            "dict",
            "__ior__",
            1,
            (receiver, arguments) => InPlaceOperation("dict", receiver, arguments[0], "update")
        );

        foreach (var type in new[] { "int", "float" })
        {
            Wrapper(type, "__int__", 0, (receiver, _) => WholeNumber(receiver));
            Wrapper(type, "__float__", 0, (receiver, _) => FloatingPoint(receiver));
            Wrapper(
                type,
                "__bool__",
                0,
                (receiver, _) =>
                    ManagedObjectProtocols.IsTrue(receiver)
                        ? PythonTruthValue.True
                        : PythonTruthValue.False
            );
            Wrapper(type, "__abs__", 0, (receiver, _) => Absolute(receiver));
            Wrapper(type, "__neg__", 0, (receiver, _) => Negated(receiver));
            Wrapper(type, "__pos__", 0, (receiver, _) => Positive(receiver));
        }

        // The numeric operators, forwards and reflected. A reflected call swaps the
        // operands; the result is the one the operator itself produces.
        foreach (
            var (name, opCode, reflected) in new (
                string Name,
                PythonOpCode OpCode,
                bool Reflected
            )[]
            {
                ("__add__", PythonOpCode.BinaryAdd, false),
                ("__radd__", PythonOpCode.BinaryAdd, true),
                ("__sub__", PythonOpCode.BinarySubtract, false),
                ("__rsub__", PythonOpCode.BinarySubtract, true),
                ("__mul__", PythonOpCode.BinaryMultiply, false),
                ("__rmul__", PythonOpCode.BinaryMultiply, true),
                ("__truediv__", PythonOpCode.BinaryTrueDivide, false),
                ("__rtruediv__", PythonOpCode.BinaryTrueDivide, true),
                ("__floordiv__", PythonOpCode.BinaryFloorDivide, false),
                ("__rfloordiv__", PythonOpCode.BinaryFloorDivide, true),
                ("__mod__", PythonOpCode.BinaryModulo, false),
                ("__rmod__", PythonOpCode.BinaryModulo, true),
                ("__pow__", PythonOpCode.BinaryPower, false),
                ("__rpow__", PythonOpCode.BinaryPower, true),
            }
        )
        {
            var operatorCode = opCode;
            var isReflected = reflected;
            foreach (var type in new[] { "int", "float" })
            {
                var owner = type;
                Wrapper(
                    type,
                    name,
                    1,
                    (receiver, arguments) =>
                        !AcceptsOperand(owner, arguments[0]) ? PythonNotImplementedValue.Instance
                        : isReflected
                            ? PythonVirtualMachine.ApplyBinaryOperator(
                                operatorCode,
                                arguments[0],
                                receiver,
                                default
                            )
                        : PythonVirtualMachine.ApplyBinaryOperator(
                            operatorCode,
                            receiver,
                            arguments[0],
                            default
                        )
                );
            }
        }

        foreach (
            var (name, opCode) in new (string, PythonOpCode)[]
            {
                ("__and__", PythonOpCode.BinaryAnd),
                ("__or__", PythonOpCode.BinaryOr),
                ("__xor__", PythonOpCode.BinaryXor),
                ("__lshift__", PythonOpCode.BinaryLeftShift),
                ("__rshift__", PythonOpCode.BinaryRightShift),
            }
        )
        {
            Wrapper(
                "int",
                name,
                1,
                (receiver, arguments) =>
                    !AcceptsOperand("int", arguments[0])
                        ? PythonNotImplementedValue.Instance
                        : PythonVirtualMachine.ApplyBinaryOperator(
                            opCode,
                            receiver,
                            arguments[0],
                            default
                        )
            );
        }

        foreach (var type in new[] { "int", "float" })
        {
            var owner = type;
            Wrapper(
                type,
                "__divmod__",
                1,
                (receiver, arguments) =>
                    !AcceptsOperand(owner, arguments[0])
                        ? PythonNotImplementedValue.Instance
                        : PythonVirtualMachine.DivideModulo([receiver, arguments[0]], default)
            );
            Wrapper(
                type,
                "__rdivmod__",
                1,
                (receiver, arguments) =>
                    !AcceptsOperand(owner, arguments[0])
                        ? PythonNotImplementedValue.Instance
                        : PythonVirtualMachine.DivideModulo([arguments[0], receiver], default)
            );
        }

        Wrapper(
            "int",
            "__index__",
            0,
            (receiver, _) => PythonWholeNumberValue.Create(Integer(receiver))
        );
        Wrapper(
            "int",
            "__invert__",
            0,
            (receiver, _) => PythonWholeNumberValue.Create(~Integer(receiver))
        );

        return slots;
    }

    /// <summary>
    /// Which values a type's comparison slots accept. A pair outside the set answers
    /// `NotImplemented`, which is what lets a reflected call be tried; a dict accepts
    /// nothing, so every ordering of dicts is `NotImplemented`.
    /// </summary>
    private static bool Comparable(string type, PythonValue other) =>
        type switch
        {
            "list" => other is PythonListValue
                || PythonSubclassStorage.StorageKindOf(other) == "list",
            "tuple" => other is PythonTupleValue,
            "str" => other is PythonTextValue,
            "bytes" => other is PythonByteSequenceValue,
            "bytearray" => other is PythonByteArrayValue or PythonByteSequenceValue,
            // A view compares with another view and with any bytes-like value; it has no
            // ordering at all, so every ordering falls through to the other operand.
            "range" => other is PythonRangeValue,
            "deque" => other is PythonDequeValue,
            "memoryview" => other
                is PythonMemoryViewValue
                    or PythonByteSequenceValue
                    or PythonByteArrayValue,
            "dict" => other is PythonDictionaryValue
                || PythonSubclassStorage.StorageKindOf(other) == "dict",
            "set" or "frozenset" => other is PythonSetValue,
            "int" => other is PythonWholeNumberValue or PythonTruthValue,
            "float" => other
                is PythonFloatingPointValue
                    or PythonWholeNumberValue
                    or PythonTruthValue,
            _ => false,
        };

    private static bool IsSubset(PythonSetValue candidate, PythonSetValue other)
    {
        foreach (var element in candidate.Elements)
        {
            if (ManagedObjectProtocols.FindSetEntry(other, element) < 0)
                return false;
        }
        return true;
    }

    private static PythonValue Compare(
        string type,
        PythonValue receiver,
        PythonValue other,
        int operation
    )
    {
        // A dict, a view and a range compare for equality alone; nothing orders them.
        if (type is "dict" or "memoryview" or "range" && operation is not (0 or 4))
            return PythonNotImplementedValue.Instance;
        if (!Comparable(type, other))
            return PythonNotImplementedValue.Instance;

        // A set orders by containment rather than by an element-wise comparison.
        if (type is "set" or "frozenset" && operation is not (0 or 4))
        {
            var receiverSet = (PythonSetValue)receiver;
            var otherSet = (PythonSetValue)other;
            var subset = IsSubset(receiverSet, otherSet);
            var superset = IsSubset(otherSet, receiverSet);
            var proper = !ManagedObjectProtocols.AreEqual(receiver, other);
            var ordered = operation switch
            {
                1 => subset && proper,
                2 => subset,
                3 => superset && proper,
                _ => superset,
            };
            return ordered ? PythonTruthValue.True : PythonTruthValue.False;
        }

        if (operation is 0 or 4)
        {
            var equal = ManagedObjectProtocols.AreEqual(receiver, other);
            var answer = operation == 0 ? equal : !equal;
            return answer ? PythonTruthValue.True : PythonTruthValue.False;
        }

        var order = ManagedObjectProtocols.CompareOrdered(receiver, other, default);
        var result = operation switch
        {
            1 => order < 0,
            2 => order <= 0,
            3 => order > 0,
            _ => order >= 0,
        };
        return result ? PythonTruthValue.True : PythonTruthValue.False;
    }

    private static PythonValue Binary(
        string type,
        PythonValue receiver,
        PythonValue other,
        PythonOpCode opCode,
        string name
    ) => PythonVirtualMachine.ApplyBinaryOperator(opCode, receiver, other, default);

    private static PythonValue Reflected(
        string type,
        PythonValue receiver,
        PythonValue other,
        PythonOpCode opCode,
        string name
    ) => PythonVirtualMachine.ApplyBinaryOperator(opCode, other, receiver, default);

    /// <summary>`__contains__` as a method descriptor, which reports its own arity.</summary>
    private static PythonTruthValue Contains(
        string type,
        PythonValue receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
            throw Fault(
                $"{type}.__contains__() takes exactly one argument ({arguments.Count} given)"
            );
        return ManagedObjectProtocols.Contains(receiver, arguments[0])
            ? PythonTruthValue.True
            : PythonTruthValue.False;
    }

    private static PythonValue GetItem(
        string type,
        PythonValue receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
            throw Fault(
                $"{type}.__getitem__() takes exactly one argument ({arguments.Count} given)"
            );
        return ManagedObjectProtocols.GetItem(receiver, arguments[0]);
    }

    private static PythonValue Reversed(
        string type,
        PythonValue receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 0)
            throw Fault($"{type}.__reversed__() takes no arguments ({arguments.Count} given)");
        return PythonReverseIterators.Create(receiver, default);
    }

    private static PythonTupleValue NewArguments(
        string type,
        PythonValue receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 0)
            throw Fault($"{type}.__getnewargs__() takes no arguments ({arguments.Count} given)");
        return new PythonTupleValue([receiver]);
    }

    private static PythonTextValue Format(
        string type,
        PythonValue receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
            throw Fault(
                $"{type}.__format__() takes exactly one argument ({arguments.Count} given)"
            );
        if (arguments[0] is not PythonTextValue specification)
            throw Fault(
                $"__format__() argument must be str, not "
                    + ManagedObjectProtocols.GetTypeName(arguments[0])
            );
        return new PythonTextValue(
            PythonValueFormatter.Format(receiver, specification.Value, default)
        );
    }

    /// <summary>
    /// `__init__` reinitializes the receiver in place: it clears it and then runs the same
    /// method the equivalent constructor call would, so the mutation rules stay in one place.
    /// </summary>
    private static void Initialize(
        string type,
        PythonValue receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        switch (receiver)
        {
            case PythonListValue list:
                list.Elements.Clear();
                break;
            case PythonByteArrayValue mutable:
                mutable.Value = [];
                break;
            case PythonDictionaryValue dictionary:
                dictionary.ClearItems();
                break;
            case PythonSetValue set:
                set.ClearEntries();
                break;
        }
        if (arguments.Count == 0)
            return;
        var update = type switch
        {
            "list" => "extend",
            "bytearray" => "extend",
            "dict" => "update",
            _ => "update",
        };
        PythonBuiltinMethods.TryGetTypeMember(type, update, out var method);
        method.Invoke(receiver, [arguments[0]]);
    }

    private static PythonValue InPlaceAdd(PythonValue receiver, PythonValue other)
    {
        var type = receiver is PythonListValue ? "list" : "bytearray";
        PythonBuiltinMethods.TryGetTypeMember(type, "extend", out var extend);
        extend.Invoke(receiver, [other]);
        return receiver;
    }

    private static PythonValue InPlaceMultiply(PythonValue receiver, PythonValue other)
    {
        // Repeat the receiver's current contents, then write them back over it.
        var repeated = PythonVirtualMachine.ApplyBinaryOperator(
            PythonOpCode.BinaryMultiply,
            receiver,
            other,
            default
        );
        switch (receiver)
        {
            case PythonListValue list when repeated is PythonListValue computed:
                list.Elements.Clear();
                list.Elements.AddRange(computed.Elements);
                return list;
            case PythonByteArrayValue mutable when repeated is PythonByteArrayValue bytes:
                PythonByteArrayMutation.RequireResizable(mutable, bytes.Value.Length);
                mutable.Value = bytes.Value;
                return mutable;
        }
        return repeated;
    }

    /// <summary>
    /// The in-place set and dict operators mutate the receiver, so the result of the
    /// corresponding method is written back into it.
    /// </summary>
    private static PythonValue InPlaceOperation(
        string type,
        PythonValue receiver,
        PythonValue other,
        string method
    )
    {
        PythonBuiltinMethods.TryGetTypeMember(type, method, out var update);
        update.Invoke(receiver, [other]);
        return receiver;
    }

    private static BigInteger Integer(PythonValue receiver) =>
        receiver switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault("__index__ returned non-int"),
        };

    private static PythonWholeNumberValue WholeNumber(PythonValue receiver) =>
        receiver switch
        {
            PythonWholeNumberValue whole => whole,
            PythonTruthValue truth => PythonWholeNumberValue.Create(truth.Value ? 1 : 0),
            PythonFloatingPointValue floating => PythonWholeNumberValue.Create(
                new BigInteger(Math.Truncate(floating.Value))
            ),
            _ => throw Fault("'__int__' returned a non-int"),
        };

    private static PythonFloatingPointValue FloatingPoint(PythonValue receiver) =>
        receiver switch
        {
            PythonFloatingPointValue floating => floating,
            PythonWholeNumberValue whole => new PythonFloatingPointValue((double)whole.Value),
            PythonTruthValue truth => new PythonFloatingPointValue(truth.Value ? 1.0 : 0.0),
            _ => throw Fault("'__float__' returned a non-float"),
        };

    private static PythonValue Absolute(PythonValue receiver) =>
        receiver is PythonFloatingPointValue floating
            ? new PythonFloatingPointValue(Math.Abs(floating.Value))
            : PythonWholeNumberValue.Create(BigInteger.Abs(Integer(receiver)));

    private static PythonValue Negated(PythonValue receiver) =>
        receiver is PythonFloatingPointValue floating
            ? new PythonFloatingPointValue(-floating.Value)
            : PythonWholeNumberValue.Create(-Integer(receiver));

    private static PythonValue Positive(PythonValue receiver) =>
        receiver is PythonFloatingPointValue floating
            ? floating
            : PythonWholeNumberValue.Create(Integer(receiver));

    /// <summary>
    /// Whether a numeric slot takes this operand. An operand of the wrong kind is what makes
    /// the slot answer `NotImplemented`, which is how a reflected call gets its turn; a
    /// numeric operand that the operation cannot complete still raises.
    /// </summary>
    private static bool AcceptsOperand(string type, PythonValue other) =>
        other is PythonWholeNumberValue or PythonTruthValue
        || type == "float" && other is PythonFloatingPointValue;

    /// <summary>The method each in-place set operator is equivalent to.</summary>
    private static string SetInPlaceMethod(string name) =>
        name switch
        {
            "__ior__" => "update",
            "__iand__" => "intersection_update",
            "__ixor__" => "symmetric_difference_update",
            _ => "difference_update",
        };

    private static PythonRuntimeException Fault(string message) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, "TypeError");
}
