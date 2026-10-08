using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// One resolved text codec: the encoding itself, the canonical name `codecs.lookup` reports,
/// and the `encodings` module stem CPython names the codec functions after.
/// </summary>
internal readonly record struct ResolvedTextCodec(
    string CanonicalName,
    string FunctionName,
    Encoding Encoding
);

/// <summary>The codecs behind `str(bytes, encoding)`, `bytes(str, encoding)`, `str.encode`, and `bytes.decode`.</summary>
internal static class PythonTextCodecs
{
    internal static Encoding Resolve(string name, TextSpan span) =>
        ResolveCodec(name, span).Encoding;

    internal static ResolvedTextCodec ResolveCodec(string name, TextSpan span)
    {
        if (name.Contains('\0', StringComparison.Ordinal))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "embedded null character",
                span,
                "ValueError"
            );
        var normalized = NormalizeName(name);
        // The function stem is the `encodings` module CPython serves the codec from, which is
        // not always the canonical name: latin-1 canonicalizes to `iso8859-1` but its codec
        // functions are `latin_1_encode`/`latin_1_decode`.
        return normalized switch
        {
            "UTF_8" or "UTF8" or "U8" or "UTF" or "UTF8_UCS2" or "UTF8_UCS4" or "CP65001" =>
                new ResolvedTextCodec(
                    "utf-8",
                    "utf_8",
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true
                    )
                ),
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
            or "US" => new ResolvedTextCodec(
                "ascii",
                "ascii",
                Encoding.GetEncoding(
                    "us-ascii",
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback
                )
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
            or "LATIN" => new ResolvedTextCodec("iso8859-1", "latin_1", Encoding.Latin1),
            "UTF_16" or "UTF16" or "U16" => new ResolvedTextCodec(
                "utf-16",
                "utf_16",
                new UnicodeEncoding(
                    bigEndian: false,
                    byteOrderMark: true,
                    throwOnInvalidBytes: true
                )
            ),
            "UTF_16_LE" or "UTF_16LE" or "UNICODELITTLEUNMARKED" => new ResolvedTextCodec(
                "utf-16-le",
                "utf_16_le",
                new UnicodeEncoding(
                    bigEndian: false,
                    byteOrderMark: false,
                    throwOnInvalidBytes: true
                )
            ),
            "UTF_16_BE" or "UTF_16BE" or "UNICODEBIGUNMARKED" => new ResolvedTextCodec(
                "utf-16-be",
                "utf_16_be",
                new UnicodeEncoding(
                    bigEndian: true,
                    byteOrderMark: false,
                    throwOnInvalidBytes: true
                )
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

    internal static byte[] Encode(
        PythonTextValue source,
        string encoding,
        string errors,
        TextSpan span
    )
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
        var codec = Resolve(encoding, span);
        return PythonTextEncoding.Encode(source, codec, errors, span);
    }
}
