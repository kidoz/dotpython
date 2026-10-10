using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Implements the common subset of Python's format-specification mini-language used by
/// f-string interpolations and format(value, spec):
/// [[fill]align][sign][0][width][,][.precision][type].
/// </summary>
internal static class PythonValueFormatter
{
    internal static string Format(PythonValue value, string specification, TextSpan span)
    {
        if (UserObjectProtocols.TryFormat(value, specification, span, out var userText))
        {
            return userText;
        }

        if (specification.Length == 0)
        {
            return value.ToDisplayString();
        }

        // Types that only carry their `repr`: object itself, the opaque builtins, and the
        // binary sequences, whose `__format__` refuses a specification outright.
        if (
            value
            is PythonExternalObjectValue
                or PythonByteSequenceValue
                or PythonByteArrayValue
                or PythonMemoryViewValue
        )
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "unsupported format string passed to "
                    + $"{ManagedObjectProtocols.GetTypeName(value)}.__format__",
                span,
                "TypeError"
            );
        }

        var spec = ParseSpecification(specification, value, span);
        if (value is PythonComplexValue)
        {
            return Pad(FormatComplex(value, spec, span), spec, numeric: true);
        }

        var text = spec.Type switch
        {
            's' when value is PythonTextValue textValue => ApplyPrecisionToText(
                textValue.Value,
                spec
            ),
            's' => throw UnknownFormatCode(spec.Type, value, span),
            'c' => FormatCharacter(value, spec, span),
            'd' or 'n' when IsIntegerValue(value) => FormatInteger(
                value,
                spec,
                radix: 10,
                upper: false,
                prefix: string.Empty,
                span
            ),
            'x' => FormatInteger(value, spec, 16, upper: false, prefix: "0x", span),
            'X' => FormatInteger(value, spec, 16, upper: true, prefix: "0X", span),
            'o' => FormatInteger(value, spec, 8, upper: false, prefix: "0o", span),
            'b' => FormatInteger(value, spec, 2, upper: false, prefix: "0b", span),
            'f' or 'F' => FormatFixed(value, spec, percent: false, span),
            '%' => FormatFixed(value, spec, percent: true, span),
            'e' or 'E' => FormatExponent(value, spec, spec.Type == 'E', span),
            'g' or 'G' => FormatGeneral(value, spec, spec.Type == 'G', span),
            'n' => FormatGeneral(value, spec, upper: false, span),
            '\0' when IsIntegerValue(value) => FormatInteger(
                value,
                spec,
                radix: 10,
                upper: false,
                prefix: string.Empty,
                span
            ),
            '\0' when value is PythonFloatingPointValue => FormatFloatDefault(value, spec, span),
            '\0' when value is PythonTextValue textValue => ApplyPrecisionToText(
                textValue.Value,
                spec
            ),
            '\0' => value.ToDisplayString(),
            _ => throw UnknownFormatCode(spec.Type, value, span),
        };
        return Pad(text, spec, IsNumericValue(value) && spec.Type != 's');
    }

    private readonly record struct Specification(
        char Fill,
        char Align,
        char Sign,
        bool Alternate,
        bool ZeroPad,
        int Width,
        char Grouping,
        int Precision,
        char Type
    );

    private static Specification ParseSpecification(
        string specification,
        PythonValue value,
        TextSpan span
    )
    {
        var fill = ' ';
        var align = '\0';
        var sign = '-';
        var alternate = false;
        var zeroPad = false;
        var width = 0;
        var grouping = '\0';
        var precision = -1;
        var type = '\0';
        var position = 0;

        if (specification.Length >= 2 && specification[1] is '<' or '>' or '^' or '=')
        {
            fill = specification[0];
            align = specification[1];
            position = 2;
        }
        else if (specification.Length >= 1 && specification[0] is '<' or '>' or '^' or '=')
        {
            align = specification[0];
            position = 1;
        }

        if (position < specification.Length && specification[position] is '+' or '-' or ' ')
        {
            sign = specification[position];
            position++;
        }

        if (position < specification.Length && specification[position] == '#')
        {
            alternate = true;
            position++;
        }

        if (position < specification.Length && specification[position] == '0')
        {
            zeroPad = true;
            position++;
        }

        while (position < specification.Length && char.IsAsciiDigit(specification[position]))
        {
            width = width * 10 + (specification[position] - '0');
            position++;
        }

        if (position < specification.Length && specification[position] is ',' or '_')
        {
            grouping = specification[position];
            position++;
        }

        if (position < specification.Length && specification[position] == '.')
        {
            position++;
            precision = 0;
            var sawDigit = false;
            while (position < specification.Length && char.IsAsciiDigit(specification[position]))
            {
                precision = precision * 10 + (specification[position] - '0');
                position++;
                sawDigit = true;
            }

            if (!sawDigit)
            {
                throw InvalidSpecification(specification, value, span);
            }
        }

        if (position < specification.Length)
        {
            type = specification[position];
            position++;
        }

        if (position != specification.Length)
        {
            throw InvalidSpecification(specification, value, span);
        }

        // A string takes no grouping, no sign, no alternate form, and nothing to align a
        // sign against.
        if (value is PythonTextValue)
        {
            if (grouping != '\0')
            {
                throw CannotSpecifyWith(grouping, type == '\0' ? 's' : type, span);
            }

            if (sign is '+' or ' ')
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4012",
                    sign == ' '
                        ? "Space not allowed in string format specifier"
                        : "Sign not allowed in string format specifier",
                    span,
                    "ValueError"
                );
            }

            if (align == '=')
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4012",
                    "'=' alignment not allowed in string format specifier",
                    span,
                    "ValueError"
                );
            }

            if (alternate)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4012",
                    "Alternate form (#) not allowed in string format specifier",
                    span,
                    "ValueError"
                );
            }
        }

        // A character has no sign of its own to show.
        if (type == 'c' && sign != '-')
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4012",
                "Sign not allowed with integer format specifier 'c'",
                span,
                "ValueError"
            );
        }

        if (zeroPad)
        {
            // Zero padding is the fill a leading `0` names; only a number aligns its sign
            // against it, so a string keeps the alignment its own kind defaults to.
            fill = '0';
            if (align == '\0' && value is not PythonTextValue)
            {
                align = '=';
            }
        }

        return new Specification(
            fill,
            align,
            sign,
            alternate,
            zeroPad,
            width,
            grouping,
            precision,
            type
        );
    }

    /// <summary>
    /// A complex number: both of its parts written with the same specification — the sign
    /// each of them carries is the join, so `f"{1-2j:+f}"` is `+1.000000-2.000000j` — while
    /// the fill, the alignment and the width are the whole's, and zero padding has no
    /// meaning between two parts.
    /// </summary>
    private static string FormatComplex(PythonValue value, Specification spec, TextSpan span)
    {
        if (spec.ZeroPad)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4012",
                "Zero padding is not allowed in complex format specifier",
                span,
                "ValueError"
            );
        }

        if (spec.Type == '\0')
        {
            return value.ToDisplayString();
        }

        if (spec.Type is not ('f' or 'F' or 'e' or 'E' or 'g' or 'G' or 'n'))
        {
            throw UnknownFormatCode(spec.Type, value, span);
        }

        var number = ((PythonComplexValue)value).Value;
        var partSpec = spec with { Fill = ' ', Align = '\0', Width = 0, ZeroPad = false };
        var real = new PythonFloatingPointValue(number.Real);
        var imaginary = new PythonFloatingPointValue(number.Imaginary);
        // The imaginary part carries the join: a `+` of its own unless it is negative,
        // which its own sign already shows.
        var imaginarySpec = partSpec with
        {
            Sign = '+',
        };
        return spec.Type switch
        {
            'f' or 'F' => FormatFixed(real, partSpec, percent: false, span)
                + FormatFixed(imaginary, imaginarySpec, percent: false, span)
                + "j",
            'e' or 'E' => FormatExponent(real, partSpec, spec.Type == 'E', span)
                + FormatExponent(imaginary, imaginarySpec, spec.Type == 'E', span)
                + "j",
            _ => FormatGeneral(real, partSpec, spec.Type == 'G', span)
                + FormatGeneral(imaginary, imaginarySpec, spec.Type == 'G', span)
                + "j",
        };
    }

    /// <summary>`chr(value)`: the type's own form of the character an ordinal stands for.</summary>    /// <summary>`chr(value)`: the type's own form of the character an ordinal stands for.</summary>
    private static string FormatCharacter(PythonValue value, Specification spec, TextSpan span)
    {
        if (spec.Precision >= 0)
            throw PrecisionNotAllowed(span);
        if (spec.Grouping != '\0')
            throw CannotSpecifyWith(spec.Grouping, 'c', span);
        var codePoint = value switch
        {
            PythonWholeNumberValue whole when whole.Value >= 0 && whole.Value <= 0x10FFFF => (int)
                whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonWholeNumberValue => throw ManagedObjectProtocols.Fault(
                "DPY4012",
                "%c arg not in range(0x110000)",
                span,
                "OverflowError"
            ),
            _ => throw UnknownFormatCode('c', value, span),
        };
        return char.ConvertFromUtf32(codePoint);
    }

    private static string FormatInteger(
        PythonValue value,
        Specification spec,
        int radix,
        bool upper,
        string prefix,
        TextSpan span
    )
    {
        if (spec.Precision >= 0)
        {
            throw PrecisionNotAllowed(span);
        }

        var groupSize = radix == 10 ? 3 : 4;
        if (spec.Grouping == ',' && radix != 10)
        {
            throw CannotSpecifyWith(',', spec.Type == '\0' ? 'd' : spec.Type, span);
        }

        var number = RequireInteger(value, spec, span);
        var magnitude = BigInteger.Abs(number);
        var digits =
            radix == 10
                ? magnitude.ToString(CultureInfo.InvariantCulture)
                : ToRadix(magnitude, radix, upper);
        return ComposeNumeric(
            number.Sign < 0,
            spec.Alternate && prefix.Length != 0 ? prefix : string.Empty,
            digits,
            spec,
            spec.Grouping == '\0' ? 0 : groupSize,
            span
        );
    }

    /// <summary>
    /// A float written with no type of its own: the shortest text that round-trips, which
    /// is what CPython prints, with the grouping a specification asks for.
    /// </summary>
    private static string FormatFloatDefault(PythonValue value, Specification spec, TextSpan span)
    {
        var floating = ((PythonFloatingPointValue)value).Value;
        var digits = new PythonFloatingPointValue(Math.Abs(floating)).ToDisplayString();
        var point = digits.IndexOf('.', StringComparison.Ordinal);
        return ComposeNumeric(
            floating < 0 || (floating == 0 && double.IsNegative(floating)),
            string.Empty,
            point < 0 ? digits : digits[..point],
            spec,
            spec.Grouping == '\0' ? 0 : 3,
            span,
            suffix: point < 0 ? string.Empty : digits[point..]
        );
    }

    private static string FormatFixed(
        PythonValue value,
        Specification spec,
        bool percent,
        TextSpan span
    )
    {
        var number = RequireDouble(value, spec, span) * (percent ? 100 : 1);
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            return ComposeNumeric(
                double.IsInfinity(number) && double.IsNegative(number),
                string.Empty,
                double.IsNaN(number) ? "nan" : "inf",
                spec,
                groupSize: 0,
                span,
                suffix: percent ? "%" : string.Empty
            );
        }

        var precision = spec.Precision >= 0 ? spec.Precision : 6;
        var digits = Math.Abs(number).ToString("F" + precision, CultureInfo.InvariantCulture);
        if (spec.Alternate && precision == 0 && !digits.Contains('.', StringComparison.Ordinal))
        {
            digits += ".";
        }

        var point = digits.IndexOf('.', StringComparison.Ordinal);
        return ComposeNumeric(
            !double.IsNaN(number) && (number < 0 || (number == 0 && double.IsNegative(number))),
            string.Empty,
            point < 0 ? digits : digits[..point],
            spec,
            spec.Grouping == '\0' || percent ? 0 : 3,
            span,
            suffix: (point < 0 ? string.Empty : digits[point..]) + (percent ? "%" : string.Empty)
        );
    }

    private static string FormatExponent(
        PythonValue value,
        Specification spec,
        bool upper,
        TextSpan span
    )
    {
        var number = RequireDouble(value, spec, span);
        var precision = spec.Precision >= 0 ? spec.Precision : 6;
        var digits =
            double.IsNaN(number) || double.IsInfinity(number)
                ? (double.IsNaN(number) ? "nan" : "inf")
                : ExponentDigits(
                    Math.Abs(number),
                    precision,
                    alternate: spec.Alternate,
                    trim: false
                );
        if (upper)
        {
            digits = digits.ToUpperInvariant();
        }

        return ComposeNumeric(number < 0, string.Empty, digits, spec, groupSize: 0, span);
    }

    /// <summary>
    /// The general format, `g` and `G` — and `n`, which is the same for a float: the value
    /// written with `precision` significant digits, in exponent form when the exponent is
    /// below -4 or reaches the precision, and with the trailing zeros the alternate form
    /// keeps and the plain form drops.
    /// </summary>
    private static string FormatGeneral(
        PythonValue value,
        Specification spec,
        bool upper,
        TextSpan span
    )
    {
        var number = RequireDouble(value, spec, span);
        var precision = Math.Max(1, spec.Precision >= 0 ? spec.Precision : 6);
        var magnitude = Math.Abs(number);
        string digits;
        var fraction = string.Empty;
        var exponentForm = false;
        if (double.IsNaN(magnitude) || double.IsInfinity(magnitude))
        {
            digits = double.IsNaN(magnitude) ? "nan" : "inf";
        }
        else
        {
            var exponent = magnitude == 0 ? 0 : (int)Math.Floor(Math.Log10(magnitude));
            exponentForm = exponent < -4 || exponent >= precision;
            if (exponentForm)
            {
                digits = ExponentDigits(magnitude, precision - 1, spec.Alternate, trim: true);
            }
            else
            {
                var decimals = Math.Max(0, precision - 1 - exponent);
                digits = magnitude.ToString("F" + decimals, CultureInfo.InvariantCulture);
                digits = TrimZeros(digits, spec.Alternate);
                if (spec.Alternate && !digits.Contains('.', StringComparison.Ordinal))
                    digits += ".";
            }

            if (!exponentForm)
            {
                var point = digits.IndexOf('.', StringComparison.Ordinal);
                fraction = point < 0 ? string.Empty : digits[point..];
                digits = point < 0 ? digits : digits[..point];
            }
        }

        if (upper)
        {
            digits = digits.ToUpperInvariant();
        }

        return ComposeNumeric(
            !double.IsNaN(number) && (number < 0 || (number == 0 && double.IsNegative(number))),
            string.Empty,
            digits,
            spec,
            // The exponent form keeps its mantissa whole, so grouping reaches the fixed form
            // alone, and only the digits before its point.
            spec.Grouping != '\0'
            && !exponentForm
                ? 3
                : 0,
            span,
            suffix: fraction
        );
    }

    /// <summary>
    /// The exponent form of a magnitude with `decimals` digits after its point: the `e`
    /// type keeps every digit it asked for, while the general form trims what it does not
    /// need.
    /// </summary>
    private static string ExponentDigits(double magnitude, int decimals, bool alternate, bool trim)
    {
        var digits = magnitude.ToString(
            "0." + new string('0', decimals) + "e+00",
            CultureInfo.InvariantCulture
        );
        var separator = digits.IndexOf('e', StringComparison.Ordinal);
        var mantissa = digits[..separator];
        if (!alternate && decimals == 0 && mantissa.EndsWith('.'))
        {
            mantissa = mantissa[..^1];
        }

        if (trim)
        {
            mantissa = TrimZeros(mantissa, alternate);
        }

        return mantissa + digits[separator..];
    }

    private static string TrimZeros(string digits, bool alternate)
    {
        if (alternate || !digits.Contains('.', StringComparison.Ordinal))
        {
            return digits;
        }

        var trimmed = digits.TrimEnd('0');
        return trimmed.EndsWith('.') ? trimmed[..^1] : trimmed;
    }

    /// <summary>Groups the digits before a number's decimal point.</summary>
    private static string GroupNumber(string digits, char separator)
    {
        var point = digits.IndexOf('.', StringComparison.Ordinal);
        return point < 0
            ? GroupDigits(digits, 3, separator)
            : GroupDigits(digits[..point], 3, separator) + digits[point..];
    }

    /// <summary>
    /// A number's sign, its base prefix, its digits and the zeros a `0` specification adds:
    /// the zeros go before the separators, so `f"{-1234:015_}"` is `-00_000_001_234`, and
    /// after the prefix, so `f"{255:#012x}"` is `0x00000000ff`.
    /// </summary>
    private static string ComposeNumeric(
        bool negative,
        string prefix,
        string digits,
        Specification spec,
        int groupSize,
        TextSpan span,
        string suffix = ""
    )
    {
        var sign = negative
            ? "-"
            : spec.Sign switch
            {
                '+' => "+",
                ' ' => " ",
                _ => string.Empty,
            };
        var zeros = 0;
        if (spec.ZeroPad && spec.Align == '=')
        {
            var available = spec.Width - sign.Length - prefix.Length - suffix.Length;
            var grouped = groupSize > 0;
            while (digits.Length + zeros < available)
            {
                var length = digits.Length + zeros;
                if (grouped)
                {
                    length += length == 0 ? 0 : (length - 1) / groupSize;
                }

                if (length >= available)
                {
                    break;
                }

                zeros++;
            }
        }

        var raw = new string('0', zeros) + digits;
        var body = groupSize > 0 ? GroupDigits(raw, groupSize, spec.Grouping) : raw;
        return sign + prefix + body + suffix;
    }

    private static string ToRadix(BigInteger value, int radix, bool upper)
    {
        if (value.IsZero)
        {
            return "0";
        }

        const string lowerDigits = "0123456789abcdef";
        const string upperDigits = "0123456789ABCDEF";
        var alphabet = upper ? upperDigits : lowerDigits;
        var builder = new StringBuilder();
        var remaining = value;
        while (!remaining.IsZero)
        {
            remaining = BigInteger.DivRem(remaining, radix, out var digit);
            builder.Insert(0, alphabet[(int)digit]);
        }

        return builder.ToString();
    }

    private static string GroupDigits(string digits, int groupSize, char separator)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < digits.Length; index++)
        {
            if (index != 0 && (digits.Length - index) % groupSize == 0)
            {
                builder.Append(separator);
            }

            builder.Append(digits[index]);
        }

        return builder.ToString();
    }

    private static string ApplySign(string digits, bool negative, Specification spec)
    {
        if (negative)
        {
            return "-" + digits;
        }

        return spec.Sign switch
        {
            '+' => "+" + digits,
            ' ' => " " + digits,
            _ => digits,
        };
    }

    private static string ApplyPrecisionToText(string text, Specification spec) =>
        spec.Precision >= 0 && spec.Precision < text.Length ? text[..spec.Precision] : text;

    private static string Pad(string text, Specification spec, bool numeric)
    {
        if (text.Length >= spec.Width)
        {
            return text;
        }

        var padding = spec.Width - text.Length;
        var align = spec.Align;
        if (align == '\0')
        {
            align = numeric ? '>' : '<';
        }

        return align switch
        {
            '<' => text + new string(spec.Fill, padding),
            '>' => new string(spec.Fill, padding) + text,
            '^' => new string(spec.Fill, padding / 2)
                + text
                + new string(spec.Fill, padding - padding / 2),
            '=' => PadAfterSign(text, spec.Fill, padding),
            _ => text,
        };
    }

    private static string PadAfterSign(string text, char fill, int padding)
    {
        var signLength = 0;
        if (text.Length != 0 && text[0] is '+' or '-' or ' ')
        {
            signLength = 1;
        }
        else if (text.Length > 1 && text[0] == '0' && text[1] is 'x' or 'X' or 'o' or 'b')
        {
            signLength = 2;
        }

        return text[..signLength] + new string(fill, padding) + text[signLength..];
    }

    private static BigInteger RequireInteger(
        PythonValue value,
        Specification spec,
        TextSpan span
    ) =>
        value switch
        {
            PythonWholeNumberValue wholeNumber => wholeNumber.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => throw UnknownFormatCode(spec.Type == '\0' ? 'd' : spec.Type, value, span),
        };

    private static double RequireDouble(PythonValue value, Specification spec, TextSpan span) =>
        value switch
        {
            PythonFloatingPointValue floating => floating.Value,
            PythonWholeNumberValue wholeNumber => (double)wholeNumber.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw UnknownFormatCode(spec.Type == '\0' ? 'f' : spec.Type, value, span),
        };

    private static bool IsIntegerValue(PythonValue value) =>
        value is PythonWholeNumberValue or PythonTruthValue;

    private static bool IsNumericValue(PythonValue value) =>
        value is PythonWholeNumberValue or PythonFloatingPointValue or PythonTruthValue;

    private static PythonRuntimeException PrecisionNotAllowed(TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4012",
            "Precision not allowed in integer format specifier",
            span,
            "ValueError"
        );

    /// <summary>`Cannot specify ',' with 'x'.`, the refusal a grouping character earns.</summary>
    private static PythonRuntimeException CannotSpecifyWith(
        char grouping,
        char type,
        TextSpan span
    ) =>
        ManagedObjectProtocols.Fault(
            "DPY4012",
            $"Cannot specify '{grouping}' with '{type}'.",
            span,
            "ValueError"
        );

    private static PythonRuntimeException UnknownFormatCode(
        char type,
        PythonValue value,
        TextSpan span
    ) =>
        ManagedObjectProtocols.Fault(
            "DPY4012",
            $"Unknown format code '{type}' for object of type "
                + $"'{ManagedObjectProtocols.GetTypeName(value)}'",
            span,
            "ValueError"
        );

    private static PythonRuntimeException InvalidSpecification(
        string specification,
        PythonValue value,
        TextSpan span
    ) =>
        ManagedObjectProtocols.Fault(
            "DPY4012",
            $"Invalid format specifier '{specification}' for object of type "
                + $"'{ManagedObjectProtocols.GetTypeName(value)}'",
            span,
            "ValueError"
        );
}
