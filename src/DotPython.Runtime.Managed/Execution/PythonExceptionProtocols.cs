using System.Collections.Concurrent;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Shared methods and construction slots for represented builtin exception types.</summary>
internal static class PythonExceptionProtocols
{
    private static readonly PythonProtocolFunctionValue AddNoteMethod = new(
        "add_note",
        (receiver, arguments) => AddNote(receiver, arguments, [], default),
        (receiver, arguments, names, _) => AddNote(receiver, arguments, names, default)
    );
    private static readonly ConcurrentDictionary<string, PythonValue> GroupMethods = new(
        StringComparer.Ordinal
    );
    private static readonly ConcurrentDictionary<string, PythonValue> Allocators = new(
        StringComparer.Ordinal
    );
    private static readonly ConcurrentDictionary<string, PythonValue> Initializers = new(
        StringComparer.Ordinal
    );

    internal static bool TryGetOwnAttribute(
        PythonExceptionTypeValue type,
        string name,
        out PythonValue value
    )
    {
        if (type.Name == "BaseException" && name == "add_note")
        {
            value = AddNoteMethod;
            return true;
        }
        if (type.Name == "BaseExceptionGroup" && name is "derive" or "split" or "subgroup")
        {
            value = GroupMethods.GetOrAdd(
                name,
                method => new PythonProtocolFunctionValue(
                    method,
                    (receiver, arguments) =>
                        InvokeGroupMethod(method, receiver, arguments, [], default),
                    (receiver, arguments, names, _) =>
                        InvokeGroupMethod(method, receiver, arguments, names, default)
                )
            );
            return true;
        }
        if (name == "__new__" && OwnsAllocator(type.Name))
        {
            value = Allocators.GetOrAdd(
                type.Name,
                owner => new PythonBuiltinFunctionValue(
                    "__new__",
                    (arguments, span) => Allocate(owner, arguments, span),
                    (arguments, _, _, span) => Allocate(owner, arguments, span)
                )
            );
            return true;
        }
        if (
            name == "__init__"
            && type.Name
                is "BaseException"
                    or "BaseExceptionGroup"
                    or "StopIteration"
                    or "SystemExit"
        )
        {
            value = Initializers.GetOrAdd(
                type.Name,
                owner => new PythonProtocolFunctionValue(
                    "__init__",
                    (instance, arguments) => Initialize(owner, instance, arguments, [], default),
                    (instance, arguments, names, _) =>
                        Initialize(owner, instance, arguments, names, default)
                )
            );
            return true;
        }
        value = null!;
        return false;
    }

    private static PythonValue InvokeGroupMethod(
        string name,
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywords,
        TextSpan span
    )
    {
        if (receiver is null)
        {
            if (arguments.Count == 0)
                throw Error(
                    $"descriptor '{name}' of 'BaseExceptionGroup' object needs an argument",
                    span
                );
            receiver = arguments[0];
            arguments = arguments.Skip(1).ToArray();
        }
        if (receiver is not PythonExceptionValue { GroupExceptions: not null } group)
            throw Error(
                $"descriptor '{name}' for 'BaseExceptionGroup' objects doesn't apply to a '{ManagedObjectProtocols.GetTypeName(receiver)}' object",
                span
            );
        if (keywords.Count != 0)
            throw Error($"{name}() takes no keyword arguments", span);
        if (arguments.Count != 1)
            throw Error($"{name} expected 1 argument, got {arguments.Count}", span);
        return UserObjectProtocols.Dispatcher!.InvokeExceptionGroupMethod(
            name,
            group,
            arguments[0],
            span
        );
    }

    private static PythonNoneValue AddNote(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywords,
        TextSpan span
    )
    {
        if (receiver is null)
        {
            if (arguments.Count == 0)
                throw Error(
                    "descriptor 'add_note' of 'BaseException' object needs an argument",
                    span
                );
            receiver = arguments[0];
            arguments = arguments.Skip(1).ToArray();
        }
        if (receiver is not PythonExceptionValue exception)
            throw Error(
                $"descriptor 'add_note' for 'BaseException' objects doesn't apply to a '{ManagedObjectProtocols.GetTypeName(receiver)}' object",
                span
            );
        if (keywords.Count != 0)
            throw Error("BaseException.add_note() takes no keyword arguments", span);
        if (arguments.Count != 1)
            throw Error(
                $"BaseException.add_note() takes exactly one argument ({arguments.Count} given)",
                span
            );
        if (arguments[0] is not PythonTextValue)
            throw Error(
                $"add_note() argument must be str, not {(arguments[0] is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(arguments[0]))}",
                span
            );
        PythonValue? notes = null;
        try
        {
            notes = ManagedObjectProtocols.GetAttribute(exception, "__notes__", span);
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError")) { }
        if (notes is null)
        {
            notes = new PythonListValue([]);
            ManagedObjectProtocols.SetAttribute(exception, "__notes__", notes, span);
        }
        if (notes is not PythonListValue list)
            throw Error("Cannot add note: __notes__ is not a list", span);
        list.Elements.Add(arguments[0]);
        return PythonNoneValue.Instance;
    }

    internal static bool TryGetAttribute(PythonValue type, string name, out PythonValue value)
    {
        if (type is PythonManagedTypeValue managed)
            return ManagedObjectProtocols.TryGetTypeAttribute(managed, name, out value);
        foreach (var entry in PythonBuiltinTypes.GetMro(type).Elements)
            if (
                entry is PythonExceptionTypeValue exception
                && TryGetOwnAttribute(exception, name, out value)
            )
                return true;
        value = null!;
        return false;
    }

    internal static bool UsesInheritedAllocator(PythonManagedTypeValue type)
    {
        foreach (var entry in PythonBuiltinTypes.GetMro(type).Elements)
        {
            if (
                entry is PythonManagedTypeValue managed
                && managed.Attributes.TryGetValue("__new__", out _)
            )
                return false;
            if (entry is PythonExceptionTypeValue builtin && OwnsAllocator(builtin.Name))
                return true;
        }
        return false;
    }

    private static bool OwnsAllocator(string name) =>
        name
            is "BaseException"
                or "Exception"
                or "ArithmeticError"
                or "LookupError"
                or "StopAsyncIteration"
                or "GeneratorExit"
                or "BaseExceptionGroup"
                or "RuntimeError"
                or "RecursionError"
                or "TypeError"
                or "ValueError"
                or "AssertionError"
                or "IndexError"
                or "OverflowError"
                or "ZeroDivisionError"
                or "OSError"
                or "UnicodeDecodeError"
                or "UnicodeEncodeError"
                or "EOFError"
                or "UnicodeError"
                or "KeyboardInterrupt";

    private static PythonExceptionValue Allocate(
        string owner,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
            throw Error($"{owner}.__new__(): not enough arguments", span);
        var type = arguments[0];
        if (!PythonTypeProtocols.IsType(type))
            throw Error(
                $"{owner}.__new__(X): X is not a type object ({ManagedObjectProtocols.GetTypeName(type)})",
                span
            );
        var ownerType = PythonBuiltinTypes.GetExceptionType(owner);
        if (
            !PythonBuiltinTypes
                .GetMro(type)
                .Elements.Any(entry => ReferenceEquals(entry, ownerType))
        )
        {
            var name = PythonTypeLayout.Name(type);
            throw Error($"{owner}.__new__({name}): {name} is not a subtype of {owner}", span);
        }
        return UserObjectProtocols.Dispatcher!.AllocateException(
            owner,
            type,
            arguments.Skip(1).ToArray(),
            span
        );
    }

    private static PythonNoneValue Initialize(
        string owner,
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        TextSpan span
    )
    {
        if (receiver is null)
        {
            if (arguments.Count == 0)
                throw Error($"descriptor '__init__' of '{owner}' object needs an argument", span);
            receiver = arguments[0];
            arguments = arguments.Skip(1).ToArray();
        }
        if (
            receiver is not PythonExceptionValue exception
            || !PythonBuiltinTypes
                .GetMro(PythonBuiltinTypes.GetRuntimeType(receiver))
                .Elements.Any(entry =>
                    ReferenceEquals(entry, PythonBuiltinTypes.GetExceptionType(owner))
                )
        )
            throw Error(
                $"descriptor '__init__' requires a '{owner}' object but received a '{ManagedObjectProtocols.GetTypeName(receiver)}'",
                span
            );
        if (keywordNames.Count != 0)
            throw Error($"{exception.TypeName}() takes no keyword arguments", span);
        ManagedObjectProtocols.ApplyBaseExceptionInit(exception, arguments);
        if (owner == "StopIteration" || owner == "SystemExit" && arguments.Count != 0)
            exception.InitializeSpecializedArguments(arguments);
        return PythonNoneValue.Instance;
    }

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
