// `object.__reduce__` — and the types that answer it themselves — follow CPython 3.14.7
// Objects/object.c (object_reduce, object_subclasshook) and the `reduce_newobj` entry:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The two object members the pickle machinery reads: `__reduce__`, which answers with the
/// type and the arguments that rebuild a value, and `__subclasshook__`, which a type
/// subclass can define to take over `issubclass`.
/// </summary>
/// <remarks>
/// Only the types that can be rebuilt from their contents answer `__reduce__`; everything
/// else reports CPython's own refusal. The protocol-2 shape `__reduce_ex__` produces, which
/// goes through `copyreg.__newobj__`, is not modelled.
/// </remarks>
internal static class PythonPickleProtocols
{
    /// <summary>The types whose `__reduce__` is their own rather than `object`'s.</summary>
    internal static bool OwnsReduce(string typeName) =>
        typeName is "set" or "frozenset" or "bytearray" or "range" or "slice" or "ellipsis";

    /// <summary>
    /// `__reduce__()`: the type a value is rebuilt from, the arguments it takes, and the
    /// state to restore afterwards — which only the types modelled here have.
    /// </summary>
    internal static PythonProtocolFunctionValue Reduce() =>
        new(
            "__reduce__",
            (receiver, arguments) =>
            {
                if (arguments.Count != 0)
                    throw Fault(
                        $"{Owner(receiver!)}.__reduce__() takes no arguments "
                            + $"({arguments.Count} given)"
                    );
                return Reduced(receiver!);
            }
        );

    /// <summary>`__subclasshook__(cls)` and `__subclasshook__(subclass)`, which answer NotImplemented.</summary>
    internal static PythonBuiltinFunctionValue SubclassHook(PythonValue owner, string typeName) =>
        new(
            "__subclasshook__",
            (arguments, _) =>
            {
                if (arguments.Count != 1)
                    throw Fault(
                        $"{typeName}.__subclasshook__() takes exactly one argument "
                            + $"({arguments.Count} given)"
                    );
                return PythonNotImplementedValue.Instance;
            }
        )
        {
            BoundTo = owner,
        };

    /// <summary>Which type's name a refusal from the member reports, which is the owner's.</summary>
    private static string Owner(PythonValue receiver)
    {
        var typeName = PythonBuiltinTypes.GetRuntimeTypeName(receiver);
        return OwnsReduce(typeName) ? typeName : "object";
    }

    private static PythonValue Reduced(PythonValue receiver) =>
        receiver switch
        {
            PythonSetValue set => new PythonTupleValue([
                PythonBuiltinTypes.ForName(set.IsFrozen ? "frozenset" : "set"),
                new PythonTupleValue([new PythonListValue([.. set.Elements])]),
                PythonNoneValue.Instance,
            ]),
            PythonByteArrayValue mutable => new PythonTupleValue([
                PythonBuiltinTypes.ByteArray,
                new PythonTupleValue([
                    new PythonTextValue(System.Text.Encoding.Latin1.GetString(mutable.Value)),
                    new PythonTextValue("latin-1"),
                ]),
                PythonNoneValue.Instance,
            ]),
            PythonRangeValue range => new PythonTupleValue([
                PythonBuiltinTypes.ForName("range"),
                new PythonTupleValue([
                    PythonWholeNumberValue.Create(range.Start),
                    PythonWholeNumberValue.Create(range.Stop),
                    PythonWholeNumberValue.Create(range.Step),
                ]),
            ]),
            PythonSliceValue slice => new PythonTupleValue([
                PythonBuiltinTypes.ForName("slice"),
                new PythonTupleValue([slice.Start, slice.Stop, slice.Step]),
            ]),
            PythonEllipsisValue => new PythonTextValue("Ellipsis"),
            _ => throw Fault(
                $"cannot pickle '{ManagedObjectProtocols.GetTypeName(receiver)}' object"
            ),
        };

    private static PythonRuntimeException Fault(string message) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, "TypeError");
}
