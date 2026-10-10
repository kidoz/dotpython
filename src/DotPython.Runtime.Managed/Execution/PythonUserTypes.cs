// The `collections` user-facing wrappers follow CPython 3.14.7 Lib/collections/__init__.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// What `collections.UserDict`, `UserList`, `UserString` and `ChainMap` share: each is a
/// class written in Python that keeps its state in an ordinary attribute — `data` for the
/// three wrappers, `maps` for the chain — and takes every other method from the abstract
/// base class it inherits.
/// </summary>
/// <remarks>
/// CPython declares all four as subclasses of a `collections.abc` class, so that is what they
/// are here: the mixins the base class carries answer everything the source does not spell
/// out, and the methods installed below work through the object protocol exactly as the
/// Python bodies do — `self.data[key]` is `__getitem__` on the attribute, `other in self` is
/// `__contains__`. They stay heap types: refusals name them by their bare name, their methods
/// report as functions, and their instances carry a `__dict__` beside the attributes.
/// </remarks>
internal static class PythonUserTypes
{
    /// <summary>The attribute a wrapper keeps its state in.</summary>
    internal const string DataMember = "data";

    /// <summary>
    /// The receiver of a method: a bound call hands the instance over beside its arguments,
    /// an unbound one — `UserList.append(items, 1)` — passes it first, the way any function
    /// object does.
    /// </summary>
    internal static (PythonValue Self, IReadOnlyList<PythonValue> RestArguments) Bound(
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

    /// <summary>`self.data`.</summary>
    internal static PythonValue Data(PythonValue self) =>
        ManagedObjectProtocols.GetAttribute(self, DataMember);

    /// <summary>`self.maps`.</summary>
    internal static PythonValue Maps(PythonValue self) =>
        ManagedObjectProtocols.GetAttribute(self, "maps");

    /// <summary>`self.maps = value`.</summary>
    internal static void SetMaps(PythonValue self, PythonValue value) =>
        ManagedObjectProtocols.SetAttribute(self, "maps", value);

    /// <summary>The elements of a sequence, in order, as a list of values.</summary>
    internal static List<PythonValue> Elements(PythonValue sequence) =>
        ManagedObjectProtocols.MaterializeValues(sequence, default);

    /// <summary>`self.data = value`.</summary>
    internal static void SetData(PythonValue self, PythonValue value) =>
        ManagedObjectProtocols.SetAttribute(self, DataMember, value);

    /// <summary>A method of the receiver, called the way a Python call reaches it.</summary>
    internal static PythonValue InvokeOn(
        PythonValue self,
        string name,
        IReadOnlyList<PythonValue> arguments
    ) => Dispatched(ManagedObjectProtocols.GetAttribute(self, name), arguments);

    /// <summary>A callable these bodies reach, called the way a Python call reaches it.</summary>
    internal static PythonValue Dispatched(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments
    ) => UserObjectProtocols.Dispatcher!.Invoke(callable, [.. arguments], default);

    /// <summary>`cls(...)`: the class a classmethod was bound to, or the receiver's class.</summary>
    internal static PythonValue Construct(PythonValue type, IReadOnlyList<PythonValue> arguments) =>
        UserObjectProtocols.Dispatcher!.CallType(type, [.. arguments], [], [], default);

    /// <summary>`type(self)`.</summary>
    internal static PythonValue ClassOf(PythonValue self) =>
        ManagedObjectProtocols.GetAttribute(self, "__class__");

    /// <summary>`isinstance(value, type)` against one of the runtime's own managed classes.</summary>
    internal static bool IsInstance(PythonValue value, PythonManagedTypeValue type) =>
        value is PythonManagedObjectValue instance && instance.Type.Mro.Contains(type);

    /// <summary>`isinstance(value, dict)` — and the subclasses of `dict` the runtime models.</summary>
    internal static bool IsDictionary(PythonValue value) =>
        PythonBuiltinTypes.IsInstance(value, PythonBuiltinTypes.Dict);

    /// <summary>A name the class dictionary carries, which is how a subclass is asked for
    /// `__missing__` before a lookup gives up.</summary>
    internal static bool HasAttribute(PythonValue target, string name)
    {
        try
        {
            ManagedObjectProtocols.GetAttribute(target, name);
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError"))
        {
            return false;
        }
        return true;
    }

    /// <summary>`key in container`, through the object protocol.</summary>
    internal static bool Contains(PythonValue container, PythonValue key) =>
        ManagedObjectProtocols.Contains(container, key);

    /// <summary>`iter(value)`, through the object protocol.</summary>
    internal static PythonIteratorValue Iterate(PythonValue value) =>
        ManagedObjectProtocols.GetIterator(value);

    /// <summary>`len(container)`.</summary>
    internal static PythonWholeNumberValue Length(PythonValue container) =>
        PythonWholeNumberValue.Create(ManagedObjectProtocols.GetLength(container));

    /// <summary>A dictionary of the keyword arguments a call carried.</summary>
    internal static PythonDictionaryValue Keywords(
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var dictionary = new PythonDictionaryValue([]);
        for (var index = 0; index < keywordNames.Count; index++)
        {
            ManagedObjectProtocols.SetItem(
                dictionary,
                new PythonTextValue(keywordNames[index]),
                keywordValues[index]
            );
        }
        return dictionary;
    }

    /// <summary>`left | right` for two dictionaries, with the left one's order in front.</summary>
    internal static PythonValue DictionaryUnion(PythonValue left, PythonValue right) =>
        InvokeOn(left, "__or__", [right]);

    /// <summary>
    /// The methods a wrapper's class dictionary carries, reported as the functions they are.
    /// A method whose source signature declares parameters accepts the keyword calls that
    /// signature allows.
    /// </summary>
    internal static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null,
        string[]? parameters = null,
        PythonValue?[]? defaults = null,
        int positionalOnly = 0
    )
    {
        var function = new PythonProtocolFunctionValue(name, body, keywords)
        {
            DeclaringType = owner,
            IsPythonMethod = true,
        };
        return parameters is null
            ? function
            : function.WithSignature(
                parameters,
                defaults ?? new PythonValue?[parameters.Length],
                positionalOnly
            );
    }

    /// <summary>
    /// `__abstractmethods__` as `ABCMeta` leaves it on a class that implements everything its
    /// bases declare.
    /// </summary>
    internal static void MarkConcrete(PythonManagedTypeValue type) =>
        type.Attributes["__abstractmethods__"] = new PythonSetValue([]) { IsFrozen = true };

    /// <summary>`inst.__dict__.update(source.__dict__)`, which is how a copy takes the
    /// attributes of the instance it copies.</summary>
    internal static void CopyAttributes(
        PythonManagedObjectValue source,
        PythonManagedObjectValue target
    )
    {
        foreach (var attribute in source.Attributes)
            target.Attributes[attribute.Key] = attribute.Value;
    }

    /// <summary>`type.__new__(cls)` for the wrappers, which keep no storage of their own.</summary>
    internal static PythonManagedObjectValue Allocate(PythonValue type) =>
        type is PythonManagedTypeValue managed
            ? new PythonManagedObjectValue(managed, PythonSubclassStorage.Allocate(managed))
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{ManagedObjectProtocols.GetTypeName(type)}' is not a type object",
                default,
                "TypeError"
            );
}
