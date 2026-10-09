// The member descriptors of the numeric types follow CPython 3.14.7 Objects/longobject.c
// and Objects/floatobject.c, whose getset tables also supply the member ordering:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The numeric members as the type object publishes them: <c>int.real</c> is a descriptor
/// that reads <c>(5).real</c>, not the value.
/// </summary>
internal static class PythonMemberDescriptors
{
    internal static PythonMemberDescriptorValue? Get(string typeName, string name)
    {
        // A bool answers int's members, under int's name.
        var ownerName = typeName == "bool" ? "int" : typeName;
        if (ownerName is not ("int" or "float" or "memoryview" or "range" or "deque"))
            return null;
        var names =
            ownerName == "int" ? IntNames
            : ownerName == "float" ? FloatNames
            : ownerName == "range" ? RangeNames
            : ownerName == "deque" ? ["maxlen"]
            : ViewNames;
        if (Array.IndexOf(names, name) < 0)
            return null;
        lock (Descriptors)
        {
            if (!Descriptors.TryGetValue((ownerName, name), out var descriptor))
            {
                descriptor = new PythonMemberDescriptorValue(
                    PythonBuiltinTypes.ForName(ownerName),
                    name,
                    ownerName == "int" ? receiver => ReadIntMember(receiver, name)
                        : ownerName == "float" ? receiver => ReadFloatMember(receiver, name)
                        : ownerName == "deque"
                            ? receiver => ReadDequeMember((PythonDequeValue)receiver, name)
                        : ownerName == "range"
                            ? receiver => ReadRangeMember((PythonRangeValue)receiver, name)
                        : receiver =>
                            PythonMemoryViewMethods.GetAttribute(receiver, name, default)
                            ?? throw ManagedObjectProtocols.Fault(
                                "DPY4023",
                                $"'memoryview' object has no attribute '{name}'",
                                default,
                                "AttributeError"
                            )
                )
                {
                    // A range declares its bounds as members; the others are getset
                    // attributes.
                    Kind = ownerName == "range" ? "member" : "attribute",
                };
                Descriptors[(ownerName, name)] = descriptor;
            }
            return descriptor;
        }
    }

    /// <summary>The bounds a range carries.</summary>
    private static readonly string[] RangeNames = ["start", "stop", "step"];

    private static PythonValue ReadDequeMember(PythonDequeValue deque, string name) =>
        deque.MaxLength is { } limit
            ? PythonWholeNumberValue.Create(limit)
            : PythonNoneValue.Instance;

    private static PythonWholeNumberValue ReadRangeMember(PythonRangeValue range, string name) =>
        name switch
        {
            "start" => PythonWholeNumberValue.Create(range.Start),
            "stop" => PythonWholeNumberValue.Create(range.Stop),
            _ => PythonWholeNumberValue.Create(range.Step),
        };

    /// <summary>The data members a view answers, in the order CPython declares them.</summary>
    private static readonly string[] ViewNames =
    [
        "obj",
        "format",
        "itemsize",
        "ndim",
        "shape",
        "strides",
        "readonly",
        "nbytes",
        "contiguous",
        "c_contiguous",
        "f_contiguous",
        "suboffsets",
    ];

    private static PythonValue ReadIntMember(PythonValue receiver, string name) =>
        name switch
        {
            "imag" => PythonWholeNumberValue.Create(0),
            "denominator" => PythonWholeNumberValue.Create(1),
            _ => AsInteger(receiver),
        };

    private static PythonValue ReadFloatMember(PythonValue receiver, string name) =>
        name == "imag" ? new PythonFloatingPointValue(0.0) : receiver;

    /// <summary>The value as an `int`, which is what reading a member of a bool produces.</summary>
    private static PythonValue AsInteger(PythonValue receiver) =>
        receiver is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? 1 : 0)
            : receiver;

    private static readonly string[] IntNames = ["real", "imag", "numerator", "denominator"];
    private static readonly string[] FloatNames = ["real", "imag"];

    private static readonly Dictionary<
        (string Owner, string Name),
        PythonMemberDescriptorValue
    > Descriptors = new();
}
