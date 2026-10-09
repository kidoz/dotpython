// Method descriptors follow CPython 3.14.7 Objects/descrobject.c (method_vectorcall and
// method_get) and the attributes PyDescr_NewMethod gives them:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// A method seen through the type rather than an instance: <c>list.append</c> where
/// <c>[].append</c> is the bound method.
/// </summary>
/// <remarks>
/// Nothing is bound yet, so the receiver is the first positional argument — a call with no
/// positional argument reports that the method is unbound, and one whose receiver is of the
/// wrong type is refused before the method itself runs. CPython keeps one descriptor per
/// type and name, so repeated lookups are the same object and <c>is</c> holds.
/// </remarks>
internal sealed record PythonMethodDescriptorValue(
    PythonBuiltinTypeValue Owner,
    string Name,
    PythonProtocolFunctionValue Function
) : PythonValue
{
    /// <summary>
    /// A slot wrapper rather than a method descriptor: the same binding, but it reports
    /// itself as a wrapper and phrases its own argument errors without naming anything.
    /// </summary>
    internal bool IsWrapper { get; init; }

    internal string OwnerName => Owner.Name;

    internal override string ToDisplayString() =>
        IsWrapper
            ? $"<slot wrapper '{Name}' of '{OwnerName}' objects>"
            : $"<method '{Name}' of '{OwnerName}' objects>";

    internal PythonValue GetAttribute(string name, TextSpan span) =>
        name switch
        {
            "__name__" => new PythonTextValue(Name),
            "__qualname__" => new PythonTextValue($"{OwnerName}.{Name}"),
            "__objclass__" => Owner,
            "__get__" => new PythonBuiltinFunctionValue(
                "__get__",
                (arguments, callSpan) => Get(arguments, callSpan),
                (arguments, names, _, callSpan) =>
                    names.Count == 0
                        ? Get(arguments, callSpan)
                        : throw Fault("wrapper __get__() takes no keyword arguments", callSpan)
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'method_descriptor' object has no attribute '{name}'",
                span,
                "AttributeError"
            ),
        };

    /// <summary>The keyword refusal, which a wrapper words differently.</summary>
    internal string KeywordRefusal =>
        IsWrapper
            ? $"wrapper {Name}() takes no keyword arguments"
            : $"{OwnerName}.{Name}() takes no keyword arguments";

    /// <summary>The callable form: the first positional argument is the receiver.</summary>
    internal PythonValue Invoke(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var (receiver, rest) = BindReceiver(arguments, span);
        return Function.Invoke(receiver, rest);
    }

    /// <summary>
    /// Splits the receiver off the front of a call, refusing an unbound call and a receiver
    /// of the wrong type the way CPython does, in that order.
    /// </summary>
    internal (PythonValue Receiver, PythonValue[] Arguments) BindReceiver(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
            throw Fault(
                IsWrapper
                    ? $"descriptor '{Name}' of '{OwnerName}' object needs an argument"
                    : $"unbound method {OwnerName}.{Name}() needs an argument",
                span
            );
        var receiver = arguments[0];
        if (!AppliesTo(receiver))
        {
            var received = ManagedObjectProtocols.GetTypeName(receiver);
            throw Fault(
                IsWrapper
                    ? $"descriptor '{Name}' requires a '{OwnerName}' object but received a "
                        + $"'{received}'"
                    : $"descriptor '{Name}' for '{OwnerName}' objects doesn't apply to a "
                        + $"'{received}' object",
                span
            );
        }
        var rest = new PythonValue[arguments.Count - 1];
        for (var index = 1; index < arguments.Count; index++)
            rest[index - 1] = arguments[index];
        return (receiver, rest);
    }

    /// <summary>
    /// Which values this type's methods accept. A type and its sibling are both exact: a
    /// frozenset is not a set, and only `bool` borrows another type's methods.
    /// </summary>
    private bool AppliesTo(PythonValue receiver) =>
        OwnerName switch
        {
            "str" => receiver is PythonTextValue,
            "bytes" => receiver is PythonByteSequenceValue,
            "bytearray" => receiver is PythonByteArrayValue,
            "memoryview" => receiver is PythonMemoryViewValue,
            "list" => receiver is PythonListValue,
            "tuple" => receiver is PythonTupleValue,
            "dict" => receiver is PythonDictionaryValue,
            "set" => receiver is PythonSetValue { IsFrozen: false },
            "frozenset" => receiver is PythonSetValue { IsFrozen: true },
            "int" => receiver is PythonWholeNumberValue or PythonTruthValue,
            "float" => receiver is PythonFloatingPointValue,
            // `object`'s own members answer for anything.
            "object" => true,
            _ => false,
        };

    private PythonValue Get(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count == 0)
            throw Fault("__get__ expected at least 1 argument, got 0", span);
        if (arguments.Count > 2)
            throw Fault($"__get__ expected at most 2 arguments, got {arguments.Count}", span);
        return Bind(arguments[0], arguments.Count == 2 ? arguments[1] : null, span);
    }

    /// <summary>
    /// `__get__(instance, owner)`: bound to an instance, or the same descriptor when the
    /// instance is None — which is how a class attribute read reaches it.
    /// </summary>
    private PythonValue Bind(PythonValue? instance, PythonValue? owner, TextSpan span)
    {
        if (instance is null or PythonNoneValue)
        {
            if (owner is null or PythonNoneValue)
                throw Fault("__get__(None, None) is invalid", span);
            return this;
        }
        if (!AppliesTo(instance))
            throw Fault(
                $"descriptor '{Name}' for '{OwnerName}' objects doesn't apply to a "
                    + $"'{ManagedObjectProtocols.GetTypeName(instance)}' object",
                span
            );

        return new PythonBoundMethodValue(Name, instance, Function) { IsWrapper = IsWrapper };
    }

    // Descriptors are interned per type and name, so identity is the answer to equality.
    public bool Equals(PythonMethodDescriptorValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    private static PythonRuntimeException Fault(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
