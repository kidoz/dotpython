// Callback validation follows CPython 3.14.7 Objects/unicodeobject.c:
// https://github.com/python/cpython/blob/v3.14.7/Objects/unicodeobject.c
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>A codec invocation's lazily resolved handler and reusable exception.</summary>
internal sealed class PythonCodecCallback(string name, TextSpan span)
{
    private PythonValue? _handler;
    private bool _builtin;
    private PythonExceptionValue? _exception;

    internal bool IsBuiltin()
    {
        if (_handler is null)
        {
            var registry =
                UserObjectProtocols.Dispatcher?.CodecErrors ?? new PythonCodecErrorRegistry();
            _handler = registry.Lookup(name, span);
            _builtin = registry.IsBuiltin(name, _handler);
        }
        return _builtin;
    }

    internal PythonExceptionValue Error(
        string encoding,
        PythonValue source,
        int start,
        int end,
        string reason
    )
    {
        if (_exception is null)
        {
            _exception = new PythonExceptionValue(
                source is PythonTextValue ? "UnicodeEncodeError" : "UnicodeDecodeError",
                string.Empty
            );
            PythonUnicodeErrors.Initialize(
                _exception,
                [
                    new PythonTextValue(encoding),
                    source,
                    PythonWholeNumberValue.Create(start),
                    PythonWholeNumberValue.Create(end),
                    new PythonTextValue(reason),
                ],
                span
            );
        }
        else
        {
            // args/object/encoding and user attributes survive subsequent calls.
            var state = _exception.UnicodeErrorState!;
            state.Start = start;
            state.End = end;
            state.Reason = new PythonTextValue(reason);
        }
        return _exception;
    }

    internal (PythonValue Replacement, long Position) Invoke(
        PythonExceptionValue error,
        bool decode
    )
    {
        _ = IsBuiltin();
        var dispatcher = UserObjectProtocols.Dispatcher!;
        dispatcher.CheckIterationWork(span);
        var result = dispatcher.Invoke(_handler!, [error], span);
        dispatcher.CheckIterationWork(span);
        var message = decode
            ? "decoding error handler must return (str, int) tuple"
            : "encoding error handler must return (str/bytes, int) tuple";
        if (result is not PythonTupleValue tuple || tuple.Elements.Length != 2)
            throw Fault(message);
        var replacement = tuple.Elements[0];
        // Decode's U conversion precedes n; encode's O conversion validates later.
        if (decode && replacement is not PythonTextValue)
            throw Fault(message);
        var positionValue = tuple.Elements[1];
        BigInteger position;
        if (positionValue is PythonWholeNumberValue number)
            position = number.Value;
        else if (positionValue is PythonTruthValue truth)
            position = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(positionValue, span, out position))
            throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(positionValue)}' object cannot be interpreted as an integer"
            );
        if (position < long.MinValue || position > long.MaxValue)
            throw Fault("Python int too large to convert to C ssize_t", "OverflowError");
        if (!decode && replacement is not (PythonTextValue or PythonByteSequenceValue))
            throw Fault(message);
        return (replacement, (long)position);
    }

    internal int NormalizePosition(long position, int length)
    {
        if (position < 0)
            position += length;
        if (position < 0 || position > length)
            throw Fault($"position {position} from error handler out of bounds", "IndexError");
        return (int)position;
    }

    private PythonRuntimeException Fault(string message, string type = "TypeError") =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
