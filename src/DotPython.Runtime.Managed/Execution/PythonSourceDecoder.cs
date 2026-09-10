using System.Diagnostics.CodeAnalysis;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Decodes the supported byte-source encodings before Python tokenization.</summary>
internal static class PythonSourceDecoder
{
    internal const int MaximumByteLength = 8 * 1024 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding StrictAscii = Encoding.GetEncoding(
        "us-ascii",
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback
    );

    internal static SourceText ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumByteLength)
        {
            throw new InvalidDataException(
                $"The Python source file '{path}' exceeds the {MaximumByteLength} byte limit."
            );
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"The Python source file '{path}' changed length while being read."
            );
        }

        return Decode(bytes, path);
    }

    internal static SourceText Decode(
        ReadOnlySpan<byte> bytes,
        string origin,
        TextSpan failureSpan = default
    )
    {
        var hasBom = bytes.StartsWith(Encoding.UTF8.Preamble);
        if (hasBom)
        {
            bytes = bytes[3..];
        }

        var remaining = bytes;
        string? cookie = null;
        for (var lineNumber = 0; lineNumber < 2 && !remaining.IsEmpty; lineNumber++)
        {
            var end = remaining.IndexOfAny((byte)'\r', (byte)'\n');
            var line = end < 0 ? remaining : remaining[..end];
            cookie = FindCookie(line, out var commentOnly);
            if (cookie is not null || !commentOnly || end < 0)
            {
                break;
            }

            var newlineLength =
                remaining[end] == '\r' && end + 1 < remaining.Length && remaining[end + 1] == '\n'
                    ? 2
                    : 1;
            remaining = remaining[(end + newlineLength)..];
        }

        var name = NormalizeEncoding(cookie ?? "utf-8");
        if (hasBom && name != "utf-8")
        {
            throw DecodeFault("encoding problem: utf-8", failureSpan);
        }

        var encoding = name switch
        {
            "utf-8" or "utf8" or "u8" or "utf" or "utf8-ucs2" or "utf8-ucs4" or "cp65001" =>
                StrictUtf8,
            "ascii"
            or "us-ascii"
            or "646"
            or "ansi-x3.4-1968"
            or "ansi-x3-4-1968"
            or "ansi-x3.4-1986"
            or "cp367"
            or "csascii"
            or "ibm367"
            or "iso646-us"
            or "iso-646.irv-1991"
            or "iso-ir-6"
            or "us" => StrictAscii,
            "latin-1"
            or "latin1"
            or "iso-8859-1"
            or "iso8859-1"
            or "iso-latin-1"
            or "l1"
            or "cp819"
            or "8859"
            or "csisolatin1"
            or "ibm819"
            or "iso8859"
            or "iso-ir-100"
            or "latin" => Encoding.Latin1,
            _ => throw DecodeFault(
                $"source encoding '{cookie}' is not supported by DotPython",
                failureSpan
            ),
        };

        string text;
        try
        {
            text = encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw DecodeFault($"source cannot be decoded as {name}", failureSpan);
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw DecodeFault("source code cannot contain null bytes", failureSpan);
        }

        // Python normalizes physical line endings even inside multiline strings.
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return new SourceText(text, origin);
    }

    private static string? FindCookie(ReadOnlySpan<byte> bytes, out bool commentOnly)
    {
        var line = Encoding.Latin1.GetString(bytes).AsSpan().TrimStart(" \t\f");
        commentOnly = line.IsEmpty || line[0] == '#';
        if (line.IsEmpty || !commentOnly)
        {
            return null;
        }

        while (true)
        {
            var start = line.IndexOf("coding", StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            line = line[(start + 6)..];
            if (line.IsEmpty || line[0] is not (':' or '='))
            {
                continue;
            }

            var value = line[1..].TrimStart(" \t");
            var length = 0;
            while (
                length < value.Length
                && (char.IsAsciiLetterOrDigit(value[length]) || value[length] is '-' or '_' or '.')
            )
            {
                length++;
            }

            if (length != 0)
            {
                return value[..length].ToString();
            }
        }
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Python source encoding aliases use lowercase normalization before codec lookup."
    )]
    private static string NormalizeEncoding(string name)
    {
        var normalized = name.ToLowerInvariant().Replace('_', '-');
        // CPython recognizes these common cookie spellings before codec lookup.
        if (normalized.StartsWith("utf-8-", StringComparison.Ordinal))
        {
            return "utf-8";
        }

        if (
            normalized.StartsWith("latin-1-", StringComparison.Ordinal)
            || normalized.StartsWith("iso-8859-1-", StringComparison.Ordinal)
            || normalized.StartsWith("iso-latin-1-", StringComparison.Ordinal)
        )
        {
            return "iso-8859-1";
        }

        return normalized;
    }

    private static PythonRuntimeException DecodeFault(string message, TextSpan span) =>
        new("DPY4021", message, span, "SyntaxError");
}
