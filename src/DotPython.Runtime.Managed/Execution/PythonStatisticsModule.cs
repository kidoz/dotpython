using System.Globalization;
using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `statistics` module over exact rational arithmetic: every sum and sum-of-squares
/// is accumulated as a fraction of the values' exact binary expansions, as CPython's
/// `Fraction`-based helpers do, and converted to a float once at the end. `NormalDist`
/// carries Wichura's AS241 inverse normal (the same rational approximation CPython uses),
/// the runtime's own `erf`/`erfc`, and Wichura's constants. Decimal and Fraction inputs
/// are the slice's gap — the runtime does not publish those modules — and so is the
/// module's kernel density estimation (`kde`, `kde_random`).
/// </summary>
internal static class PythonStatisticsModule
{
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("StatisticsError", StatisticsErrorType);
        globals.SetValue("NormalDist", NormalDistType);
        globals.SetValue("LinearRegression", LinearRegressionType);
        globals.SetValue("mean", Function("mean", ["data"], Mean));
        globals.SetValue("fmean", Function("fmean", ["data", "weights"], FMean));
        globals.SetValue("geometric_mean", Function("geometric_mean", ["data"], GeometricMean));
        globals.SetValue(
            "harmonic_mean",
            Function("harmonic_mean", ["data", "weights"], HarmonicMean)
        );
        globals.SetValue("median", Function("median", ["data"], Median));
        globals.SetValue("median_low", Function("median_low", ["data"], MedianLow));
        globals.SetValue("median_high", Function("median_high", ["data"], MedianHigh));
        globals.SetValue(
            "median_grouped",
            Function("median_grouped", ["data", "interval"], MedianGrouped)
        );
        globals.SetValue("mode", Function("mode", ["data"], Mode));
        globals.SetValue("multimode", Function("multimode", ["data"], MultiMode));
        globals.SetValue("variance", Function("variance", ["data", "xbar"], Variance));
        globals.SetValue("pvariance", Function("pvariance", ["data", "mu"], PVariance));
        globals.SetValue("stdev", Function("stdev", ["data", "xbar"], Stdev));
        globals.SetValue("pstdev", Function("pstdev", ["data", "mu"], PStdev));
        globals.SetValue("covariance", Function("covariance", ["x", "y"], Covariance));
        globals.SetValue(
            "correlation",
            KeywordFunction(
                "correlation",
                2,
                Correlation,
                ["method"],
                [new PythonTextValue("linear")]
            )
        );
        globals.SetValue(
            "linear_regression",
            KeywordFunction(
                "linear_regression",
                2,
                LinearRegression,
                ["proportional"],
                [PythonTruthValue.False]
            )
        );
        globals.SetValue(
            "quantiles",
            KeywordFunction(
                "quantiles",
                1,
                Quantiles,
                ["n", "method"],
                [PythonWholeNumberValue.Create(4), new PythonTextValue("exclusive")]
            )
        );
        // The 3.13+ kernel density estimation is the slice's gap: the names exist, so
        // star-import and `__all__` stay whole, and the call says it is not implemented.
        var unimplemented = new PythonBuiltinFunctionValue(
            "kde",
            (_, _) =>
                throw ManagedObjectProtocols.Fault(
                    "DPY4055",
                    "kernel density estimation is not implemented in this runtime",
                    default,
                    "NotImplementedError"
                )
        );
        globals.SetValue("kde", unimplemented);
        globals.SetValue(
            "kde_random",
            new PythonBuiltinFunctionValue(
                "kde_random",
                (_, _) =>
                    throw ManagedObjectProtocols.Fault(
                        "DPY4055",
                        "kernel density estimation is not implemented in this runtime",
                        default,
                        "NotImplementedError"
                    )
            )
        );
        globals.SetValue(
            "__all__",
            new PythonListValue([
                new PythonTextValue("NormalDist"),
                new PythonTextValue("StatisticsError"),
                new PythonTextValue("correlation"),
                new PythonTextValue("covariance"),
                new PythonTextValue("fmean"),
                new PythonTextValue("geometric_mean"),
                new PythonTextValue("harmonic_mean"),
                new PythonTextValue("kde"),
                new PythonTextValue("kde_random"),
                new PythonTextValue("linear_regression"),
                new PythonTextValue("mean"),
                new PythonTextValue("median"),
                new PythonTextValue("median_grouped"),
                new PythonTextValue("median_high"),
                new PythonTextValue("median_low"),
                new PythonTextValue("mode"),
                new PythonTextValue("multimode"),
                new PythonTextValue("pstdev"),
                new PythonTextValue("pvariance"),
                new PythonTextValue("quantiles"),
                new PythonTextValue("stdev"),
                new PythonTextValue("variance"),
            ])
        );
    }

    private static readonly PythonManagedTypeValue StatisticsErrorType =
        CreateStatisticsErrorType();

    private static readonly PythonManagedTypeValue NormalDistType = CreateNormalDistType();

    private static readonly PythonManagedTypeValue LinearRegressionType =
        CreateLinearRegressionType();

    /// <summary>`statistics.StatisticsError`, a `ValueError`.</summary>
    private static PythonManagedTypeValue CreateStatisticsErrorType()
    {
        var exceptionBase = PythonBuiltinTypes.GetExceptionType("ValueError");
        var type = new PythonManagedTypeValue("StatisticsError", exceptionBaseName: "ValueError")
        {
            Module = "statistics",
            LayoutBase = exceptionBase,
        };
        type.Attributes["__module__"] = new PythonTextValue("statistics");
        type.Attributes["__qualname__"] = new PythonTextValue("StatisticsError");
        type.Attributes["__doc__"] = new PythonTextValue("Error for statistics functions.");
        type.SetDeclaredBases(new PythonTupleValue([exceptionBase]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, .. PythonBuiltinTypes.GetMro(exceptionBase).Elements])
        );
        return type;
    }

    /// <summary>`LinearRegression`, the named tuple `linear_regression` answers.</summary>
    private static PythonManagedTypeValue CreateLinearRegressionType()
    {
        var factory = (PythonBuiltinFunctionValue)PythonNamedTuple.Function;
        var created = factory.Invoke(
            [new PythonTextValue("LinearRegression"), new PythonTextValue("slope intercept")],
            default
        );
        if (created is PythonManagedTypeValue type)
        {
            type.Module = "statistics";
            type.Attributes["__module__"] = new PythonTextValue("statistics");
            return type;
        }
        throw TypeFault("the LinearRegression type could not be built");
    }

    // -----------------------------------------------------------------------------
    // Wording and arguments
    // -----------------------------------------------------------------------------

    private static PythonRaisedException Error(string message, TextSpan span = default)
    {
        _ = span;
        return new PythonRaisedException(
            new PythonExceptionValue("StatisticsError", message)
            {
                ManagedType = StatisticsErrorType,
            }
        );
    }

    /// <summary>The error CPython raises with more than one argument: `(message, value)`.</summary>
    private static PythonRaisedException ErrorWithArgs(
        string message,
        PythonValue value,
        TextSpan span
    )
    {
        var raised = UserObjectProtocols.Dispatcher!.Invoke(
            StatisticsErrorType,
            [new PythonTextValue(message), value],
            span
        );
        return raised is PythonExceptionValue exception
            ? new PythonRaisedException(exception)
            : throw TypeFault("the error could not be raised", span);
    }

    private static PythonRuntimeException TypeFault(string message, TextSpan span = default) =>
        ManagedObjectProtocols.Fault("DPY4055", message, span, "TypeError");

    private static PythonRuntimeException ValueFault(string message, TextSpan span = default) =>
        ManagedObjectProtocols.Fault("DPY4055", message, span, "ValueError");

    /// <summary>A module function whose arguments bind by name onto the positional form.</summary>
    private static PythonBuiltinFunctionValue Function(
        string name,
        string[] parameters,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body
    ) =>
        new PythonBuiltinFunctionValue(name, body).WithSignature(
            parameters,
            [.. parameters.Select((_, index) => index == 0 ? null : PythonNoneValue.Instance)]
        );

    /// <summary>
    /// A module function with keyword-only parameters after its positional ones. The
    /// keyword names the call may use are fixed; the body sees them in that order.
    /// </summary>
    private static PythonBuiltinFunctionValue KeywordFunction(
        string name,
        int positionalCount,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body,
        string[] keywordNames,
        PythonValue[] defaults
    ) =>
        new PythonBuiltinFunctionValue(
            name,
            body,
            (positional, names, values, span) =>
            {
                if (positional.Count > positionalCount)
                {
                    throw TypeFault(
                        $"{name}() takes at most {positionalCount} positional argument"
                            + $"{(positionalCount == 1 ? string.Empty : "s")} "
                            + $"({positional.Count} given)",
                        span
                    );
                }
                if (positional.Count < positionalCount)
                {
                    throw TypeFault(
                        $"{name}() missing required argument "
                            + $"'{(name == "quantiles" ? "data" : positionalCount == 1 ? "x" : "x")}'"
                            + $" (pos {positional.Count + 1})",
                        span
                    );
                }
                var supplied = new PythonValue?[keywordNames.Length];
                for (var index = 0; index < names.Count; index++)
                {
                    var slot = Array.IndexOf(keywordNames, names[index]);
                    if (slot < 0)
                    {
                        throw TypeFault(
                            $"{name}() got an unexpected keyword argument '{names[index]}'",
                            span
                        );
                    }
                    if (supplied[slot] is not null)
                    {
                        throw TypeFault(
                            $"{name}() got multiple values for argument '{names[index]}'",
                            span
                        );
                    }
                    supplied[slot] = values[index];
                }
                var arguments = new List<PythonValue>(positional);
                for (var index = 0; index < keywordNames.Length; index++)
                {
                    arguments.Add(supplied[index] ?? defaults[index]);
                }
                return body(arguments, span);
            }
        );

    private static List<PythonValue> Values(PythonValue data, TextSpan span) =>
        ManagedObjectProtocols.MaterializeValues(data, span);

    // -----------------------------------------------------------------------------
    // Exact rational arithmetic
    // -----------------------------------------------------------------------------

    /// <summary>One exact rational, the way CPython's `Fraction` holds one.</summary>
    private readonly record struct Ratio(BigInteger Numerator, BigInteger Denominator)
    {
        internal static Ratio Zero => new(BigInteger.Zero, BigInteger.One);

        internal Ratio Add(Ratio other) =>
            new(
                Numerator * other.Denominator + other.Numerator * Denominator,
                Denominator * other.Denominator
            );

        internal Ratio Negate() => new(-Numerator, Denominator);

        internal Ratio Multiply(Ratio other) =>
            new(Numerator * other.Numerator, Denominator * other.Denominator);

        internal Ratio Divide(Ratio other) =>
            new(Numerator * other.Denominator, Denominator * other.Numerator);

        internal bool IsInteger => (Numerator % Denominator).IsZero;

        internal int CompareTo(Ratio other) =>
            (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

        /// <summary>The nearest double, ties to even, as `float(Fraction)` answers.</summary>
        internal double ToDouble()
        {
            if (Numerator.IsZero)
            {
                return 0.0;
            }
            var negative = Numerator.Sign * Denominator.Sign < 0;
            var numerator = BigInteger.Abs(Numerator);
            var denominator = BigInteger.Abs(Denominator);
            var shift = 54 - (int)(numerator.GetBitLength() - denominator.GetBitLength());
            if (shift > 0)
            {
                numerator <<= shift;
            }
            else
            {
                denominator <<= -shift;
            }
            var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
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
            return negative ? -result : result;
        }
    }

    /// <summary>How a value that is not finite enters the exact sums.</summary>
    private enum Special
    {
        None,
        NotANumber,
        PositiveInfinity,
        NegativeInfinity,
    }

    /// <summary>The numeric type the exact result converts back to.</summary>
    private enum Kind
    {
        Int,
        Float,
        Fraction,
        Decimal,
    }

    private static Kind KindOf(PythonValue value) =>
        PythonDecimalModule.TryDecimalRatio(value, out _, out _) >= 0 ? Kind.Decimal
        : PythonFractionsModule.TryFractionParts(value, out _, out _) ? Kind.Fraction
        : value is PythonFloatingPointValue ? Kind.Float
        : Kind.Int;

    private static string KindName(Kind kind) =>
        kind switch
        {
            Kind.Float => "float",
            Kind.Fraction => "Fraction",
            Kind.Decimal => "Decimal",
            _ => "int",
        };

    /// <summary>
    /// `_coerce`: ints join the other type, Fraction and float meet at float, and
    /// Decimal refuses every other type.
    /// </summary>
    private static Kind Coerce(Kind left, Kind right, TextSpan span)
    {
        if (left == right)
        {
            return left;
        }
        if (left == Kind.Int)
        {
            return right;
        }
        if (right == Kind.Int)
        {
            return left;
        }
        if (
            (left == Kind.Float && right == Kind.Fraction)
            || (left == Kind.Fraction && right == Kind.Float)
        )
        {
            return Kind.Float;
        }
        throw TypeFault($"don't know how to coerce {KindName(left)} and {KindName(right)}", span);
    }

    /// <summary>`_exact_ratio`: a value's exact numerator and denominator.</summary>
    private static (Ratio Ratio, Special Special) ExactRatio(PythonValue value)
    {
        if (PythonFractionsModule.TryFractionParts(value, out var numerator, out var denominator))
        {
            return (new Ratio(numerator, denominator), Special.None);
        }
        var decimalKind = PythonDecimalModule.TryDecimalRatio(
            value,
            out var decimalNumerator,
            out var decimalDenominator
        );
        if (decimalKind >= 0)
        {
            return decimalKind switch
            {
                1 => (Ratio.Zero, Special.NotANumber),
                2 => (Ratio.Zero, Special.PositiveInfinity),
                3 => (Ratio.Zero, Special.NegativeInfinity),
                _ => (new Ratio(decimalNumerator, decimalDenominator), Special.None),
            };
        }
        return value switch
        {
            PythonWholeNumberValue whole => (new Ratio(whole.Value, BigInteger.One), Special.None),
            PythonTruthValue truth => (
                new Ratio(truth.Value ? BigInteger.One : BigInteger.Zero, BigInteger.One),
                Special.None
            ),
            PythonFloatingPointValue floating => ExactRatio(floating.Value),
            _ => throw TypeFault(
                $"can't convert type '{ManagedObjectProtocols.GetTypeName(value)}' "
                    + "to numerator/denominator"
            ),
        };
    }

    private static (Ratio Ratio, Special Special) ExactRatio(double value)
    {
        if (double.IsNaN(value))
        {
            return (Ratio.Zero, Special.NotANumber);
        }
        if (double.IsPositiveInfinity(value))
        {
            return (Ratio.Zero, Special.PositiveInfinity);
        }
        if (double.IsNegativeInfinity(value))
        {
            return (Ratio.Zero, Special.NegativeInfinity);
        }
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }
        var power = exponent - 1075;
        var numerator = new BigInteger(mantissa);
        if (negative)
        {
            numerator = -numerator;
        }
        return power >= 0
            ? (new Ratio(numerator << power, BigInteger.One), Special.None)
            : (new Ratio(numerator, BigInteger.One << -power), Special.None);
    }

    private static Special Combine(Special left, Special right) =>
        left == Special.None ? right
        : right == Special.None ? left
        : left == right ? left
        : Special.NotANumber;

    private static double ToDouble(PythonValue value, TextSpan span)
    {
        if (PythonFractionsModule.TryFractionParts(value, out var numerator, out var denominator))
        {
            return new Ratio(numerator, denominator).ToDouble();
        }
        var decimalKind = PythonDecimalModule.TryDecimalRatio(
            value,
            out var decimalNumerator,
            out var decimalDenominator
        );
        if (decimalKind >= 0)
        {
            return decimalKind switch
            {
                1 => double.NaN,
                2 => double.PositiveInfinity,
                3 => double.NegativeInfinity,
                _ => new Ratio(decimalNumerator, decimalDenominator).ToDouble(),
            };
        }
        return value switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floating => floating.Value,
            PythonTruthValue truth => truth.Value ? 1.0 : 0.0,
            _ => throw TypeFault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted "
                    + "as a real number",
                span
            ),
        };
    }

    /// <summary>`_convert`: the exact value in the type the data called for.</summary>
    private static PythonValue Convert(Ratio value, Kind kind) =>
        kind switch
        {
            Kind.Fraction => PythonFractionsModule.FromRatio(value.Numerator, value.Denominator),
            Kind.Decimal => PythonDecimalModule.DecimalFromRatio(
                value.Numerator,
                value.Denominator
            ),
            Kind.Float => new PythonFloatingPointValue(value.ToDouble()),
            _ => value.IsInteger
                ? PythonWholeNumberValue.Create(value.Numerator / value.Denominator)
                : new PythonFloatingPointValue(value.ToDouble()),
        };

    private static PythonValue SpecialValue(Special special, Kind kind)
    {
        if (kind == Kind.Decimal)
        {
            return special switch
            {
                Special.NotANumber => PythonDecimalModule.DecimalSpecial("NaN", false),
                Special.PositiveInfinity => PythonDecimalModule.DecimalSpecial("Infinity", false),
                _ => PythonDecimalModule.DecimalSpecial("Infinity", true),
            };
        }
        return special switch
        {
            Special.NotANumber => new PythonFloatingPointValue(double.NaN),
            Special.PositiveInfinity => new PythonFloatingPointValue(double.PositiveInfinity),
            _ => new PythonFloatingPointValue(double.NegativeInfinity),
        };
    }

    private sealed class ExactSum
    {
        internal Ratio Total { get; private set; } = Ratio.Zero;

        internal int Count { get; private set; }

        internal Kind Kind { get; private set; } = Kind.Int;

        internal Special SpecialValue { get; private set; }

        internal void Add(PythonValue value)
        {
            var (ratio, special) = ExactRatio(value);
            Count++;
            if (special != Special.None)
            {
                SpecialValue = Combine(SpecialValue, special);
                return;
            }
            Total = Total.Add(ratio);
            Kind = Coerce(Kind, KindOf(value), default);
        }

        /// <summary>Adds an already exact term of the given type.</summary>
        internal void AddRatio(Ratio ratio, Kind kind)
        {
            Count++;
            Kind = Coerce(Kind, kind, default);
            Total = Total.Add(ratio);
        }
    }

    // -----------------------------------------------------------------------------
    // Measures of central tendency
    // -----------------------------------------------------------------------------

    private static PythonValue Mean(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var sum = new ExactSum();
        foreach (var value in Values(arguments[0], span))
        {
            sum.Add(value);
        }
        if (sum.Count < 1)
        {
            throw Error("mean requires at least one data point", span);
        }
        if (sum.SpecialValue != Special.None)
        {
            return SpecialValue(sum.SpecialValue, sum.Kind);
        }
        return Convert(sum.Total.Divide(new Ratio(sum.Count, BigInteger.One)), sum.Kind);
    }

    private static PythonValue FMean(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var data = Values(arguments[0], span);
        var weights = arguments.Count > 1 ? arguments[1] : null;
        if (weights is not null and not PythonNoneValue)
        {
            var weightValues = Values(weights, span);
            if (weightValues.Count != data.Count)
            {
                throw Error("data and weights must be the same length", span);
            }
            var productTotal = Ratio.Zero;
            var productKind = Kind.Int;
            for (var index = 0; index < data.Count; index++)
            {
                var leftKind = KindOf(data[index]);
                var rightKind = KindOf(weightValues[index]);
                var termKind = ProductKind(leftKind, rightKind, span);
                if (termKind == Kind.Float)
                {
                    var product = ToDouble(data[index], span) * ToDouble(weightValues[index], span);
                    productTotal = productTotal.Add(ExactRatio(product).Ratio);
                }
                else
                {
                    var left = ExactRatio(data[index]);
                    var right = ExactRatio(weightValues[index]);
                    if (left.Special != Special.None || right.Special != Special.None)
                    {
                        var special = left.Special != Special.None ? left.Special : right.Special;
                        return SpecialValue(special, productKind);
                    }
                    productTotal = productTotal.Add(left.Ratio.Multiply(right.Ratio));
                }
                productKind = SumKind(productKind, termKind, span);
            }
            var denominator = PythonFSum.Sum(weightValues.Select(value => ToDouble(value, span)));
            if (denominator == 0)
            {
                throw Error("sum of weights must be non-zero", span);
            }
            if (productKind == Kind.Decimal)
            {
                throw TypeFault(
                    "unsupported operand type(s) for /: 'decimal.Decimal' and 'float'",
                    span
                );
            }
            return new PythonFloatingPointValue(productTotal.ToDouble() / denominator);
        }
        if (data.Count == 0)
        {
            throw Error("fmean requires at least one data point", span);
        }
        return new PythonFloatingPointValue(
            PythonFSum.Sum(data.Select(value => ToDouble(value, span))) / data.Count
        );
    }

    private static Kind ProductKind(Kind left, Kind right, TextSpan span)
    {
        if (left == Kind.Int)
        {
            return right;
        }
        if (right == Kind.Int)
        {
            return left;
        }
        if (left == right)
        {
            return left;
        }
        if (
            (left == Kind.Float && right == Kind.Fraction)
            || (left == Kind.Fraction && right == Kind.Float)
        )
        {
            return Kind.Float;
        }
        throw TypeFault(
            $"unsupported operand type(s) for *: '{TypeName(left)}' and '{TypeName(right)}'",
            span
        );
    }

    private static Kind SumKind(Kind left, Kind right, TextSpan span)
    {
        if (left == right)
        {
            return left;
        }
        if (left == Kind.Int)
        {
            return right;
        }
        if (right == Kind.Int)
        {
            return left;
        }
        if (
            (left == Kind.Float && right == Kind.Fraction)
            || (left == Kind.Fraction && right == Kind.Float)
        )
        {
            return Kind.Float;
        }
        throw TypeFault(
            $"unsupported operand type(s) for +: '{TypeName(left)}' and '{TypeName(right)}'",
            span
        );
    }

    private static string TypeName(Kind kind) =>
        kind switch
        {
            Kind.Decimal => "decimal.Decimal",
            Kind.Fraction => "fractions.Fraction",
            Kind.Float => "float",
            _ => "int",
        };

    private static PythonValue GeometricMean(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var logs = new List<double>();
        var foundZero = false;
        var count = 0;
        foreach (var value in Values(arguments[0], span))
        {
            var number = ToDouble(value, span);
            count++;
            if (number > 0.0 || double.IsNaN(number))
            {
                logs.Add(Math.Log(number));
            }
            else if (number == 0.0)
            {
                foundZero = true;
            }
            else
            {
                throw ErrorWithArgs("No negative inputs allowed", value, span);
            }
        }
        var total = PythonFSum.Sum(logs);
        if (count == 0)
        {
            throw Error("Must have a non-empty dataset", span);
        }
        if (double.IsNaN(total))
        {
            return new PythonFloatingPointValue(double.NaN);
        }
        if (foundZero)
        {
            return new PythonFloatingPointValue(
                total == double.PositiveInfinity ? double.NaN : 0.0
            );
        }
        return new PythonFloatingPointValue(Math.Exp(total / count));
    }

    private static PythonValue HarmonicMean(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var data = Values(arguments[0], span);
        const string negative = "harmonic mean does not support negative values";
        var weighted = arguments.Count > 1 && arguments[1] is not PythonNoneValue;
        var weights = weighted ? Values(arguments[1], span) : [];
        if (weighted && weights.Count != data.Count)
        {
            throw Error("Number of weights does not match data size", span);
        }
        var count = data.Count;
        if (count < 1)
        {
            throw Error("harmonic_mean requires at least one data point", span);
        }
        if (count == 1 && !weighted)
        {
            if (!IsNumeric(data[0]))
            {
                throw TypeFault("unsupported type", span);
            }
            if (IsNegative(data[0]))
            {
                throw Error(negative, span);
            }
            return data[0];
        }
        var sumOfWeights = Ratio.Zero;
        if (weighted)
        {
            var weightSum = new ExactSum();
            foreach (var weight in weights)
            {
                RequireComparable(weight, span);
                if (IsNegative(weight))
                {
                    throw Error(negative, span);
                }
                weightSum.Add(weight);
            }
            sumOfWeights = weightSum.Total;
        }
        else
        {
            sumOfWeights = new Ratio(count, BigInteger.One);
        }
        var terms = new ExactSum();
        for (var index = 0; index < count; index++)
        {
            var value = data[index];
            RequireComparable(value, span);
            if (IsNegative(value))
            {
                throw Error(negative, span);
            }
            var weight = weighted ? weights[index] : PythonWholeNumberValue.Create(1);
            if (IsZero(weight))
            {
                terms.AddRatio(Ratio.Zero, Kind.Int);
                continue;
            }
            if (IsZero(value))
            {
                return PythonWholeNumberValue.Create(0);
            }
            var leftKind = KindOf(weight);
            var rightKind = KindOf(value);
            Kind kind;
            if (leftKind == Kind.Int)
            {
                kind = rightKind;
            }
            else if (rightKind == Kind.Int)
            {
                kind = leftKind;
            }
            else if (leftKind == rightKind)
            {
                kind = leftKind;
            }
            else if (
                (leftKind == Kind.Float && rightKind == Kind.Fraction)
                || (leftKind == Kind.Fraction && rightKind == Kind.Float)
            )
            {
                kind = Kind.Float;
            }
            else
            {
                throw TypeFault(
                    $"unsupported operand type(s) for /: '{TypeName(leftKind)}' and "
                        + $"'{TypeName(rightKind)}'",
                    span
                );
            }
            if (kind is Kind.Fraction or Kind.Decimal)
            {
                terms.AddRatio(ExactRatio(weight).Ratio.Divide(ExactRatio(value).Ratio), kind);
            }
            else
            {
                var quotient = ToDouble(weight, span) / ToDouble(value, span);
                terms.AddRatio(ExactRatio(quotient).Ratio, Kind.Float);
            }
        }
        if (terms.SpecialValue != Special.None)
        {
            return SpecialValue(terms.SpecialValue, terms.Kind);
        }
        if (terms.Total.CompareTo(Ratio.Zero) <= 0)
        {
            throw Error("Weighted sum must be positive", span);
        }
        return Convert(sumOfWeights.Divide(terms.Total), terms.Kind);
    }

    private static bool IsNumeric(PythonValue value) =>
        value is PythonWholeNumberValue or PythonTruthValue or PythonFloatingPointValue
        || PythonFractionsModule.TryFractionParts(value, out _, out _)
        || PythonDecimalModule.TryDecimalRatio(value, out _, out _) >= 0;

    private static void RequireComparable(PythonValue value, TextSpan span)
    {
        if (!IsNumeric(value))
        {
            throw TypeFault(
                $"'<' not supported between instances of "
                    + $"'{ManagedObjectProtocols.GetTypeName(value)}' and 'int'",
                span
            );
        }
    }

    private static bool IsNegative(PythonValue value)
    {
        var (ratio, special) = ExactRatio(value);
        return special == Special.NegativeInfinity
            || (special == Special.None && ratio.Numerator.Sign < 0);
    }

    private static bool IsZero(PythonValue value)
    {
        var (ratio, special) = ExactRatio(value);
        return special == Special.None && ratio.Numerator.IsZero;
    }

    private static PythonValue Median(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var data = SortedValues(arguments[0], span);
        var count = data.Count;
        if (count == 0)
        {
            throw Error("no median for empty data", span);
        }
        var middle = count / 2;
        if (count % 2 == 1)
        {
            return data[middle];
        }
        var sum = new ExactSum();
        sum.Add(data[middle - 1]);
        sum.Add(data[middle]);
        var kind = SumKind(KindOf(data[middle - 1]), KindOf(data[middle]), span);
        // The middle values are divided, so integers produce a float.
        return Convert(
            sum.Total.Divide(new Ratio(2, BigInteger.One)),
            kind == Kind.Int ? Kind.Float : kind
        );
    }

    private static PythonValue MedianLow(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var data = SortedValues(arguments[0], span);
        return data.Count == 0
            ? throw Error("no median for empty data", span)
            : data[(data.Count - 1) / 2];
    }

    private static PythonValue MedianHigh(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var data = SortedValues(arguments[0], span);
        return data.Count == 0
            ? throw Error("no median for empty data", span)
            : data[data.Count / 2];
    }

    private static PythonValue MedianGrouped(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var data = SortedValues(arguments[0], span);
        var count = data.Count;
        if (count == 0)
        {
            throw Error("no median for empty data", span);
        }
        var x = ToDouble(data[count / 2], span);
        var interval =
            arguments.Count > 1 && arguments[1] is not PythonNoneValue
                ? ToDouble(arguments[1], span)
                : 1.0;
        var lower = 0;
        while (lower < count && ToDouble(data[lower], span) < x)
        {
            lower++;
        }
        var upper = lower;
        while (upper < count && ToDouble(data[upper], span) == x)
        {
            upper++;
        }
        var l = x - interval / 2.0;
        return new PythonFloatingPointValue(l + interval * (count / 2.0 - lower) / (upper - lower));
    }

    private static PythonValue Mode(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var counts = Frequencies(Values(arguments[0], span));
        if (counts.Count == 0)
        {
            throw Error("no mode for empty data", span);
        }
        var best = counts[0];
        foreach (var entry in counts)
        {
            if (entry.Count > best.Count)
            {
                best = entry;
            }
        }
        return best.Value;
    }

    private static PythonValue MultiMode(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var counts = Frequencies(Values(arguments[0], span));
        if (counts.Count == 0)
        {
            return new PythonListValue([]);
        }
        var high = counts.Max(entry => entry.Count);
        return new PythonListValue([
            .. counts.Where(entry => entry.Count == high).Select(entry => entry.Value),
        ]);
    }

    private sealed record Frequency(PythonValue Value, int Count);

    /// <summary>The distinct values in first-seen order, with the count of each.</summary>
    private static List<Frequency> Frequencies(List<PythonValue> data)
    {
        var order = new List<Frequency>();
        var index = new Dictionary<object, int>();
        foreach (var value in data)
        {
            var key = FrequencyKey(value);
            if (index.TryGetValue(key, out var slot))
            {
                order[slot] = order[slot] with { Count = order[slot].Count + 1 };
                continue;
            }
            index[key] = order.Count;
            order.Add(new Frequency(value, 1));
        }
        return order;
    }

    /// <summary>
    /// The key a `Counter` would group by: numeric values by their exact rational value,
    /// so `1`, `1.0` and `Fraction(1, 1)` share a count, and everything else by its own
    /// Python equality.
    /// </summary>
    private static object FrequencyKey(PythonValue value)
    {
        if (!IsNumeric(value))
        {
            return value;
        }
        var (ratio, special) = ExactRatio(value);
        if (special != Special.None)
        {
            return special;
        }
        // Exact ratios are not reduced (a float's 1.0 is 2**52/2**52), so reduce
        // before the value equality a Counter key uses.
        var gcd = BigInteger.GreatestCommonDivisor(ratio.Numerator, ratio.Denominator);
        return new RatioKey(new Ratio(ratio.Numerator / gcd, ratio.Denominator / gcd));
    }

    private readonly record struct RatioKey(Ratio Value);

    // -----------------------------------------------------------------------------
    // Measures of spread
    // -----------------------------------------------------------------------------

    private static PythonValue Variance(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        VarianceCore(arguments, span, sample: true);

    private static PythonValue PVariance(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        VarianceCore(arguments, span, sample: false);

    private static PythonValue VarianceCore(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span,
        bool sample
    )
    {
        var (deviation, count, kind, special) = SumOfSquares(arguments, span);
        if (sample && count < 2)
        {
            throw Error("variance requires at least two data points", span);
        }
        if (!sample && count < 1)
        {
            throw Error("pvariance requires at least one data point", span);
        }
        if (special != Special.None)
        {
            return SpecialValue(special, kind);
        }
        return Convert(
            deviation.Divide(new Ratio(sample ? count - 1 : count, BigInteger.One)),
            kind
        );
    }

    /// <summary>`_ss`: the exact sum of squared deviations from the mean (or the given centre).</summary>
    private static (Ratio Deviation, int Count, Kind Kind, Special Special) SumOfSquares(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var data = Values(arguments[0], span);
        var centre =
            arguments.Count > 1 && arguments[1] is not PythonNoneValue ? arguments[1] : null;
        if (centre is not null)
        {
            var (given, givenSpecial) = ExactRatio(centre);
            if (givenSpecial != Special.None)
            {
                return (Ratio.Zero, data.Count, Kind.Int, givenSpecial);
            }
            var total = Ratio.Zero;
            var centreResultKind = Kind.Int;
            var special = Special.None;
            var centreKind = KindOf(centre);
            foreach (var value in data)
            {
                var (ratio, valueSpecial) = ExactRatio(value);
                if (valueSpecial != Special.None)
                {
                    special = Combine(special, valueSpecial);
                    continue;
                }
                var difference = ratio.Add(given.Negate());
                total = total.Add(difference.Multiply(difference));
                centreResultKind = Coerce(
                    centreResultKind,
                    Coerce(KindOf(value), centreKind, default),
                    default
                );
            }
            return (total, data.Count, centreResultKind, special);
        }
        var sum = Ratio.Zero;
        var squares = Ratio.Zero;
        var kind = Kind.Int;
        var found = Special.None;
        var count = 0;
        foreach (var value in data)
        {
            var (ratio, valueSpecial) = ExactRatio(value);
            count++;
            if (valueSpecial != Special.None)
            {
                found = Combine(found, valueSpecial);
                continue;
            }
            sum = sum.Add(ratio);
            squares = squares.Add(ratio.Multiply(ratio));
            kind = Coerce(kind, KindOf(value), default);
        }
        if (count == 0)
        {
            return (Ratio.Zero, 0, kind, Special.None);
        }
        var n = new Ratio(count, BigInteger.One);
        var deviation = n.Multiply(squares).Add(sum.Multiply(sum).Negate()).Divide(n);
        return (deviation, count, kind, found);
    }

    private static PythonValue Stdev(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        StdevCore(arguments, span, sample: true);

    private static PythonValue PStdev(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        StdevCore(arguments, span, sample: false);

    private static PythonValue StdevCore(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span,
        bool sample
    )
    {
        var (deviation, count, kind, special) = SumOfSquares(arguments, span);
        if (sample && count < 2)
        {
            throw Error("stdev requires at least two data points", span);
        }
        if (!sample && count < 1)
        {
            throw Error("pstdev requires at least one data point", span);
        }
        if (special != Special.None)
        {
            throw ValueFault("inf or nan encountered in data", span);
        }
        var meanSquare = deviation.Divide(new Ratio(sample ? count - 1 : count, BigInteger.One));
        return kind == Kind.Decimal
            ? PythonDecimalModule.DecimalSqrtOfRatio(meanSquare.Numerator, meanSquare.Denominator)
            : new PythonFloatingPointValue(SqrtOfRatio(meanSquare));
    }

    // -----------------------------------------------------------------------------
    // Relations between two inputs
    // -----------------------------------------------------------------------------

    private static void RefuseDecimalSubtraction(IReadOnlyList<PythonValue> values, TextSpan span)
    {
        foreach (var value in values)
        {
            if (PythonDecimalModule.TryDecimalRatio(value, out _, out _) >= 0)
            {
                throw TypeFault(
                    "unsupported operand type(s) for -: 'decimal.Decimal' and 'float'",
                    span
                );
            }
        }
    }

    private static PythonValue Covariance(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var x = Values(arguments[0], span);
        var y = Values(arguments[1], span);
        var count = x.Count;
        if (y.Count != count)
        {
            throw Error(
                "covariance requires that both inputs have same number of data points",
                span
            );
        }
        if (count < 2)
        {
            throw Error("covariance requires at least two data points", span);
        }
        RefuseDecimalSubtraction(x, span);
        RefuseDecimalSubtraction(y, span);
        var xbar = PythonFSum.Sum(x.Select(value => ToDouble(value, span))) / count;
        var ybar = PythonFSum.Sum(y.Select(value => ToDouble(value, span))) / count;
        var left = new double[count];
        var right = new double[count];
        for (var index = 0; index < count; index++)
        {
            left[index] = ToDouble(x[index], span) - xbar;
            right[index] = ToDouble(y[index], span) - ybar;
        }
        // `sumprod`: each product is exact before the one correctly-rounded sum.
        return new PythonFloatingPointValue(SumProdExact([.. left], [.. right]) / (count - 1));
    }

    private static PythonValue Correlation(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var x = Values(arguments[0], span);
        var y = Values(arguments[1], span);
        var count = x.Count;
        if (y.Count != count)
        {
            throw Error(
                "correlation requires that both inputs have same number of data points",
                span
            );
        }
        if (count < 2)
        {
            throw Error("correlation requires at least two data points", span);
        }
        var method =
            arguments.Count > 2 && arguments[2] is PythonTextValue text ? text.Value : "linear";
        if (method is not ("linear" or "ranked"))
        {
            throw ValueFault(
                $"Unknown method: {new PythonTextValue(method).ToRepresentationString()}",
                span
            );
        }
        List<double> left;
        List<double> right;
        if (method == "ranked")
        {
            var start = (count - 1) / -2.0;
            left = Rank(x, start, span);
            right = Rank(y, start, span);
        }
        else
        {
            RefuseDecimalSubtraction(x, span);
            RefuseDecimalSubtraction(y, span);
            var xbar = PythonFSum.Sum(x.Select(value => ToDouble(value, span))) / count;
            var ybar = PythonFSum.Sum(y.Select(value => ToDouble(value, span))) / count;
            left = [.. x.Select(value => ToDouble(value, span) - xbar)];
            right = [.. y.Select(value => ToDouble(value, span) - ybar)];
        }
        var sxy = SumProdExact(left, right);
        var sxx = SumProdExact(left, left);
        var syy = SumProdExact(right, right);
        var denominator = SqrtProd(sxx, syy);
        if (denominator == 0)
        {
            throw Error("at least one of the inputs is constant", span);
        }
        return new PythonFloatingPointValue(sxy / denominator);
    }

    private static PythonValue LinearRegression(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var x = Values(arguments[0], span);
        var y = Values(arguments[1], span);
        var count = x.Count;
        if (y.Count != count)
        {
            throw Error(
                "linear regression requires that both inputs have same number of data points",
                span
            );
        }
        if (count < 2)
        {
            throw Error("linear regression requires at least two data points", span);
        }
        var proportional = arguments.Count > 2 && ManagedObjectProtocols.IsTrue(arguments[2]);
        List<double> left;
        List<double> right;
        double xbar = 0.0,
            ybar = 0.0;
        if (proportional)
        {
            foreach (var value in x.Concat(y))
            {
                if (PythonDecimalModule.TryDecimalRatio(value, out _, out _) >= 0)
                {
                    throw TypeFault(
                        "unsupported operand type(s) for +: 'decimal.Decimal' and 'float'",
                        span
                    );
                }
            }
            left = [.. x.Select(value => ToDouble(value, span))];
            right = [.. y.Select(value => ToDouble(value, span))];
        }
        else
        {
            RefuseDecimalSubtraction(x, span);
            RefuseDecimalSubtraction(y, span);
            xbar = PythonFSum.Sum(x.Select(value => ToDouble(value, span))) / count;
            ybar = PythonFSum.Sum(y.Select(value => ToDouble(value, span))) / count;
            left = [.. x.Select(value => ToDouble(value, span) - xbar)];
            right = [.. y.Select(value => ToDouble(value, span) - ybar)];
        }
        var sxy = SumProdExact(left, right) + 0.0;
        var sxx = SumProdExact(left, left);
        if (sxx == 0)
        {
            throw Error("x is constant", span);
        }
        var slope = sxy / sxx;
        var intercept = proportional ? 0.0 : ybar - slope * xbar;
        return LinearRegressionValue(slope, intercept, span);
    }

    private static PythonManagedObjectValue LinearRegressionValue(
        double slope,
        double intercept,
        TextSpan span
    )
    {
        var dispatcher = UserObjectProtocols.Dispatcher!;
        return (PythonManagedObjectValue)
            dispatcher.InvokeWithKeywords(
                LinearRegressionType,
                [],
                ["slope", "intercept"],
                [new PythonFloatingPointValue(slope), new PythonFloatingPointValue(intercept)],
                span
            );
    }

    // -----------------------------------------------------------------------------
    // Quantiles and ranking
    // -----------------------------------------------------------------------------

    private static PythonListValue Quantiles(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var n =
            arguments.Count > 1 ? (int)PythonBuiltinFunctions.RequireIndex(arguments[1], span) : 4;
        var method =
            arguments.Count > 2 && arguments[2] is PythonTextValue text ? text.Value : "exclusive";
        if (n < 1)
        {
            throw Error("n must be at least 1", span);
        }
        var data = SortedValues(arguments[0], span);
        var count = data.Count;
        if (count < 2)
        {
            if (count == 1)
            {
                return new PythonListValue([.. Enumerable.Repeat(data[0], n - 1)]);
            }
            throw Error("must have at least one data point", span);
        }
        if (method is not ("exclusive" or "inclusive"))
        {
            throw ValueFault(
                $"Unknown method: {new PythonTextValue(method).ToRepresentationString()}",
                span
            );
        }
        var result = new List<PythonValue>();
        var m = method == "inclusive" ? count - 1 : count + 1;
        for (var index = 1; index < n; index++)
        {
            var product = index * m;
            var j = product / n;
            int delta;
            if (method == "inclusive")
            {
                delta = product - j * n;
            }
            else
            {
                j = j < 1 ? 1 : (j > count - 1 ? count - 1 : j);
                delta = product - j * n;
            }
            var lower = data[method == "inclusive" ? j : j - 1];
            var upper = data[method == "inclusive" ? j + 1 : j];
            var interpolated = ExactRatio(lower)
                .Ratio.Multiply(new Ratio(n - delta, BigInteger.One))
                .Add(ExactRatio(upper).Ratio.Multiply(new Ratio(delta, BigInteger.One)))
                .Divide(new Ratio(n, BigInteger.One));
            var kind = SumKind(KindOf(lower), KindOf(upper), span);
            result.Add(Convert(interpolated, kind == Kind.Int ? Kind.Float : kind));
        }
        return new PythonListValue(result);
    }

    private static List<PythonValue> SortedValues(PythonValue data, TextSpan span)
    {
        var decorated = Values(data, span).Select((value, index) => (Value: value, index)).ToList();
        decorated.Sort(
            (left, right) =>
            {
                var order = CompareValues(left.Value, right.Value, span);
                return order != 0 ? order : left.index.CompareTo(right.index);
            }
        );
        return [.. decorated.Select(entry => entry.Value)];
    }

    /// <summary>Python's numeric ordering, exact when both values are finite rationals.</summary>
    private static int CompareValues(PythonValue left, PythonValue right, TextSpan span)
    {
        var (leftRatio, leftSpecial) = ExactRatio(left);
        var (rightRatio, rightSpecial) = ExactRatio(right);
        if (leftSpecial == Special.None && rightSpecial == Special.None)
        {
            return leftRatio.CompareTo(rightRatio);
        }
        return ToDouble(left, span).CompareTo(ToDouble(right, span));
    }

    /// <summary>`_rank`: ties averaged.</summary>
    private static List<double> Rank(List<PythonValue> data, double start, TextSpan span)
    {
        var decorated = data.Select((value, index) => (Value: value, index)).ToList();
        decorated.Sort(
            (left, right) =>
            {
                var order = CompareValues(left.Value, right.Value, span);
                return order != 0 ? order : left.index.CompareTo(right.index);
            }
        );
        var result = new double[data.Count];
        var position = start - 1;
        var slot = 0;
        while (slot < decorated.Count)
        {
            var start2 = slot;
            while (
                slot + 1 < decorated.Count
                && CompareValues(decorated[slot + 1].Value, decorated[slot].Value, span) == 0
            )
            {
                slot++;
            }
            var size = slot - start2 + 1;
            var rank = position + (size + 1) / 2.0;
            for (var index = start2; index <= slot; index++)
            {
                result[decorated[index].index] = rank;
            }
            position += size;
            slot++;
        }
        return [.. result];
    }

    // -----------------------------------------------------------------------------
    // Numeric helpers
    // -----------------------------------------------------------------------------

    /// <summary>`math.sumprod`'s exact sum of products.</summary>
    private static double SumProdExact(List<double> left, List<double> right)
    {
        var total = Ratio.Zero;
        var special = Special.None;
        for (var index = 0; index < left.Count; index++)
        {
            var (a, aSpecial) = ExactRatio(left[index]);
            var (b, bSpecial) = ExactRatio(right[index]);
            if (aSpecial != Special.None || bSpecial != Special.None)
            {
                var found =
                    aSpecial != Special.None
                        ? (
                            bSpecial != Special.None
                                ? MultiplySpecials(aSpecial, bSpecial)
                                : aSpecial
                        )
                        : bSpecial;
                special = Combine(special, found);
                continue;
            }
            total = total.Add(a.Multiply(b));
        }
        return special switch
        {
            Special.PositiveInfinity => double.PositiveInfinity,
            Special.NegativeInfinity => double.NegativeInfinity,
            Special.NotANumber => double.NaN,
            _ => total.ToDouble(),
        };
    }

    private static Special MultiplySpecials(Special left, Special right)
    {
        var sign =
            (left == Special.NegativeInfinity ? -1 : 1)
            * (right == Special.NegativeInfinity ? -1 : 1);
        return sign > 0 ? Special.PositiveInfinity : Special.NegativeInfinity;
    }

    /// <summary>`_sqrtprod`: sqrt(x * y) without overflow or underflow in between.</summary>
    private static double SqrtProd(double x, double y)
    {
        var h = Math.Sqrt(x * y);
        if (!double.IsFinite(h))
        {
            if (double.IsInfinity(h) && !double.IsInfinity(x) && !double.IsInfinity(y))
            {
                const double scale = 7.458340731200207e-155;
                return SqrtProd(scale * x, scale * y) / scale;
            }
            return h;
        }
        if (h == 0)
        {
            if (x != 0 && y != 0)
            {
                const double scale = 4.4586592720294206e161;
                return SqrtProd(scale * x, scale * y) / scale;
            }
            return h;
        }
        var correction = PythonFSum.Sum([x * y, -h * h]);
        return h + correction / (2.0 * h);
    }

    /// <summary>`_float_sqrt_of_frac`: the square root of a ratio, correctly rounded.</summary>
    private static double SqrtOfRatio(Ratio ratio)
    {
        var numerator = BigInteger.Abs(ratio.Numerator);
        var denominator = BigInteger.Abs(ratio.Denominator);
        var q = FloorDiv((int)numerator.GetBitLength() - (int)denominator.GetBitLength() - 109, 2);
        BigInteger top;
        BigInteger bottom;
        if (q >= 0)
        {
            top = IntegerSqrtOfRatio(numerator, denominator << (2 * q)) << q;
            bottom = BigInteger.One;
        }
        else
        {
            top = IntegerSqrtOfRatio(numerator << (-2 * q), denominator);
            bottom = BigInteger.One << -q;
        }
        // `numerator / denominator` is a correctly rounded true division in Python;
        // converting each side separately would truncate the numerator's low bits.
        return new Ratio(top, bottom).ToDouble();
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static BigInteger IntegerSqrtOfRatio(BigInteger numerator, BigInteger denominator)
    {
        var root = IntegerSqrt(numerator / denominator);
        return root * root * denominator == numerator ? root : root | BigInteger.One;
    }

    private static BigInteger IntegerSqrt(BigInteger value)
    {
        if (value < 2)
        {
            return value;
        }
        var estimate = BigInteger.One << (int)((value.GetBitLength() + 1) / 2);
        while (true)
        {
            var next = (estimate + value / estimate) >> 1;
            if (next >= estimate)
            {
                return estimate;
            }
            estimate = next;
        }
    }

    // -----------------------------------------------------------------------------
    // NormalDist
    // -----------------------------------------------------------------------------

    private static readonly double Sqrt2 = Math.Sqrt(2.0);

    private sealed record Gaussian(double Mu, double Sigma);

    private static PythonManagedTypeValue CreateNormalDistType()
    {
        var type = new PythonManagedTypeValue("NormalDist") { Module = "statistics" };
        type.Attributes["__module__"] = new PythonTextValue("statistics");
        type.Attributes["__qualname__"] = new PythonTextValue("NormalDist");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Normal distribution of a random variable"
        );
        type.Attributes["__new__"] = Method(
            "NormalDist",
            "__new__",
            (_, arguments) => NewNormalDist(arguments),
            (_, positional, names, values) =>
                NewNormalDist(
                    BindConstructor(["mu", "sigma"], [0.0, 1.0], positional, names, values)
                )
        );
        type.Attributes["mu"] = GaussianMember("mu", gaussian => gaussian.Mu);
        type.Attributes["sigma"] = GaussianMember("sigma", gaussian => gaussian.Sigma);
        type.Attributes["mean"] = GaussianProperty("mean", gaussian => gaussian.Mu);
        type.Attributes["median"] = GaussianProperty("median", gaussian => gaussian.Mu);
        type.Attributes["mode"] = GaussianProperty("mode", gaussian => gaussian.Mu);
        type.Attributes["stdev"] = GaussianProperty("stdev", gaussian => gaussian.Sigma);
        type.Attributes["variance"] = GaussianProperty(
            "variance",
            gaussian => gaussian.Sigma * gaussian.Sigma
        );
        type.Attributes["from_samples"] = new PythonClassMethodValue(
            Method(
                "NormalDist",
                "from_samples",
                (cls, arguments) => NormalDistFromSamples(cls!, arguments)
            )
        );
        type.Attributes["samples"] = Method(
            "NormalDist",
            "samples",
            (self, arguments) => NormalDistSamples(self!, arguments, seed: null),
            (target, positional, names, values) =>
            {
                PythonValue? seed = null;
                for (var index = 0; index < names.Count; index++)
                {
                    if (names[index] != "seed")
                    {
                        throw TypeFault(
                            $"samples() got an unexpected keyword argument '{names[index]}'"
                        );
                    }
                    seed = values[index];
                }
                return NormalDistSamples(target!, positional, seed);
            }
        );
        type.Attributes["pdf"] = Method(
            "NormalDist",
            "pdf",
            (self, arguments) => NormalDistPdf(self!, arguments)
        );
        type.Attributes["cdf"] = Method(
            "NormalDist",
            "cdf",
            (self, arguments) => NormalDistCdf(self!, arguments)
        );
        type.Attributes["inv_cdf"] = Method(
            "NormalDist",
            "inv_cdf",
            (self, arguments) => NormalDistInvCdf(self!, arguments)
        );
        type.Attributes["quantiles"] = PythonUserTypes.Method(
            "NormalDist",
            "quantiles",
            (self, arguments) => NormalDistQuantiles(self!, arguments),
            null,
            ["n"],
            [PythonWholeNumberValue.Create(4)]
        );
        type.Attributes["overlap"] = Method(
            "NormalDist",
            "overlap",
            (self, arguments) => NormalDistOverlap(self!, arguments)
        );
        type.Attributes["zscore"] = Method(
            "NormalDist",
            "zscore",
            (self, arguments) => NormalDistZScore(self!, arguments)
        );
        type.Attributes["__repr__"] = Method(
            "NormalDist",
            "__repr__",
            (self, _) => new PythonTextValue(RepresentGaussian(GaussianOf(self!)))
        );
        type.Attributes["__str__"] = type.Attributes["__repr__"];
        type.Attributes["__eq__"] = GaussianComparison("__eq__", (left, right) => left == right);
        type.Attributes["__ne__"] = GaussianComparison("__ne__", (left, right) => left != right);
        type.Attributes["__hash__"] = Method(
            "NormalDist",
            "__hash__",
            (self, _) =>
            {
                var gaussian = GaussianOf(self!);
                return PythonWholeNumberValue.Create(HashCode.Combine(gaussian.Mu, gaussian.Sigma));
            }
        );
        type.Attributes["__add__"] = Method(
            "NormalDist",
            "__add__",
            (self, arguments) => GaussianAdd(self!, arguments, subtract: false)
        );
        type.Attributes["__radd__"] = type.Attributes["__add__"];
        type.Attributes["__sub__"] = Method(
            "NormalDist",
            "__sub__",
            (self, arguments) => GaussianAdd(self!, arguments, subtract: true)
        );
        type.Attributes["__rsub__"] = Method(
            "NormalDist",
            "__rsub__",
            (self, arguments) => GaussianReverse(self!, arguments)
        );
        type.Attributes["__mul__"] = Method(
            "NormalDist",
            "__mul__",
            (self, arguments) => GaussianScale(self!, arguments, divide: false)
        );
        type.Attributes["__rmul__"] = type.Attributes["__mul__"];
        type.Attributes["__truediv__"] = Method(
            "NormalDist",
            "__truediv__",
            (self, arguments) => GaussianScale(self!, arguments, divide: true)
        );
        type.Attributes["__pos__"] = Method("NormalDist", "__pos__", (self, _) => self!);
        type.Attributes["__neg__"] = Method(
            "NormalDist",
            "__neg__",
            (self, _) =>
            {
                var gaussian = GaussianOf(self!);
                return GaussianValue(-gaussian.Mu, gaussian.Sigma);
            }
        );
        return type;
    }

    private static Gaussian GaussianOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: Gaussian gaussian }
            ? gaussian
            : throw TypeFault("the value is not a NormalDist");

    private static PythonManagedObjectValue GaussianValue(double mu, double sigma) =>
        new(NormalDistType, new Gaussian(mu, sigma));

    private static PythonManagedObjectValue NewNormalDist(IReadOnlyList<PythonValue> arguments)
    {
        var values = WithoutClass(arguments);
        var mu = values.Length > 0 ? ToDouble(values[0], default) : 0.0;
        var sigma = values.Length > 1 ? ToDouble(values[1], default) : 1.0;
        if (sigma < 0.0)
        {
            throw Error("sigma must be non-negative");
        }
        return GaussianValue(mu, sigma);
    }

    private static PythonValue[] WithoutClass(IReadOnlyList<PythonValue> arguments) =>
        arguments.Count > 0 && arguments[0] is PythonManagedTypeValue
            ? [.. arguments.Skip(1)]
            : [.. arguments];

    private static PythonValue[] BindConstructor(
        string[] parameters,
        double[] defaults,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var slots = new PythonValue?[parameters.Length];
        for (var index = 0; index < positional.Count && index < parameters.Length; index++)
        {
            slots[index] = positional[index];
        }
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(parameters, names[index]);
            if (slot < 0)
            {
                throw TypeFault(
                    $"NormalDist() got an unexpected keyword argument '{names[index]}'"
                );
            }
            slots[slot] = values[index];
        }
        return
        [
            .. slots.Select((slot, index) => slot ?? new PythonFloatingPointValue(defaults[index])),
        ];
    }

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null
    ) => PythonUserTypes.Method(owner, name, body, keywords);

    private static PythonPropertyValue GaussianMember(string name, Func<Gaussian, double> read) =>
        new(
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) => new PythonFloatingPointValue(read(GaussianOf(arguments[0])))
            ),
            null,
            null
        );

    private static PythonPropertyValue GaussianProperty(string name, Func<Gaussian, double> read) =>
        GaussianMember(name, read);

    private static string RepresentGaussian(Gaussian gaussian) =>
        $"NormalDist(mu={new PythonFloatingPointValue(gaussian.Mu).ToRepresentationString()}, "
        + $"sigma={new PythonFloatingPointValue(gaussian.Sigma).ToRepresentationString()})";

    private static PythonProtocolFunctionValue GaussianComparison(
        string name,
        Func<Gaussian, Gaussian, bool> compare
    ) =>
        Method(
            "NormalDist",
            name,
            (self, arguments) =>
                arguments.Count == 1
                && arguments[0] is PythonManagedObjectValue { Payload: Gaussian other }
                    ? PythonTruthValue.FromBoolean(compare(GaussianOf(self!), other))
                    : PythonNotImplementedValue.Instance
        );

    private static PythonValue GaussianAdd(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        bool subtract
    )
    {
        if (arguments.Count != 1)
        {
            return PythonNotImplementedValue.Instance;
        }
        var gaussian = GaussianOf(self);
        var sign = subtract ? -1 : 1;
        if (arguments[0] is PythonManagedObjectValue { Payload: Gaussian other })
        {
            return GaussianValue(
                gaussian.Mu + sign * other.Mu,
                Math.Sqrt(gaussian.Sigma * gaussian.Sigma + other.Sigma * other.Sigma)
            );
        }
        return GaussianValue(gaussian.Mu + sign * ToDouble(arguments[0], default), gaussian.Sigma);
    }

    private static PythonValue GaussianReverse(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
        {
            return PythonNotImplementedValue.Instance;
        }
        var gaussian = GaussianOf(self);
        return GaussianValue(-(gaussian.Mu - ToDouble(arguments[0], default)), gaussian.Sigma);
    }

    private static PythonValue GaussianScale(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        bool divide
    )
    {
        if (arguments.Count != 1)
        {
            return PythonNotImplementedValue.Instance;
        }
        var factor = ToDouble(arguments[0], default);
        var gaussian = GaussianOf(self);
        return divide
            ? GaussianValue(gaussian.Mu / factor, gaussian.Sigma / Math.Abs(factor))
            : GaussianValue(gaussian.Mu * factor, gaussian.Sigma * Math.Abs(factor));
    }

    private static PythonManagedObjectValue NormalDistFromSamples(
        PythonValue cls,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
        {
            throw TypeFault("from_samples() takes exactly one argument");
        }
        var data = Values(arguments[0], default);
        var (deviation, count, _, special) = SumOfSquares([arguments[0]], default);
        if (count < 2 || special != Special.None)
        {
            throw Error("stdev requires at least two data points");
        }
        var sum = new ExactSum();
        foreach (var value in data)
        {
            sum.Add(value);
        }
        var mean = sum.Total.Divide(new Ratio(sum.Count, BigInteger.One)).ToDouble();
        var sigma = SqrtOfRatio(deviation.Divide(new Ratio(count - 1, BigInteger.One)));
        _ = cls;
        return GaussianValue(mean, sigma);
    }

    private static PythonListValue NormalDistSamples(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        PythonValue? seed
    )
    {
        if (arguments.Count != 1)
        {
            throw TypeFault($"samples() takes exactly one positional argument");
        }
        var n = (int)PythonBuiltinFunctions.RequireIndex(arguments[0], default);
        var gaussian = GaussianOf(self);
        var dispatcher = UserObjectProtocols.Dispatcher!;
        var module = dispatcher.ImportModule("random", string.Empty, default);
        var randomType = ManagedObjectProtocols.GetAttribute(module, "Random", default);
        var instance = seed is null ? module : dispatcher.Invoke(randomType, [seed], default);
        var drawMethod = ManagedObjectProtocols.GetAttribute(instance, "random", default);
        var result = new List<PythonValue>(n);
        for (var index = 0; index < n; index++)
        {
            var p = ToDouble(dispatcher.Invoke(drawMethod, [], default), default);
            result.Add(new PythonFloatingPointValue(gaussian.Mu + gaussian.Sigma * InvCdf(p)));
        }
        return new PythonListValue(result);
    }

    private static PythonFloatingPointValue NormalDistPdf(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var gaussian = GaussianOf(self);
        var variance = gaussian.Sigma * gaussian.Sigma;
        if (variance == 0)
        {
            throw Error("pdf() not defined when sigma is zero");
        }
        var x = RequireSingle("pdf", arguments);
        var difference = x - gaussian.Mu;
        return new PythonFloatingPointValue(
            Math.Exp(difference * difference / (-2.0 * variance))
                / Math.Sqrt(2.0 * Math.PI * variance)
        );
    }

    private static PythonFloatingPointValue NormalDistCdf(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var gaussian = GaussianOf(self);
        if (gaussian.Sigma == 0)
        {
            throw Error("cdf() not defined when sigma is zero");
        }
        var x = RequireSingle("cdf", arguments);
        return new PythonFloatingPointValue(
            0.5 * Erfc((gaussian.Mu - x) / (gaussian.Sigma * Sqrt2))
        );
    }

    private static PythonFloatingPointValue NormalDistInvCdf(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var gaussian = GaussianOf(self);
        var p = RequireSingle("inv_cdf", arguments);
        if (p <= 0.0 || p >= 1.0)
        {
            throw Error("p must be in the range 0.0 < p < 1.0");
        }
        return new PythonFloatingPointValue(gaussian.Mu + gaussian.Sigma * InvCdf(p));
    }

    private static PythonListValue NormalDistQuantiles(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var n =
            arguments.Count == 1
                ? (int)PythonBuiltinFunctions.RequireIndex(arguments[0], default)
                : 4;
        var gaussian = GaussianOf(self);
        var result = new List<PythonValue>(Math.Max(n - 1, 0));
        for (var index = 1; index < n; index++)
        {
            result.Add(
                new PythonFloatingPointValue(
                    gaussian.Mu + gaussian.Sigma * InvCdf((double)index / n)
                )
            );
        }
        return new PythonListValue(result);
    }

    private static PythonFloatingPointValue NormalDistOverlap(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
        {
            throw TypeFault("overlap() takes exactly one argument");
        }
        if (arguments[0] is not PythonManagedObjectValue { Payload: Gaussian other })
        {
            throw TypeFault("Expected another NormalDist instance");
        }
        var x = GaussianOf(self);
        var y = other;
        if ((y.Sigma, y.Mu).CompareTo((x.Sigma, x.Mu)) < 0)
        {
            (x, y) = (y, x);
        }
        var xVariance = x.Sigma * x.Sigma;
        var yVariance = y.Sigma * y.Sigma;
        if (xVariance == 0 || yVariance == 0)
        {
            throw Error("overlap() not defined when sigma is zero");
        }
        var difference = yVariance - xVariance;
        var distance = Math.Abs(y.Mu - x.Mu);
        if (difference == 0)
        {
            return new PythonFloatingPointValue(Erfc(distance / (2.0 * x.Sigma * Sqrt2)));
        }
        var a = x.Mu * yVariance - y.Mu * xVariance;
        var b =
            x.Sigma
            * y.Sigma
            * Math.Sqrt(distance * distance + difference * Math.Log(yVariance / xVariance));
        var x1 = (a + b) / difference;
        var x2 = (a - b) / difference;
        return new PythonFloatingPointValue(
            1.0 - (Math.Abs(Cdf(y, x1) - Cdf(x, x1)) + Math.Abs(Cdf(y, x2) - Cdf(x, x2)))
        );
    }

    private static double Cdf(Gaussian gaussian, double x) =>
        0.5 * Erfc((gaussian.Mu - x) / (gaussian.Sigma * Sqrt2));

    private static PythonFloatingPointValue NormalDistZScore(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var gaussian = GaussianOf(self);
        if (gaussian.Sigma == 0)
        {
            throw Error("zscore() not defined when sigma is zero");
        }
        var x = RequireSingle("zscore", arguments);
        return new PythonFloatingPointValue((x - gaussian.Mu) / gaussian.Sigma);
    }

    private static double RequireSingle(string name, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count != 1)
        {
            throw TypeFault($"{name}() takes exactly one argument");
        }
        return ToDouble(arguments[0], default);
    }

    // -----------------------------------------------------------------------------
    // The normal distribution's own numerics
    // -----------------------------------------------------------------------------

    /// <summary>
    /// The error function, by its Maclaurin series near zero and the continued fraction
    /// for the complementary function far out — the runtime's own implementation, so the
    /// last bit can differ from the platform's `erf`.
    /// </summary>
    internal static double Erf(double x)
    {
        if (double.IsNaN(x))
        {
            return x;
        }
        var magnitude = Math.Abs(x);
        if (magnitude < 2.0)
        {
            // erf(x) = 2/sqrt(pi) * sum_n (-1)^n x^(2n+1) / (n! (2n+1))
            var term = x;
            var sum = x;
            var square = x * x;
            for (var n = 1; n < 200; n++)
            {
                term *= -square / n;
                var add = term / (2 * n + 1);
                sum += add;
                if (Math.Abs(add) < 1e-18 * Math.Abs(sum))
                {
                    break;
                }
            }
            return 2.0 / Math.Sqrt(Math.PI) * sum;
        }
        var complementary = Erfc(magnitude);
        return x > 0 ? 1.0 - complementary : complementary - 1.0;
    }

    /// <summary>The complementary error function by its continued fraction.</summary>
    internal static double Erfc(double x)
    {
        if (double.IsNaN(x))
        {
            return x;
        }
        if (x < 0)
        {
            return 2.0 - Erfc(-x);
        }
        if (x == 0)
        {
            return 1.0;
        }
        if (x < 2.0)
        {
            return 1.0 - Erf(x);
        }
        // erfc(x) = exp(-x^2) / (x sqrt(pi)) * 1/(1 + 1/(2x^2)/(1 + 2/(2x^2)/(1 + ...)))
        const double tiny = 1e-300;
        var f = tiny;
        var c = f;
        var d = 0.0;
        for (var n = 1; n <= 300; n++)
        {
            var a = n / (2.0 * x * x);
            d = 1.0 + a * d;
            if (d == 0.0)
            {
                d = tiny;
            }
            d = 1.0 / d;
            c = 1.0 + a / c;
            if (c == 0.0)
            {
                c = tiny;
            }
            var delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1.0) < 1e-16)
            {
                break;
            }
        }
        return Math.Exp(-x * x) / (x * Math.Sqrt(Math.PI)) * f;
    }

    /// <summary>Wichura's AS241 rational approximation, as CPython's `statistics` uses it.</summary>
    internal static double InvCdf(double p)
    {
        var q = p - 0.5;
        double x;
        if (Math.Abs(q) <= 0.425)
        {
            var r = 0.180625 - q * q;
            var num =
                (
                    (
                        (
                            (
                                (
                                    (
                                        (2.5090809287301226727e3 * r + 3.3430575583588128105e4) * r
                                        + 6.7265770927008700853e4
                                    ) * r
                                    + 4.5921953931549871457e4
                                ) * r
                                + 1.3731693765509461125e4
                            ) * r
                            + 1.9715909503065514427e3
                        ) * r
                        + 1.3314166789178437745e2
                    ) * r
                    + 3.3871328727963666080e0
                ) * q;
            var den = (
                (
                    (
                        (
                            (
                                (
                                    (5.2264952788528545610e3 * r + 2.8729085735721942674e4) * r
                                    + 3.9307895800092710610e4
                                ) * r
                                + 2.1213794301586595867e4
                            ) * r
                            + 5.3941960214247511077e3
                        ) * r
                        + 6.8718700749205790830e2
                    ) * r
                    + 4.2313330701600911252e1
                ) * r
                + 1.0
            );
            x = num / den;
            return x;
        }
        var r2 = q <= 0.0 ? p : 1.0 - p;
        r2 = Math.Sqrt(-Math.Log(r2));
        if (r2 <= 5.0)
        {
            r2 -= 1.6;
            var num = (
                (
                    (
                        (
                            (
                                (
                                    (7.74545014278341407640e-4 * r2 + 2.27238449892691845833e-2)
                                        * r2
                                    + 2.41780725177450611770e-1
                                ) * r2
                                + 1.27045825245236838258e0
                            ) * r2
                            + 3.64784832476320460504e0
                        ) * r2
                        + 5.76949722146069140550e0
                    ) * r2
                    + 4.63033784615654529590e0
                ) * r2
                + 1.42343711074968357734e0
            );
            var den = (
                (
                    (
                        (
                            (
                                (
                                    (1.05075007164441684324e-9 * r2 + 5.47593808499534494600e-4)
                                        * r2
                                    + 1.51986665636164571966e-2
                                ) * r2
                                + 1.48103976427480074590e-1
                            ) * r2
                            + 6.89767334985100004550e-1
                        ) * r2
                        + 1.67638483018380384940e0
                    ) * r2
                    + 2.05319162663775882187e0
                ) * r2
                + 1.0
            );
            x = num / den;
        }
        else
        {
            r2 -= 5.0;
            var num = (
                (
                    (
                        (
                            (
                                (
                                    (2.01033439929228813265e-7 * r2 + 2.71155556874348757815e-5)
                                        * r2
                                    + 1.24266094738807843860e-3
                                ) * r2
                                + 2.65321895265761230930e-2
                            ) * r2
                            + 2.96560571828504891230e-1
                        ) * r2
                        + 1.78482653991729133580e0
                    ) * r2
                    + 5.46378491116411436990e0
                ) * r2
                + 6.65790464350110377720e0
            );
            var den = (
                (
                    (
                        (
                            (
                                (
                                    (2.04426310338993978564e-15 * r2 + 1.42151175831644588870e-7)
                                        * r2
                                    + 1.84631831751005468180e-5
                                ) * r2
                                + 7.86869131145613259100e-4
                            ) * r2
                            + 1.48753612908506148525e-2
                        ) * r2
                        + 1.36929880922735805310e-1
                    ) * r2
                    + 5.99832206555887937690e-1
                ) * r2
                + 1.0
            );
            x = num / den;
        }
        return q < 0.0 ? -x : x;
    }
}
