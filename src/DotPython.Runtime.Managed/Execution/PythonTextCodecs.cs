using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>The codecs behind `str(bytes, encoding)`, `bytes(str, encoding)`, `str.encode`, and `bytes.decode`.</summary>
internal static class PythonTextCodecs
{
    internal static Encoding Resolve(string name, TextSpan span)
    {
        if (name.Contains('\0', StringComparison.Ordinal))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "embedded null character",
                span,
                "ValueError"
            );
        var normalized = NormalizeName(name);
        return normalized switch
        {
            "UTF_8" or "UTF8" or "U8" or "UTF" or "UTF8_UCS2" or "UTF8_UCS4" or "CP65001" =>
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            "ASCII"
            or "US_ASCII"
            or "646"
            or "ANSI_X3.4_1968"
            or "ANSI_X3_4_1968"
            or "ANSI_X3.4_1986"
            or "CP367"
            or "CSASCII"
            or "IBM367"
            or "ISO646_US"
            or "ISO_646.IRV_1991"
            or "ISO_IR_6"
            or "US" => Encoding.GetEncoding(
                "us-ascii",
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback
            ),
            "LATIN_1"
            or "LATIN1"
            or "ISO_8859_1"
            or "ISO8859_1"
            or "L1"
            or "8859"
            or "CP819"
            or "CSISOLATIN1"
            or "IBM819"
            or "ISO8859"
            or "ISO_8859_1_1987"
            or "ISO_IR_100"
            or "LATIN" => Encoding.Latin1,
            "UTF_16" or "UTF16" or "U16" => new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: true,
                throwOnInvalidBytes: true
            ),
            "UTF_16_LE" or "UTF_16LE" or "UNICODELITTLEUNMARKED" => new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: false,
                throwOnInvalidBytes: true
            ),
            "UTF_16_BE" or "UTF_16BE" or "UNICODEBIGUNMARKED" => new UnicodeEncoding(
                bigEndian: true,
                byteOrderMark: false,
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

    private static string NormalizeName(string name)
    {
        // CPython normalizes runs of punctuation/space to one separator and
        // keeps dots for aliases such as ANSI_X3.4_1968.
        var normalized = new StringBuilder(name.Length);
        var separator = false;
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '.')
            {
                if (separator && normalized.Length > 0)
                    normalized.Append('_');
                normalized.Append(char.ToUpperInvariant(character));
                separator = false;
            }
            else
            {
                separator = true;
            }
        }
        return normalized.ToString();
    }

    internal static string Decode(byte[] bytes, string encoding, string errors, TextSpan span)
    {
        if (
            encoding.Contains('\0', StringComparison.Ordinal)
            || errors.Contains('\0', StringComparison.Ordinal)
        )
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "embedded null character",
                span,
                "ValueError"
            );
        // CPython returns the shared empty string before codec/handler lookup.
        // Public argument binding has already validated the names themselves.
        if (bytes.Length == 0)
            return string.Empty;
        var codec = Resolve(encoding, span);
        return PythonBytesDecoding.Decode(
            bytes,
            codec.CodePage,
            codec.GetPreamble().Length != 0,
            errors,
            span
        );
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

    private static Encoding ReplacementEncoder(Encoding codec, string replacement) =>
        Encoding.GetEncoding(
            codec.CodePage,
            new EncoderReplacementFallback(replacement),
            codec.DecoderFallback
        );
}
