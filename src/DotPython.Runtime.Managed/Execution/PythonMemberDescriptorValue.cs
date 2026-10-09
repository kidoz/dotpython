// Member descriptors follow CPython 3.14.7 Objects/descrobject.c (PyDescr_NewGetSet and
// getset_get) — the type-level view of `int.real` and the numbers beside it:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// A value member seen through the type rather than an instance: <c>int.real</c> where
/// <c>(5).real</c> is the value itself.
/// </summary>
/// <remarks>
/// It reads, so `__get__` answers the value for an instance and the descriptor itself for
/// `None`; it is not callable, and CPython interns one per type and name.
/// </remarks>
internal sealed record PythonMemberDescriptorValue(
    PythonBuiltinTypeValue Owner,
    string Name,
    Func<PythonValue, PythonValue> Read
) : PythonValue
{
    internal string OwnerName => Owner.Name;

    /// <summary>The word CPython declares the descriptor with, `member` or `attribute`.</summary>
    internal string Kind { get; init; } = "attribute";

    internal override string ToDisplayString() =>
        $"<{Kind} '{Name}' of '{Owner.QualifiedName}' objects>";

    /// <summary>
    /// Which values carry the member. A bool answers int's members, and only int defines
    /// `numerator` and `denominator`.
    /// </summary>
    internal bool AppliesTo(PythonValue receiver) =>
        OwnerName switch
        {
            "int" => receiver is PythonWholeNumberValue or PythonTruthValue,
            "float" => receiver is PythonFloatingPointValue,
            "memoryview" => receiver is PythonMemoryViewValue,
            "range" => receiver is PythonRangeValue,
            "deque" => receiver is PythonDequeValue,
            _ => false,
        };

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
                $"'getset_descriptor' object has no attribute '{name}'",
                span,
                "AttributeError"
            ),
        };

    /// <summary>The value the member reads, which is what a descriptor access produces.</summary>
    internal PythonValue Get(PythonValue receiver) => Read(receiver);

    private PythonValue Get(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count == 0)
            throw Fault("__get__ expected at least 1 argument, got 0", span);
        if (arguments.Count > 2)
            throw Fault($"__get__ expected at most 2 arguments, got {arguments.Count}", span);
        return Bind(arguments[0], arguments.Count == 2 ? arguments[1] : null, span);
    }

    /// <summary>
    /// `__get__(instance, owner)`: the value for an instance, or the descriptor itself for
    /// `None`, which is how a class attribute read reaches it.
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
                $"descriptor '{Name}' for '{Owner.QualifiedName}' objects doesn't apply to a "
                    + $"'{ManagedObjectProtocols.GetTypeName(instance)}' object",
                span
            );
        return Read(instance);
    }

    // Interned per type and name, so identity is the answer to equality.
    public bool Equals(PythonMemberDescriptorValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    private static PythonRuntimeException Fault(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
