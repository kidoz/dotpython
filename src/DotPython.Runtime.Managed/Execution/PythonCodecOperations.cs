// Stateless codec dispatch follows CPython 3.14.7 Modules/_codecsmodule.c,
// Python/codecs.c and Python/errors.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Stateless module entry points over the represented text codecs.</summary>
internal static class PythonCodecOperations
{
    private static readonly string[] Parameters = ["obj", "encoding", "errors"];

    internal static PythonBuiltinFunctionValue Create(bool decode)
    {
        var name = decode ? "decode" : "encode";
        return new PythonBuiltinFunctionValue(
            name,
            (arguments, span) => Call(decode, arguments, [], [], span),
            (arguments, names, values, span) => Call(decode, arguments, names, values, span)
        );
    }

    private static PythonValue Call(
        bool decode,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var name = decode ? "decode" : "encode";
        var count = arguments.Count + names.Count;
        if (count > 3)
            throw Error(
                $"{name}() takes at most 3 {(arguments.Count == 0 ? "keyword arguments" : "arguments")} ({count} given)",
                span
            );
        if (arguments.Count == 0 && !names.Contains("obj"))
            throw Error($"{name}() missing required argument 'obj' (pos 1)", span);
        // Clinic checks duplicate slots in parameter order before unknown keywords.
        for (var i = 0; i < arguments.Count; i++)
            if (names.Contains(Parameters[i]))
                throw Error(
                    $"argument for {name}() given by name ('{Parameters[i]}') and position ({i + 1})",
                    span
                );
        var slots = PythonKeywordArguments.Bind(
            name,
            Parameters,
            0,
            arguments,
            names,
            values,
            span
        );
        var encoding = slots[1] is { } encodingValue
            ? PythonCodecs.ValidateName(encodingValue, $"{name}() argument 'encoding'", span)
            : "utf-8";
        var errors = slots[2] is { } errorsValue
            ? PythonCodecs.ValidateName(errorsValue, $"{name}() argument 'errors'", span)
            : "strict";
        // Lookup occurs even for empty input, and outside the annotated codec call.
        var codec = PythonTextCodecs.Resolve(encoding, span);
        try
        {
            if (decode)
            {
                if (slots[0] is not PythonByteSequenceValue bytes)
                    throw Error(
                        $"a bytes-like object is required, not '{ManagedObjectProtocols.GetTypeName(slots[0]!)}'",
                        span
                    );
                return new PythonTextValue(
                    PythonBytesDecoding.Decode(
                        bytes.Value,
                        codec.CodePage,
                        codec.GetPreamble().Length != 0,
                        errors,
                        span
                    )
                );
            }
            if (slots[0] is not PythonTextValue text)
            {
                var encoderName = codec.CodePage switch
                {
                    65001 => "utf_8",
                    20127 => "ascii",
                    28591 => "latin_1",
                    1200 => codec.GetPreamble().Length == 0 ? "utf_16_le" : "utf_16",
                    1201 => "utf_16_be",
                    _ => throw new InvalidOperationException("Unsupported encoding code page."),
                };
                throw Error(
                    $"{encoderName}_encode() argument 1 must be str, not {(slots[0] is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(slots[0]!))}",
                    span
                );
            }
            return PythonByteSequenceValue.Create(
                PythonTextEncoding.Encode(text, codec, errors, span)
            );
        }
        catch (PythonRaisedException raised)
        {
            AddNote(raised, decode, encoding, span);
            throw;
        }
        catch (PythonRuntimeException fault) when (IsPythonFault(fault))
        {
            var indicator = new PythonErrorIndicator();
            indicator.TrySetFromRuntimeFault(fault);
            var raised = indicator.GetRaisedException()!;
            AddNote(raised, decode, encoding, span);
            throw raised;
        }
    }

    private static void AddNote(
        PythonRaisedException raised,
        bool decode,
        string encoding,
        TextSpan span
    )
    {
        try
        {
            // Use BaseException's implementation, ignoring add_note overrides.
            PythonExceptionProtocols.AddNote(
                raised.Value,
                $"{(decode ? "decoding" : "encoding")} with '{encoding}' codec failed",
                span
            );
        }
        catch (PythonRaisedException noteError)
        {
            if (!ReferenceEquals(noteError.Value, raised.Value))
                noteError.Value.Context = raised.Value;
            throw;
        }
        catch (PythonRuntimeException fault) when (IsPythonFault(fault))
        {
            var indicator = new PythonErrorIndicator();
            indicator.TrySetFromRuntimeFault(fault);
            var noteError = indicator.GetRaisedException()!;
            if (!ReferenceEquals(noteError.Value, raised.Value))
                noteError.Value.Context = raised.Value;
            throw noteError;
        }
    }

    private static bool IsPythonFault(PythonRuntimeException fault) =>
        (
            fault.PythonExceptionTypeName
            ?? PythonErrorIndicator.GetPythonExceptionTypeName(fault.Code)
        )
            is not null;

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
