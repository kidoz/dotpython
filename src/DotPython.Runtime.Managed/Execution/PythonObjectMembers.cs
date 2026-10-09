// The members every type inherits from `object` follow CPython 3.14.7 Objects/object.c
// (object_format, object_getstate and the type's own constructor entry points) and
// Objects/typeobject.c (type_new's object.__new__ and type_class_getitem):
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// What a builtin type inherits from <c>object</c>: the members that are not the type's own
/// but still answer through it, and the two constructor entry points a type object carries.
/// </summary>
/// <remarks>
/// `object`'s members are reported with `object` as their owner — `list.__format__` is a
/// `method '__format__' of 'object' objects` — while the message a failing format reports
/// names the receiver instead. `__new__` and `__class_getitem__` are plain builtins, the
/// same shape `__init_subclass__` already has.
/// </remarks>
internal static class PythonObjectMembers
{
    /// <summary>
    /// The members a value answers from `object` rather than from its own type. Only the
    /// types backed by a method table reach this, which is every type the tables cover.
    /// </summary>
    internal static bool TryGetValueMember(
        PythonValue target,
        string typeName,
        string name,
        out PythonProtocolFunctionValue member,
        out bool isWrapper
    )
    {
        // `__getattribute__` and `__init__` are slots; the rest are methods.
        isWrapper = name is "__getattribute__" or "__init__";
        switch (name)
        {
            case "__format__":
                // A type that formats its own way answers first.
                if (PythonSlotMethods.TryGet(typeName, name, out var own))
                {
                    member = own.Function;
                    return true;
                }
                member = Format();
                return true;
            case "__getattribute__":
                member = GetAttribute();
                return true;
            case "__getstate__":
                member = GetState();
                return true;
            case "__dir__":
                member = Dir(SizeOwner(typeName));
                return true;
            case "__reduce__":
                member = PythonPickleProtocols.Reduce();
                return true;
            case "__sizeof__":
                if (!PythonIntrospection.HasSize(typeName))
                {
                    member = null!;
                    return false;
                }
                member = Size(SizeOwner(typeName));
                return true;
            default:
                member = null!;
                return false;
        }
    }

    /// <summary>The member as a descriptor on the type object, or null when it has none.</summary>
    /// <summary>`int` publishes its own `__sizeof__`; a bool answers that one.</summary>
    private static bool OwnsSize(string typeName, string name) =>
        name == "__sizeof__" && typeName is "int" or "bool";

    /// <summary>Which type's name a size or directory report uses for its owner.</summary>
    private static string SizeOwner(string typeName) => typeName == "int" ? "int" : "object";

    /// <summary>The members `object` contributes when a type defines no own one.</summary>
    private static readonly string[] Names =
    [
        "__dir__",
        "__format__",
        "__getattribute__",
        "__getstate__",
        "__init__",
        "__reduce__",
        "__sizeof__",
    ];

    /// <summary>The types that format their own way, so object's member is not theirs.</summary>
    private static readonly string[] OwnFormats = ["str", "int", "float", "bool"];

    internal static PythonMethodDescriptorValue? GetTypeDescriptor(string typeName, string name)
    {
        if (Array.IndexOf(Names, name) < 0)
            return null;
        if (name == "__format__" && Array.IndexOf(OwnFormats, typeName) >= 0)
            return null;
        // `__sizeof__` is only published where the size itself is modelled.
        if (name == "__sizeof__" && !PythonIntrospection.HasSize(typeName))
            return null;
        // `object` itself is where that member's own argument rules live, which this does
        // not model; a type with a constructor of its own answers first.
        if (
            name == "__init__"
            && (typeName == "object" || PythonSlotMethods.TryGet(typeName, name, out _))
        )
        {
            return null;
        }
        // `int` publishes its own size and a bool answers that one; the types that can
        // rebuild themselves publish their own `__reduce__`; everything else sees
        // `object`'s.
        var owner =
            name == "__reduce__" && PythonPickleProtocols.OwnsReduce(typeName) ? typeName
            : OwnsSize(typeName, name) ? "int"
            : "object";
        lock (Descriptors)
        {
            if (!Descriptors.TryGetValue((owner, name), out var descriptor))
            {
                var member = name switch
                {
                    "__dir__" => Dir(owner),
                    "__format__" => Format(),
                    "__getattribute__" => GetAttribute(),
                    "__getstate__" => GetState(),
                    "__reduce__" => PythonPickleProtocols.Reduce(),
                    "__sizeof__" => Size(owner),
                    _ => Initialize(),
                };
                descriptor = new PythonMethodDescriptorValue(
                    PythonBuiltinTypes.ForName(owner),
                    name,
                    member
                )
                {
                    // `__getattribute__` and `__init__` are slots; `__format__` and
                    // `__getstate__` are methods.
                    IsWrapper = name is "__getattribute__" or "__init__",
                };
                Descriptors[(owner, name)] = descriptor;
            }
            return descriptor;
        }
    }

    /// <summary>
    /// `object.__new__`: an empty instance of the class it is called on. Extra arguments go
    /// to `__init__`, so they are ignored, and only a type the class is compatible with is
    /// accepted.
    /// </summary>
    internal static PythonBuiltinFunctionValue? GetTypeFunction(string typeName, string name)
    {
        switch (name)
        {
            case "__new__":
                return new PythonBuiltinFunctionValue(
                    "__new__",
                    (arguments, span) =>
                    {
                        if (arguments.Count == 0)
                            throw Fault($"{typeName}.__new__(): not enough arguments", span);
                        if (arguments[0] is not PythonBuiltinTypeValue cls)
                            throw Fault(
                                $"{typeName}.__new__(X): X is not a type object "
                                    + $"({ManagedObjectProtocols.GetTypeName(arguments[0])})",
                                span
                            );
                        if (!IsSubclass(cls.Name, typeName))
                            throw Fault(
                                $"{typeName}.__new__({cls.Name}): {cls.Name} is not a subtype "
                                    + $"of {typeName}",
                                span
                            );
                        // A type whose own constructor is the allocator uses the arguments
                        // and reports its own errors; one that inherits `object.__new__`
                        // takes its values in `__init__` instead, so extra arguments are
                        // simply not its business.
                        var own = Array.IndexOf(AllocatingTypes, cls.Name) >= 0;
                        var values = own ? new PythonValue[arguments.Count - 1] : [];
                        for (var index = 1; index < arguments.Count && own; index++)
                            values[index - 1] = arguments[index];
                        return cls.Construct(values, span);
                    },
                    (_, _, _, span) =>
                        throw Fault($"{typeName}.__new__(): not enough arguments", span)
                );
            case "__class_getitem__":
                if (!IsSubscribable(typeName))
                    return null;
                return new PythonBuiltinFunctionValue(
                    "__class_getitem__",
                    (arguments, span) =>
                    {
                        if (arguments.Count != 1)
                            throw Fault(
                                $"{typeName}.__class_getitem__() takes exactly one argument "
                                    + $"({arguments.Count} given)",
                                span
                            );
                        return PythonGenericAliasValue.Create(
                            PythonBuiltinTypes.ForName(typeName),
                            arguments[0]
                        );
                    }
                );
            case "__subclasshook__":
                return PythonPickleProtocols.SubclassHook(
                    PythonBuiltinTypes.ForName(typeName),
                    typeName
                );
            default:
                return null;
        }
    }

    /// <summary>
    /// The types whose `__new__` is their own constructor. The rest — `list`, `bytearray`,
    /// `dict` and `set` — allocate empty and fill in `__init__`.
    /// </summary>
    private static readonly string[] AllocatingTypes =
    [
        "tuple",
        "str",
        "bytes",
        "frozenset",
        "int",
        "float",
        "bool",
        "range",
        "complex",
    ];

    /// <summary>`int` is a subtype of `bool`'s base but not of `bool`, and vice versa.</summary>
    private static bool IsSubclass(string candidate, string typeName) =>
        candidate == typeName || (typeName == "int" && candidate == "bool");

    private static bool IsSubscribable(string typeName) =>
        typeName is "list" or "tuple" or "dict" or "set" or "frozenset";

    private static PythonProtocolFunctionValue Format() =>
        new PythonProtocolFunctionValue(
            "__format__",
            (receiver, arguments) =>
            {
                if (arguments.Count != 1)
                    throw Fault(
                        $"object.__format__() takes exactly one argument "
                            + $"({arguments.Count} given)"
                    );
                if (arguments[0] is not PythonTextValue specification)
                    throw Fault(
                        "__format__() argument must be str, not "
                            + ManagedObjectProtocols.GetTypeName(arguments[0])
                    );
                // An empty specification is `str()`, and anything else is refused rather
                // than formatted, which is what object's own implementation does.
                if (specification.Value.Length != 0)
                    throw Fault(
                        $"unsupported format string passed to "
                            + $"{PythonBuiltinTypes.GetRuntimeTypeName(receiver!)}.__format__"
                    );
                return new PythonTextValue(receiver!.ToDisplayString());
            }
        );

    private static PythonProtocolFunctionValue GetAttribute() =>
        new(
            "__getattribute__",
            (receiver, arguments) =>
            {
                if (arguments.Count != 1)
                    throw Fault($"expected 1 argument, got {arguments.Count}");
                if (arguments[0] is not PythonTextValue name)
                    throw Fault(
                        "attribute name must be string, not "
                            + $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}'"
                    );
                return ManagedObjectProtocols.GetAttribute(receiver!, name.Value);
            }
        );

    /// <summary>
    /// `object.__init__`: an initializer that does nothing. A type whose `__new__` is
    /// overridden accepts any arguments here, which is every builtin type.
    /// </summary>
    private static PythonProtocolFunctionValue Initialize() =>
        new("__init__", (_, _) => PythonNoneValue.Instance);

    /// <summary>The size of the value behind the name, refused with its owner's wording.</summary>
    private static PythonProtocolFunctionValue Dir(string owner = "object") =>
        new(
            "__dir__",
            (receiver, arguments) =>
                arguments.Count == 0
                    ? PythonIntrospection.Names(receiver!)
                    : throw Fault($"{owner}.__dir__() takes no arguments ({arguments.Count} given)")
        );

    private static PythonProtocolFunctionValue Size(string owner = "object") =>
        new(
            "__sizeof__",
            (receiver, arguments) =>
            {
                if (arguments.Count != 0)
                    throw Fault(
                        $"{owner}.__sizeof__() takes no arguments ({arguments.Count} given)"
                    );
                return PythonIntrospection.TryGetSize(receiver!, out var size)
                    ? PythonWholeNumberValue.Create(size)
                    : throw Fault($"'{ManagedObjectProtocols.GetTypeName(receiver!)}' has no size");
            }
        );

    private static PythonProtocolFunctionValue GetState() =>
        new(
            "__getstate__",
            (receiver, arguments) =>
                arguments.Count == 0
                    ? PythonNoneValue.Instance
                    : throw Fault(
                        $"object.__getstate__() takes no arguments ({arguments.Count} given)"
                    )
        );

    private static readonly Dictionary<
        (string Owner, string Name),
        PythonMethodDescriptorValue
    > Descriptors = new();

    private static PythonRuntimeException Fault(string message, TextSpan span = default) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
