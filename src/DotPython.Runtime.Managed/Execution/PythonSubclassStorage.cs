// Subclassing the storage builtins follows CPython 3.14.7 Objects/typeobject.c
// (type_new_set_layout / subtype_alloc) and the instance layouts of Objects/dictobject.c,
// Objects/listobject.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The storage a subclass of a builtin carries: an instance of `class D(dict)` is a dict with
/// a class of its own, so the value it holds is the builtin it descends from.
/// </summary>
/// <remarks>
/// Only the two mutable containers are modelled so far. The instance keeps its own attributes
/// beside the storage, and every operation the builtin answers is answered for the storage.
/// </remarks>
internal static class PythonSubclassStorage
{
    /// <summary>The builtin storages a class may be built on.</summary>
    private static readonly string[] Supported = ["dict", "list", "tuple"];

    /// <summary>Whether a class may name this builtin as its base.</summary>
    internal static bool Supports(string builtinName) => Array.IndexOf(Supported, builtinName) >= 0;

    /// <summary>
    /// The storage a class allocates, from the builtin its layout settles on, or null when
    /// the class is not a storage subclass.
    /// </summary>
    internal static PythonValue? Allocate(PythonManagedTypeValue type)
    {
        var kind = StorageKindOf(type);
        return kind switch
        {
            "dict" => new PythonDictionaryValue([]),
            "list" => new PythonListValue([]),
            "tuple" => new PythonTupleValue([]),
            _ => null,
        };
    }

    /// <summary>
    /// The storage a class takes when it is constructed with these arguments. A dictionary or
    /// a list allocates empty and is filled in place, while a tuple is built by its own
    /// allocator — `tuple.__new__(cls, iterable)` reads the argument the builtin reads — so a
    /// tuple subclass takes its contents from the constructor's arguments.
    /// </summary>
    internal static PythonValue? AllocateFor(
        PythonManagedTypeValue type,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) =>
        StorageKindOf(type) switch
        {
            { } kind when IsAllocating(kind) => Fill(
                kind,
                positional,
                keywordNames,
                keywordValues,
                span
            ),
            _ => Allocate(type),
        };

    /// <summary>The storages whose builtin constructor reads the arguments — the tuple's does,
    /// and the two containers ignore theirs, as CPython's own `__new__` slots do.</summary>
    internal static bool IsAllocating(string kind) => kind == "tuple";

    /// <summary>
    /// The storage built from an instance-call's arguments, which is the builtin's own
    /// constructor: `D({'a': 1})` is `dict({'a': 1})` behind the subclass.
    /// </summary>
    internal static PythonValue Fill(
        string kind,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var type = kind switch
        {
            "dict" => PythonBuiltinTypes.Dict,
            "tuple" => PythonBuiltinTypes.Tuple,
            _ => PythonBuiltinTypes.List,
        };
        return keywordNames.Count == 0 || type.ConstructWithKeywords is null
            ? type.Construct(positional, span)
            : type.ConstructWithKeywords(positional, keywordNames, keywordValues, span);
    }

    /// <summary>The builtin a class descends from, or null when it descends from none.</summary>
    internal static string? StorageKindOf(PythonManagedTypeValue type) =>
        PythonTypeLayout.GetSolidBase(type) is PythonBuiltinTypeValue { Name: var name }
        && Supports(name)
            ? name
            : null;

    /// <summary>The storage behind an instance, or null when it carries none.</summary>
    internal static PythonValue? Of(PythonValue value) =>
        value is PythonManagedObjectValue instance
        && instance.Payload is PythonDictionaryValue or PythonListValue or PythonTupleValue
            ? (PythonValue)instance.Payload
            : null;

    /// <summary>
    /// The value an operation works on: the storage behind a subclass instance, and the
    /// value itself for everything else. A plain instance is left alone, so `object()` and
    /// user classes are untouched.
    /// </summary>
    internal static PythonValue Resolve(PythonValue value) => Of(value) ?? value;

    /// <summary>The builtin a subclass instance descends from, or null.</summary>
    internal static string? StorageKindOf(PythonValue value) =>
        value is PythonManagedObjectValue instance ? StorageKindOf(instance.Type) : null;

    /// <summary>
    /// The name a message reports for a value: a subclass instance reports its own class,
    /// which is what CPython's `unhashable type: 'D'` does.
    /// </summary>
    internal static string ReportedName(PythonValue value) =>
        value is PythonManagedObjectValue instance
            ? instance.Type.QualifiedDisplayName
            : ManagedObjectProtocols.GetTypeName(value);
}
