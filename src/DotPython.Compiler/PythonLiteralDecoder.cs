using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;

namespace DotPython.Compiler;

internal static class PythonLiteralDecoder
{
    internal static PythonConstant Decode(
        PythonConstantExpression expression,
        ICollection<Diagnostic> diagnostics
    )
    {
        try
        {
            return expression.ConstantKind switch
            {
                PythonConstantKind.NoneLiteral => new PythonConstant(
                    PythonConstantType.NoneValue,
                    null
                ),
                PythonConstantKind.BooleanLiteral => new PythonConstant(
                    PythonConstantType.TruthValue,
                    expression.TokenText == "True"
                ),
                PythonConstantKind.IntegerLiteral => new PythonConstant(
                    PythonConstantType.WholeNumber,
                    DecodeInteger(expression.TokenText)
                ),
                PythonConstantKind.FloatLiteral => new PythonConstant(
                    PythonConstantType.FloatingPoint,
                    double.Parse(
                        expression.TokenText.Replace("_", string.Empty, StringComparison.Ordinal),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture
                    )
                ),
                PythonConstantKind.ImaginaryLiteral => new PythonConstant(
                    PythonConstantType.ComplexNumber,
                    DecodeImaginary(expression.TokenText)
                ),
                PythonConstantKind.StringLiteral => new PythonConstant(
                    PythonConstantType.TextValue,
                    DecodeString(expression.TokenText)
                ),
                PythonConstantKind.BytesLiteral => new PythonConstant(
                    PythonConstantType.ByteSequence,
                    DecodeBytes(expression.TokenText)
                ),
                PythonConstantKind.FormattedStringLiteral
                or PythonConstantKind.TemplateStringLiteral => UnsupportedInterpolatedString(
                    expression,
                    diagnostics
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(expression)),
            };
        }
        catch (FormatException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "DPY3003",
                    "The literal value could not be decoded.",
                    DiagnosticSeverity.Error,
                    expression.Span
                )
            );
            return new PythonConstant(PythonConstantType.NoneValue, null);
        }
        catch (OverflowException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "DPY3003",
                    "The literal value is outside the supported range.",
                    DiagnosticSeverity.Error,
                    expression.Span
                )
            );
            return new PythonConstant(PythonConstantType.NoneValue, null);
        }
    }

    private static BigInteger DecodeInteger(string text)
    {
        var normalized = text.Replace("_", string.Empty, StringComparison.Ordinal);
        var numberBase = 10;
        var start = 0;

        if (
            normalized.Length > 1
            && normalized[0] == '0'
            && normalized[1] is not ('b' or 'B' or 'o' or 'O' or 'x' or 'X')
            && normalized.Any(character => character != '0')
        )
        {
            throw new FormatException();
        }

        if (normalized.Length > 2 && normalized[0] == '0')
        {
            numberBase = char.ToLowerInvariant(normalized[1]) switch
            {
                'b' => 2,
                'o' => 8,
                'x' => 16,
                _ => 10,
            };
            start = numberBase == 10 ? 0 : 2;
        }

        var result = BigInteger.Zero;
        for (var index = start; index < normalized.Length; index++)
        {
            var digit = DecodeDigit(normalized[index]);
            if (digit >= numberBase)
            {
                throw new FormatException();
            }

            result = result * numberBase + digit;
        }

        return result;
    }

    private static Complex DecodeImaginary(string text)
    {
        var number = text[..^1].Replace("_", string.Empty, StringComparison.Ordinal);
        return new Complex(
            0,
            double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture)
        );
    }

    private static string DecodeString(string tokenText)
    {
        var (prefix, content) = SplitStringToken(tokenText);
        return prefix.Contains('r', StringComparison.OrdinalIgnoreCase)
            ? content
            : DecodeEscapes(content);
    }

    private static byte[] DecodeBytes(string tokenText)
    {
        var value = DecodeString(tokenText);
        var bytes = new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] > byte.MaxValue)
            {
                throw new FormatException();
            }

            bytes[index] = (byte)value[index];
        }

        return bytes;
    }

    private static (string Prefix, string Content) SplitStringToken(string tokenText)
    {
        var quoteIndex = tokenText.IndexOfAny(['\'', '"']);
        if (quoteIndex < 0)
        {
            throw new FormatException();
        }

        var quote = tokenText[quoteIndex];
        var quoteLength =
            quoteIndex + 2 < tokenText.Length
            && tokenText[quoteIndex + 1] == quote
            && tokenText[quoteIndex + 2] == quote
                ? 3
                : 1;
        var contentStart = quoteIndex + quoteLength;
        var contentLength = tokenText.Length - contentStart - quoteLength;
        if (contentLength < 0)
        {
            throw new FormatException();
        }

        return (tokenText[..quoteIndex], tokenText.Substring(contentStart, contentLength));
    }

    internal static string DecodeEscapes(string content)
    {
        var builder = new StringBuilder(content.Length);
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != '\\' || index + 1 >= content.Length)
            {
                builder.Append(content[index]);
                continue;
            }

            var escaped = content[++index];
            switch (escaped)
            {
                case '\n':
                    break;
                case '\r' when index + 1 < content.Length && content[index + 1] == '\n':
                    index++;
                    break;
                case '\\':
                case '\'':
                case '"':
                    builder.Append(escaped);
                    break;
                case 'a':
                    builder.Append('\a');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'v':
                    builder.Append('\v');
                    break;
                case 'x':
                    builder.Append((char)DecodeHex(content, ref index, 2));
                    break;
                case 'u':
                    builder.Append((char)DecodeHex(content, ref index, 4));
                    break;
                case 'U':
                    builder.Append(char.ConvertFromUtf32(DecodeHex(content, ref index, 8)));
                    break;
                case >= '0' and <= '7':
                    builder.Append((char)DecodeOctal(content, ref index, escaped));
                    break;
                default:
                    builder.Append('\\').Append(escaped);
                    break;
            }
        }

        return builder.ToString();
    }

    private static int DecodeHex(string content, ref int index, int length)
    {
        if (index + length >= content.Length)
        {
            throw new FormatException();
        }

        var value = 0;
        for (var count = 0; count < length; count++)
        {
            value = value * 16 + DecodeDigit(content[++index]);
        }

        return value;
    }

    private static int DecodeOctal(string content, ref int index, char first)
    {
        var value = first - '0';
        for (var count = 1; count < 3 && index + 1 < content.Length; count++)
        {
            var next = content[index + 1];
            if (next is < '0' or > '7')
            {
                break;
            }

            index++;
            value = value * 8 + next - '0';
        }

        return value;
    }

    private static int DecodeDigit(char value) =>
        value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => throw new FormatException(),
        };

    private static PythonConstant UnsupportedInterpolatedString(
        PythonConstantExpression expression,
        ICollection<Diagnostic> diagnostics
    )
    {
        diagnostics.Add(
            new Diagnostic(
                "DPY3004",
                "Formatted and template strings are not executable in this runtime slice.",
                DiagnosticSeverity.Error,
                expression.Span
            )
        );
        return new PythonConstant(PythonConstantType.NoneValue, null);
    }
}
