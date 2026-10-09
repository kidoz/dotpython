// Bytes `%` formatting follows CPython 3.14.7 Objects/bytesobject.c (PyBytes_FormatEx) and
// the conversions it shares with the text formatter:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The classic <c>%</c> operator over bytes: the same template language as the text
/// formatter, with the conversions a byte string accepts.
/// </summary>
/// <remarks>
/// A byte string formats values that are byte-like — `%s` and `%b` take any bytes-like
/// object, `%c` one byte or an integer below 256, and the numeric conversions an integer.
/// A text operand is refused rather than encoded, which is where this differs from `str`.
/// </remarks>
internal static class PythonBytesFormatting
{
    internal static PythonByteSequenceValue FormatPercent(
        byte[] template,
        PythonValue operand,
        TextSpan span
    )
    {
        var usesMapping = UsesMappingKeys(template);
        if (usesMapping && operand is not PythonDictionaryValue)
            throw Fault("format requires a mapping", span, "TypeError");
        PythonValue[] arguments = operand switch
        {
            _ when usesMapping => [],
            PythonTupleValue tuple => tuple.Elements,
            _ => [operand],
        };
        var mapping = usesMapping ? (PythonDictionaryValue)operand : null;
        var argumentIndex = 0;
        var result = new List<byte>(template.Length);
        var position = 0;
        while (position < template.Length)
        {
            var current = template[position++];
            if (current != (byte)'%')
            {
                result.Add(current);
                continue;
            }

            if (position >= template.Length)
                throw Fault("incomplete format", span, "ValueError");

            if (template[position] == (byte)'%')
            {
                result.Add((byte)'%');
                position++;
                continue;
            }

            string? mappingKey = null;
            if (template[position] == (byte)'(')
            {
                var close = Array.IndexOf(template, (byte)')', position + 1);
                if (close < 0)
                    throw Fault("incomplete format key", span, "ValueError");
                mappingKey = System.Text.Encoding.Latin1.GetString(
                    template,
                    position + 1,
                    close - position - 1
                );
                position = close + 1;
            }

            var leftAlign = false;
            var showSign = false;
            var spaceSign = false;
            var zeroPad = false;
            var alternate = false;
            while (position < template.Length)
            {
                switch ((char)template[position])
                {
                    case '-':
                        leftAlign = true;
                        break;
                    case '+':
                        showSign = true;
                        break;
                    case ' ':
                        spaceSign = true;
                        break;
                    case '0':
                        zeroPad = true;
                        break;
                    case '#':
                        alternate = true;
                        break;
                    default:
                        goto flagsDone;
                }
                position++;
            }
            flagsDone:

            var width = 0;
            if (position < template.Length && template[position] == (byte)'*')
            {
                position++;
                width = PythonTextFormatting.RequireFormatInteger(
                    NextArgument(arguments, ref argumentIndex, mapping, null, span),
                    span
                );
                if (width < 0)
                {
                    leftAlign = true;
                    width = -width;
                }
            }
            else
            {
                while (position < template.Length && char.IsAsciiDigit((char)template[position]))
                    width = width * 10 + (template[position++] - '0');
            }

            var precision = -1;
            if (position < template.Length && template[position] == (byte)'.')
            {
                position++;
                precision = 0;
                if (position < template.Length && template[position] == (byte)'*')
                {
                    position++;
                    precision = Math.Max(
                        0,
                        PythonTextFormatting.RequireFormatInteger(
                            NextArgument(arguments, ref argumentIndex, mapping, null, span),
                            span
                        )
                    );
                }
                else
                {
                    while (
                        position < template.Length && char.IsAsciiDigit((char)template[position])
                    )
                        precision = precision * 10 + (template[position++] - '0');
                }
            }

            while (position < template.Length && (char)template[position] is 'h' or 'l' or 'L')
                position++;

            if (position >= template.Length)
                throw Fault("incomplete format", span, "ValueError");

            var conversion = (char)template[position++];
            var value = NextArgument(arguments, ref argumentIndex, mapping, mappingKey, span);
            var text = conversion switch
            {
                's' or 'b' => RequireBytesLike(value, span),
                'd' or 'i' or 'u' => PythonTextFormatting.FormatPercentInteger(
                    value,
                    conversion,
                    alternate: false,
                    showSign,
                    spaceSign,
                    span
                ),
                'o' or 'x' or 'X' => PythonTextFormatting.FormatPercentInteger(
                    value,
                    conversion,
                    alternate,
                    showSign,
                    spaceSign,
                    span
                ),
                'e' or 'E' or 'f' or 'F' or 'g' or 'G' =>
                    PythonTextFormatting.FormatPercentFloating(
                        value,
                        conversion,
                        precision,
                        alternate,
                        showSign,
                        spaceSign,
                        span
                    ),
                'c' => FormatCharacter(value, span),
                'r' or 'a' => value.ToRepresentationString(),
                _ => throw Fault(
                    $"unsupported format character '{conversion}' "
                        + $"(0x{(int)conversion:x2}) at index {position - 1}",
                    span,
                    "ValueError"
                ),
            };

            if (conversion is 's' or 'b' or 'r' or 'a' && precision >= 0 && precision < text.Length)
                text = text[..precision];

            if (text.Length < width)
            {
                if (leftAlign)
                {
                    text += new string(' ', width - text.Length);
                }
                else if (zeroPad && conversion is not ('s' or 'b' or 'r' or 'a' or 'c'))
                {
                    var signLength = text.Length != 0 && text[0] is '+' or '-' or ' ' ? 1 : 0;
                    text =
                        text[..signLength]
                        + new string('0', width - text.Length)
                        + text[signLength..];
                }
                else
                {
                    text = new string(' ', width - text.Length) + text;
                }
            }

            result.AddRange(System.Text.Encoding.Latin1.GetBytes(text));
        }

        // A mapping is an operand in its own right, and one the template did not use is
        // not a leftover; only positional arguments are counted.
        if (
            mapping is null
            && operand is not PythonDictionaryValue
            && argumentIndex < arguments.Length
        )
            throw Fault("not all arguments converted during bytes formatting", span, "TypeError");
        return PythonByteSequenceValue.Create([.. result]);
    }

    /// <summary>`%s` and `%b` take a bytes-like value and nothing else.</summary>
    private static string RequireBytesLike(PythonValue value, TextSpan span)
    {
        if (!ManagedObjectProtocols.TryGetByteContent(value, out var contents))
            throw Fault(
                "%b requires a bytes-like object, or an object that implements "
                    + $"__bytes__, not '{ManagedObjectProtocols.GetTypeName(value)}'",
                span,
                "TypeError"
            );
        return System.Text.Encoding.Latin1.GetString(contents);
    }

    /// <summary>`%c` takes one byte, given as an integer below 256 or as a lone byte.</summary>
    private static string FormatCharacter(PythonValue value, TextSpan span)
    {
        if (ManagedObjectProtocols.TryGetByteContent(value, out var contents))
        {
            if (contents.Length != 1)
                throw Fault(
                    "%c requires an integer in range(256) or a single byte, not "
                        + ManagedObjectProtocols.GetTypeName(value),
                    span,
                    "TypeError"
                );
            return ((char)contents[0]).ToString();
        }
        var number = value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? System.Numerics.BigInteger.One : 0,
            _ => throw Fault(
                "%c requires an integer in range(256) or a single byte, not "
                    + ManagedObjectProtocols.GetTypeName(value),
                span,
                "TypeError"
            ),
        };
        if (number < 0 || number > 255)
            throw Fault("%c arg not in range(256)", span, "OverflowError");
        return ((char)(int)number).ToString();
    }

    /// <summary>The next positional argument, or the one a mapping key names.</summary>
    private static PythonValue NextArgument(
        PythonValue[] arguments,
        ref int index,
        PythonDictionaryValue? mapping,
        string? mappingKey,
        TextSpan span
    )
    {
        if (mappingKey is not null && mapping is not null)
        {
            var key = PythonByteSequenceValue.Create(
                System.Text.Encoding.Latin1.GetBytes(mappingKey)
            );
            if (ManagedObjectProtocols.TryFindDictionaryItem(mapping, key, out var item))
                return item.Value;
            throw ManagedObjectProtocols.MissingKey(key);
        }
        if (index >= arguments.Length)
            throw Fault("not enough arguments for format string", span, "TypeError");
        return arguments[index++];
    }

    private static bool UsesMappingKeys(byte[] template)
    {
        for (var index = 0; index + 1 < template.Length; index++)
        {
            if (template[index] == (byte)'%' && template[index + 1] == (byte)'(')
                return true;
        }
        return false;
    }

    private static PythonRuntimeException Fault(string message, TextSpan span, string type) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
