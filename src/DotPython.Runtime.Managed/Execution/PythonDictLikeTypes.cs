// The `collections` dict-likes follow CPython 3.14.7 Modules/_collectionsmodule.c and
// Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// What `collections.Counter`, `collections.defaultdict` and `collections.OrderedDict` share:
/// each is a dictionary whose instances carry a class of their own, so the instance holds a
/// `PythonDictionaryValue` as its storage and the runtime's dict protocol answers for it.
/// </summary>
/// <remarks>
/// CPython declares all three as subclasses of `dict` — the two C types with a qualified
/// `tp_name`, `Counter` as a class written in Python — and this is the same storage the
/// runtime gives a user `class D(dict)`. What is added beside the storage is per-instance
/// state the C types keep in their own struct, held here in a side table keyed by the
/// instance.
/// </remarks>
internal static class PythonDictLikeTypes
{
    /// <summary>
    /// The per-instance state a dict-like carries beside its storage: the
    /// `default_factory` a `defaultdict` was built with.
    /// </summary>
    internal sealed class State
    {
        internal PythonValue Factory { get; set; } = PythonNoneValue.Instance;
    }

    private static readonly ConditionalWeakTable<PythonManagedObjectValue, State> States = new();

    /// <summary>An instance of a dict-like class, with an empty dictionary for storage.</summary>
    internal static PythonManagedObjectValue Allocate(PythonManagedTypeValue type) =>
        new(type, new PythonDictionaryValue([]));

    /// <summary>
    /// The receiver and remaining arguments of a method: a bound call hands the instance
    /// over beside the arguments, while an unbound one — `Counter.total(c)` — passes it
    /// first, the way any function object does.
    /// </summary>
    internal static (
        PythonManagedObjectValue Receiver,
        IReadOnlyList<PythonValue> RestArguments
    ) Bound(PythonValue? receiver, IReadOnlyList<PythonValue> arguments) =>
        receiver is not null ? (Instance(receiver, "collections"), arguments)
        : arguments.Count > 0 ? (Instance(arguments[0], "collections"), [.. arguments.Skip(1)])
        : throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "unbound method call needs a receiver",
            default,
            "TypeError"
        );

    /// <summary>The instance a native method was handed.</summary>
    internal static PythonManagedObjectValue Instance(PythonValue? receiver, string owner) =>
        receiver as PythonManagedObjectValue
        ?? throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"descriptor '{owner}' requires a '{owner}' object",
            default,
            "TypeError"
        );

    /// <summary>The dictionary a dict-like instance carries.</summary>
    internal static PythonDictionaryValue Storage(PythonValue instance) =>
        instance is PythonManagedObjectValue { Payload: PythonDictionaryValue storage }
            ? storage
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "descriptor requires a dictionary subclass instance",
                default,
                "TypeError"
            );

    /// <summary>
    /// Carries the state a copy needs — a `defaultdict`'s factory — from one instance to
    /// another, which `copy.copy`, `copy.deepcopy` and `__reduce__` rebuilds both rely on.
    /// </summary>
    internal static void CopyState(
        PythonManagedObjectValue source,
        PythonManagedObjectValue copy
    ) => StateOf(copy).Factory = StateOf(source).Factory;

    /// <summary>The state beside an instance's storage, created on first use.</summary>
    internal static State StateOf(PythonValue instance) =>
        States.GetOrCreateValue(Instance(instance, "collections"));

    /// <summary>
    /// Fills a dict-like's storage from constructor arguments, with `dict`'s own rules: one
    /// mapping or iterable of pairs, and keywords as further items. The builtin constructor
    /// does the work, so its arity and key diagnostics are the ones `defaultdict` reports.
    /// </summary>
    internal static void Fill(
        PythonValue instance,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var source =
            keywordNames.Count == 0
                ? PythonBuiltinTypes.Dict.Construct(positional, span)
                : PythonBuiltinTypes.Dict.ConstructWithKeywords!(
                    positional,
                    keywordNames,
                    keywordValues,
                    span
                );
        Merge(Storage(instance), (PythonDictionaryValue)source, span);
    }

    /// <summary>Copies every item of a mapping into another dictionary.</summary>
    internal static void Merge(
        PythonDictionaryValue target,
        PythonDictionaryValue source,
        TextSpan span = default
    ) => PythonBuiltinMethods.MergeInto(target, source, span);

    /// <summary>
    /// The dictionary a mapping contributes: an exact dict, a mapping proxy over one, or a
    /// dictionary subclass instance's storage.
    /// </summary>
    internal static PythonDictionaryValue MappingStorage(PythonValue mapping) =>
        mapping switch
        {
            PythonDictionaryValue dictionary => dictionary,
            PythonMappingProxyValue { Mapping: PythonDictionaryValue inner } => inner,
            _ => Storage(mapping),
        };

    /// <summary>Whether a value is something `|` accepts as the other mapping.</summary>
    internal static bool IsMapping(PythonValue value) =>
        value is PythonDictionaryValue || PythonSubclassStorage.StorageKindOf(value) == "dict";

    /// <summary>
    /// The repr all three share: the class's own name — the bare name CPython's `_PyType_Name`
    /// reports — whatever the class prints before the dictionary, and the dictionary itself
    /// in the builtin's repr.
    /// </summary>
    internal static string Represent(
        PythonValue instance,
        string prefix = "",
        bool omitEmpty = false
    )
    {
        var type = Instance(instance, "collections").Type;
        var storage = Storage(instance);
        if (omitEmpty && storage.Items.Count == 0)
            return $"{type.Name}()";
        return $"{type.Name}({prefix}{storage.ToRepresentationString()})";
    }

    /// <summary>The repr of the factory a `defaultdict` repr prints first.</summary>
    internal static string FactoryRepresentation(PythonValue instance) =>
        StateOf(instance).Factory.ToRepresentationString();
}
