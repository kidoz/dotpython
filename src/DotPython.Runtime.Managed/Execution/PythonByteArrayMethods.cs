// The bytearray method surface follows CPython 3.14.7 Objects/bytearrayobject.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The methods of <c>bytearray</c>: the whole <c>bytes</c> surface, retyped so a non-mutating
/// method returns a bytearray, plus the mutators that only a bytearray has.
/// </summary>
internal static class PythonByteArrayMethods
{
    internal static Dictionary<string, PythonProtocolFunctionValue> CreateTable()
    {
        var table = new Dictionary<string, PythonProtocolFunctionValue>(StringComparer.Ordinal);
        foreach (var (name, method) in PythonBytesMethods.CreateTable())
        {
            // Every `bytes` recipe works on the contents; only the result's type differs,
            // and `bytearray.upper()` is a bytearray.
            table[name] = Retype(name, method);
        }
        // `decode` lives outside the shared recipe table, as it does for bytes.
        table["decode"] = Retype("decode", PythonBytesText.DecodeMethod);
        table["copy"] = Copy();
        table["append"] = Append();
        table["extend"] = Extend();
        table["insert"] = Insert();
        table["pop"] = Pop();
        table["remove"] = Remove();
        table["clear"] = Clear();
        table["reverse"] = Reverse();
        return table;
    }

    /// <summary>
    /// Runs a `bytes` method over the bytearray's contents and re-wraps any bytes result as a
    /// bytearray. The array the method returns is freshly built by that method or copied from
    /// an interned singleton, so the mutable result never aliases shared storage.
    /// </summary>
    private static PythonProtocolFunctionValue Retype(
        string name,
        PythonProtocolFunctionValue method
    ) =>
        new(
            name,
            (target, arguments) =>
                Retype(name, method, target, arguments, invokeWithKeywords: false),
            (target, positional, names, values) =>
                Retype(name, method, target, positional, invokeWithKeywords: true, values, names)
        );

    private static PythonValue Retype(
        string name,
        PythonProtocolFunctionValue method,
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool invokeWithKeywords,
        IReadOnlyList<PythonValue>? named = null,
        IReadOnlyList<string>? names = null
    )
    {
        var mutable = (PythonByteArrayValue)target!;
        if (invokeWithKeywords && method.InvokeWithKeywords is null)
        {
            // Not every bytes recipe accepts keywords, and CPython names the type and method.
            throw Fault(
                $"{ManagedObjectProtocols.GetTypeName(mutable)}.{name}() takes no keyword "
                    + "arguments",
                "TypeError"
            );
        }
        var view = new PythonByteSequenceValue(mutable.Value);
        var read = invokeWithKeywords
            ? method.InvokeWithKeywords!(view, positional, names!, named!)
            : method.Invoke(view, positional);
        return RetypeResult(read);
    }

    /// <summary>
    /// A `bytes` recipe returns bytes, a list of them, or a tuple of them; each one becomes a
    /// bytearray. The arrays are freshly built or copied from an interned singleton, so the
    /// mutable results never share storage.
    /// </summary>
    private static PythonValue RetypeResult(PythonValue value) =>
        value switch
        {
            PythonByteSequenceValue bytes => new PythonByteArrayValue([.. bytes.Value]),
            PythonListValue list => new PythonListValue([.. list.Elements.Select(RetypeResult)]),
            PythonTupleValue tuple => new PythonTupleValue([
                .. tuple.Elements.Select(RetypeResult),
            ]),
            _ => value,
        };

    private static PythonProtocolFunctionValue Copy() =>
        new(
            "copy",
            (target, arguments) =>
            {
                RequireArguments("copy", arguments, 0, 0);
                return new PythonByteArrayValue([.. ((PythonByteArrayValue)target!).Value]);
            }
        );

    private static PythonProtocolFunctionValue Append() =>
        new(
            "append",
            (target, arguments) =>
            {
                RequireArguments("append", arguments, 1, 1);
                var mutable = (PythonByteArrayValue)target!;
                var value = RequireByte(arguments[0], "append");
                var grown = new byte[mutable.Value.Length + 1];
                mutable.Value.CopyTo(grown, 0);
                grown[^1] = value;
                mutable.Value = grown;
                return PythonNoneValue.Instance;
            }
        );

    private static PythonProtocolFunctionValue Extend() =>
        new(
            "extend",
            (target, arguments) =>
            {
                RequireArguments("extend", arguments, 1, 1);
                var mutable = (PythonByteArrayValue)target!;
                // The bytes constructor already converts any iterable of integers and
                // reports CPython's errors for the values it cannot accept.
                var addition = PythonBytesConstruction
                    .ConstructNamed("bytearray", [arguments[0]], [], [], default)
                    .Value;
                var grown = new byte[mutable.Value.Length + addition.Length];
                mutable.Value.CopyTo(grown, 0);
                addition.CopyTo(grown, mutable.Value.Length);
                mutable.Value = grown;
                return PythonNoneValue.Instance;
            }
        );

    private static PythonProtocolFunctionValue Insert() =>
        new(
            "insert",
            (target, arguments) =>
            {
                RequireArguments("insert", arguments, 2, 2);
                var mutable = (PythonByteArrayValue)target!;
                var value = RequireByte(arguments[1], "insert");
                var index = RequireIndex(arguments[0], mutable.Value.Length);
                if (index < 0)
                    index += mutable.Value.Length;
                index = Math.Clamp(index, 0, mutable.Value.Length);
                var grown = new byte[mutable.Value.Length + 1];
                mutable.Value.AsSpan(0, index).CopyTo(grown);
                grown[index] = value;
                mutable.Value.AsSpan(index).CopyTo(grown.AsSpan(index + 1));
                mutable.Value = grown;
                return PythonNoneValue.Instance;
            }
        );

    private static PythonProtocolFunctionValue Pop() =>
        new(
            "pop",
            (target, arguments) =>
            {
                RequireArguments("pop", arguments, 0, 1);
                var mutable = (PythonByteArrayValue)target!;
                if (mutable.Value.Length == 0)
                    throw Fault("pop from empty bytearray", "IndexError");
                var index =
                    arguments.Count == 0 ? -1 : RequireIndex(arguments[0], mutable.Value.Length);
                if (index < 0)
                    index += mutable.Value.Length;
                if (index < 0 || index >= mutable.Value.Length)
                    throw Fault("pop index out of range", "IndexError");
                var removed = mutable.Value[index];
                var shrunk = new byte[mutable.Value.Length - 1];
                mutable.Value.AsSpan(0, index).CopyTo(shrunk);
                mutable.Value.AsSpan(index + 1).CopyTo(shrunk.AsSpan(index));
                mutable.Value = shrunk;
                return PythonWholeNumberValue.Create(removed);
            }
        );

    private static PythonProtocolFunctionValue Remove() =>
        new(
            "remove",
            (target, arguments) =>
            {
                RequireArguments("remove", arguments, 1, 1);
                var mutable = (PythonByteArrayValue)target!;
                var value = RequireByte(arguments[0], "remove");
                var index = mutable.Value.AsSpan().IndexOf(value);
                if (index < 0)
                    throw Fault("value not found in bytearray", "ValueError");
                var shrunk = new byte[mutable.Value.Length - 1];
                mutable.Value.AsSpan(0, index).CopyTo(shrunk);
                mutable.Value.AsSpan(index + 1).CopyTo(shrunk.AsSpan(index));
                mutable.Value = shrunk;
                return PythonNoneValue.Instance;
            }
        );

    private static PythonProtocolFunctionValue Clear() =>
        new(
            "clear",
            (target, arguments) =>
            {
                RequireArguments("clear", arguments, 0, 0);
                ((PythonByteArrayValue)target!).Value = [];
                return PythonNoneValue.Instance;
            }
        );

    private static PythonProtocolFunctionValue Reverse() =>
        new(
            "reverse",
            (target, arguments) =>
            {
                RequireArguments("reverse", arguments, 0, 0);
                var mutable = (PythonByteArrayValue)target!;
                Array.Reverse(mutable.Value);
                return PythonNoneValue.Instance;
            }
        );

    private static byte RequireByte(PythonValue value, string name)
    {
        var number = value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be "
                    + "interpreted as an integer",
                "TypeError"
            ),
        };
        if (number < 0 || number > 255)
            throw Fault("byte must be in range(0, 256)", "ValueError");
        return (byte)number;
    }

    private static int RequireIndex(PythonValue value, int length)
    {
        if (value is PythonWholeNumberValue whole)
        {
            var index = whole.Value;
            if (index > int.MaxValue)
                return int.MaxValue;
            if (index < int.MinValue)
                return int.MinValue;
            return (int)index;
        }
        if (value is PythonTruthValue truth)
            return truth.Value ? 1 : 0;
        throw Fault(
            "slice indices must be integers or None or have an __index__ method",
            "TypeError"
        );
    }

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int minimum,
        int maximum
    )
    {
        if (arguments.Count >= minimum && arguments.Count <= maximum)
            return;
        var expectation = minimum == maximum ? $"{maximum}" : $"between {minimum} and {maximum}";
        throw Fault(
            $"Method '{name}' expected {expectation} argument(s), "
                + $"but received {arguments.Count}.",
            "TypeError"
        );
    }

    private static PythonRuntimeException Fault(string message, string pythonType) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, pythonType);
}
