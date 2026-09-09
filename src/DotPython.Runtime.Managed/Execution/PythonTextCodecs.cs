using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>The codecs behind `str(bytes, encoding)`, `bytes(str, encoding)`, `str.encode`, and `bytes.decode`.</summary>
internal static class PythonTextCodecs
{
    internal static Encoding Resolve(string name, TextSpan span)
    {
        var normalized = name.Trim().ToUpperInvariant().Replace('_', '-');
        return normalized switch
        {
            "UTF-8" or "UTF8" or "U8" => new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ),
            "ASCII" or "US-ASCII" or "646" => Encoding.GetEncoding(
                "us-ascii",
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback
            ),
            "LATIN-1" or "LATIN1" or "ISO-8859-1" or "ISO8859-1" or "L1" => Encoding.Latin1,
            "UTF-16" or "UTF16" => new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: true,
                throwOnInvalidBytes: true
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"unknown encoding: {name}",
                span,
                "LookupError"
            ),
        };
    }

    internal static string Decode(byte[] bytes, string encoding, string errors, TextSpan span)
    {
        var codec = Resolve(encoding, span);
        try
        {
            return errors switch
            {
                "strict" => codec.GetString(bytes),
                "ignore" => ReplacementDecoder(codec, string.Empty).GetString(bytes),
                "replace" => ReplacementDecoder(codec, "\ufffd").GetString(bytes),
                _ => throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"unknown error handler name '{errors}'",
                    span,
                    "LookupError"
                ),
            };
        }
        catch (DecoderFallbackException exception)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{encoding}' codec can't decode byte 0x{(exception.BytesUnknown is { Length: > 0 } unknown ? unknown[0] : 0):x2} in position {exception.Index}: invalid start byte",
                span,
                "UnicodeDecodeError"
            );
        }
    }

    internal static byte[] Encode(string text, string encoding, string errors, TextSpan span)
    {
        var codec = Resolve(encoding, span);
        try
        {
            return errors switch
            {
                "strict" => codec.GetBytes(text),
                "ignore" => ReplacementEncoder(codec, string.Empty).GetBytes(text),
                "replace" => ReplacementEncoder(codec, "?").GetBytes(text),
                _ => throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"unknown error handler name '{errors}'",
                    span,
                    "LookupError"
                ),
            };
        }
        catch (EncoderFallbackException exception)
        {
            var character = (int)exception.CharUnknown;
            var escaped =
                character <= byte.MaxValue ? $"\\x{character:x2}"
                : character <= char.MaxValue ? $"\\u{character:x4}"
                : $"\\U{character:x8}";
            var range = codec.CodePage == Encoding.ASCII.CodePage ? 128 : 256;
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{encoding}' codec can't encode character '{escaped}' in position {exception.Index}: ordinal not in range({range})",
                span,
                "UnicodeEncodeError"
            );
        }
    }

    private static Encoding ReplacementDecoder(Encoding codec, string replacement) =>
        Encoding.GetEncoding(
            codec.CodePage,
            codec.EncoderFallback,
            new DecoderReplacementFallback(replacement)
        );

    private static Encoding ReplacementEncoder(Encoding codec, string replacement) =>
        Encoding.GetEncoding(
            codec.CodePage,
            new EncoderReplacementFallback(replacement),
            codec.DecoderFallback
        );
}
