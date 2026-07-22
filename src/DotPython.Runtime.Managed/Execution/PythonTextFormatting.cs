using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Implements the classic `%` string-formatting operator and the `str.format` template
/// language over the shared format-spec engine in <see cref="PythonValueFormatter"/>.
/// </summary>
internal static class PythonTextFormatting
{
    internal static string FormatPercent(string template, PythonValue operand, TextSpan span)
    {
        var usesMapping = operand is PythonDictionaryValue && TemplateUsesMappingKeys(template);
        PythonValue[] arguments = operand switch
        {
            _ when usesMapping => [],
            PythonTupleValue tuple => tuple.Elements,
            _ => [operand],
        };
        var mapping = usesMapping ? (PythonDictionaryValue)operand : null;
        var argumentIndex = 0;
        var builder = new StringBuilder();
        var position = 0;
        while (position < template.Length)
        {
            var character = template[position++];
            if (character != '%')
            {
                builder.Append(character);
                continue;
            }

            if (position >= template.Length)
            {
                throw Fault("Incomplete format.", span);
            }

            if (template[position] == '%')
            {
                builder.Append('%');
                position++;
                continue;
            }

            string? mappingKey = null;
            if (template[position] == '(')
            {
                var close = template.IndexOf(')', position + 1);
                if (close < 0)
                {
                    throw Fault("Incomplete format key.", span);
                }

                mappingKey = template[(position + 1)..close];
                position = close + 1;
            }

            var leftAlign = false;
            var showSign = false;
            var spaceSign = false;
            var zeroPad = false;
            var alternate = false;
            while (position < template.Length)
            {
                var flag = template[position];
                if (flag == '-')
                {
                    leftAlign = true;
                }
                else if (flag == '+')
                {
                    showSign = true;
                }
                else if (flag == ' ')
                {
                    spaceSign = true;
                }
                else if (flag == '0')
                {
                    zeroPad = true;
                }
                else if (flag == '#')
                {
                    alternate = true;
                }
                else
                {
                    break;
                }

                position++;
            }

            var width = 0;
            if (position < template.Length && template[position] == '*')
            {
                position++;
                width = RequireFormatInteger(
                    NextArgument(arguments, ref argumentIndex, mapping, mappingKey: null, span),
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
                while (position < template.Length && char.IsAsciiDigit(template[position]))
                {
                    width = width * 10 + (template[position++] - '0');
                }
            }

            var precision = -1;
            if (position < template.Length && template[position] == '.')
            {
                position++;
                precision = 0;
                if (position < template.Length && template[position] == '*')
                {
                    position++;
                    precision = Math.Max(
                        0,
                        RequireFormatInteger(
                            NextArgument(
                                arguments,
                                ref argumentIndex,
                                mapping,
                                mappingKey: null,
                                span
                            ),
                            span
                        )
                    );
                }
                else
                {
                    while (position < template.Length && char.IsAsciiDigit(template[position]))
                    {
                        precision = precision * 10 + (template[position++] - '0');
                    }
                }
            }

            while (position < template.Length && template[position] is 'h' or 'l' or 'L')
            {
                position++;
            }

            if (position >= template.Length)
            {
                throw Fault("Incomplete format.", span);
            }

            var conversion = template[position++];
            var value = NextArgument(arguments, ref argumentIndex, mapping, mappingKey, span);
            var text = conversion switch
            {
                's' => value.ToDisplayString(),
                'r' or 'a' => value.ToRepresentationString(),
                'c' => FormatCharacter(value, span),
                'd' or 'i' or 'u' => FormatPercentInteger(
                    value,
                    10,
                    upper: false,
                    alternate: false,
                    showSign,
                    spaceSign,
                    span
                ),
                'o' => FormatPercentInteger(value, 8, false, alternate, showSign, spaceSign, span),
                'x' => FormatPercentInteger(value, 16, false, alternate, showSign, spaceSign, span),
                'X' => FormatPercentInteger(value, 16, true, alternate, showSign, spaceSign, span),
                'e' or 'E' or 'f' or 'F' or 'g' or 'G' => FormatPercentFloating(
                    value,
                    conversion,
                    precision,
                    alternate,
                    showSign,
                    spaceSign,
                    span
                ),
                _ => throw Fault(
                    $"Unsupported format character '{conversion}' "
                        + $"(0x{(int)conversion:x2}) at index {position - 1}.",
                    span
                ),
            };

            if (conversion is 's' or 'r' or 'a' && precision >= 0 && precision < text.Length)
            {
                text = text[..precision];
            }

            if (text.Length < width)
            {
                if (leftAlign)
                {
                    text += new string(' ', width - text.Length);
                }
                else if (zeroPad && conversion is not ('s' or 'r' or 'a' or 'c'))
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

            builder.Append(text);
        }

        if (mapping is null && argumentIndex < arguments.Length)
        {
            throw Fault("Not all arguments converted during string formatting.", span, "TypeError");
        }

        return builder.ToString();
    }

    private static bool TemplateUsesMappingKeys(string template)
    {
        var position = 0;
        while (position < template.Length)
        {
            if (template[position++] != '%' || position >= template.Length)
            {
                continue;
            }

            if (template[position] == '%')
            {
                position++;
                continue;
            }

            return template[position] == '(';
        }

        return false;
    }

    private static PythonValue NextArgument(
        PythonValue[] arguments,
        ref int argumentIndex,
        PythonDictionaryValue? mapping,
        string? mappingKey,
        TextSpan span
    )
    {
        if (mapping is not null)
        {
            if (mappingKey is null)
            {
                throw Fault("Not enough arguments for format string.", span, "TypeError");
            }

            return ManagedObjectProtocols.GetItem(mapping, new PythonTextValue(mappingKey), span);
        }

        if (argumentIndex >= arguments.Length)
        {
            throw Fault("Not enough arguments for format string.", span, "TypeError");
        }

        return arguments[argumentIndex++];
    }

    private static int RequireFormatInteger(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonWholeNumberValue whole
                when whole.Value >= int.MinValue && whole.Value <= int.MaxValue => (int)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault("* wants int.", span, "TypeError"),
        };

    private static string FormatCharacter(PythonValue value, TextSpan span)
    {
        switch (value)
        {
            case PythonTextValue text when text.Value.Length == 1:
            case PythonTextValue astral
                when astral.Value.Length == 2
                    && char.IsSurrogatePair(astral.Value[0], astral.Value[1]):
                return ((PythonTextValue)value).Value;
            case PythonWholeNumberValue whole when whole.Value >= 0 && whole.Value <= 0x10FFFF:
            {
                var codePoint = (int)whole.Value;
                return codePoint is >= 0xD800 and <= 0xDFFF or < 0x10000
                    ? ((char)codePoint).ToString()
                    : char.ConvertFromUtf32(codePoint);
            }
            case PythonWholeNumberValue:
                throw Fault("%c arg not in range(0x110000).", span, "OverflowError");
            default:
                throw Fault("%c requires an int or a unicode character.", span, "TypeError");
        }
    }

    private static string FormatPercentInteger(
        PythonValue value,
        int radix,
        bool upper,
        bool alternate,
        bool showSign,
        bool spaceSign,
        TextSpan span
    )
    {
        var number = value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            PythonFloatingPointValue floating when radix == 10 => new BigInteger(
                Math.Truncate(floating.Value)
            ),
            _ => throw Fault(
                $"{(radix == 10 ? "%d" : "%x")} format: a real number is required, not "
                    + $"{ManagedObjectProtocols.GetTypeName(value)}.",
                span,
                "TypeError"
            ),
        };
        var digits = ToRadix(BigInteger.Abs(number), radix, upper);
        if (alternate && radix == 8)
        {
            digits = "0o" + digits;
        }
        else if (alternate && radix == 16)
        {
            digits = (upper ? "0X" : "0x") + digits;
        }

        return ApplySign(digits, number.Sign < 0, showSign, spaceSign);
    }

    private static string FormatPercentFloating(
        PythonValue value,
        char conversion,
        int precision,
        bool alternate,
        bool showSign,
        bool spaceSign,
        TextSpan span
    )
    {
        var number = value switch
        {
            PythonFloatingPointValue floating => floating.Value,
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonTruthValue truth => truth.Value ? 1d : 0d,
            _ => throw Fault(
                "A float is required for floating-point formatting, not "
                    + $"{ManagedObjectProtocols.GetTypeName(value)}.",
                span,
                "TypeError"
            ),
        };
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            var special = double.IsNaN(number) ? "nan" : "inf";
            if (char.IsUpper(conversion))
            {
                special = special.ToUpperInvariant();
            }

            return ApplySign(special, number < 0, showSign, spaceSign);
        }

        if (precision < 0)
        {
            precision = 6;
        }

        var magnitude = Math.Abs(number);
        string digits;
        switch (conversion)
        {
            case 'f' or 'F':
                digits = magnitude.ToString("F" + precision, CultureInfo.InvariantCulture);
                break;
            case 'e' or 'E':
                digits = FormatExponent(magnitude, precision, char.IsUpper(conversion));
                break;
            default:
            {
                var significant = Math.Max(precision, 1);
                var scientific = FormatExponent(magnitude, significant - 1, upper: false);
                var exponent = int.Parse(
                    scientific[(scientific.IndexOf('e', StringComparison.Ordinal) + 1)..],
                    CultureInfo.InvariantCulture
                );
                if (exponent < -4 || exponent >= significant)
                {
                    var mantissa = scientific[..scientific.IndexOf('e', StringComparison.Ordinal)];
                    if (!alternate)
                    {
                        mantissa = TrimGeneralZeros(mantissa);
                    }

                    digits =
                        mantissa
                        + (char.IsUpper(conversion) ? "E" : "e")
                        + scientific[scientific.IndexOf('e', StringComparison.Ordinal) + 1] // sign
                        + scientific[(scientific.IndexOf('e', StringComparison.Ordinal) + 2)..];
                }
                else
                {
                    digits = magnitude.ToString(
                        "F" + Math.Max(significant - 1 - exponent, 0),
                        CultureInfo.InvariantCulture
                    );
                    if (!alternate)
                    {
                        digits = TrimGeneralZeros(digits);
                    }
                }

                break;
            }
        }

        return ApplySign(
            digits,
            number < 0 || (number == 0 && double.IsNegative(number)),
            showSign,
            spaceSign
        );
    }

    private static string FormatExponent(double magnitude, int precision, bool upper)
    {
        var pattern = "0." + new string('0', precision) + "e+00";
        var text = magnitude.ToString(pattern, CultureInfo.InvariantCulture);
        if (precision == 0)
        {
            text = text.Replace("0.e", "0e", StringComparison.Ordinal);
        }

        return upper ? text.ToUpperInvariant() : text;
    }

    private static string TrimGeneralZeros(string digits)
    {
        if (!digits.Contains('.', StringComparison.Ordinal))
        {
            return digits;
        }

        digits = digits.TrimEnd('0');
        return digits.EndsWith('.') ? digits[..^1] : digits;
    }

    private static string ApplySign(string digits, bool negative, bool showSign, bool spaceSign)
    {
        if (negative)
        {
            return "-" + digits;
        }

        if (showSign)
        {
            return "+" + digits;
        }

        return spaceSign ? " " + digits : digits;
    }

    private static string ToRadix(BigInteger magnitude, int radix, bool upper)
    {
        if (magnitude.IsZero)
        {
            return "0";
        }

        const string lowerDigits = "0123456789abcdef";
        const string upperDigits = "0123456789ABCDEF";
        var alphabet = upper ? upperDigits : lowerDigits;
        var builder = new StringBuilder();
        var remaining = magnitude;
        while (!remaining.IsZero)
        {
            remaining = BigInteger.DivRem(remaining, radix, out var digit);
            builder.Insert(0, alphabet[(int)digit]);
        }

        return builder.ToString();
    }

    internal static string FormatTemplate(
        string template,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var autoIndex = 0;
        var sawAuto = false;
        var sawManual = false;
        return FormatTemplateCore(
            template,
            positional,
            keywordNames,
            keywordValues,
            ref autoIndex,
            ref sawAuto,
            ref sawManual,
            span
        );
    }

    private static string FormatTemplateCore(
        string template,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        ref int autoIndex,
        ref bool sawAuto,
        ref bool sawManual,
        TextSpan span
    )
    {
        var builder = new StringBuilder();
        var position = 0;
        while (position < template.Length)
        {
            var character = template[position];
            if (character == '}')
            {
                if (position + 1 < template.Length && template[position + 1] == '}')
                {
                    builder.Append('}');
                    position += 2;
                    continue;
                }

                throw Fault("Single '}' encountered in format string.", span);
            }

            if (character != '{')
            {
                builder.Append(character);
                position++;
                continue;
            }

            if (position + 1 < template.Length && template[position + 1] == '{')
            {
                builder.Append('{');
                position += 2;
                continue;
            }

            var fieldEnd = FindFieldEnd(template, position + 1, span);
            var field = template[(position + 1)..fieldEnd];
            position = fieldEnd + 1;

            var specification = string.Empty;
            var specIndex = FindTopLevelColon(field);
            if (specIndex >= 0)
            {
                specification = field[(specIndex + 1)..];
                field = field[..specIndex];
            }

            char? conversion = null;
            var conversionIndex = field.IndexOf('!', StringComparison.Ordinal);
            if (conversionIndex >= 0)
            {
                var conversionText = field[(conversionIndex + 1)..];
                if (conversionText is not ("s" or "r" or "a"))
                {
                    throw Fault($"Invalid conversion specification: '!{conversionText}'.", span);
                }

                conversion = conversionText[0];
                field = field[..conversionIndex];
            }

            var value = ResolveField(
                field,
                positional,
                keywordNames,
                keywordValues,
                ref autoIndex,
                ref sawAuto,
                ref sawManual,
                span
            );
            if (conversion is not null)
            {
                value = new PythonTextValue(
                    conversion == 's' ? value.ToDisplayString() : value.ToRepresentationString()
                );
            }

            if (specification.Contains('{', StringComparison.Ordinal))
            {
                specification = FormatTemplateCore(
                    specification,
                    positional,
                    keywordNames,
                    keywordValues,
                    ref autoIndex,
                    ref sawAuto,
                    ref sawManual,
                    span
                );
            }

            builder.Append(PythonValueFormatter.Format(value, specification, span));
        }

        return builder.ToString();
    }

    private static int FindFieldEnd(string template, int start, TextSpan span)
    {
        var depth = 0;
        for (var position = start; position < template.Length; position++)
        {
            switch (template[position])
            {
                case '{':
                    depth++;
                    break;
                case '}' when depth == 0:
                    return position;
                case '}':
                    depth--;
                    break;
            }
        }

        throw Fault("Expected '}' before end of string.", span);
    }

    private static int FindTopLevelColon(string field)
    {
        var depth = 0;
        for (var position = 0; position < field.Length; position++)
        {
            switch (field[position])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ':' when depth == 0:
                    return position;
            }
        }

        return -1;
    }

    private static PythonValue ResolveField(
        string field,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        ref int autoIndex,
        ref bool sawAuto,
        ref bool sawManual,
        TextSpan span
    )
    {
        var accessorStart = field.Length;
        for (var position = 0; position < field.Length; position++)
        {
            if (field[position] is '.' or '[')
            {
                accessorStart = position;
                break;
            }
        }

        var reference = field[..accessorStart];
        PythonValue value;
        if (reference.Length == 0)
        {
            if (sawManual)
            {
                throw Fault(
                    "Cannot switch from manual field specification to automatic field numbering.",
                    span
                );
            }

            sawAuto = true;
            if (autoIndex >= positional.Count)
            {
                throw Fault(
                    "Replacement index out of range for positional args tuple.",
                    span,
                    "IndexError"
                );
            }

            value = positional[autoIndex++];
        }
        else if (reference.All(char.IsAsciiDigit))
        {
            if (sawAuto)
            {
                throw Fault(
                    "Cannot switch from automatic field numbering to manual field specification.",
                    span
                );
            }

            sawManual = true;
            var index = int.Parse(reference, CultureInfo.InvariantCulture);
            if (index >= positional.Count)
            {
                throw Fault(
                    "Replacement index out of range for positional args tuple.",
                    span,
                    "IndexError"
                );
            }

            value = positional[index];
        }
        else
        {
            var found = -1;
            for (var index = 0; index < keywordNames.Count; index++)
            {
                if (string.Equals(keywordNames[index], reference, StringComparison.Ordinal))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
            {
                throw Fault($"'{reference}'", span, "KeyError");
            }

            value = keywordValues[found];
        }

        var accessorPosition = accessorStart;
        while (accessorPosition < field.Length)
        {
            if (field[accessorPosition] == '.')
            {
                var end = accessorPosition + 1;
                while (end < field.Length && field[end] is not ('.' or '['))
                {
                    end++;
                }

                value = ManagedObjectProtocols.GetAttribute(
                    value,
                    field[(accessorPosition + 1)..end],
                    span
                );
                accessorPosition = end;
            }
            else if (field[accessorPosition] == '[')
            {
                var close = field.IndexOf(']', accessorPosition + 1);
                if (close < 0)
                {
                    throw Fault("Missing ']' in format field.", span);
                }

                var key = field[(accessorPosition + 1)..close];
                PythonValue index =
                    key.All(char.IsAsciiDigit) && key.Length != 0
                        ? PythonWholeNumberValue.Create(
                            BigInteger.Parse(key, CultureInfo.InvariantCulture)
                        )
                        : new PythonTextValue(key);
                value = ManagedObjectProtocols.GetItem(value, index, span);
                accessorPosition = close + 1;
            }
            else
            {
                throw Fault("Invalid format field accessor.", span);
            }
        }

        return value;
    }

    private static PythonRuntimeException Fault(
        string message,
        TextSpan span,
        string exceptionType = "ValueError"
    ) => new("DPY4033", message, span, exceptionType);
}
