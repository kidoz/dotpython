// Error-handler semantics adapted from CPython 3.14.7 Python/codecs.c:
// https://github.com/python/cpython/blob/v3.14.7/Python/codecs.c
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>The bounded built-in error-handler surface of codecs.</summary>
internal static class PythonCodecs
{
    private static readonly ConditionalWeakTable<PythonTextValue, byte[]> NameBytes = new();

    private const int MaximumReplacement = 10_000_000;

    internal static Dictionary<string, PythonValue> CreateHandlers()
    {
        var handlers = new Dictionary<string, PythonValue>(StringComparer.Ordinal);
        foreach (
            var name in new[]
            {
                "strict",
                "ignore",
                "replace",
                "xmlcharrefreplace",
                "backslashreplace",
                "namereplace",
                "surrogatepass",
                "surrogateescape",
            }
        )
        {
            var functionName = name is "surrogatepass" or "surrogateescape"
                ? name
                : name + "_errors";
            var handler = Unary(
                functionName,
                functionName,
                (value, span) => Handle(name, value, span)
            );
            handlers.Add(name, handler);
        }
        return handlers;
    }

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        var registry =
            UserObjectProtocols.Dispatcher?.CodecErrors ?? new PythonCodecErrorRegistry();
        foreach (
            var name in new[]
            {
                "strict",
                "ignore",
                "replace",
                "xmlcharrefreplace",
                "backslashreplace",
                "namereplace",
            }
        )
            globals.SetValue(name + "_errors", registry.Lookup(name, default));
        globals.SetValue(
            "lookup_error",
            Unary(
                "lookup_error",
                "_codecs.lookup_error",
                (value, span) =>
                    registry.Lookup(ValidateName(value, "lookup_error() argument", span), span)
            )
        );
        PythonValue Register(IReadOnlyList<PythonValue> arguments, TextSpan span)
        {
            if (arguments.Count != 2)
                throw Error($"register_error expected 2 arguments, got {arguments.Count}", span);
            var name = ValidateName(arguments[0], "register_error() argument 1", span);
            if (!ManagedObjectProtocols.IsCallable(arguments[1]))
                throw Error("handler must be callable", span);
            registry.Register(name, arguments[1]);
            return PythonNoneValue.Instance;
        }
        globals.SetValue(
            "register_error",
            new PythonBuiltinFunctionValue(
                "register_error",
                Register,
                (arguments, names, _, span) =>
                    names.Count == 0
                        ? Register(arguments, span)
                        : throw Error("_codecs.register_error() takes no keyword arguments", span)
            )
        );
    }

    internal static string ValidateName(PythonValue value, string argument, TextSpan span)
    {
        if (value is not PythonTextValue text)
            throw Error(
                $"{argument} must be str, not {(value is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(value))}",
                span
            );
        return ConvertName(text, span);
    }

    // CPython memoizes PyUnicode_AsUTF8 on the string object, even when its
    // strict handler supplied a replacement. Weak identity keys retain no VM.
    private static byte[] Utf8Name(PythonTextValue text, TextSpan span) =>
        NameBytes.GetValue(
            text,
            key => PythonTextEncoding.Encode(key, new UTF8Encoding(false), "strict", span)
        );

    internal static string ConvertName(PythonTextValue text, TextSpan span)
    {
        var utf8 = Utf8Name(text, span);
        if (Array.IndexOf(utf8, (byte)0) >= 0)
            throw Error("embedded null character", span, "ValueError");
        return PythonBytesDecoding.Decode(utf8, 65001, false, "strict", span);
    }

    internal static PythonRuntimeException UnknownHandler(string name, TextSpan span)
    {
        var utf8 = Utf8Name(new PythonTextValue(name), span);
        var length = Math.Min(400, utf8.Length);
        while (length < utf8.Length && length > 0 && (utf8[length] & 0xc0) == 0x80)
            length--;
        return Error(
            $"unknown error handler name '{Encoding.UTF8.GetString(utf8, 0, length)}'",
            span,
            "LookupError"
        );
    }

    private static PythonBuiltinFunctionValue Unary(
        string name,
        string diagnosticName,
        Func<PythonValue, TextSpan, PythonValue> invoke
    )
    {
        PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span)
        {
            if (arguments.Count != 1)
                throw Error(
                    $"{diagnosticName}() takes exactly one argument ({arguments.Count} given)",
                    span
                );
            CheckWork(span);
            return invoke(arguments[0], span);
        }
        return new PythonBuiltinFunctionValue(
            name,
            Call,
            (arguments, names, _, span) =>
                names.Count == 0
                    ? Call(arguments, span)
                    : throw Error($"{diagnosticName}() takes no keyword arguments", span)
        );
    }

    private static PythonTupleValue Handle(string name, PythonValue value, TextSpan span)
    {
        if (name == "strict")
            throw value is PythonExceptionValue exception
                ? new PythonRaisedException(exception)
                : Error("codec must pass exception instance", span);
        if (value is not PythonExceptionValue error || !PythonUnicodeErrors.IsApplicable(error))
            throw WrongType(value, span);
        var kind = PythonUnicodeErrors.GetKind(error);
        if (
            (
                name is "xmlcharrefreplace" or "namereplace"
                && kind != PythonUnicodeErrors.Kind.Encode
            )
            || (
                name is "surrogatepass" or "surrogateescape"
                && kind == PythonUnicodeErrors.Kind.Translate
            )
        )
            throw WrongType(value, span);

        var state = error.UnicodeErrorState;
        var encoding = name == "surrogatepass" ? GetEncoding(state?.Encoding, error, span) : 0;
        var asBytes = kind == PythonUnicodeErrors.Kind.Decode;
        var source = RequireMember(state?.Object, "object", asBytes, span);
        var count = source is PythonTextValue text
            ? PythonTextTraversal.Count(text.Value, span)
            : ((PythonByteSequenceValue)source).Value.Length;
        // Unicode error C getters clamp independently, even for empty objects.
        var start = (int)Math.Clamp(state?.Start ?? 0, 0, Math.Max(0, count - 1));
        var end = (int)Math.Clamp(state?.End ?? 0, Math.Min(1, count), count);
        var length = Math.Max(0, end - start);
        if (name == "ignore")
            return Result(new PythonTextValue(""), end);
        if (name == "replace")
        {
            var size = asBytes ? 1 : length;
            CheckSize(size, span);
            // Charge the materialization before allocating the replacement.
            for (var i = 0; i < size; i += 256)
                CheckWork(span);
            return Result(
                new PythonTextValue(
                    new string(kind == PythonUnicodeErrors.Kind.Encode ? '?' : '\ufffd', size)
                ),
                end
            );
        }
        if (asBytes)
            return Decode(
                name,
                ((PythonByteSequenceValue)source).Value,
                start,
                end,
                encoding,
                error,
                span
            );

        var builder = new StringBuilder();
        var bytes = name is "surrogatepass" or "surrogateescape" ? new List<byte>() : null;
        var position = 0;
        foreach (
            var character in PythonTextTraversal.Enumerate(((PythonTextValue)source).Value, span)
        )
        {
            if (position >= end)
                break;
            if (position++ < start)
                continue;
            var ch = character.Value;
            if (bytes is not null)
            {
                if (name == "surrogateescape")
                {
                    if (ch is < 0xdc80 or > 0xdcff)
                        throw new PythonRaisedException(error);
                    CheckSize(bytes.Count + 1, span);
                    bytes.Add((byte)(ch - 0xdc00));
                }
                else
                {
                    if (ch is < 0xd800 or > 0xdfff)
                        throw new PythonRaisedException(error);
                    CheckSize(bytes.Count + ByteLength(encoding), span);
                    if (encoding == 8)
                    {
                        bytes.Add((byte)(0xe0 | ch >> 12));
                        bytes.Add((byte)(0x80 | (ch >> 6 & 0x3f)));
                        bytes.Add((byte)(0x80 | (ch & 0x3f)));
                    }
                    else
                    {
                        var width = ByteLength(encoding);
                        for (var i = 0; i < width; i++)
                            bytes.Add((byte)(ch >> (8 * (encoding > 0 ? i : width - i - 1))));
                    }
                }
                continue;
            }
            var replacement = name switch
            {
                "xmlcharrefreplace" => "&#" + ch.ToString(CultureInfo.InvariantCulture) + ";",
                "namereplace"
                    when PythonUnicodeNames.GetName(ch, includeAliasesAndSequences: true)
                        is { } unicodeName => "\\N{" + unicodeName + "}",
                _ => Hex(ch),
            };
            CheckSize(builder.Length + replacement.Length, span);
            builder.Append(replacement);
        }
        CheckWork(span);
        return Result(
            bytes is null
                ? new PythonTextValue(builder.ToString())
                : new PythonByteSequenceValue(bytes.ToArray()),
            name == "namereplace" ? Math.Max(start, end) : end
        );
    }

    private static PythonTupleValue Decode(
        string name,
        byte[] source,
        int start,
        int end,
        int encoding,
        PythonExceptionValue error,
        TextSpan span
    )
    {
        if (name == "backslashreplace")
        {
            CheckSize((long)Math.Max(0, end - start) * 4, span);
            var builder = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                if (((i - start) & 255) == 0)
                    CheckWork(span);
                builder.Append(Hex(source[i]));
            }
            CheckWork(span);
            return Result(new PythonTextValue(builder.ToString()), end);
        }
        if (name == "surrogateescape")
        {
            var builder = new StringBuilder();
            var consumed = 0;
            while (consumed < 4 && consumed < end - start && source[start + consumed] >= 128)
                builder.Append((char)(0xdc00 + source[start + consumed++]));
            if (consumed == 0)
                throw new PythonRaisedException(error);
            return Result(new PythonTextValue(builder.ToString()), start + consumed);
        }
        // surrogatepass reads one complete code unit sequence, ignoring end.
        var width = ByteLength(encoding);
        uint ch = 0;
        if (source.Length - start >= width)
        {
            if (encoding == 8)
            {
                if (
                    (source[start] & 0xf0) == 0xe0
                    && (source[start + 1] & 0xc0) == 0x80
                    && (source[start + 2] & 0xc0) == 0x80
                )
                    ch = (uint)(
                        (source[start] & 0x0f) << 12
                        | (source[start + 1] & 0x3f) << 6
                        | source[start + 2] & 0x3f
                    );
            }
            else
            {
                for (var i = 0; i < width; i++)
                    ch |= (uint)source[start + i] << (8 * (encoding > 0 ? i : width - i - 1));
            }
        }
        if (ch is < 0xd800 or > 0xdfff)
            throw new PythonRaisedException(error);
        return Result(new PythonTextValue(((char)ch).ToString()), start + width);
    }

    private static int GetEncoding(PythonValue? value, PythonExceptionValue error, TextSpan span)
    {
        var text = (PythonTextValue)RequireMember(value, "encoding", false, span);
        var name = Encoding.UTF8.GetString(Utf8Name(text, span));
        var nul = name.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
            name = name[..nul];
        if (name == "cp65001")
            return 8;
        if (name.StartsWith("utf", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = name[3..];
            if (suffix.StartsWith('-') || suffix.StartsWith('_'))
                suffix = suffix[1..];
            if (suffix == "8")
                return 8;
            if (
                suffix.StartsWith("16", StringComparison.Ordinal)
                || suffix.StartsWith("32", StringComparison.Ordinal)
            )
            {
                var bits = suffix[0] == '1' ? 16 : 32;
                suffix = suffix[2..];
                if (suffix.Length == 0)
                    return BitConverter.IsLittleEndian ? bits : -bits;
                if (suffix.StartsWith('-') || suffix.StartsWith('_'))
                    suffix = suffix[1..];
                if (suffix.Equals("le", StringComparison.OrdinalIgnoreCase))
                    return bits;
                if (suffix.Equals("be", StringComparison.OrdinalIgnoreCase))
                    return -bits;
            }
        }
        throw new PythonRaisedException(error);
    }

    private static PythonValue RequireMember(
        PythonValue? value,
        string name,
        bool asBytes,
        TextSpan span
    )
    {
        if (value is null)
            throw Error($"UnicodeError '{name}' attribute is not set", span);
        if (asBytes ? value is not PythonByteSequenceValue : value is not PythonTextValue)
            throw Error(
                $"UnicodeError '{name}' attribute must be a {(asBytes ? "bytes" : "string")}",
                span
            );
        return value;
    }

    private static int ByteLength(int encoding) => encoding == 8 ? 3 : Math.Abs(encoding) / 8;

    private static string Hex(int ch) =>
        ch <= 0xff ? $"\\x{ch:x2}"
        : ch <= 0xffff ? $"\\u{ch:x4}"
        : $"\\U{ch:x8}";

    private static PythonTupleValue Result(PythonValue replacement, int end) =>
        new([replacement, PythonWholeNumberValue.Create(end)]);

    private static void CheckWork(TextSpan span) =>
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);

    private static void CheckSize(long size, TextSpan span)
    {
        if (size > MaximumReplacement)
            throw Error(
                "codec error handler replacement exceeds the managed materialization limit",
                span,
                "OverflowError"
            );
    }

    private static PythonRuntimeException WrongType(PythonValue value, TextSpan span) =>
        Error(
            $"don't know how to handle {ManagedObjectProtocols.GetTypeName(value)} in error callback",
            span
        );

    private static PythonRuntimeException Error(
        string message,
        TextSpan span,
        string type = "TypeError"
    ) => ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
