using System.Collections.Concurrent;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Allocation and initialization slots for represented builtin exception types.</summary>
internal static class PythonExceptionProtocols
{
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
