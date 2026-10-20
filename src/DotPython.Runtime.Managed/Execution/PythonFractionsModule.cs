using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `fractions` module: `Fraction` over the runtime's exact integer arithmetic, with
/// CPython 3.14's construction rules (integers, rationals, floats through their exact
/// integer ratio, numeric strings with underscores and exponents), its normalized
/// two-field state, the Knuth-style arithmetic with the reflected fallbacks that promote
/// to float and complex, its comparison rules against floats (infinities and NaNs
/// included), the numeric hash shared with `int` and `float`, `limit_denominator`,
/// `from_float`/`from_number`, and the general and float-style `__format__` grammars.
/// The `numbers` ABCs and `Decimal` are not part of this runtime, so the MRO stops at
/// `object` and `from_decimal` can only report the missing module.
/// </summary>
internal static class PythonFractionsModule
{
    private static readonly BigInteger HashModulus = (BigInteger.One << 61) - 1;

    private const long HashInfinity = 314159;

    private static readonly BigInteger Two = new(2);

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("Fraction", FractionType);
        globals.SetValue("__all__", new PythonListValue([new PythonTextValue("Fraction")]));
    }

    private static readonly PythonManagedTypeValue FractionType = CreateFractionType();

    private sealed record FractionState(BigInteger Numerator, BigInteger Denominator);

    private static PythonManagedTypeValue CreateFractionType()
    {
        var type = new PythonManagedTypeValue("Fraction") { Module = "fractions" };
        type.Attributes["__module__"] = new PythonTextValue("fractions");
        type.Attributes["__qualname__"] = new PythonTextValue("Fraction");
        type.Attributes["__doc__"] = new PythonTextValue(
            "This class implements rational numbers.\n"
                + "\n"
                + "In the two-argument form of the constructor, Fraction(8, 6) will\n"
                + "produce a rational number equivalent to 4/3. Both arguments must\n"
                + "be Rational. The numerator defaults to 0 and the denominator\n"
                + "defaults to 1 so that Fraction(3) == 3 and Fraction() == 0.\n"
                + "\n"
                + "Fractions can also be constructed from:\n"
                + "\n"
                + "  - numeric strings similar to those accepted by the\n"
                + "    float constructor (for example, '-2.3' or '1e10')\n"
                + "\n"
                + "  - strings of the form '123/456'\n"
                + "\n"
                + "  - float and Decimal instances\n"
                + "\n"
                + "  - other Rational instances (including integers)\n"
        );
        type.Attributes["__slots__"] = new PythonTupleValue([
            new PythonTextValue("_numerator"),
            new PythonTextValue("_denominator"),
        ]);
        type.HasDeclaredSlots = true;
        type.Slots = PythonSlotLayout.Create(
            type,
            new PythonTupleValue([
                new PythonTextValue("_numerator"),
                new PythonTextValue("_denominator"),
            ]),
            default
        );
        type.Attributes["__new__"] = PythonUserTypes.Method(
            "Fraction",
            "__new__",
            (_, arguments) => NewFraction(ClassOf(arguments), Rest(arguments), [], [], default),
            (_, positional, names, values) =>
                NewFraction(ClassOf(positional), Rest(positional), names, values, default)
        );
        type.Attributes["numerator"] = new PythonPropertyValue(
            new PythonProtocolFunctionValue(
                "numerator",
                (_, arguments) => PythonWholeNumberValue.Create(FractionOf(arguments[0]).Numerator)
            ),
            null,
            null
        );
        type.Attributes["denominator"] = new PythonPropertyValue(
            new PythonProtocolFunctionValue(
                "denominator",
                (_, arguments) =>
                    PythonWholeNumberValue.Create(FractionOf(arguments[0]).Denominator)
            ),
            null,
            null
        );
        type.Attributes["__repr__"] = Method(
            "Fraction",
            "__repr__",
            (self, _) => new PythonTextValue(Represent(self!))
        );
        type.Attributes["__str__"] = Method(
            "Fraction",
            "__str__",
            (self, _) => new PythonTextValue(Str(self!))
        );
        type.Attributes["__format__"] = Method(
            "Fraction",
            "__format__",
            (self, arguments) => Format(self!, arguments[0])
        );
        type.Attributes["__hash__"] = Method(
            "Fraction",
            "__hash__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return PythonWholeNumberValue.Create(Hash(state.Numerator, state.Denominator));
            }
        );
        type.Attributes["__bool__"] = Method(
            "Fraction",
            "__bool__",
            (self, _) => PythonTruthValue.FromBoolean(!FractionOf(self!).Numerator.IsZero)
        );
        type.Attributes["__int__"] = Method(
            "Fraction",
            "__int__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return PythonWholeNumberValue.Create(Truncate(state.Numerator, state.Denominator));
            }
        );
        type.Attributes["__float__"] = Method(
            "Fraction",
            "__float__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return new PythonFloatingPointValue(
                    RatioToDouble(state.Numerator, state.Denominator)
                );
            }
        );
        type.Attributes["__trunc__"] = Method(
            "Fraction",
            "__trunc__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return PythonWholeNumberValue.Create(Truncate(state.Numerator, state.Denominator));
            }
        );
        type.Attributes["__floor__"] = Method(
            "Fraction",
            "__floor__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return PythonWholeNumberValue.Create(FloorDiv(state.Numerator, state.Denominator));
            }
        );
        type.Attributes["__ceil__"] = Method(
            "Fraction",
            "__ceil__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return PythonWholeNumberValue.Create(
                    -FloorDiv(-state.Numerator, state.Denominator)
                );
            }
        );
        type.Attributes["__round__"] = Method(
            "Fraction",
            "__round__",
            (self, arguments) =>
                Round(FractionOf(self!), arguments.Count == 0 ? null : arguments[0], self!),
            ["ndigits"],
            [PythonNoneValue.Instance]
        );
        type.Attributes["__pos__"] = Method(
            "Fraction",
            "__pos__",
            (self, _) => Create(FractionType, FractionOf(self!))
        );
        type.Attributes["__neg__"] = Method(
            "Fraction",
            "__neg__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return Create(FractionType, -state.Numerator, state.Denominator);
            }
        );
        type.Attributes["__abs__"] = Method(
            "Fraction",
            "__abs__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return Create(FractionType, BigInteger.Abs(state.Numerator), state.Denominator);
            }
        );
        AddOperators(type);
        AddComparisons(type);
        type.Attributes["is_integer"] = Method(
            "Fraction",
            "is_integer",
            (self, _) => PythonTruthValue.FromBoolean(FractionOf(self!).Denominator.IsOne)
        );
        type.Attributes["as_integer_ratio"] = Method(
            "Fraction",
            "as_integer_ratio",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return new PythonTupleValue([
                    PythonWholeNumberValue.Create(state.Numerator),
                    PythonWholeNumberValue.Create(state.Denominator),
                ]);
            }
        );
        type.Attributes["limit_denominator"] = Method(
            "Fraction",
            "limit_denominator",
            (self, arguments) =>
                LimitDenominator(
                    self!,
                    arguments.Count == 0 ? PythonWholeNumberValue.Create(1_000_000) : arguments[0]
                ),
            ["max_denominator"],
            [PythonWholeNumberValue.Create(1_000_000)]
        );
        type.Attributes["from_float"] = ClassMethod(
            "Fraction",
            "from_float",
            (cls, arguments) => FromFloat(cls!, arguments[0]),
            ["f"],
            [null]
        );
        type.Attributes["from_number"] = ClassMethod(
            "Fraction",
            "from_number",
            (cls, arguments) => FromNumber(cls!, arguments[0]),
            ["number"],
            [null]
        );
        type.Attributes["from_decimal"] = ClassMethod(
            "Fraction",
            "from_decimal",
            (cls, arguments) => FromDecimal(cls!, arguments[0], default),
            ["dec"],
            [null]
        );
        type.Attributes["__reduce__"] = Method(
            "Fraction",
            "__reduce__",
            (self, _) =>
            {
                var state = FractionOf(self!);
                return new PythonTupleValue([
                    TypeOf(self!),
                    new PythonTupleValue([
                        PythonWholeNumberValue.Create(state.Numerator),
                        PythonWholeNumberValue.Create(state.Denominator),
                    ]),
                ]);
            }
        );
        type.Attributes["__copy__"] = Method("Fraction", "__copy__", (self, _) => Copy(self!));
        type.Attributes["__deepcopy__"] = Method(
            "Fraction",
            "__deepcopy__",
            (self, _) => Copy(self!),
            ["memo"],
            [null]
        );
        return type;
    }

    // -----------------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------------

    private static PythonManagedTypeValue ClassOf(IReadOnlyList<PythonValue> arguments) =>
        arguments[0] as PythonManagedTypeValue
        ?? throw TypeFault(
            "super(type, obj): obj is not an instance or subtype of type (Fraction)."
        );

    private static PythonValue[] Rest(IReadOnlyList<PythonValue> arguments) =>
        [.. arguments.Skip(1)];

    private static FractionState FractionOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: FractionState state }
            ? state
            : throw TypeFault(
                "'" + ManagedObjectProtocols.GetTypeName(value) + "' object is not a Fraction"
            );

    private static PythonManagedObjectValue NewFraction(
        PythonManagedTypeValue cls,
        PythonValue[] arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (arguments.Length > 2)
        {
            throw TypeFault(
                $"{cls.Name}.__new__() takes from 1 to 3 positional arguments but "
                    + $"{arguments.Length + 1} were given",
                span
            );
        }
        var slots = new PythonValue?[2];
        for (var index = 0; index < arguments.Length; index++)
        {
            slots[index] = arguments[index];
        }
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = keywordNames[index] switch
            {
                "numerator" => 0,
                "denominator" => 1,
                _ => -1,
            };
            if (slot < 0)
            {
                throw TypeFault(
                    $"{cls.Name}.__new__() got an unexpected keyword argument "
                        + $"'{keywordNames[index]}'",
                    span
                );
            }
            if (slots[slot] is not null)
            {
                throw TypeFault(
                    $"{cls.Name}.__new__() got multiple values for argument "
                        + $"'{keywordNames[index]}'",
                    span
                );
            }
            slots[slot] = keywordValues[index];
        }
        var numerator = slots[0] ?? PythonWholeNumberValue.Create(0);
        var denominatorValue = slots[1];
        if (denominatorValue is PythonNoneValue)
        {
            denominatorValue = null;
        }
        return Build(cls, numerator, denominatorValue, span);
    }

    private static PythonManagedObjectValue Build(
        PythonManagedTypeValue cls,
        PythonValue numeratorValue,
        PythonValue? denominatorValue,
        TextSpan span
    )
    {
        BigInteger numerator;
        BigInteger denominator;
        if (denominatorValue is null)
        {
            if (numeratorValue is PythonWholeNumberValue exact)
            {
                return Create(cls, exact.Value, BigInteger.One);
            }
            if (RationalParts(numeratorValue) is { } rational)
            {
                return Create(cls, rational.Numerator, rational.Denominator);
            }
            if (
                numeratorValue is PythonFloatingPointValue
                || (
                    numeratorValue is not PythonManagedTypeValue
                    && HasAttribute(numeratorValue, "as_integer_ratio")
                )
            )
            {
                var ratio = IntegerRatioOf(numeratorValue, span);
                return Create(cls, ratio.Numerator, ratio.Denominator);
            }
            if (numeratorValue is PythonTextValue text)
            {
                (numerator, denominator) = ParseFractionString(text.Value, span);
            }
            else
            {
                throw TypeFault(
                    "argument should be a string or a Rational instance or have the "
                        + "as_integer_ratio() method",
                    span
                );
            }
        }
        else if (
            numeratorValue is PythonWholeNumberValue first
            && denominatorValue is PythonWholeNumberValue second
        )
        {
            numerator = first.Value;
            denominator = second.Value;
        }
        else if (
            RationalParts(numeratorValue) is { } left
            && RationalParts(denominatorValue) is { } right
        )
        {
            numerator = left.Numerator * right.Denominator;
            denominator = right.Numerator * left.Denominator;
        }
        else
        {
            throw TypeFault("both arguments should be Rational instances", span);
        }
        if (denominator.IsZero)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                $"Fraction({numerator.ToString(CultureInfo.InvariantCulture)}, 0)",
                span,
                "ZeroDivisionError"
            );
        }
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        if (denominator.Sign < 0)
        {
            gcd = -gcd;
        }
        return Create(cls, numerator / gcd, denominator / gcd);
    }

    private static bool HasAttribute(PythonValue value, string name)
    {
        try
        {
            _ = ManagedObjectProtocols.GetAttribute(value, name, default);
            return true;
        }
        catch (PythonRuntimeException exception)
            when (exception.PythonExceptionTypeName == "AttributeError")
        {
            return false;
        }
    }

    private static FractionState IntegerRatioOf(PythonValue value, TextSpan span)
    {
        var method = ManagedObjectProtocols.GetAttribute(value, "as_integer_ratio", span);
        var result = UserObjectProtocols.Dispatcher!.Invoke(method, [], span);
        var parts = ManagedObjectProtocols.MaterializeValues(result, span);
        if (parts.Count != 2)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                parts.Count < 2
                    ? $"not enough values to unpack (expected 2, got {parts.Count})"
                    : $"too many values to unpack (expected 2, got {parts.Count})",
                span,
                "ValueError"
            );
        }
        return new FractionState(RequireInteger(parts[0], span), RequireInteger(parts[1], span));
    }

    private static BigInteger RequireInteger(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => throw TypeFault(
                "'"
                    + ManagedObjectProtocols.GetTypeName(value)
                    + "' object cannot be interpreted as an integer",
                span
            ),
        };

    private readonly record struct Rational(BigInteger Numerator, BigInteger Denominator);

    /// <summary>The rational an integer or a Fraction is; null for anything else.</summary>
    private static Rational? RationalParts(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => new Rational(whole.Value, BigInteger.One),
            PythonTruthValue truth => new Rational(
                truth.Value ? BigInteger.One : BigInteger.Zero,
                BigInteger.One
            ),
            PythonManagedObjectValue { Payload: FractionState state } => new Rational(
                state.Numerator,
                state.Denominator
            ),
            _ => null,
        };

    private static PythonManagedObjectValue Create(
        PythonValue cls,
        BigInteger numerator,
        BigInteger denominator
    ) => new((PythonManagedTypeValue)cls, new FractionState(numerator, denominator));

    /// <summary>A value in lowest terms with a positive denominator, as the constructor builds it.</summary>
    private static PythonManagedObjectValue Normalized(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return Create(FractionType, numerator / gcd, denominator / gcd);
    }

    private static PythonManagedObjectValue Create(PythonValue cls, FractionState state) =>
        new((PythonManagedTypeValue)cls, state);

    private static PythonManagedTypeValue TypeOf(PythonValue self) =>
        self is PythonManagedObjectValue instance ? instance.Type : FractionType;

    private static PythonValue Copy(PythonValue self) =>
        ReferenceEquals(TypeOf(self), FractionType) ? self : Create(TypeOf(self), FractionOf(self));

    // -----------------------------------------------------------------------------
    // String parsing
    // -----------------------------------------------------------------------------

    private static (BigInteger Numerator, BigInteger Denominator) ParseFractionString(
        string text,
        TextSpan span
    )
    {
        var i = 0;
        var n = text.Length;
        while (i < n && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        var sign = false;
        if (i < n && text[i] is '-' or '+')
        {
            sign = text[i] == '-';
            i++;
        }
        if (
            !(
                (i < n && char.IsAsciiDigit(text[i]))
                || (i + 1 < n && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
            )
        )
        {
            throw InvalidLiteral(text, span);
        }
        var num = TakeDigits(text, ref i);
        BigInteger numerator = num.Length == 0 ? BigInteger.Zero : ParseDigits(num);
        var denominator = BigInteger.One;
        var matchedDenominator = false;
        var afterNumber = i;
        var j = i;
        while (j < n && char.IsWhiteSpace(text[j]))
        {
            j++;
        }
        if (j < n && text[j] == '/')
        {
            j++;
            while (j < n && char.IsWhiteSpace(text[j]))
            {
                j++;
            }
            var start = j;
            var denom = TakeDigits(text, ref j);
            if (denom.Length == 0 || !HasUnderscoreRule(denom))
            {
                throw InvalidLiteral(text, span);
            }
            _ = start;
            denominator = ParseDigits(denom);
            matchedDenominator = true;
            i = j;
        }
        if (!matchedDenominator)
        {
            i = afterNumber;
            if (i < n && text[i] == '.')
            {
                i++;
                var decimalDigits = TakeDigits(text, ref i);
                if (decimalDigits.Length != 0)
                {
                    var digits = decimalDigits.Replace("_", string.Empty, StringComparison.Ordinal);
                    var scale = BigInteger.Pow(10, digits.Length);
                    numerator = numerator * scale + ParseDigits(digits);
                    denominator *= scale;
                }
            }
            if (i < n && (text[i] == 'e' || text[i] == 'E'))
            {
                i++;
                var negativeExponent = false;
                if (i < n && text[i] is '-' or '+')
                {
                    negativeExponent = text[i] == '-';
                    i++;
                }
                var exp = TakeDigits(text, ref i);
                if (exp.Length == 0)
                {
                    throw InvalidLiteral(text, span);
                }
                var exponent = ParseDigits(exp);
                if (negativeExponent)
                {
                    exponent = -exponent;
                }
                if (exponent.Sign >= 0)
                {
                    numerator *= BigInteger.Pow(10, (int)exponent);
                }
                else
                {
                    denominator *= BigInteger.Pow(10, (int)(-exponent));
                }
            }
        }
        while (i < n && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        if (i != n)
        {
            throw InvalidLiteral(text, span);
        }
        if (sign)
        {
            numerator = -numerator;
        }
        return (numerator, denominator);
    }

    private static string TakeDigits(string text, ref int index)
    {
        var start = index;
        var n = text.Length;
        while (index < n)
        {
            if (char.IsAsciiDigit(text[index]))
            {
                index++;
            }
            else if (
                text[index] == '_'
                && index > start
                && index + 1 < n
                && char.IsAsciiDigit(text[index + 1])
            )
            {
                index++;
            }
            else
            {
                break;
            }
        }
        return text[start..index];
    }

    private static bool HasUnderscoreRule(string digits)
    {
        if (digits.StartsWith('_') || digits.EndsWith('_'))
        {
            return false;
        }
        return !digits.Contains("__", StringComparison.Ordinal);
    }

    private static BigInteger ParseDigits(string digits) =>
        BigInteger.Parse(
            digits.Replace("_", string.Empty, StringComparison.Ordinal),
            NumberStyles.None,
            CultureInfo.InvariantCulture
        );

    private static PythonRuntimeException InvalidLiteral(string text, TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4059",
            "Invalid literal for Fraction: " + new PythonTextValue(text).ToRepresentationString(),
            span,
            "ValueError"
        );

    // -----------------------------------------------------------------------------
    // Text forms
    // -----------------------------------------------------------------------------

    internal static string Represent(PythonValue self)
    {
        var state = FractionOf(self);
        return $"{ManagedObjectProtocols.GetTypeName(self)}({state.Numerator}, {state.Denominator})";
    }

    internal static string Str(PythonValue self)
    {
        var state = FractionOf(self);
        return state.Denominator.IsOne
            ? state.Numerator.ToString(CultureInfo.InvariantCulture)
            : $"{state.Numerator}/{state.Denominator}";
    }

    // -----------------------------------------------------------------------------
    // Arithmetic
    // -----------------------------------------------------------------------------

    private enum Arithmetic
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        FloorDivide,
        Modulo,
        DivMod,
    }

    private static void AddOperators(PythonManagedTypeValue type)
    {
        AddPair(type, "__add__", "__radd__", Arithmetic.Add);
        AddPair(type, "__sub__", "__rsub__", Arithmetic.Subtract);
        AddPair(type, "__mul__", "__rmul__", Arithmetic.Multiply);
        AddPair(type, "__truediv__", "__rtruediv__", Arithmetic.Divide);
        AddPair(type, "__floordiv__", "__rfloordiv__", Arithmetic.FloorDivide);
        AddPair(type, "__mod__", "__rmod__", Arithmetic.Modulo);
        AddPair(type, "__divmod__", "__rdivmod__", Arithmetic.DivMod);
        type.Attributes["__pow__"] = Method(
            "Fraction",
            "__pow__",
            (self, arguments) =>
                arguments.Count > 1 && arguments[1] is not PythonNoneValue
                    ? PythonNotImplementedValue.Instance
                    : Power(self!, arguments[0])
        );
        type.Attributes["__rpow__"] = Method(
            "Fraction",
            "__rpow__",
            (self, arguments) =>
                arguments.Count > 1 && arguments[1] is not PythonNoneValue
                    ? PythonNotImplementedValue.Instance
                    : ReversePower(self!, arguments[0])
        );
    }

    private static void AddPair(
        PythonManagedTypeValue type,
        string forward,
        string reverse,
        Arithmetic operation
    )
    {
        type.Attributes[forward] = Method(
            "Fraction",
            forward,
            (self, arguments) => Forward(self!, arguments[0], operation)
        );
        type.Attributes[reverse] = Method(
            "Fraction",
            reverse,
            (self, arguments) => Backward(self!, arguments[0], operation)
        );
    }

    private static PythonValue Forward(PythonValue self, PythonValue other, Arithmetic operation)
    {
        if (RationalParts(other) is { } rational)
        {
            var state = FractionOf(self);
            return Exact(new Rational(state.Numerator, state.Denominator), rational, operation);
        }
        if (other is PythonFloatingPointValue floating)
        {
            return FloatFallback(self, floating.Value, operation);
        }
        if (other is PythonComplexValue complex && SupportsComplex(operation))
        {
            return ComplexResult(Complex(self), complex.Value, operation);
        }
        return PythonNotImplementedValue.Instance;
    }

    private static PythonValue Backward(PythonValue self, PythonValue other, Arithmetic operation)
    {
        if (RationalParts(other) is { } rational)
        {
            var state = FractionOf(self);
            return Exact(rational, new Rational(state.Numerator, state.Denominator), operation);
        }
        if (other is PythonFloatingPointValue floating)
        {
            return FloatFallbackReverse(floating.Value, self, operation);
        }
        if (other is PythonComplexValue complex && SupportsComplex(operation))
        {
            return ComplexResult(complex.Value, Complex(self), operation);
        }
        return PythonNotImplementedValue.Instance;
    }

    private static bool SupportsComplex(Arithmetic operation) =>
        operation
            is Arithmetic.Add
                or Arithmetic.Subtract
                or Arithmetic.Multiply
                or Arithmetic.Divide;

    private static PythonValue Exact(Rational a, Rational b, Arithmetic operation)
    {
        var (na, da) = (a.Numerator, a.Denominator);
        var (nb, db) = (b.Numerator, b.Denominator);
        switch (operation)
        {
            case Arithmetic.Add:
            {
                var gcd = BigInteger.GreatestCommonDivisor(da, db);
                if (gcd.IsOne)
                {
                    return Create(FractionType, na * db + da * nb, da * db);
                }
                var s = da / gcd;
                var t = na * (db / gcd) + nb * s;
                var gcd2 = BigInteger.GreatestCommonDivisor(t, gcd);
                return gcd2.IsOne
                    ? Create(FractionType, t, s * db)
                    : Create(FractionType, t / gcd2, s * (db / gcd2));
            }
            case Arithmetic.Subtract:
            {
                var gcd = BigInteger.GreatestCommonDivisor(da, db);
                if (gcd.IsOne)
                {
                    return Create(FractionType, na * db - da * nb, da * db);
                }
                var s = da / gcd;
                var t = na * (db / gcd) - nb * s;
                var gcd2 = BigInteger.GreatestCommonDivisor(t, gcd);
                return gcd2.IsOne
                    ? Create(FractionType, t, s * db)
                    : Create(FractionType, t / gcd2, s * (db / gcd2));
            }
            case Arithmetic.Multiply:
            {
                var g1 = BigInteger.GreatestCommonDivisor(na, db);
                if (!g1.IsOne)
                {
                    na /= g1;
                    db /= g1;
                }
                var g2 = BigInteger.GreatestCommonDivisor(nb, da);
                if (!g2.IsOne)
                {
                    nb /= g2;
                    da /= g2;
                }
                return Create(FractionType, na * nb, db * da);
            }
            case Arithmetic.Divide:
            {
                if (nb.IsZero)
                {
                    throw ZeroFraction(db);
                }
                var g1 = BigInteger.GreatestCommonDivisor(na, nb);
                if (!g1.IsOne)
                {
                    na /= g1;
                    nb /= g1;
                }
                var g2 = BigInteger.GreatestCommonDivisor(db, da);
                if (!g2.IsOne)
                {
                    da /= g2;
                    db /= g2;
                }
                var n = na * db;
                var d = nb * da;
                if (d.Sign < 0)
                {
                    n = -n;
                    d = -d;
                }
                return Create(FractionType, n, d);
            }
            case Arithmetic.FloorDivide:
            {
                RequireNonZero(nb, da);
                return PythonWholeNumberValue.Create(FloorDiv(na * db, da * nb));
            }
            case Arithmetic.Modulo:
            {
                RequireNonZero(nb, da);
                return Normalized(FloorRem(na * db, da * nb), da * db);
            }
            default:
            {
                RequireNonZero(nb, da);
                var divisor = da * nb;
                var dividend = na * db;
                var quotient = FloorDiv(dividend, divisor);
                var remainder = FloorRem(dividend, divisor);
                return new PythonTupleValue([
                    PythonWholeNumberValue.Create(quotient),
                    Normalized(remainder, da * db),
                ]);
            }
        }
    }

    private static void RequireNonZero(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                "division by zero",
                default,
                "ZeroDivisionError"
            );
        }
        _ = denominator;
    }

    private static PythonRuntimeException ZeroFraction(BigInteger denominator) =>
        ManagedObjectProtocols.Fault(
            "DPY4059",
            $"Fraction({denominator}, 0)",
            default,
            "ZeroDivisionError"
        );

    private static double ToDouble(PythonValue self)
    {
        var state = FractionOf(self);
        return RatioToDouble(state.Numerator, state.Denominator);
    }

    private static System.Numerics.Complex Complex(PythonValue self) => new(ToDouble(self), 0.0);

    private static PythonValue FloatFallback(PythonValue self, double other, Arithmetic operation)
    {
        var value = ToDouble(self);
        return operation switch
        {
            Arithmetic.Add => new PythonFloatingPointValue(value + other),
            Arithmetic.Subtract => new PythonFloatingPointValue(value - other),
            Arithmetic.Multiply => new PythonFloatingPointValue(value * other),
            Arithmetic.Divide => new PythonFloatingPointValue(value / other),
            Arithmetic.FloorDivide => new PythonFloatingPointValue(FloatFloorDivide(value, other)),
            Arithmetic.Modulo => new PythonFloatingPointValue(FloatModulo(value, other)),
            _ => new PythonTupleValue([
                new PythonFloatingPointValue(FloatFloorDivide(value, other)),
                new PythonFloatingPointValue(FloatModulo(value, other)),
            ]),
        };
    }

    private static PythonValue FloatFallbackReverse(
        double other,
        PythonValue self,
        Arithmetic operation
    )
    {
        var value = ToDouble(self);
        return operation switch
        {
            Arithmetic.Add => new PythonFloatingPointValue(other + value),
            Arithmetic.Subtract => new PythonFloatingPointValue(other - value),
            Arithmetic.Multiply => new PythonFloatingPointValue(other * value),
            Arithmetic.Divide => new PythonFloatingPointValue(other / value),
            Arithmetic.FloorDivide => new PythonFloatingPointValue(FloatFloorDivide(other, value)),
            Arithmetic.Modulo => new PythonFloatingPointValue(FloatModulo(other, value)),
            _ => new PythonTupleValue([
                new PythonFloatingPointValue(FloatFloorDivide(other, value)),
                new PythonFloatingPointValue(FloatModulo(other, value)),
            ]),
        };
    }

    private static double FloatFloorDivide(double left, double right)
    {
        if (right == 0.0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                "division by zero",
                default,
                "ZeroDivisionError"
            );
        }
        return Math.Floor(left / right);
    }

    private static double FloatModulo(double left, double right)
    {
        if (right == 0.0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                "division by zero",
                default,
                "ZeroDivisionError"
            );
        }
        var quotient = Math.Floor(left / right);
        return left - quotient * right;
    }

    private static PythonComplexValue ComplexResult(
        System.Numerics.Complex left,
        System.Numerics.Complex right,
        Arithmetic operation
    ) =>
        new PythonComplexValue(
            operation switch
            {
                Arithmetic.Add => left + right,
                Arithmetic.Subtract => left - right,
                Arithmetic.Multiply => left * right,
                _ => left / right,
            }
        );

    private static PythonValue Power(PythonValue self, PythonValue exponent)
    {
        if (RationalParts(exponent) is { } rational)
        {
            if (rational.Denominator.IsOne)
            {
                var power = rational.Numerator;
                var state = FractionOf(self);
                if (power.Sign >= 0)
                {
                    return Create(
                        FractionType,
                        BigInteger.Pow(state.Numerator, (int)power),
                        BigInteger.Pow(state.Denominator, (int)power)
                    );
                }
                var positive = -power;
                if (state.Numerator.Sign > 0)
                {
                    return Create(
                        FractionType,
                        BigInteger.Pow(state.Denominator, (int)positive),
                        BigInteger.Pow(state.Numerator, (int)positive)
                    );
                }
                if (state.Numerator.IsZero)
                {
                    throw ZeroFraction(BigInteger.Pow(state.Denominator, (int)positive));
                }
                return Create(
                    FractionType,
                    BigInteger.Pow(-state.Denominator, (int)positive),
                    BigInteger.Pow(-state.Numerator, (int)positive)
                );
            }
            return FloatPower(
                ToDouble(self),
                ToDouble(Create(FractionType, rational.Numerator, rational.Denominator))
            );
        }
        if (exponent is PythonFloatingPointValue floating)
        {
            return FloatPower(ToDouble(self), floating.Value);
        }
        if (exponent is PythonComplexValue complex)
        {
            return new PythonComplexValue(
                System.Numerics.Complex.Pow(Complex(self), complex.Value)
            );
        }
        return PythonNotImplementedValue.Instance;
    }

    private static PythonValue ReversePower(PythonValue self, PythonValue value)
    {
        var state = FractionOf(self);
        if (state.Denominator.IsOne && state.Numerator.Sign >= 0)
        {
            return IntegerPower(value, state.Numerator);
        }
        if (RationalParts(value) is { } other)
        {
            return Power(Create(FractionType, other.Numerator, other.Denominator), self);
        }
        if (state.Denominator.IsOne)
        {
            return IntegerPower(value, state.Numerator);
        }
        if (value is PythonFloatingPointValue floating)
        {
            return FloatPower(floating.Value, ToDouble(self));
        }
        if (value is PythonComplexValue complex)
        {
            return new PythonComplexValue(
                System.Numerics.Complex.Pow(complex.Value, Complex(self))
            );
        }
        return FloatPower(ToDouble(value), ToDouble(self));
    }

    private static PythonValue IntegerPower(PythonValue value, BigInteger exponent)
    {
        if (value is PythonWholeNumberValue whole)
        {
            if (exponent.Sign >= 0)
            {
                return PythonWholeNumberValue.Create(BigInteger.Pow(whole.Value, (int)exponent));
            }
            if (whole.Value.IsZero)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4059",
                    "0.0 cannot be raised to a negative power",
                    default,
                    "ZeroDivisionError"
                );
            }
            return new PythonFloatingPointValue(
                1.0 / Math.Pow((double)whole.Value, (double)-exponent)
            );
        }
        if (value is PythonTruthValue truth)
        {
            return PythonWholeNumberValue.Create(truth.Value || exponent.IsZero ? 1 : 0);
        }
        if (value is PythonManagedObjectValue { Payload: FractionState })
        {
            return Power(value, PythonWholeNumberValue.Create(exponent));
        }
        if (value is PythonFloatingPointValue floating)
        {
            return new PythonFloatingPointValue(Math.Pow(floating.Value, (double)exponent));
        }
        if (value is PythonComplexValue complex)
        {
            return new PythonComplexValue(
                System.Numerics.Complex.Pow(complex.Value, (double)exponent)
            );
        }
        return PythonNotImplementedValue.Instance;
    }

    /// <summary>`float ** float`, with CPython's complex result for a negative base.</summary>
    private static PythonValue FloatPower(double value, double exponent)
    {
        if (value < 0 && exponent != Math.Floor(exponent))
        {
            return new PythonComplexValue(System.Numerics.Complex.Pow(value, exponent));
        }
        return new PythonFloatingPointValue(Math.Pow(value, exponent));
    }

    // -----------------------------------------------------------------------------
    // Comparisons
    // -----------------------------------------------------------------------------

    private static void AddComparisons(PythonManagedTypeValue type)
    {
        type.Attributes["__eq__"] = Method(
            "Fraction",
            "__eq__",
            (self, arguments) => EqualsValue(self!, arguments[0])
        );
        type.Attributes["__lt__"] = Method(
            "Fraction",
            "__lt__",
            (self, arguments) => RichCompare(self!, arguments[0], Comparison.Less)
        );
        type.Attributes["__le__"] = Method(
            "Fraction",
            "__le__",
            (self, arguments) => RichCompare(self!, arguments[0], Comparison.LessEqual)
        );
        type.Attributes["__gt__"] = Method(
            "Fraction",
            "__gt__",
            (self, arguments) => RichCompare(self!, arguments[0], Comparison.Greater)
        );
        type.Attributes["__ge__"] = Method(
            "Fraction",
            "__ge__",
            (self, arguments) => RichCompare(self!, arguments[0], Comparison.GreaterEqual)
        );
    }

    private enum Comparison
    {
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
    }

    private static PythonValue EqualsValue(PythonValue self, PythonValue other)
    {
        if (RationalParts(other) is { } rational)
        {
            var state = FractionOf(self);
            return PythonTruthValue.FromBoolean(
                state.Numerator == rational.Numerator && state.Denominator == rational.Denominator
            );
        }
        if (other is PythonComplexValue complex)
        {
            if (complex.Value.Imaginary != 0.0)
            {
                return PythonNotImplementedValue.Instance;
            }
            other = new PythonFloatingPointValue(complex.Value.Real);
        }
        if (other is PythonFloatingPointValue floating)
        {
            if (double.IsNaN(floating.Value) || double.IsInfinity(floating.Value))
            {
                return PythonTruthValue.FromBoolean(0.0 == floating.Value);
            }
            var converted = FractionOf(FloatFraction(floating.Value));
            var state = FractionOf(self);
            return PythonTruthValue.FromBoolean(
                state.Numerator == converted.Numerator && state.Denominator == converted.Denominator
            );
        }
        return PythonNotImplementedValue.Instance;
    }

    private static PythonManagedObjectValue FloatFraction(double value)
    {
        var (numerator, denominator) = RatioOfDouble(value);
        return Create(FractionType, numerator, denominator);
    }

    private static PythonValue RichCompare(
        PythonValue self,
        PythonValue other,
        Comparison comparison
    )
    {
        var state = FractionOf(self);
        if (RationalParts(other) is { } rational)
        {
            return PythonTruthValue.FromBoolean(
                Compare(
                    state.Numerator * rational.Denominator,
                    state.Denominator * rational.Numerator,
                    comparison
                )
            );
        }
        if (other is PythonFloatingPointValue floating)
        {
            if (double.IsNaN(floating.Value) || double.IsInfinity(floating.Value))
            {
                return PythonTruthValue.FromBoolean(Compare(0.0, floating.Value, comparison));
            }
            var converted = FractionOf(FloatFraction(floating.Value));
            return PythonTruthValue.FromBoolean(
                Compare(
                    state.Numerator * converted.Denominator,
                    state.Denominator * converted.Numerator,
                    comparison
                )
            );
        }
        return PythonNotImplementedValue.Instance;
    }

    private static bool Compare(double left, double right, Comparison comparison) =>
        comparison switch
        {
            Comparison.Less => left < right,
            Comparison.LessEqual => left <= right,
            Comparison.Greater => left > right,
            _ => left >= right,
        };

    private static bool Compare(BigInteger left, BigInteger right, Comparison comparison) =>
        comparison switch
        {
            Comparison.Less => left < right,
            Comparison.LessEqual => left <= right,
            Comparison.Greater => left > right,
            _ => left >= right,
        };

    // -----------------------------------------------------------------------------
    // Conversion and hashing
    // -----------------------------------------------------------------------------

    internal static BigInteger Truncate(BigInteger numerator, BigInteger denominator) =>
        numerator.Sign < 0 ? -((-numerator) / denominator) : numerator / denominator;

    internal static BigInteger FloorDiv(BigInteger left, BigInteger right)
    {
        var quotient = BigInteger.DivRem(left, right, out var remainder);
        if (remainder.Sign != 0 && (remainder.Sign < 0) != (right.Sign < 0))
        {
            quotient -= 1;
        }
        return quotient;
    }

    private static BigInteger FloorRem(BigInteger left, BigInteger right) =>
        left - (FloorDiv(left, right) * right);

    private static PythonValue Round(
        FractionState state,
        PythonValue? digitsValue,
        PythonValue self
    )
    {
        if (digitsValue is null or PythonNoneValue)
        {
            return PythonWholeNumberValue.Create(RoundHalfEven(state));
        }
        var digits = RequireInteger(digitsValue, default);
        if (digits > 10_000)
        {
            return self;
        }
        if (digits < -10_000)
        {
            return PythonWholeNumberValue.Create(0);
        }
        var shift = BigInteger.Pow(10, (int)BigInteger.Abs(digits));
        if (digits.Sign > 0)
        {
            return Normalized(
                RoundHalfEven(new FractionState(state.Numerator * shift, state.Denominator)),
                shift
            );
        }
        return Normalized(
            RoundHalfEven(new FractionState(state.Numerator, state.Denominator * shift)) * shift,
            BigInteger.One
        );
    }

    /// <summary>`round()` on a rational: the nearest integer, ties to even.</summary>
    private static BigInteger RoundHalfEven(FractionState state)
    {
        var floor = FloorDiv(state.Numerator, state.Denominator);
        var remainder = FloorRem(state.Numerator, state.Denominator);
        var doubled = remainder * Two;
        if (doubled < state.Denominator)
        {
            return floor;
        }
        if (doubled > state.Denominator)
        {
            return floor + 1;
        }
        return floor.IsEven ? floor : floor + 1;
    }

    private static PythonManagedObjectValue LimitDenominator(PythonValue self, PythonValue maxValue)
    {
        var state = FractionOf(self);
        if (!TryNumeric(maxValue, out var max))
        {
            throw TypeFault(
                "'"
                    + ManagedObjectProtocols.GetTypeName(maxValue)
                    + "' object cannot be interpreted as an integer"
            );
        }
        if (max < 1.0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                "max_denominator should be at least 1",
                default,
                "ValueError"
            );
        }
        if ((double)state.Denominator <= max)
        {
            return Create(FractionType, state);
        }
        var maxDenominator = new BigInteger(Math.Floor(max));
        BigInteger p0 = 0,
            q0 = 1,
            p1 = 1,
            q1 = 0;
        var n = state.Numerator;
        var d = state.Denominator;
        while (true)
        {
            var a = FloorDiv(n, d);
            var q2 = q0 + (a * q1);
            if (q2 > maxDenominator)
            {
                break;
            }
            (p0, q0, p1, q1) = (p1, q1, p0 + (a * p1), q2);
            (n, d) = (d, n - (a * d));
        }
        var k = (maxDenominator - q0) / q1;
        if (2 * d * (q0 + (k * q1)) <= state.Denominator)
        {
            return Create(FractionType, p1, q1);
        }
        return Create(FractionType, p0 + (k * p1), q0 + (k * q1));
    }

    private static bool TryNumeric(PythonValue value, out double result)
    {
        switch (value)
        {
            case PythonWholeNumberValue whole:
                result = (double)whole.Value;
                return true;
            case PythonTruthValue truth:
                result = truth.Value ? 1.0 : 0.0;
                return true;
            case PythonFloatingPointValue floating:
                result = floating.Value;
                return true;
            default:
                result = 0.0;
                return false;
        }
    }

    private static PythonManagedObjectValue FromFloat(PythonValue cls, PythonValue value)
    {
        if (RationalParts(value) is { } rational && value is not PythonManagedObjectValue)
        {
            return Create(cls, rational.Numerator, rational.Denominator);
        }
        if (value is not PythonFloatingPointValue floating)
        {
            throw TypeFault(
                $"{ClassOfValue(cls).Name}.from_float() only takes floats, not "
                    + value.ToRepresentationString()
                    + $" ({ManagedObjectProtocols.GetTypeName(value)})"
            );
        }
        var (numerator, denominator) = RatioOfDouble(floating.Value);
        return Create(cls, numerator, denominator);
    }

    private static PythonManagedObjectValue FromNumber(PythonValue cls, PythonValue value)
    {
        if (value is PythonWholeNumberValue whole)
        {
            return Create(cls, whole.Value, BigInteger.One);
        }
        if (RationalParts(value) is { } rational)
        {
            return Create(cls, rational.Numerator, rational.Denominator);
        }
        if (
            value is PythonFloatingPointValue
            || (value is not PythonManagedTypeValue && HasAttribute(value, "as_integer_ratio"))
        )
        {
            var ratio = IntegerRatioOf(value, default);
            return Create(cls, ratio.Numerator, ratio.Denominator);
        }
        throw TypeFault(
            "argument should be a Rational instance or have the as_integer_ratio() method"
        );
    }

    private static PythonManagedObjectValue FromDecimal(
        PythonValue cls,
        PythonValue value,
        TextSpan span
    )
    {
        var decimalModule = UserObjectProtocols.Dispatcher!.ImportModule(
            "decimal",
            string.Empty,
            span
        );
        var decimalType = ManagedObjectProtocols.GetAttribute(decimalModule, "Decimal", span);
        PythonValue decimalValue;
        if (RationalParts(value) is { } rational)
        {
            decimalValue = UserObjectProtocols.Dispatcher!.Invoke(
                decimalType,
                [PythonWholeNumberValue.Create(rational.Numerator / rational.Denominator)],
                span
            );
        }
        else if (
            value is PythonManagedObjectValue instance
            && ReferenceEquals(instance.Type, decimalType)
        )
        {
            decimalValue = value;
        }
        else
        {
            throw TypeFault(
                $"{ClassOfValue(cls).Name}.from_decimal() only takes Decimals, not "
                    + value.ToRepresentationString()
                    + $" ({ManagedObjectProtocols.GetTypeName(value)})"
            );
        }
        var ratio = IntegerRatioOf(decimalValue, span);
        return Create(cls, ratio.Numerator, ratio.Denominator);
    }

    private static PythonManagedTypeValue ClassOfValue(PythonValue cls) =>
        cls as PythonManagedTypeValue ?? throw TypeFault("the class is not a class");

    internal static long Hash(BigInteger numerator, BigInteger denominator)
    {
        BigInteger hash;
        if (!TryModularInverse(denominator, HashModulus, out var inverse))
        {
            hash = HashInfinity;
        }
        else
        {
            var inner = PythonNumericHash.Integer(BigInteger.Abs(numerator));
            hash = PythonNumericHash.Integer(inner * inverse);
        }
        var result = numerator.Sign < 0 ? -hash : hash;
        if (result == -1)
        {
            result = -2;
        }
        return (long)result;
    }

    private static bool TryModularInverse(
        BigInteger value,
        BigInteger modulus,
        out BigInteger inverse
    )
    {
        BigInteger oldR = ((value % modulus) + modulus) % modulus,
            r = modulus;
        BigInteger oldS = 1,
            s = 0;
        while (!r.IsZero)
        {
            var quotient = oldR / r;
            (oldR, r) = (r, oldR - (quotient * r));
            (oldS, s) = (s, oldS - (quotient * s));
        }
        if (!oldR.IsOne)
        {
            inverse = BigInteger.Zero;
            return false;
        }
        inverse = ((oldS % modulus) + modulus) % modulus;
        return true;
    }

    internal static double RatioToDouble(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero)
        {
            return 0.0;
        }
        var negative = numerator.Sign * denominator.Sign < 0;
        var top = BigInteger.Abs(numerator);
        var bottom = BigInteger.Abs(denominator);
        var shift = 54 - (int)(top.GetBitLength() - bottom.GetBitLength());
        if (shift > 0)
        {
            top <<= shift;
        }
        else
        {
            bottom <<= -shift;
        }
        var quotient = BigInteger.DivRem(top, bottom, out var remainder);
        var drop = (int)quotient.GetBitLength() - 53;
        double result;
        if (drop <= 0)
        {
            result = Math.ScaleB((double)quotient, -shift);
        }
        else
        {
            var high = quotient >> drop;
            var low = quotient - (high << drop);
            var half = BigInteger.One << (drop - 1);
            var roundUp = low > half || (low == half && (!remainder.IsZero || !high.IsEven));
            if (roundUp)
            {
                high += 1;
            }
            result = Math.ScaleB((double)high, drop - shift);
        }
        if (double.IsInfinity(result))
        {
            // Fraction.__float__ is an integer true division; an unrepresentable
            // magnitude is CPython's overflow, not an infinity.
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                "integer division result too large for a float",
                default,
                "OverflowError"
            );
        }
        return negative ? -result : result;
    }

    private static (BigInteger Numerator, BigInteger Denominator) RatioOfDouble(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var negative = (bits >> 63) != 0;
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        var significand = bits & ((1UL << 52) - 1);
        if (exponentBits == 0x7ff)
        {
            if (significand != 0)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4059",
                    "cannot convert NaN to integer ratio",
                    default,
                    "ValueError"
                );
            }
            throw ManagedObjectProtocols.Fault(
                "DPY4059",
                "cannot convert Infinity to integer ratio",
                default,
                "OverflowError"
            );
        }
        BigInteger numerator;
        BigInteger denominator;
        if (exponentBits == 0)
        {
            numerator = significand;
            denominator = BigInteger.One << 1074;
        }
        else
        {
            numerator = significand | (1UL << 52);
            var exponent = exponentBits - 1023 - 52;
            if (exponent >= 0)
            {
                numerator <<= exponent;
                denominator = BigInteger.One;
            }
            else
            {
                denominator = BigInteger.One << -exponent;
            }
        }
        if (negative)
        {
            numerator = -numerator;
        }
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return (numerator / gcd, denominator / gcd);
    }

    // -----------------------------------------------------------------------------
    // Formatting
    // -----------------------------------------------------------------------------

    private static PythonTextValue Format(PythonValue self, PythonValue specValue)
    {
        if (specValue is not PythonTextValue spec)
        {
            throw TypeFault(
                "__format__() argument must be str, not "
                    + ManagedObjectProtocols.GetTypeName(specValue)
            );
        }
        if (TryParseGeneralFormat(spec.Value, out var general))
        {
            return new PythonTextValue(FormatGeneral(self, general));
        }
        if (TryParseFloatFormat(spec.Value, out var floatSpec))
        {
            if (floatSpec.ExplicitAlign is null || floatSpec.HasZeroPad is null)
            {
                return new PythonTextValue(FormatFloatStyle(self, floatSpec));
            }
        }
        throw ManagedObjectProtocols.Fault(
            "DPY4059",
            "Invalid format specifier "
                + spec.ToRepresentationString()
                + " for object of type "
                + new PythonTextValue(
                    ManagedObjectProtocols.GetTypeName(self)
                ).ToRepresentationString(),
            default,
            "ValueError"
        );
    }

    private sealed record GeneralFormat(
        char Fill,
        char Align,
        char Sign,
        bool Alternate,
        int MinimumWidth,
        char? ThousandsSeparator
    );

    private static bool TryParseGeneralFormat(string spec, out GeneralFormat format)
    {
        var i = 0;
        var n = spec.Length;
        var fill = ' ';
        var align = '>';
        if (i + 1 < n && spec[i + 1] is '<' or '=' or '>' or '^')
        {
            fill = spec[i];
            align = spec[i + 1];
            i += 2;
        }
        else if (i < n && spec[i] is '<' or '=' or '>' or '^')
        {
            align = spec[i];
            i++;
        }
        var sign = '-';
        if (i < n && spec[i] is '-' or '+' or ' ')
        {
            sign = spec[i];
            i++;
        }
        var alternate = false;
        if (i < n && spec[i] == '#')
        {
            alternate = true;
            i++;
        }
        var widthStart = i;
        if (i < n && spec[i] == '0')
        {
            i++;
        }
        else
        {
            while (i < n && char.IsAsciiDigit(spec[i]))
            {
                i++;
            }
        }
        var minimumWidth = 0;
        if (i > widthStart)
        {
            minimumWidth = int.Parse(
                spec[widthStart..i],
                NumberStyles.None,
                CultureInfo.InvariantCulture
            );
        }
        char? separator = null;
        if (i < n && spec[i] is ',' or '_')
        {
            separator = spec[i];
            i++;
        }
        format = new GeneralFormat(fill, align, sign, alternate, minimumWidth, separator);
        return i == n;
    }

    private static string FormatGeneral(PythonValue self, GeneralFormat format)
    {
        var state = FractionOf(self);
        var sign =
            state.Numerator.Sign < 0 ? "-"
            : format.Sign == '-' ? ""
            : format.Sign.ToString();
        var separator = format.ThousandsSeparator?.ToString() ?? string.Empty;
        var body =
            state.Denominator > 1 || format.Alternate
                ? GroupDigits(BigInteger.Abs(state.Numerator), separator)
                    + "/"
                    + GroupDigits(state.Denominator, separator)
                : GroupDigits(BigInteger.Abs(state.Numerator), separator);
        var padding = Math.Max(0, format.MinimumWidth - sign.Length - body.Length);
        var fill = new string(format.Fill, padding);
        return format.Align switch
        {
            '<' => sign + body + fill,
            '^' => fill[..(padding / 2)] + sign + body + fill[(padding / 2)..],
            '=' => sign + fill + body,
            _ => fill + sign + body,
        };
    }

    private static string SeparateDigits(string digits, char separator)
    {
        if (digits.Length <= 3)
        {
            return digits;
        }
        var first = 1 + ((digits.Length - 1) % 3);
        var builder = new StringBuilder(digits.Length + (digits.Length / 3));
        builder.Append(digits[..first]);
        for (var index = first; index < digits.Length; index += 3)
        {
            builder.Append(separator).Append(digits, index, 3);
        }
        return builder.ToString();
    }

    private static string GroupDigits(BigInteger value, string separator)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture);
        if (separator.Length == 0 || digits.Length <= 3)
        {
            return digits;
        }
        var first = 1 + ((digits.Length - 1) % 3);
        var builder = new StringBuilder(digits.Length + (digits.Length / 3));
        builder.Append(digits[..first]);
        for (var index = first; index < digits.Length; index += 3)
        {
            builder.Append(separator).Append(digits, index, 3);
        }
        return builder.ToString();
    }

    private sealed record FloatFormat(
        char Fill,
        char? ExplicitAlign,
        char Align,
        char Sign,
        bool NoNegativeZero,
        bool Alternate,
        bool? HasZeroPad,
        int MinimumWidth,
        char? ThousandsSeparator,
        int? Precision,
        char? FractionSeparator,
        char Presentation
    );

    private static bool TryParseFloatFormat(string spec, out FloatFormat format)
    {
        var i = 0;
        var n = spec.Length;
        var fill = ' ';
        var align = '>';
        char? explicitAlign = null;
        if (i + 1 < n && spec[i + 1] is '<' or '=' or '>' or '^')
        {
            fill = spec[i];
            explicitAlign = spec[i + 1];
            align = spec[i + 1];
            i += 2;
        }
        else if (i < n && spec[i] is '<' or '=' or '>' or '^')
        {
            explicitAlign = spec[i];
            align = spec[i];
            i++;
        }
        var sign = '-';
        if (i < n && spec[i] is '-' or '+' or ' ')
        {
            sign = spec[i];
            i++;
        }
        var noNegativeZero = false;
        if (i < n && spec[i] == 'z')
        {
            noNegativeZero = true;
            i++;
        }
        var alternate = false;
        if (i < n && spec[i] == '#')
        {
            alternate = true;
            i++;
        }
        bool? hasZeroPad = null;
        if (i + 1 < n && spec[i] == '0' && char.IsAsciiDigit(spec[i + 1]))
        {
            hasZeroPad = true;
            i++;
        }
        var widthStart = i;
        while (i < n && char.IsAsciiDigit(spec[i]))
        {
            i++;
        }
        var minimumWidth = 0;
        if (i > widthStart)
        {
            minimumWidth = int.Parse(
                spec[widthStart..i],
                NumberStyles.None,
                CultureInfo.InvariantCulture
            );
        }
        char? thousandsSeparator = null;
        if (i < n && spec[i] is ',' or '_')
        {
            thousandsSeparator = spec[i];
            i++;
        }
        int? precision = null;
        char? fractionSeparator = null;
        if (i < n && spec[i] == '.')
        {
            var afterPoint = i + 1;
            if (
                afterPoint >= n
                || (spec[afterPoint] is not (',' or '_') && !char.IsAsciiDigit(spec[afterPoint]))
            )
            {
                format = null!;
                return false;
            }
            i = afterPoint;
            var precisionStart = i;
            while (i < n && char.IsAsciiDigit(spec[i]))
            {
                i++;
            }
            precision =
                i > precisionStart
                    ? int.Parse(
                        spec[precisionStart..i],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture
                    )
                    : 0;
            if (i < n && spec[i] is ',' or '_')
            {
                fractionSeparator = spec[i];
                i++;
            }
        }
        if (i >= n || spec[i] is not ('e' or 'E' or 'f' or 'F' or 'g' or 'G' or '%'))
        {
            format = null!;
            return false;
        }
        var presentation = spec[i];
        i++;
        if (i != n)
        {
            format = null!;
            return false;
        }
        format = new FloatFormat(
            fill,
            explicitAlign,
            align,
            sign,
            noNegativeZero,
            alternate,
            hasZeroPad,
            minimumWidth,
            thousandsSeparator,
            precision,
            fractionSeparator,
            presentation
        );
        return true;
    }

    private static string FormatFloatStyle(PythonValue self, FloatFormat spec)
    {
        var state = FractionOf(self);
        var align = spec.Align;
        var zeroPad = spec.HasZeroPad == true;
        var signPrefix = spec.Sign == '-' ? string.Empty : spec.Sign.ToString();
        if (align == '=' && spec.Fill == '0')
        {
            zeroPad = true;
        }
        var presentation = spec.Presentation;
        var precision = spec.Precision ?? 6;
        var trimZeros = presentation is 'g' or 'G' && !spec.Alternate;
        var trimPoint = !spec.Alternate;
        var exponentIndicator = presentation is 'E' or 'F' or 'G' ? 'E' : 'e';
        int pointPosition;
        BigInteger significand;
        bool scientific;
        bool negative;
        var exponent = 0;
        if (presentation is 'f' or 'F' or '%')
        {
            var roundExponent = -precision;
            if (presentation == '%')
            {
                roundExponent -= 2;
            }
            (negative, significand) = RoundToExponent(
                state.Numerator,
                state.Denominator,
                roundExponent,
                spec.NoNegativeZero
            );
            scientific = false;
            pointPosition = precision;
        }
        else
        {
            var figures = presentation is 'g' or 'G' ? Math.Max(precision, 1) : precision + 1;
            (negative, significand, exponent) = RoundToFigures(
                state.Numerator,
                state.Denominator,
                figures
            );
            scientific = presentation is 'e' or 'E' || exponent > 0 || exponent + figures <= -4;
            pointPosition = scientific ? figures - 1 : -exponent;
        }
        string suffix;
        if (presentation == '%')
        {
            suffix = "%";
        }
        else if (scientific)
        {
            suffix = exponentIndicator + FormatExponent(exponent + pointPosition);
        }
        else
        {
            suffix = string.Empty;
        }
        var digits = significand.ToString(CultureInfo.InvariantCulture);
        if (digits.Length < pointPosition + 1)
        {
            digits = new string('0', pointPosition + 1 - digits.Length) + digits;
        }
        var sign = negative ? "-" : signPrefix;
        var leading = digits[..(digits.Length - pointPosition)];
        var fraction = digits[(digits.Length - pointPosition)..];
        if (trimZeros)
        {
            fraction = fraction.TrimEnd('0');
        }
        var separator = trimPoint && fraction.Length == 0 ? string.Empty : ".";
        if (spec.FractionSeparator is { } fractionSeparator && fraction.Length != 0)
        {
            var pieces = new List<string>();
            for (var index = 0; index < fraction.Length; index += 3)
            {
                pieces.Add(fraction.Substring(index, Math.Min(3, fraction.Length - index)));
            }
            fraction = string.Join(fractionSeparator.ToString(), pieces);
        }
        var trailing = separator + fraction + suffix;
        if (zeroPad)
        {
            var minimumLeading = spec.MinimumWidth - sign.Length - trailing.Length;
            if (minimumLeading > 0)
            {
                var target = spec.ThousandsSeparator is not null
                    ? ((3 * minimumLeading) / 4) + 1
                    : minimumLeading;
                leading = leading.PadLeft(target, '0');
            }
        }
        if (spec.ThousandsSeparator is { } thousandsSeparator)
        {
            leading = SeparateDigits(leading, thousandsSeparator);
        }
        var body = leading + trailing;
        var padding = Math.Max(0, spec.MinimumWidth - sign.Length - body.Length);
        var fill = new string(spec.Fill, padding);
        return align switch
        {
            '<' => sign + body + fill,
            '^' => fill[..(padding / 2)] + sign + body + fill[(padding / 2)..],
            '=' => sign + fill + body,
            _ => fill + sign + body,
        };
    }

    private static string FormatExponent(int exponent)
    {
        var magnitude = Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        return (exponent < 0 ? "-" : "+") + magnitude;
    }

    private static (bool Negative, BigInteger Significand) RoundToExponent(
        BigInteger numerator,
        BigInteger denominator,
        int exponent,
        bool noNegativeZero
    )
    {
        if (exponent >= 0)
        {
            denominator *= BigInteger.Pow(10, exponent);
        }
        else
        {
            numerator *= BigInteger.Pow(10, -exponent);
        }
        var adjusted = numerator + (denominator >> 1);
        var quotient = FloorDiv(adjusted, denominator);
        var remainder = FloorRem(adjusted, denominator);
        if (remainder.IsZero && denominator.IsEven)
        {
            quotient &= -2;
        }
        var negative = noNegativeZero ? quotient.Sign < 0 : numerator.Sign < 0;
        return (negative, BigInteger.Abs(quotient));
    }

    private static (bool Negative, BigInteger Significand, int Exponent) RoundToFigures(
        BigInteger numerator,
        BigInteger denominator,
        int figures
    )
    {
        if (numerator.IsZero)
        {
            return (false, BigInteger.Zero, 1 - figures);
        }
        var numeratorText = BigInteger.Abs(numerator).ToString(CultureInfo.InvariantCulture);
        var denominatorText = denominator.ToString(CultureInfo.InvariantCulture);
        var magnitude =
            numeratorText.Length
            - denominatorText.Length
            + (string.CompareOrdinal(denominatorText, numeratorText) <= 0 ? 1 : 0);
        var exponent = magnitude - figures;
        var (negative, significand) = RoundToExponent(
            numerator,
            denominator,
            exponent,
            noNegativeZero: false
        );
        if (significand.ToString(CultureInfo.InvariantCulture).Length == figures + 1)
        {
            significand /= 10;
            exponent += 1;
        }
        return (negative, significand, exponent);
    }

    // -----------------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------------

    private static PythonTextValue Text(string value) => new(value);

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    ) => PythonUserTypes.Method(owner, name, body, null, parameters, defaults);

    private static PythonClassMethodValue ClassMethod(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    ) => new(PythonUserTypes.Method(owner, name, body, null, parameters, defaults));

    private static PythonRuntimeException TypeFault(string message, TextSpan span = default) =>
        ManagedObjectProtocols.Fault("DPY4059", message, span, "TypeError");
}
