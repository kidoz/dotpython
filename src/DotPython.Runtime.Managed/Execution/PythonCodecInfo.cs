// The `codecs.lookup` surface follows CPython 3.14.7 Lib/codecs.py and
// Modules/_codecsmodule.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The bounded <c>codecs.lookup</c>/<c>codecs.getencoder</c>/<c>codecs.getdecoder</c> surface
/// over the represented text codecs.
/// </summary>
/// <remarks>
/// A <c>CodecInfo</c> carries the members the represented codecs can answer: the canonical
/// <c>name</c>, the stateless <c>encode</c> and <c>decode</c> callables, and
/// <c>_is_text_encoding</c>. Its <c>incrementalencoder</c>, <c>incrementaldecoder</c>,
/// <c>streamreader</c> and <c>streamwriter</c> members, and the <c>count</c>/<c>index</c>
/// methods its namedtuple base would provide, are absent.
/// </remarks>
internal static class PythonCodecInfo
{
    /// <summary>The `CodecInfo` class `type(codecs.lookup(...))` reports.</summary>
    private static readonly PythonManagedTypeValue CodecInfoType = new("CodecInfo")
    {
        Module = "codecs",
    };

    /// <summary>
    /// What an instance carries: the encoding to run, the canonical name `name` reports, and
    /// the `encodings` module stem its codec functions are named after.
    /// </summary>
    private sealed record Payload(ResolvedTextCodec Codec);

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("CodecInfo", CodecInfoType);
        globals.SetValue("lookup", CreateLookup());
        globals.SetValue("getencoder", CreateGet("getencoder", encoder: true));
        globals.SetValue("getdecoder", CreateGet("getdecoder", encoder: false));
    }

    internal static PythonManagedTypeValue Type => CodecInfoType;

    internal static PythonValue Lookup(PythonValue encoding, TextSpan span)
    {
        if (encoding is not PythonTextValue name)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"lookup() argument must be str, not {ManagedObjectProtocols.GetTypeName(encoding)}",
                span,
                "TypeError"
            );
        }
        var codec = PythonTextCodecs.ResolveCodec(name.Value, span);
        var instance = new PythonManagedObjectValue(CodecInfoType, new Payload(codec));
        instance.Attributes["name"] = new PythonTextValue(codec.CanonicalName);
        instance.Attributes["encode"] = CreateCodecFunction(codec, encoder: true);
        instance.Attributes["decode"] = CreateCodecFunction(codec, encoder: false);
        instance.Attributes["_is_text_encoding"] = PythonTruthValue.True;
        return instance;
    }

    /// <summary>The `CodecInfo` an instance is, or null for any other managed object.</summary>
    internal static string? InstanceName(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: Payload payload }
            ? payload.Codec.CanonicalName
            : null;

    /// <summary>`_codecs.lookup`: exactly one positional-only argument.</summary>
    private static PythonBuiltinFunctionValue CreateLookup() =>
        new(
            "lookup",
            (arguments, span) =>
            {
                if (arguments.Count != 1)
                    throw Arity(
                        "_codecs.lookup() takes exactly one argument",
                        arguments.Count,
                        span
                    );
                return Lookup(arguments[0], span);
            },
            (_, names, _, span) =>
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    names.Count == 0
                        ? "_codecs.lookup() takes exactly one argument (0 given)"
                        : "_codecs.lookup() takes no keyword arguments",
                    span,
                    "TypeError"
                )
        );

    /// <summary>`getencoder`/`getdecoder`: a Python-level wrapper, so its arity errors name it.</summary>
    private static PythonBuiltinFunctionValue CreateGet(string name, bool encoder)
    {
        PythonValue Resolve(PythonValue encoding, TextSpan span)
        {
            var info = Lookup(encoding, span);
            return ManagedObjectProtocols.GetAttributeCore(
                info,
                encoder ? "encode" : "decode",
                span
            );
        }

        IReadOnlyList<PythonValue> Bind(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> names,
            IReadOnlyList<PythonValue> values,
            TextSpan span
        )
        {
            var count = arguments.Count + names.Count;
            if (count == 0)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() missing 1 required positional argument: 'encoding'",
                    span,
                    "TypeError"
                );
            if (count > 1)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() takes 1 positional argument but {count} were given",
                    span,
                    "TypeError"
                );
            return names.Count == 0 ? arguments : values;
        }

        return new PythonBuiltinFunctionValue(
            name,
            (arguments, span) => Resolve(Bind(arguments, [], [], span)[0], span),
            (arguments, names, values, span) =>
                Resolve(Bind(arguments, names, values, span)[0], span)
        );
    }

    /// <summary>
    /// The stateless codec callable a `CodecInfo` exposes. Consumed counts are in the source
    /// unit — characters for an encoder, bytes for a decoder — as CPython reports them.
    /// </summary>
    private static PythonBuiltinFunctionValue CreateCodecFunction(
        ResolvedTextCodec codec,
        bool encoder
    )
    {
        var name = $"{codec.FunctionName}_{(encoder ? "encode" : "decode")}";

        PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span)
        {
            if (arguments.Count == 0)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() takes at least 1 argument (0 given)",
                    span,
                    "TypeError"
                );
            if (arguments.Count > 2)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() takes at most 2 arguments ({arguments.Count} given)",
                    span,
                    "TypeError"
                );
            var errors =
                arguments.Count == 2
                    ? PythonCodecs.ValidateName(arguments[1], $"{name}() argument 2", span)
                    : "strict";
            return encoder
                ? Encode(arguments[0], codec, errors, span, name)
                : Decode(arguments[0], codec, errors, span);
        }

        return new PythonBuiltinFunctionValue(
            name,
            Call,
            (_, names, _, span) =>
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() takes no keyword arguments",
                    span,
                    "TypeError"
                )
        );
    }

    private static PythonTupleValue Encode(
        PythonValue value,
        ResolvedTextCodec codec,
        string errors,
        TextSpan span,
        string name
    )
    {
        if (value is not PythonTextValue text)
        {
            var kind =
                value is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(value);
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() argument 1 must be str, not {kind}",
                span,
                "TypeError"
            );
        }
        var bytes = PythonTextEncoding.Encode(text, codec.Encoding, errors, span);
        return new PythonTupleValue([
            PythonByteSequenceValue.Create(bytes),
            PythonWholeNumberValue.Create(PythonTextTraversal.Count(text.Value, span)),
        ]);
    }

    private static PythonTupleValue Decode(
        PythonValue value,
        ResolvedTextCodec codec,
        string errors,
        TextSpan span
    )
    {
        if (!ManagedObjectProtocols.TryGetByteContent(value, out var contents))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"a bytes-like object is required, not '{ManagedObjectProtocols.GetTypeName(value)}'",
                span,
                "TypeError"
            );
        }
        var text = PythonBytesDecoding.Decode(
            contents,
            codec.Encoding.CodePage,
            codec.Encoding.GetPreamble().Length != 0,
            errors,
            span
        );
        return new PythonTupleValue([
            new PythonTextValue(text),
            PythonWholeNumberValue.Create(contents.Length),
        ]);
    }

    private static PythonRuntimeException Arity(string lead, int count, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", $"{lead} ({count} given)", span, "TypeError");
}
