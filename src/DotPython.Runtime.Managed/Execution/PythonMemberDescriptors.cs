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
        if (ownerName is not ("int" or "float"))
            return null;
        var names = ownerName == "int" ? IntNames : FloatNames;
        if (Array.IndexOf(names, name) < 0)
            return null;
        lock (Descriptors)
        {
            if (!Descriptors.TryGetValue((ownerName, name), out var descriptor))
            {
                descriptor = new PythonMemberDescriptorValue(
                    PythonBuiltinTypes.ForName(ownerName),
                    name,
                    ownerName == "int"
                        ? receiver => ReadIntMember(receiver, name)
                        : receiver => ReadFloatMember(receiver, name)
                );
                Descriptors[(ownerName, name)] = descriptor;
            }
            return descriptor;
        }
    }

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
