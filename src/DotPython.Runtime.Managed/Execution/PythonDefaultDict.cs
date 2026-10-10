// The `collections.defaultdict` surface follows CPython 3.14.7 Modules/_collectionsmodule.c
// (defdict_*):
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>collections.defaultdict</c> type: a dict that calls a factory for a key it does
/// not hold, stores what the factory returned, and answers with it.
/// </summary>
/// <remarks>
/// A static type in CPython, so refusals name it <c>collections.defaultdict</c>, its methods
/// report as C methods, and its instances carry no <c>__dict__</c>. The dictionary behaviour
/// is the storage the class allocates — the machinery a user subclass of <c>dict</c> uses —
/// and this file adds the factory, the miss hook, the repr and the copy surface.
/// <c>default_factory</c> is a member rather than a property in CPython; here it is answered
/// through the state beside the storage.
/// </remarks>
internal static class PythonDefaultDict
{
    private const string Owner = "collections.defaultdict";

    /// <summary>The `collections.defaultdict` type object.</summary>
    internal static readonly PythonManagedTypeValue Type = CreateType();

    private static PythonManagedTypeValue CreateType()
    {
        var type = new PythonManagedTypeValue("defaultdict")
        {
            Module = "collections",
            QualName = "defaultdict",
            LayoutBase = PythonBuiltinTypes.Dict,
            ReportsQualifiedName = true,
        };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinTypes.Dict]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, PythonBuiltinTypes.Dict, PythonBuiltinFunctions.Object])
        );
        // The C type has no instance dictionary, so an unknown attribute is refused with
        // `no __dict__ for setting new attributes` rather than being stored.
        type.Slots = PythonSlotLayout.Create(type, new PythonTupleValue([]), default);
        type.Attributes["__module__"] = new PythonTextValue("collections");
        type.Attributes["__doc__"] = new PythonTextValue(
            "defaultdict(default_factory=None, /, [...]) --> dict with default factory\n"
                + "\n"
                + "The default factory is called without arguments to produce\n"
                + "a new value when a key is not present, in __getitem__ only.\n"
                + "A defaultdict compares equal to a dict with the same items.\n"
                + "All remaining arguments are treated the same as if they were\n"
                + "passed to the dict constructor, including keyword arguments.\n"
        );
        type.Attributes["__init__"] = Method(
            "__init__",
            (receiver, arguments) => Initialize(receiver, arguments, [], []),
            (receiver, arguments, names, values) => Initialize(receiver, arguments, names, values),
            wrapper: true
        );
        type.Attributes["__missing__"] = Method(
            "__missing__",
            (receiver, arguments) => Missing(receiver, arguments)
        );
        type.Attributes["default_factory"] = CreateFactoryMember();
        type.Attributes["__repr__"] = Method(
            "__repr__",
            (receiver, arguments) =>
                new PythonTextValue(
                    PythonDictLikeTypes.Represent(
                        RequireNoArguments(receiver, arguments, "__repr__"),
                        PythonDictLikeTypes.FactoryRepresentation(receiver!) + ", "
                    )
                ),
            wrapper: true
        );
        type.Attributes["copy"] = Method(
            "copy",
            (receiver, arguments) => Copy(RequireNoArguments(receiver, arguments, "copy"))
        );
        type.Attributes["__copy__"] = Method(
            "__copy__",
            (receiver, arguments) => Copy(RequireNoArguments(receiver, arguments, "__copy__"))
        );
        type.Attributes["__reduce__"] = Method(
            "__reduce__",
            (receiver, arguments) => Reduce(RequireNoArguments(receiver, arguments, "__reduce__"))
        );
        type.Attributes["__or__"] = Method(
            "__or__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: false),
            wrapper: true
        );
        type.Attributes["__ror__"] = Method(
            "__ror__",
            (receiver, arguments) => Merge(receiver, arguments, reflected: true),
            wrapper: true
        );
        return type;
    }

    /// <summary>A method of the type, reported the way the C type reports it.</summary>
    private static PythonProtocolFunctionValue Method(
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null,
        bool wrapper = false
    ) => new(name, body, keywords) { DeclaringType = Owner, IsSlotWrapper = wrapper };

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// `defaultdict(factory=None, /, *args, **kwargs)`: the first positional argument is the
    /// factory, which must be callable or None, and the rest fills the dictionary with
    /// `dict`'s own rules.
    /// </summary>
    private static PythonNoneValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var instance = PythonDictLikeTypes.Instance(receiver, Owner);
        var start = 0;
        PythonValue factory = PythonNoneValue.Instance;
        if (arguments.Count > 0)
        {
            factory = arguments[0];
            start = 1;
            // The factory is checked before the rest of the arguments are looked at,
            // which is the order CPython validates in.
            if (factory is not PythonNoneValue && !ManagedObjectProtocols.IsCallable(factory))
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "first argument must be callable or None",
                    default,
                    "TypeError"
                );
            }
        }
        PythonDictLikeTypes.StateOf(instance).Factory = factory;
        PythonDictLikeTypes.Fill(
            instance,
            [.. arguments.Skip(start)],
            keywordNames,
            keywordValues,
            default
        );
        return PythonNoneValue.Instance;
    }

    // -------------------------------------------------------------------------
    // Members
    // -------------------------------------------------------------------------

    /// <summary>
    /// `default_factory`: a readable, writable and deletable member. Only the constructor
    /// validates the factory, so any value may be assigned, and deleting it leaves None,
    /// which is what CPython's cleared member answers.
    /// </summary>
    private static PythonPropertyValue CreateFactoryMember() =>
        new(
            new PythonProtocolFunctionValue(
                "default_factory",
                (_, arguments) => PythonDictLikeTypes.StateOf(arguments[0]).Factory
            ),
            new PythonProtocolFunctionValue(
                "setter",
                (_, arguments) =>
                {
                    PythonDictLikeTypes.StateOf(arguments[0]).Factory = arguments[1];
                    return PythonNoneValue.Instance;
                }
            ),
            new PythonProtocolFunctionValue(
                "deleter",
                (_, arguments) =>
                {
                    PythonDictLikeTypes.StateOf(arguments[0]).Factory = PythonNoneValue.Instance;
                    return PythonNoneValue.Instance;
                }
            )
        )
        {
            MemberDisplay = $"{Owner}",
        };

    // -------------------------------------------------------------------------
    // Behaviour
    // -------------------------------------------------------------------------

    /// <summary>
    /// `defaultdict.__missing__(key)`: None refuses with a KeyError, anything else is
    /// called and stored under the key — through `self[key] = ...`, so a subclass's own
    /// `__setitem__` sees it — before its value is returned.
    /// </summary>
    private static PythonValue Missing(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"defaultdict.__missing__() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        var instance = PythonDictLikeTypes.Instance(receiver, Owner);
        var key = arguments[0];
        var factory = PythonDictLikeTypes.StateOf(instance).Factory;
        if (factory is PythonNoneValue)
            throw ManagedObjectProtocols.MissingKey(key);
        // The factory is called the way Python source calls it, which reaches user
        // functions as well as the builtins.
        var value = UserObjectProtocols.Dispatcher is { } dispatcher
            ? dispatcher.Invoke(factory, [], default)
            : ManagedObjectProtocols.Call(factory, []);
        ManagedObjectProtocols.SetItem(instance, key, value);
        return value;
    }

    private static PythonManagedObjectValue Copy(PythonManagedObjectValue instance)
    {
        var copy = PythonDictLikeTypes.Allocate(instance.Type);
        PythonDictLikeTypes.StateOf(copy).Factory = PythonDictLikeTypes.StateOf(instance).Factory;
        PythonDictLikeTypes.Merge(
            PythonDictLikeTypes.Storage(copy),
            PythonDictLikeTypes.Storage(instance)
        );
        return copy;
    }

    /// <summary>
    /// `__reduce__`: the type, the factory as the single constructor argument, and an
    /// iterator over `items()` — the five-element shape CPython returns, whose dictionary
    /// argument and item-replacement hook are the `None` it leaves out.
    /// </summary>
    private static PythonTupleValue Reduce(PythonManagedObjectValue instance)
    {
        var storage = PythonDictLikeTypes.Storage(instance);
        return new PythonTupleValue([
            instance.Type,
            new PythonTupleValue([PythonDictLikeTypes.StateOf(instance).Factory]),
            PythonNoneValue.Instance,
            PythonNoneValue.Instance,
            ManagedObjectProtocols.GetIterator(
                new PythonDictionaryViewValue("dict_items", storage)
            ),
        ]);
    }

    /// <summary>
    /// `defaultdict.__or__` and `__ror__`: a new defaultdict carrying the same factory,
    /// holding the union of the two mappings. Anything that is not a mapping answers
    /// NotImplemented, so the other operand's reflected slot gets its turn.
    /// </summary>
    private static PythonValue Merge(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool reflected
    )
    {
        var instance = PythonDictLikeTypes.Instance(receiver, Owner);
        if (arguments.Count != 1 || !PythonDictLikeTypes.IsMapping(arguments[0]))
            return PythonNotImplementedValue.Instance;
        var other = arguments[0];
        var result = PythonDictLikeTypes.Allocate(instance.Type);
        PythonDictLikeTypes.StateOf(result).Factory = PythonDictLikeTypes.StateOf(instance).Factory;
        var left = reflected ? other : instance;
        var right = reflected ? instance : other;
        PythonDictLikeTypes.Merge(
            PythonDictLikeTypes.Storage(result),
            PythonDictLikeTypes.MappingStorage(left)
        );
        PythonDictLikeTypes.Merge(
            PythonDictLikeTypes.Storage(result),
            PythonDictLikeTypes.MappingStorage(right)
        );
        return result;
    }

    /// <summary>
    /// The receiver of a method that takes no arguments, with CPython's own refusal for a
    /// call that passes some.
    /// </summary>
    private static PythonManagedObjectValue RequireNoArguments(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        string name
    )
    {
        if (arguments.Count != 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"defaultdict.{name}() takes no arguments ({arguments.Count} given)",
                default,
                "TypeError"
            );
        return PythonDictLikeTypes.Instance(receiver, Owner);
    }
}
