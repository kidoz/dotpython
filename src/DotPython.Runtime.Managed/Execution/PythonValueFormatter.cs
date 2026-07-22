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
        if (specification.Length == 0)
        {
            return value.ToDisplayString();
        }

        var spec = ParseSpecification(specification, span);
        var text = spec.Type switch
        {
            's' or '\0' when value is PythonTextValue textValue => ApplyPrecisionToText(
                textValue.Value,
                spec
            ),
            's' => throw UnknownFormatCode(spec.Type, value, span),
            'd' => FormatInteger(RequireInteger(value, span), spec, 10, upper: false),
            'x' => FormatInteger(RequireInteger(value, span), spec, 16, upper: false),
            'X' => FormatInteger(RequireInteger(value, span), spec, 16, upper: true),
            'o' => FormatInteger(RequireInteger(value, span), spec, 8, upper: false),
            'b' => FormatInteger(RequireInteger(value, span), spec, 2, upper: false),
            'f' or 'F' => FormatFixed(RequireDouble(value, span), spec, percent: false),
            '%' => FormatFixed(RequireDouble(value, span) * 100, spec, percent: true),
            'e' or 'E' => FormatExponent(RequireDouble(value, span), spec, spec.Type == 'E'),
            '\0' when value is PythonWholeNumberValue or PythonTruthValue => FormatInteger(
                RequireInteger(value, span),
                spec,
                10,
                upper: false
            ),
            '\0' when value is PythonFloatingPointValue floating => ApplySign(
                new PythonFloatingPointValue(Math.Abs(floating.Value)).ToDisplayString(),
                floating.Value < 0,
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
        bool ZeroPad,
        int Width,
        bool Grouping,
        int Precision,
        char Type
    );

    private static Specification ParseSpecification(string specification, TextSpan span)
    {
        var fill = ' ';
        var align = '\0';
        var sign = '-';
        var zeroPad = false;
        var width = 0;
        var grouping = false;
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

        if (position < specification.Length && specification[position] == ',')
        {
            grouping = true;
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
                throw InvalidSpecification(span);
            }
        }

        if (position < specification.Length)
        {
            type = specification[position];
            position++;
        }

        if (position != specification.Length)
        {
            throw InvalidSpecification(span);
        }

        if (zeroPad && align == '\0')
        {
            fill = '0';
            align = '=';
        }

        return new Specification(fill, align, sign, zeroPad, width, grouping, precision, type);
    }

    private static string FormatInteger(BigInteger value, Specification spec, int radix, bool upper)
    {
        var magnitude = BigInteger.Abs(value);
        var digits = radix switch
        {
            10 => magnitude.ToString(CultureInfo.InvariantCulture),
            _ => ToRadix(magnitude, radix, upper),
        };
        if (spec.Grouping && radix == 10)
        {
            digits = GroupDigits(digits, 3);
        }

        return ApplySign(digits, value.Sign < 0, spec);
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

    private static string FormatFixed(double value, Specification spec, bool percent)
    {
        var precision = spec.Precision >= 0 ? spec.Precision : 6;
        var digits = Math.Abs(value).ToString("F" + precision, CultureInfo.InvariantCulture);
        if (spec.Grouping)
        {
            var pointIndex = digits.IndexOf('.', StringComparison.Ordinal);
            var whole = pointIndex < 0 ? digits : digits[..pointIndex];
            var fraction = pointIndex < 0 ? string.Empty : digits[pointIndex..];
            digits = GroupDigits(whole, 3) + fraction;
        }

        if (percent)
        {
            digits += "%";
        }

        return ApplySign(digits, value < 0 || (value == 0 && double.IsNegative(value)), spec);
    }

    private static string FormatExponent(double value, Specification spec, bool upper)
    {
        var precision = spec.Precision >= 0 ? spec.Precision : 6;
        var format = "0." + new string('0', precision) + "e+00";
        var digits = Math.Abs(value).ToString(format, CultureInfo.InvariantCulture);
        if (precision == 0)
        {
            digits = digits.Replace("0.e", "0e", StringComparison.Ordinal);
        }

        if (upper)
        {
            digits = digits.ToUpperInvariant();
        }

        return ApplySign(digits, value < 0, spec);
    }

    private static string GroupDigits(string digits, int groupSize)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < digits.Length; index++)
        {
            if (index != 0 && (digits.Length - index) % groupSize == 0)
            {
                builder.Append(',');
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
        var signLength = text.Length != 0 && text[0] is '+' or '-' or ' ' ? 1 : 0;
        return text[..signLength] + new string(fill, padding) + text[signLength..];
    }

    private static BigInteger RequireInteger(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonWholeNumberValue wholeNumber => wholeNumber.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => throw UnknownFormatCode('d', value, span),
        };

    private static double RequireDouble(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonFloatingPointValue floating => floating.Value,
            PythonWholeNumberValue wholeNumber => (double)wholeNumber.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw UnknownFormatCode('f', value, span),
        };

    private static bool IsNumericValue(PythonValue value) =>
        value is PythonWholeNumberValue or PythonFloatingPointValue or PythonTruthValue;

    private static PythonRuntimeException UnknownFormatCode(
        char type,
        PythonValue value,
        TextSpan span
    ) =>
        ManagedObjectProtocols.Fault(
            "DPY4012",
            $"Unknown format code '{type}' for object of type "
                + $"'{ManagedObjectProtocols.GetTypeName(value)}'.",
            span,
            "ValueError"
        );

    private static PythonRuntimeException InvalidSpecification(TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4012",
            "The format specification is invalid.",
            span,
            "ValueError"
        );
}
