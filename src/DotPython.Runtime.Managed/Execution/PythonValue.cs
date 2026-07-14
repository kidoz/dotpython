using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Compiler.Bytecode;

namespace DotPython.Runtime.Managed.Execution;

internal abstract record PythonValue
{
    internal abstract string ToDisplayString();

    internal virtual string ToRepresentationString() => ToDisplayString();
}

internal sealed record PythonNoneValue : PythonValue
{
    internal static PythonNoneValue Instance { get; } = new();

    private PythonNoneValue() { }

    internal override string ToDisplayString() => "None";
}

internal sealed record PythonTruthValue : PythonValue
{
    internal static PythonTruthValue False { get; } = new(false);

    internal static PythonTruthValue True { get; } = new(true);

    private PythonTruthValue(bool value)
    {
        Value = value;
    }

    internal bool Value { get; }

    internal static PythonTruthValue FromBoolean(bool value) => value ? True : False;

    internal override string ToDisplayString() => Value ? "True" : "False";
}

internal sealed record PythonWholeNumberValue(BigInteger Value) : PythonValue
{
    private const int LargestCachedValue = 256;
    private const int SmallestCachedValue = -5;
    private static readonly PythonWholeNumberValue[] CachedValues = CreateCachedValues();

    internal static PythonWholeNumberValue Create(BigInteger value)
    {
        if (value >= SmallestCachedValue && value <= LargestCachedValue)
        {
            return CachedValues[(int)value - SmallestCachedValue];
        }

        return new PythonWholeNumberValue(value);
    }

    internal override string ToDisplayString() => Value.ToString(CultureInfo.InvariantCulture);

    private static PythonWholeNumberValue[] CreateCachedValues()
    {
        var values = new PythonWholeNumberValue[LargestCachedValue - SmallestCachedValue + 1];
        for (var value = SmallestCachedValue; value <= LargestCachedValue; value++)
        {
            values[value - SmallestCachedValue] = new PythonWholeNumberValue(value);
        }

        return values;
    }
}

internal sealed record PythonFloatingPointValue(double Value) : PythonValue
{
    internal override string ToDisplayString()
    {
        if (double.IsNaN(Value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(Value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(Value))
        {
            return "-inf";
        }

        var text = Value
            .ToString("R", CultureInfo.InvariantCulture)
            .Replace("E", "e", StringComparison.Ordinal);
        return
            text.Contains('.', StringComparison.Ordinal)
            || text.Contains('e', StringComparison.Ordinal)
            ? text
            : $"{text}.0";
    }
}

internal sealed record PythonComplexValue(Complex Value) : PythonValue
{
    internal override string ToDisplayString()
    {
        var real = FormatComponent(Value.Real);
        var imaginary = FormatComponent(Math.Abs(Value.Imaginary));
        var sign = Value.Imaginary < 0 ? "-" : "+";

        if (Value.Real == 0)
        {
            return $"{(Value.Imaginary < 0 ? "-" : string.Empty)}{imaginary}j";
        }

        return $"({real}{sign}{imaginary}j)";
    }

    private static string FormatComponent(double value)
    {
        var text = new PythonFloatingPointValue(value).ToDisplayString();
        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}

internal sealed record PythonTextValue(string Value) : PythonValue
{
    internal override string ToDisplayString() => Value;

    internal override string ToRepresentationString()
    {
        var delimiter =
            Value.Contains('\'', StringComparison.Ordinal)
            && !Value.Contains('"', StringComparison.Ordinal)
                ? '"'
                : '\'';
        var builder = new StringBuilder().Append(delimiter);
        foreach (var rune in Value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case var value when value == delimiter:
                    builder.Append('\\').Append(value);
                    break;
                case var value when IsPythonPrintable(rune):
                    builder.Append(rune.ToString());
                    break;
                case <= byte.MaxValue:
                    builder.Append(CultureInfo.InvariantCulture, $"\\x{rune.Value:x2}");
                    break;
                case <= char.MaxValue:
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:x4}");
                    break;
                default:
                    builder.Append(CultureInfo.InvariantCulture, $"\\U{rune.Value:x8}");
                    break;
            }
        }

        return builder.Append(delimiter).ToString();
    }

    private static bool IsPythonPrintable(Rune rune)
    {
        if (rune.Value == ' ')
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune)
            is not (
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator
            );
    }
}

internal sealed record PythonByteSequenceValue(byte[] Value) : PythonValue
{
    internal override string ToDisplayString()
    {
        var builder = new StringBuilder("b'");
        foreach (var item in Value)
        {
            switch (item)
            {
                case (byte)'\\':
                    builder.Append("\\\\");
                    break;
                case (byte)'\'':
                    builder.Append("\\'");
                    break;
                case >= 32 and < 127:
                    builder.Append((char)item);
                    break;
                default:
                    builder.Append(CultureInfo.InvariantCulture, $"\\x{item:x2}");
                    break;
            }
        }

        return builder.Append('\'').ToString();
    }

    public bool Equals(PythonByteSequenceValue? other) =>
        other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Value)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

internal sealed record PythonBuiltinFunctionValue(
    string Name,
    Func<IReadOnlyList<PythonValue>, PythonValue> Invoke
) : PythonValue
{
    internal override string ToDisplayString() => $"<built-in function {Name}>";
}

internal sealed record PythonFunctionValue(
    string Name,
    PreparedPythonCode Code,
    PythonGlobalNamespace Globals,
    PythonCell[] Closure
) : PythonValue
{
    internal override string ToDisplayString() => $"<function {Name}>";
}

internal sealed record PythonListValue(List<PythonValue> Elements) : PythonValue
{
    internal override string ToDisplayString() =>
        $"[{string.Join(", ", Elements.Select(element => element.ToRepresentationString()))}]";
}

internal sealed record PythonTupleValue(PythonValue[] Elements) : PythonValue
{
    internal override string ToDisplayString()
    {
        if (Elements.Length == 0)
        {
            return "()";
        }

        var contents = string.Join(
            ", ",
            Elements.Select(element => element.ToRepresentationString())
        );
        return Elements.Length == 1 ? $"({contents},)" : $"({contents})";
    }
}
