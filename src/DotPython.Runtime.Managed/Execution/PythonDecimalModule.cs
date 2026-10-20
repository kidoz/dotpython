using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `decimal` module: a `Decimal` over a sign, an arbitrary-precision coefficient and
/// a base-ten exponent, with the signals, rounding modes and context precision CPython's
/// C implementation applies. The module publishes the arithmetic and conversion surface
/// statistics and ordinary code use — construction from strings, integers, exact floats
/// and tuples, the canonical text forms, exact comparisons and numeric hashing,
/// context-rounded `+ - * / // % divmod **`, `quantize`, `sqrt`, and the default
/// half-even context with its rounding constants — while the exotic context machinery
/// (traps, flags, `extended`/`capitals`, and the full `__format__` grammar) remains the
/// recorded gap.
/// </summary>
internal static class PythonDecimalModule
{
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        // A context belongs to the engine that built its type.
        _currentContext = null;
        globals.SetValue("Decimal", DecimalType);
        globals.SetValue("DecimalTuple", DecimalTupleType);
        globals.SetValue("Context", ContextType);
        globals.SetValue("getcontext", GetContextFunction());
        globals.SetValue(
            "setcontext",
            PythonModuleFunction.Create("setcontext", ["context"], [], [], SetContext)
        );
        globals.SetValue(
            "localcontext",
            PythonModuleFunction.Create(
                "localcontext",
                [],
                ["ctx"],
                [PythonNoneValue.Instance],
                LocalContext
            )
        );
        foreach (var rounding in Roundings)
        {
            globals.SetValue(rounding, new PythonTextValue(rounding));
        }
        foreach (var name in ExceptionNames)
        {
            globals.SetValue(name, ExceptionTypes[name]);
        }
    }

    private static readonly string[] Roundings =
    [
        "ROUND_CEILING",
        "ROUND_DOWN",
        "ROUND_FLOOR",
        "ROUND_HALF_DOWN",
        "ROUND_HALF_EVEN",
        "ROUND_HALF_UP",
        "ROUND_UP",
        "ROUND_05UP",
    ];

    private enum Special
    {
        Finite,
        Infinity,
        NaN,
        SNaN,
    }

    private sealed record DecimalState(
        int Sign,
        BigInteger Coefficient,
        long Exponent,
        Special Special,
        string Payload
    );

    private sealed class DecimalContext
    {
        internal int Prec = 28;

        internal string Rounding = "ROUND_HALF_EVEN";

        internal int Emin = -999999;

        internal int Emax = 999999;

        internal int Clamp;

        internal int Capitals = 1;
    }

    [ThreadStatic]
    private static PythonManagedObjectValue? _currentContext;

    private static readonly PythonManagedTypeValue DecimalType = CreateDecimalType();

    private static readonly PythonManagedTypeValue ContextType = CreateContextType();

    private static PythonManagedTypeValue CreateContextType()
    {
        var type = new PythonManagedTypeValue("Context") { Module = "decimal" };
        type.Attributes["__module__"] = Text("decimal");
        type.Attributes["__qualname__"] = Text("Context");
        type.Attributes["prec"] = ContextProperty("prec");
        type.Attributes["rounding"] = ContextProperty("rounding");
        type.Attributes["Emin"] = ContextProperty("Emin");
        type.Attributes["Emax"] = ContextProperty("Emax");
        type.Attributes["clamp"] = ContextProperty("clamp");
        type.Attributes["capitals"] = ContextProperty("capitals");
        type.Attributes["copy"] = Method(
            "Context",
            "copy",
            (self, _) => ContextValue(CopyContext(ContextOf(self!)))
        );
        type.Attributes["__repr__"] = Method(
            "Context",
            "__repr__",
            (self, _) =>
            {
                var context = ContextOf(self!);
                return Text(
                    $"Context(prec={context.Prec}, rounding={context.Rounding}, "
                        + $"Emin={context.Emin}, Emax={context.Emax}, "
                        + $"capitals={context.Capitals}, clamp={context.Clamp}, "
                        + "flags=[], traps=[])"
                );
            }
        );
        return type;
    }

    private static PythonPropertyValue ContextProperty(string name) =>
        new(
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) =>
                    ContextOf(arguments[0]) switch
                    {
                        var context when name == "prec" => PythonWholeNumberValue.Create(
                            context.Prec
                        ),
                        var context when name == "rounding" => Text(context.Rounding),
                        var context when name == "Emin" => PythonWholeNumberValue.Create(
                            context.Emin
                        ),
                        var context when name == "Emax" => PythonWholeNumberValue.Create(
                            context.Emax
                        ),
                        var context when name == "clamp" => PythonWholeNumberValue.Create(
                            context.Clamp
                        ),
                        var context => PythonWholeNumberValue.Create(context.Capitals),
                    }
            ),
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) =>
                {
                    var context = ContextOf(arguments[0]);
                    switch (name)
                    {
                        case "prec":
                            context.Prec = (int)RequireInteger(arguments[1], default);
                            break;
                        case "rounding":
                            context.Rounding = arguments[1] is PythonTextValue rounding
                                ? rounding.Value
                                : throw TypeFault("rounding must be a string");
                            break;
                        case "Emin":
                            context.Emin = (int)RequireInteger(arguments[1], default);
                            break;
                        case "Emax":
                            context.Emax = (int)RequireInteger(arguments[1], default);
                            break;
                        case "clamp":
                            context.Clamp = (int)RequireInteger(arguments[1], default);
                            break;
                        default:
                            context.Capitals = (int)RequireInteger(arguments[1], default);
                            break;
                    }
                    return PythonNoneValue.Instance;
                }
            ),
            null
        );

    private static DecimalContext ContextOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: DecimalContext context }
            ? context
            : throw TypeFault("the value is not a decimal context");

    private static DecimalContext CopyContext(DecimalContext context) =>
        new()
        {
            Prec = context.Prec,
            Rounding = context.Rounding,
            Emin = context.Emin,
            Emax = context.Emax,
            Clamp = context.Clamp,
            Capitals = context.Capitals,
        };

    private static PythonManagedObjectValue ContextValue(DecimalContext context) =>
        new(ContextType, context);

    private static PythonManagedObjectValue CurrentContext()
    {
        if (_currentContext is null)
        {
            _currentContext = ContextValue(new DecimalContext());
        }
        return _currentContext;
    }

    private static PythonBuiltinFunctionValue GetContextFunction() =>
        new(
            "getcontext",
            (arguments, span) =>
            {
                if (arguments.Count != 0)
                {
                    throw TypeFault("getcontext() takes no arguments", span);
                }
                return CurrentContext();
            }
        );

    private static PythonValue SetContext(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        _ = ContextOf(arguments[0]);
        _currentContext = (PythonManagedObjectValue)arguments[0];
        return PythonNoneValue.Instance;
    }

    private static PythonValue LocalContext(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var context =
            arguments[0] is PythonNoneValue
                ? CopyContext(ContextOf(CurrentContext()))
                : CopyContext(ContextOf(arguments[0]));
        return new PythonManagedObjectValue(
            ContextManagerType,
            new ContextManagerState(ContextValue(context))
        );
    }

    private sealed class ContextManagerState(PythonManagedObjectValue entered)
    {
        internal PythonManagedObjectValue Entered { get; } = entered;

        internal PythonManagedObjectValue? Saved { get; set; }
    }

    private static readonly PythonManagedTypeValue ContextManagerType = CreateContextManagerType();

    private static PythonManagedTypeValue CreateContextManagerType()
    {
        var type = new PythonManagedTypeValue("_ContextManager") { Module = "decimal" };
        type.Attributes["__module__"] = Text("decimal");
        type.Attributes["__enter__"] = Method(
            "_ContextManager",
            "__enter__",
            (self, _) =>
            {
                var state = ManagerOf(self!);
                state.Saved = CurrentContext();
                _currentContext = state.Entered;
                return state.Entered;
            }
        );
        type.Attributes["__exit__"] = Method(
            "_ContextManager",
            "__exit__",
            (self, _) =>
            {
                var state = ManagerOf(self!);
                if (state.Saved is not null)
                {
                    _currentContext = state.Saved;
                }
                return PythonTruthValue.False;
            }
        );
        return type;
    }

    private static ContextManagerState ManagerOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: ContextManagerState state }
            ? state
            : throw TypeFault("the value is not a decimal context manager");

    // -----------------------------------------------------------------------------
    // The Decimal type
    // -----------------------------------------------------------------------------

    private static readonly PythonManagedTypeValue DecimalTupleType = CreateDecimalTupleType();

    private static PythonManagedTypeValue CreateDecimalTupleType()
    {
        var factory = (PythonBuiltinFunctionValue)PythonNamedTuple.Function;
        var created = factory.Invoke([Text("DecimalTuple"), Text("sign digits exponent")], default);
        if (created is PythonManagedTypeValue type)
        {
            type.Module = "decimal";
            type.Attributes["__module__"] = Text("decimal");
            return type;
        }
        throw TypeFault("the DecimalTuple type could not be built");
    }

    private sealed record FractionParts(BigInteger Numerator, BigInteger Denominator);

    private static PythonManagedTypeValue CreateDecimalType()
    {
        var type = new PythonManagedTypeValue("Decimal") { Module = "decimal" };
        type.Attributes["__module__"] = Text("decimal");
        type.Attributes["__qualname__"] = Text("Decimal");
        type.Attributes["__new__"] = PythonUserTypes.Method(
            "Decimal",
            "__new__",
            (_, arguments) => NewDecimal(arguments[0], Rest(arguments), [], [], default),
            (_, positional, names, values) =>
                NewDecimal(positional[0], Rest(positional), names, values, default)
        );
        type.Attributes["__repr__"] = Method(
            "Decimal",
            "__repr__",
            (self, _) => Text($"Decimal('{Str(self!)}')")
        );
        type.Attributes["__str__"] = Method("Decimal", "__str__", (self, _) => Text(Str(self!)));
        type.Attributes["__hash__"] = Method(
            "Decimal",
            "__hash__",
            (self, _) => PythonWholeNumberValue.Create(Hash(self!, DecimalOf(self!)))
        );
        type.Attributes["__bool__"] = Method(
            "Decimal",
            "__bool__",
            (self, _) =>
            {
                var state = DecimalOf(self!);
                return PythonTruthValue.FromBoolean(
                    state.Special != Special.Finite || !state.Coefficient.IsZero
                );
            }
        );
        type.Attributes["__int__"] = Method(
            "Decimal",
            "__int__",
            (self, _) => PythonWholeNumberValue.Create(ToInteger(DecimalOf(self!)))
        );
        type.Attributes["__float__"] = Method(
            "Decimal",
            "__float__",
            (self, _) => new PythonFloatingPointValue(ToDouble(DecimalOf(self!)))
        );
        type.Attributes["__pos__"] = Method("Decimal", "__pos__", (self, _) => self!);
        type.Attributes["__neg__"] = Method("Decimal", "__neg__", (self, _) => Negate(self!));
        type.Attributes["__abs__"] = Method("Decimal", "__abs__", (self, _) => Abs(self!));
        type.Attributes["__eq__"] = Method(
            "Decimal",
            "__eq__",
            (self, arguments) => EqualsValue(self!, arguments[0])
        );
        type.Attributes["__ne__"] = Method(
            "Decimal",
            "__ne__",
            (self, arguments) => NotEqualsValue(self!, arguments[0])
        );
        type.Attributes["__lt__"] = Method(
            "Decimal",
            "__lt__",
            (self, arguments) => Order(self!, arguments[0], Comparison.Less)
        );
        type.Attributes["__le__"] = Method(
            "Decimal",
            "__le__",
            (self, arguments) => Order(self!, arguments[0], Comparison.LessEqual)
        );
        type.Attributes["__gt__"] = Method(
            "Decimal",
            "__gt__",
            (self, arguments) => Order(self!, arguments[0], Comparison.Greater)
        );
        type.Attributes["__ge__"] = Method(
            "Decimal",
            "__ge__",
            (self, arguments) => Order(self!, arguments[0], Comparison.GreaterEqual)
        );
        AddArithmetic(type);
        type.Attributes["as_tuple"] = Method(
            "Decimal",
            "as_tuple",
            (self, _) => AsTuple(DecimalOf(self!))
        );
        type.Attributes["adjusted"] = Method(
            "Decimal",
            "adjusted",
            (self, _) =>
            {
                var state = DecimalOf(self!);
                if (state.Special != Special.Finite)
                {
                    throw Signal("InvalidOperation");
                }
                return PythonWholeNumberValue.Create(Adjusted(state));
            }
        );
        type.Attributes["as_integer_ratio"] = Method(
            "Decimal",
            "as_integer_ratio",
            (self, _) => AsIntegerRatio(DecimalOf(self!))
        );
        type.Attributes["is_finite"] = Predicate(
            "is_finite",
            state => state.Special == Special.Finite
        );
        type.Attributes["is_infinite"] = Predicate(
            "is_infinite",
            state => state.Special == Special.Infinity
        );
        type.Attributes["is_nan"] = Predicate(
            "is_nan",
            state => state.Special is Special.NaN or Special.SNaN
        );
        type.Attributes["is_snan"] = Predicate("is_snan", state => state.Special == Special.SNaN);
        type.Attributes["is_qnan"] = Predicate("is_qnan", state => state.Special == Special.NaN);
        type.Attributes["is_signed"] = Predicate("is_signed", state => state.Sign != 0);
        type.Attributes["copy_abs"] = Method(
            "Decimal",
            "copy_abs",
            (self, _) => Copy(DecimalOf(self!) with { Sign = 0 })
        );
        type.Attributes["copy_negate"] = Method(
            "Decimal",
            "copy_negate",
            (self, _) =>
            {
                var state = DecimalOf(self!);
                return Copy(state with { Sign = state.Sign == 0 ? 1 : 0 });
            }
        );
        type.Attributes["copy_sign"] = Method(
            "Decimal",
            "copy_sign",
            (self, arguments) =>
            {
                var state = DecimalOf(self!);
                var other = DecimalOf(RequireDecimal(arguments[0]));
                return Copy(state with { Sign = other.Sign });
            }
        );
        type.Attributes["quantize"] = MethodWithKeywords(
            "Decimal",
            "quantize",
            (self, arguments) => Quantize(self!, arguments, [], [], default),
            (self, positional, names, values) => Quantize(self!, positional, names, values, default)
        );
        type.Attributes["sqrt"] = Method(
            "Decimal",
            "sqrt",
            (self, _) => SquareRoot(self!, default)
        );
        type.Attributes["from_float"] = new PythonClassMethodValue(
            Method(
                "Decimal",
                "from_float",
                (cls, arguments) =>
                    CopyTo(
                        cls!,
                        arguments[0] is PythonFloatingPointValue floating
                            ? FromDouble(floating.Value)
                            : throw TypeFault(
                                $"from_float() requires a float, not "
                                    + $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}'"
                            )
                    )
            )
        );
        type.Attributes["__getattr__"] = Method(
            "Decimal",
            "__getattr__",
            (_, arguments) =>
                throw ManagedObjectProtocols.Fault(
                    "DPY4060",
                    "'decimal.Decimal' object has no attribute "
                        + "'"
                        + (
                            arguments[0] is PythonTextValue name
                                ? name.Value
                                : ManagedObjectProtocols.GetTypeName(arguments[0])
                        )
                        + "'",
                    default,
                    "AttributeError"
                )
        );
        type.Attributes["max"] = Method(
            "Decimal",
            "max",
            (self, arguments) => Extreme(self!, arguments[0], maximum: true)
        );
        type.Attributes["min"] = Method(
            "Decimal",
            "min",
            (self, arguments) => Extreme(self!, arguments[0], maximum: false)
        );
        type.Attributes["__format__"] = Method(
            "Decimal",
            "__format__",
            (self, arguments) => Format(self!, arguments[0])
        );
        type.Attributes["__round__"] = Method(
            "Decimal",
            "__round__",
            (self, arguments) =>
                RoundValue(
                    self!,
                    arguments.Count == 0 || arguments[0] is PythonNoneValue ? null : arguments[0]
                ),
            ["ndigits"],
            [PythonNoneValue.Instance]
        );
        return type;
    }

    private static PythonProtocolFunctionValue Predicate(
        string name,
        Func<DecimalState, bool> predicate
    ) =>
        Method(
            "Decimal",
            name,
            (self, _) => PythonTruthValue.FromBoolean(predicate(DecimalOf(self!)))
        );

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    ) => PythonUserTypes.Method(owner, name, body, null, parameters, defaults);

    private static PythonProtocolFunctionValue MethodWithKeywords(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker keywords
    ) => PythonUserTypes.Method(owner, name, body, keywords);

    private static PythonTextValue Text(string value) => new(value);

    private static PythonRuntimeException TypeFault(string message, TextSpan span = default) =>
        ManagedObjectProtocols.Fault("DPY4060", message, span, "TypeError");

    // -----------------------------------------------------------------------------
    // Sharing with statistics
    // -----------------------------------------------------------------------------

    /// <summary>
    /// A Decimal's exact rational value: 0 for finite, 1 for NaN, 2 for +Infinity,
    /// 3 for -Infinity and -1 when the value is not a Decimal.
    /// </summary>
    internal static int TryDecimalRatio(
        PythonValue value,
        out BigInteger numerator,
        out BigInteger denominator
    )
    {
        numerator = BigInteger.Zero;
        denominator = BigInteger.One;
        if (value is not PythonManagedObjectValue { Payload: DecimalState state })
        {
            return -1;
        }
        switch (state.Special)
        {
            case Special.NaN:
            case Special.SNaN:
                return 1;
            case Special.Infinity:
                return state.Sign == 1 ? 3 : 2;
        }
        (numerator, denominator) = RatioOf(state);
        return 0;
    }

    /// <summary>`Decimal(numerator) / Decimal(denominator)` in the current context.</summary>
    internal static PythonValue DecimalFromRatio(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsOne)
        {
            return Copy(
                new DecimalState(
                    numerator.Sign < 0 ? 1 : 0,
                    BigInteger.Abs(numerator),
                    0,
                    Special.Finite,
                    string.Empty
                )
            );
        }
        var left = Copy(
            new DecimalState(
                numerator.Sign < 0 ? 1 : 0,
                BigInteger.Abs(numerator),
                0,
                Special.Finite,
                string.Empty
            )
        );
        var right = Copy(
            new DecimalState(
                denominator.Sign < 0 ? 1 : 0,
                BigInteger.Abs(denominator),
                0,
                Special.Finite,
                string.Empty
            )
        );
        return Divide(DecimalOf(left), DecimalOf(right), default);
    }

    /// <summary>A special Decimal value (NaN or a signed Infinity).</summary>
    internal static PythonManagedObjectValue DecimalSpecial(string name, bool negative)
    {
        var special = name == "NaN" ? Special.NaN : Special.Infinity;
        return Copy(new DecimalState(negative ? 1 : 0, BigInteger.Zero, 0, special, string.Empty));
    }

    /// <summary>`next_plus()`/`next_minus()` for the sqrt-of-ratio correction.</summary>
    private static DecimalState NextValue(DecimalState state, bool plus)
    {
        var increaseMagnitude = plus ^ (state.Sign == 1);
        var coefficient = state.Coefficient;
        var exponent = state.Exponent;
        if (increaseMagnitude)
        {
            coefficient += 1;
        }
        else if (coefficient > 1)
        {
            coefficient -= 1;
        }
        else
        {
            var digits = Math.Max(Digits(coefficient).Length, 1);
            coefficient = BigInteger.Pow(10, digits) - 1;
            exponent -= 1;
        }
        return state with { Coefficient = coefficient, Exponent = exponent };
    }

    /// <summary>The decimal square root of an exact ratio, as statistics' stdev answers.</summary>
    internal static PythonValue DecimalSqrtOfRatio(BigInteger numerator, BigInteger denominator)
    {
        if (numerator.IsZero)
        {
            return Copy(new DecimalState(0, BigInteger.Zero, -1, Special.Finite, string.Empty));
        }
        if (numerator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }
        var quotient = DecimalOf(DecimalFromRatio(numerator, denominator));
        var root = DecimalOf(SquareRoot(Copy(quotient), default));
        var (rootNumerator, rootDenominator) = RatioOf(root);
        var plus = NextValue(root, plus: true);
        var (plusNumerator, plusDenominator) = RatioOf(plus);
        if (
            4 * numerator * BigInteger.Pow(rootDenominator * plusDenominator, 2)
            > denominator
                * BigInteger.Pow(
                    (rootDenominator * plusNumerator) + (plusDenominator * rootNumerator),
                    2
                )
        )
        {
            return Copy(plus);
        }
        var minus = NextValue(root, plus: false);
        var (minusNumerator, minusDenominator) = RatioOf(minus);
        if (
            4 * numerator * BigInteger.Pow(rootDenominator * minusDenominator, 2)
            < denominator
                * BigInteger.Pow(
                    (rootDenominator * minusNumerator) + (minusDenominator * rootNumerator),
                    2
                )
        )
        {
            return Copy(minus);
        }
        return Copy(root);
    }

    // -----------------------------------------------------------------------------
    // Comparisons
    // -----------------------------------------------------------------------------

    private enum Comparison
    {
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
    }

    /// <summary>A value comparison may see: a Decimal, an int, or a float.</summary>
    private readonly record struct Comparable(DecimalState? State, double? Float);

    private static Comparable? AsComparable(PythonValue value) =>
        value switch
        {
            PythonManagedObjectValue { Payload: DecimalState state } => new Comparable(state, null),
            PythonWholeNumberValue whole => new Comparable(
                new DecimalState(
                    whole.Value.Sign < 0 ? 1 : 0,
                    BigInteger.Abs(whole.Value),
                    0,
                    Special.Finite,
                    string.Empty
                ),
                null
            ),
            PythonTruthValue truth => new Comparable(
                new DecimalState(
                    0,
                    truth.Value ? BigInteger.One : BigInteger.Zero,
                    0,
                    Special.Finite,
                    string.Empty
                ),
                null
            ),
            PythonFloatingPointValue floating => new Comparable(null, floating.Value),
            _ => null,
        };

    private static Comparable Normalize(Comparable value) =>
        value.State is not null ? value : new Comparable(FromDouble(value.Float!.Value), null);

    private static PythonValue EqualsValue(PythonValue self, PythonValue other)
    {
        var state = DecimalOf(self);
        if (AsComparable(other) is not { } comparable)
        {
            return PythonNotImplementedValue.Instance;
        }
        var right = Normalize(comparable).State!;
        if (state.Special == Special.SNaN || right.Special == Special.SNaN)
        {
            throw Signal("InvalidOperation");
        }
        if (state.Special is Special.NaN || right.Special is Special.NaN)
        {
            return PythonTruthValue.False;
        }
        return PythonTruthValue.FromBoolean(CompareStates(state, right) == 0);
    }

    private static PythonValue NotEqualsValue(PythonValue self, PythonValue other)
    {
        var state = DecimalOf(self);
        if (AsComparable(other) is not { } comparable)
        {
            return PythonNotImplementedValue.Instance;
        }
        var right = Normalize(comparable).State!;
        if (state.Special == Special.SNaN || right.Special == Special.SNaN)
        {
            throw Signal("InvalidOperation");
        }
        if (state.Special is Special.NaN || right.Special is Special.NaN)
        {
            return PythonTruthValue.True;
        }
        return PythonTruthValue.FromBoolean(CompareStates(state, right) != 0);
    }

    private static PythonValue Order(PythonValue self, PythonValue other, Comparison comparison)
    {
        var state = DecimalOf(self);
        if (AsComparable(other) is not { } comparable)
        {
            return PythonNotImplementedValue.Instance;
        }
        var right = Normalize(comparable).State!;
        if (
            state.Special is Special.NaN or Special.SNaN
            || right.Special is Special.NaN or Special.SNaN
        )
        {
            throw Signal("InvalidOperation");
        }
        var order = CompareStates(state, right);
        return PythonTruthValue.FromBoolean(
            comparison switch
            {
                Comparison.Less => order < 0,
                Comparison.LessEqual => order <= 0,
                Comparison.Greater => order > 0,
                _ => order >= 0,
            }
        );
    }

    private static int CompareStates(DecimalState left, DecimalState right)
    {
        var leftInfinity = left.Special == Special.Infinity;
        var rightInfinity = right.Special == Special.Infinity;
        if (leftInfinity || rightInfinity)
        {
            if (leftInfinity && rightInfinity)
            {
                return left.Sign.CompareTo(right.Sign) * -1;
            }
            if (leftInfinity)
            {
                return left.Sign == 1 ? -1 : 1;
            }
            return right.Sign == 1 ? 1 : -1;
        }
        if (left.Coefficient.IsZero && right.Coefficient.IsZero)
        {
            return 0;
        }
        if (left.Coefficient.IsZero)
        {
            return right.Sign == 1 ? 1 : -1;
        }
        if (right.Coefficient.IsZero)
        {
            return left.Sign == 1 ? -1 : 1;
        }
        if (left.Sign != right.Sign)
        {
            return left.Sign == 1 ? -1 : 1;
        }
        var magnitude = CompareMagnitude(left, right);
        return left.Sign == 1 ? -magnitude : magnitude;
    }

    private static int CompareMagnitude(DecimalState left, DecimalState right)
    {
        var leftAdjusted = Adjusted(left);
        var rightAdjusted = Adjusted(right);
        if (leftAdjusted != rightAdjusted)
        {
            return leftAdjusted < rightAdjusted ? -1 : 1;
        }
        var leftDigits = Digits(left.Coefficient);
        var rightDigits = Digits(right.Coefficient);
        var length = Math.Max(leftDigits.Length, rightDigits.Length);
        for (var index = 0; index < length; index++)
        {
            var leftDigit = index < leftDigits.Length ? leftDigits[index] : '0';
            var rightDigit = index < rightDigits.Length ? rightDigits[index] : '0';
            if (leftDigit != rightDigit)
            {
                return leftDigit < rightDigit ? -1 : 1;
            }
        }
        return 0;
    }

    // -----------------------------------------------------------------------------
    // Arithmetic
    // -----------------------------------------------------------------------------

    private static void AddArithmetic(PythonManagedTypeValue type)
    {
        AddOperation(type, "__add__", "__radd__", "+", Add);
        AddOperation(type, "__sub__", "__rsub__", "-", Subtract);
        AddOperation(type, "__mul__", "__rmul__", "*", Multiply);
        AddOperation(type, "__truediv__", "__rtruediv__", "/", Divide);
        AddOperation(type, "__floordiv__", "__rfloordiv__", "//", FloorDivide);
        AddOperation(type, "__mod__", "__rmod__", "%", Modulo);
        AddOperation(type, "__divmod__", "__rdivmod__", "divmod()", DivMod);
        type.Attributes["__pow__"] = Method(
            "Decimal",
            "__pow__",
            (self, arguments) => Power(self!, arguments[0], default)
        );
        type.Attributes["__rpow__"] = Method(
            "Decimal",
            "__rpow__",
            (self, arguments) => ReversePower(self!, arguments[0], default)
        );
    }

    private static void AddOperation(
        PythonManagedTypeValue type,
        string forward,
        string reverse,
        string symbol,
        Func<DecimalState, DecimalState, TextSpan, PythonValue> operation
    )
    {
        type.Attributes[forward] = Method(
            "Decimal",
            forward,
            (self, arguments) =>
            {
                if (arguments[0] is PythonFloatingPointValue)
                {
                    throw TypeFault(
                        $"unsupported operand type(s) for {symbol}: 'decimal.Decimal' and "
                            + "'float'"
                    );
                }
                return Operand(arguments[0]) is { } other
                    ? operation(DecimalOf(self!), other, default)
                    : PythonNotImplementedValue.Instance;
            }
        );
        type.Attributes[reverse] = Method(
            "Decimal",
            reverse,
            (self, arguments) =>
            {
                if (arguments[0] is PythonFloatingPointValue)
                {
                    throw TypeFault(
                        $"unsupported operand type(s) for {symbol}: 'float' and "
                            + "'decimal.Decimal'"
                    );
                }
                return Operand(arguments[0]) is { } other
                    ? operation(other, DecimalOf(self!), default)
                    : PythonNotImplementedValue.Instance;
            }
        );
    }

    private static DecimalState? Operand(PythonValue other) =>
        other switch
        {
            PythonManagedObjectValue { Payload: DecimalState state } => state,
            PythonWholeNumberValue whole => new DecimalState(
                whole.Value.Sign < 0 ? 1 : 0,
                BigInteger.Abs(whole.Value),
                0,
                Special.Finite,
                string.Empty
            ),
            PythonTruthValue truth => new DecimalState(
                0,
                truth.Value ? BigInteger.One : BigInteger.Zero,
                0,
                Special.Finite,
                string.Empty
            ),
            _ => null,
        };

    private static DecimalState? NanResult(DecimalState left, DecimalState right, bool signaling)
    {
        if (left.Special == Special.SNaN || right.Special == Special.SNaN)
        {
            throw Signal("InvalidOperation");
        }
        if (left.Special == Special.NaN)
        {
            return left;
        }
        if (right.Special == Special.NaN)
        {
            return right;
        }
        _ = signaling;
        return null;
    }

    private static PythonManagedObjectValue Add(
        DecimalState left,
        DecimalState right,
        TextSpan span
    )
    {
        if (NanResult(left, right, false) is { } nan)
        {
            return Copy(nan);
        }
        if (left.Special == Special.Infinity || right.Special == Special.Infinity)
        {
            if (left.Special == Special.Infinity && right.Special == Special.Infinity)
            {
                if (left.Sign != right.Sign)
                {
                    throw Signal("InvalidOperation", span);
                }
                return Copy(left);
            }
            return Copy(left.Special == Special.Infinity ? left : right);
        }
        var (leftCoefficient, rightCoefficient, exponent) = Align(left, right);
        var signedLeft = left.Sign == 1 ? -leftCoefficient : leftCoefficient;
        var signedRight = right.Sign == 1 ? -rightCoefficient : rightCoefficient;
        var total = signedLeft + signedRight;
        var sign = total.Sign < 0 ? 1 : 0;
        var coefficient = BigInteger.Abs(total);
        return Copy(RoundToPrecision(sign, coefficient, exponent, span));
    }

    private static PythonManagedObjectValue Subtract(
        DecimalState left,
        DecimalState right,
        TextSpan span
    )
    {
        var negated = right with { Sign = right.Sign == 0 ? 1 : 0 };
        return Add(left, negated, span);
    }

    private static (BigInteger Left, BigInteger Right, long Exponent) Align(
        DecimalState left,
        DecimalState right
    )
    {
        if (left.Exponent == right.Exponent)
        {
            return (left.Coefficient, right.Coefficient, left.Exponent);
        }
        if (left.Exponent > right.Exponent)
        {
            return (
                left.Coefficient * BigInteger.Pow(10, (int)(left.Exponent - right.Exponent)),
                right.Coefficient,
                right.Exponent
            );
        }
        return (
            left.Coefficient,
            right.Coefficient * BigInteger.Pow(10, (int)(right.Exponent - left.Exponent)),
            left.Exponent
        );
    }

    private static PythonManagedObjectValue Multiply(
        DecimalState left,
        DecimalState right,
        TextSpan span
    )
    {
        if (NanResult(left, right, false) is { } nan)
        {
            return Copy(nan);
        }
        var sign = left.Sign ^ right.Sign;
        if (left.Special == Special.Infinity || right.Special == Special.Infinity)
        {
            var finite = left.Special == Special.Infinity ? right : left;
            if (finite.Special == Special.Finite && finite.Coefficient.IsZero)
            {
                throw Signal("InvalidOperation", span);
            }
            return Copy(new DecimalState(sign, BigInteger.Zero, 0, Special.Infinity, string.Empty));
        }
        var coefficient = left.Coefficient * right.Coefficient;
        var exponent = left.Exponent + right.Exponent;
        return Copy(RoundToPrecision(sign, coefficient, exponent, span));
    }

    private static PythonManagedObjectValue Divide(
        DecimalState left,
        DecimalState right,
        TextSpan span
    )
    {
        if (NanResult(left, right, false) is { } nan)
        {
            return Copy(nan);
        }
        var context = ContextOf(CurrentContext());
        var sign = left.Sign ^ right.Sign;
        if (left.Special == Special.Infinity || right.Special == Special.Infinity)
        {
            if (left.Special == Special.Infinity && right.Special == Special.Infinity)
            {
                throw Signal("InvalidOperation", span);
            }
            if (left.Special == Special.Infinity)
            {
                return Infinity(sign);
            }
            return Copy(
                new DecimalState(
                    sign,
                    BigInteger.Zero,
                    Etiny(context),
                    Special.Finite,
                    string.Empty
                )
            );
        }
        if (right.Coefficient.IsZero)
        {
            if (left.Coefficient.IsZero)
            {
                throw Signal("DivisionUndefined", span);
            }
            throw Signal("DivisionByZero", span);
        }
        if (left.Coefficient.IsZero)
        {
            return Copy(
                RoundToPrecision(sign, BigInteger.Zero, left.Exponent - right.Exponent, span)
            );
        }
        var shift =
            Digits(right.Coefficient).Length - Digits(left.Coefficient).Length + context.Prec + 1;
        var exponent = left.Exponent - right.Exponent - shift;
        BigInteger quotient;
        BigInteger remainder;
        if (shift >= 0)
        {
            var dividend = left.Coefficient * BigInteger.Pow(10, shift);
            quotient = BigInteger.DivRem(dividend, right.Coefficient, out remainder);
        }
        else
        {
            var divisor = right.Coefficient * BigInteger.Pow(10, -shift);
            quotient = BigInteger.DivRem(left.Coefficient, divisor, out remainder);
        }
        if (!remainder.IsZero)
        {
            if ((quotient % 5).IsZero)
            {
                quotient += 1;
            }
        }
        else
        {
            var ideal = left.Exponent - right.Exponent;
            while (exponent < ideal && (quotient % 10).IsZero)
            {
                quotient /= 10;
                exponent++;
            }
        }
        return Copy(RoundToPrecision(sign, quotient, exponent, span));
    }

    private static PythonManagedObjectValue Negate(PythonValue self)
    {
        var state = DecimalOf(self);
        return Copy(state with { Sign = state.Sign == 0 ? 1 : 0 });
    }

    private static PythonManagedObjectValue Abs(PythonValue self) =>
        Copy(DecimalOf(self) with { Sign = 0 });

    private static PythonManagedObjectValue Infinity(int sign) =>
        new(
            DecimalType,
            new DecimalState(sign, BigInteger.Zero, 0, Special.Infinity, string.Empty)
        );

    private static long Etiny(DecimalContext context) => context.Emin - context.Prec + 1;

    private sealed record DivModResult(PythonValue Quotient, PythonValue Remainder);

    private static PythonValue FloorDivide(DecimalState left, DecimalState right, TextSpan span)
    {
        if (NanResult(left, right, false) is { } nan)
        {
            return Copy(nan);
        }
        if (left.Special == Special.Infinity)
        {
            throw Signal("InvalidOperation", span);
        }
        if (right.Special != Special.Infinity && right.Coefficient.IsZero)
        {
            throw left.Coefficient.IsZero
                ? Signal("DivisionUndefined", span)
                : Signal("DivisionByZero", span);
        }
        return DivideParts(left, right, span).Quotient;
    }

    private static PythonValue Modulo(DecimalState left, DecimalState right, TextSpan span)
    {
        if (NanResult(left, right, false) is { } nan)
        {
            return Copy(nan);
        }
        if (left.Special == Special.Infinity)
        {
            throw Signal("InvalidOperation", span);
        }
        if (right.Special != Special.Infinity && right.Coefficient.IsZero)
        {
            throw left.Coefficient.IsZero
                ? Signal("DivisionUndefined", span)
                : Signal("InvalidOperation", span);
        }
        return DivideParts(left, right, span).Remainder;
    }

    private static PythonValue DivMod(DecimalState left, DecimalState right, TextSpan span)
    {
        if (NanResult(left, right, false) is { } nan)
        {
            return new PythonTupleValue([Copy(nan), Copy(nan)]);
        }
        if (left.Special == Special.Infinity)
        {
            throw Signal("InvalidOperation", span);
        }
        if (right.Special != Special.Infinity && right.Coefficient.IsZero)
        {
            if (left.Coefficient.IsZero)
            {
                throw Signal("DivisionUndefined", span);
            }
            throw new PythonRaisedException(
                new PythonExceptionValue(
                    "InvalidOperation",
                    "[<class 'decimal.InvalidOperation'>, <class 'decimal.DivisionByZero'>]"
                )
                {
                    ManagedType = ExceptionTypes["InvalidOperation"],
                }
            );
        }
        var parts = DivideParts(left, right, span);
        return new PythonTupleValue([parts.Quotient, parts.Remainder]);
    }

    private static DivModResult DivideParts(DecimalState left, DecimalState right, TextSpan span)
    {
        var context = ContextOf(CurrentContext());
        var sign = left.Sign ^ right.Sign;
        var ideal =
            right.Special == Special.Infinity
                ? left.Exponent
                : Math.Min(left.Exponent, right.Exponent);
        var expDiff = Adjusted(left) - (right.Special == Special.Infinity ? 0 : Adjusted(right));
        if (left.Coefficient.IsZero || right.Special == Special.Infinity || expDiff <= -2)
        {
            return new DivModResult(
                Copy(new DecimalState(sign, BigInteger.Zero, 0, Special.Finite, string.Empty)),
                Copy(Fix(Rescale(left, ideal, context.Rounding, span)))
            );
        }
        if (expDiff <= context.Prec)
        {
            var (leftCoefficient, rightCoefficient, _) = Align(left, right);
            var quotient = BigInteger.DivRem(leftCoefficient, rightCoefficient, out var remainder);
            if (quotient < BigInteger.Pow(10, context.Prec))
            {
                return new DivModResult(
                    Copy(new DecimalState(sign, quotient, 0, Special.Finite, string.Empty)),
                    Copy(
                        Fix(
                            new DecimalState(
                                left.Sign,
                                remainder,
                                ideal,
                                Special.Finite,
                                string.Empty
                            )
                        )
                    )
                );
            }
        }
        throw Signal("DivisionImpossible", span);
    }

    private static DecimalState Fix(DecimalState state) =>
        RoundToPrecision(state.Sign, state.Coefficient, state.Exponent, default);

    private static DecimalState Rescale(
        DecimalState state,
        long targetExponent,
        string rounding,
        TextSpan span
    )
    {
        if (state.Exponent == targetExponent)
        {
            return state;
        }
        if (state.Exponent > targetExponent)
        {
            return state with
            {
                Coefficient =
                    state.Coefficient * BigInteger.Pow(10, (int)(state.Exponent - targetExponent)),
                Exponent = targetExponent,
            };
        }
        var drop = (int)(targetExponent - state.Exponent);
        var divisor = BigInteger.Pow(10, drop);
        var quotient = BigInteger.DivRem(state.Coefficient, divisor, out var remainder);
        if (ShouldRoundUp(state.Sign, quotient, remainder, divisor, rounding))
        {
            quotient += 1;
        }
        return new DecimalState(state.Sign, quotient, targetExponent, Special.Finite, string.Empty);
    }

    private static bool ShouldRoundUp(
        int sign,
        BigInteger quotient,
        BigInteger remainder,
        BigInteger divisor,
        string rounding
    )
    {
        if (remainder.IsZero)
        {
            return false;
        }
        var half = divisor / 2;
        return rounding switch
        {
            "ROUND_DOWN" => false,
            "ROUND_UP" => true,
            "ROUND_HALF_UP" => remainder >= half,
            "ROUND_HALF_DOWN" => remainder > half,
            "ROUND_HALF_EVEN" => remainder > half || (remainder == half && !quotient.IsEven),
            "ROUND_CEILING" => sign == 0,
            "ROUND_FLOOR" => sign == 1,
            _ => quotient % 10 == 0 || quotient % 10 == 5,
        };
    }

    private static DecimalState RoundToPrecision(
        int sign,
        BigInteger coefficient,
        long exponent,
        TextSpan span
    )
    {
        var context = ContextOf(CurrentContext());
        var etiny = Etiny(context);
        if (coefficient.IsZero)
        {
            var clamped = Math.Min(Math.Max(exponent, etiny), context.Emax);
            return new DecimalState(sign, BigInteger.Zero, clamped, Special.Finite, string.Empty);
        }
        var digits = Digits(coefficient).Length;
        while (digits > context.Prec && coefficient % 10 == 0)
        {
            coefficient /= 10;
            exponent++;
            digits--;
        }
        var adjusted = exponent + digits - 1;
        if (adjusted > context.Emax)
        {
            throw Signal("Overflow", span);
        }
        var exponentMin = digits + exponent - context.Prec;
        if (exponentMin < etiny)
        {
            exponentMin = etiny;
        }
        if (exponent >= exponentMin)
        {
            return new DecimalState(sign, coefficient, exponent, Special.Finite, string.Empty);
        }
        var drop = exponentMin - exponent;
        if (drop >= digits)
        {
            // Every digit is discarded; round the leading digit position.
            var divisor = BigInteger.Pow(10, (int)drop);
            var rounded = ShouldRoundUp(
                sign,
                BigInteger.Zero,
                coefficient,
                divisor,
                context.Rounding
            )
                ? BigInteger.One
                : BigInteger.Zero;
            var roundedExponent = exponentMin;
            var roundedAdjusted = roundedExponent + (rounded.IsZero ? 1 : 1) - 1;
            if (roundedAdjusted > context.Emax)
            {
                throw Signal("Overflow", span);
            }
            return new DecimalState(sign, rounded, roundedExponent, Special.Finite, string.Empty);
        }
        var power = BigInteger.Pow(10, (int)drop);
        var quotient = BigInteger.DivRem(coefficient, power, out var remainder);
        if (ShouldRoundUp(sign, quotient, remainder, power, context.Rounding))
        {
            quotient += 1;
            if (Digits(quotient).Length > context.Prec)
            {
                quotient /= 10;
                exponentMin += 1;
            }
        }
        return new DecimalState(sign, quotient, exponentMin, Special.Finite, string.Empty);
    }

    private static PythonValue Power(PythonValue self, PythonValue exponent, TextSpan span)
    {
        var state = DecimalOf(self);
        var power = IntegerValue(exponent);
        if (power is not null)
        {
            return PowerState(state, power.Value, span);
        }
        if (exponent is PythonFloatingPointValue)
        {
            throw TypeFault(
                "unsupported operand type(s) for ** or pow(): 'decimal.Decimal' and " + "'float'",
                span
            );
        }
        if (exponent is PythonManagedObjectValue { Payload: DecimalState exponentState })
        {
            return RationalPower(state, exponentState, span);
        }
        return PythonNotImplementedValue.Instance;
    }

    private static PythonManagedObjectValue RationalPower(
        DecimalState baseState,
        DecimalState exponentState,
        TextSpan span
    )
    {
        var numerator =
            exponentState.Sign == 1 ? -exponentState.Coefficient : exponentState.Coefficient;
        var denominator = BigInteger.One;
        if (exponentState.Exponent >= 0)
        {
            numerator *= BigInteger.Pow(10, (int)exponentState.Exponent);
        }
        else
        {
            denominator = BigInteger.Pow(10, (int)-exponentState.Exponent);
        }
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        if (!gcd.IsZero)
        {
            numerator /= gcd;
            denominator /= gcd;
        }
        if (denominator > 4096)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "fractional powers with a large denominator are not supported in this runtime",
                span,
                "NotImplementedError"
            );
        }
        var root = (int)denominator;
        if (baseState.Special == Special.SNaN)
        {
            throw Signal("InvalidOperation", span);
        }
        if (baseState.Special == Special.NaN)
        {
            return Copy(baseState);
        }
        if (numerator.IsZero)
        {
            return Copy(new DecimalState(0, BigInteger.One, 0, Special.Finite, string.Empty));
        }
        if (baseState.Special == Special.Infinity)
        {
            var odd = root % 2 == 1 && !numerator.IsEven;
            var sign = baseState.Sign == 1 && odd ? 1 : 0;
            return numerator.Sign < 0
                ? Copy(new DecimalState(sign, BigInteger.Zero, 0, Special.Finite, string.Empty))
                : Infinity(sign);
        }
        var negativeBase = baseState.Sign == 1;
        if (negativeBase && root % 2 == 0)
        {
            throw Signal("InvalidOperation", span);
        }
        if (baseState.Coefficient.IsZero)
        {
            return numerator.Sign < 0
                ? Infinity(negativeBase && !numerator.IsEven ? 1 : 0)
                : Copy(new DecimalState(0, BigInteger.Zero, 0, Special.Finite, string.Empty));
        }
        var magnitude = baseState with { Sign = 0 };
        var powered = ExactPower(magnitude, BigInteger.Abs(numerator));
        var result = RootOf(powered, root, span);
        var resultSign = negativeBase && !numerator.IsEven ? 1 : 0;
        result = result with { Sign = resultSign };
        return numerator.Sign < 0 ? Divide(One, result, span) : Copy(result);
    }

    private static readonly DecimalState One = new(
        0,
        BigInteger.One,
        0,
        Special.Finite,
        string.Empty
    );

    private static DecimalState ExactPower(DecimalState state, BigInteger exponent)
    {
        var limit = (int)BigInteger.Min(exponent, 1_000_000);
        return new DecimalState(
            0,
            BigInteger.Pow(state.Coefficient, limit),
            state.Exponent * limit,
            Special.Finite,
            string.Empty
        );
    }

    private static DecimalState RootOf(DecimalState state, int root, TextSpan span)
    {
        var context = ContextOf(CurrentContext());
        var adjust = (root - ((int)(state.Exponent % root) + root) % root) % root;
        var coefficient = state.Coefficient * BigInteger.Pow(10, adjust);
        var exponent = state.Exponent + adjust;
        var exact = IntegerNthRoot(coefficient, root);
        if (BigInteger.Pow(exact, root) == coefficient)
        {
            var digits = Digits(exact);
            var pad = Math.Max(0, context.Prec - digits.Length);
            return new DecimalState(
                0,
                exact * BigInteger.Pow(10, pad),
                (exponent / root) - pad,
                Special.Finite,
                string.Empty
            );
        }
        var extra = 0;
        while (
            Digits(coefficient * BigInteger.Pow(10, extra * root)).Length
            < root * (context.Prec + 2)
        )
        {
            extra++;
        }
        var scaled = coefficient * BigInteger.Pow(10, extra * root);
        var scaledRoot = IntegerNthRoot(scaled, root);
        var resultExponent = (exponent / root) - extra;
        return RoundToPrecision(0, scaledRoot, resultExponent, span);
    }

    private static BigInteger IntegerNthRoot(BigInteger value, int root)
    {
        if (value.IsZero)
        {
            return BigInteger.Zero;
        }
        var bits = (int)value.GetBitLength();
        var estimate = BigInteger.One << ((bits + root - 1) / root);
        while (true)
        {
            var powered = BigInteger.Pow(estimate, root - 1);
            var next = ((root - 1) * estimate + (value / powered)) / root;
            if (next >= estimate)
            {
                return estimate;
            }
            estimate = next;
        }
    }

    private static BigInteger? IntegerValue(PythonValue value)
    {
        switch (value)
        {
            case PythonWholeNumberValue whole:
                return whole.Value;
            case PythonTruthValue truth:
                return truth.Value ? BigInteger.One : BigInteger.Zero;
            case PythonManagedObjectValue { Payload: DecimalState state }
                when state.Special == Special.Finite:
            {
                if (state.Exponent >= 0)
                {
                    return state.Sign == 1
                        ? -(state.Coefficient * BigInteger.Pow(10, (int)state.Exponent))
                        : state.Coefficient * BigInteger.Pow(10, (int)state.Exponent);
                }
                var divisor = BigInteger.Pow(10, (int)-state.Exponent);
                return (state.Coefficient % divisor).IsZero
                    ? (
                        state.Sign == 1
                            ? -(state.Coefficient / divisor)
                            : state.Coefficient / divisor
                    )
                    : null;
            }
            default:
                return null;
        }
    }

    private static PythonManagedObjectValue PowerState(
        DecimalState state,
        BigInteger exponent,
        TextSpan span
    )
    {
        if (state.Special is Special.NaN or Special.SNaN)
        {
            return state.Special == Special.SNaN
                ? throw Signal("InvalidOperation", span)
                : Copy(state);
        }
        if (exponent.IsZero)
        {
            return Copy(new DecimalState(0, BigInteger.One, 0, Special.Finite, string.Empty));
        }
        if (state.Special == Special.Infinity)
        {
            var oddNegative = state.Sign == 1 && !exponent.IsEven;
            if (exponent.Sign < 0)
            {
                return Copy(
                    new DecimalState(
                        oddNegative ? 1 : 0,
                        BigInteger.Zero,
                        0,
                        Special.Finite,
                        string.Empty
                    )
                );
            }
            return Infinity(oddNegative ? 1 : 0);
        }
        if (state.Coefficient.IsZero)
        {
            return exponent.Sign < 0
                ? Infinity(state.Sign)
                : Copy(
                    new DecimalState(
                        state.Sign == 1 && !exponent.IsEven ? 1 : 0,
                        BigInteger.Zero,
                        0,
                        Special.Finite,
                        string.Empty
                    )
                );
        }
        if (exponent.Sign < 0)
        {
            var positive = PowerState(state, -exponent, span);
            return Divide(
                new DecimalState(0, BigInteger.One, 0, Special.Finite, string.Empty),
                DecimalOf(positive),
                span
            );
        }
        var limit = (int)BigInteger.Min(exponent, 1_000_000);
        var coefficient = BigInteger.Pow(state.Coefficient, limit);
        var resultExponent = state.Exponent * limit;
        var sign = state.Sign == 1 && !exponent.IsEven ? 1 : 0;
        return Copy(RoundToPrecision(sign, coefficient, resultExponent, span));
    }

    private static PythonValue ReversePower(PythonValue self, PythonValue value, TextSpan span)
    {
        var state = DecimalOf(self);
        if (value is PythonWholeNumberValue or PythonTruthValue)
        {
            var integer = IntegerValue(value)!.Value;
            var sign = integer.Sign < 0 ? 1 : 0;
            var baseState = new DecimalState(
                sign,
                BigInteger.Abs(integer),
                0,
                Special.Finite,
                string.Empty
            );
            return PowerState(baseState, IntegerValue(Copy(state)) ?? BigInteger.Zero, span);
        }
        return PythonNotImplementedValue.Instance;
    }

    // -----------------------------------------------------------------------------
    // quantize, sqrt, round, format
    // -----------------------------------------------------------------------------

    private static PythonManagedObjectValue Quantize(
        PythonValue self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        PythonValue? target = positional.Count > 0 ? positional[0] : null;
        string? rounding = null;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            switch (keywordNames[index])
            {
                case "exp":
                    target = keywordValues[index];
                    break;
                case "rounding":
                    rounding = keywordValues[index] is PythonTextValue text ? text.Value : null;
                    break;
                case "context":
                    break;
                default:
                    throw TypeFault(
                        $"quantize() got an unexpected keyword argument "
                            + $"'{keywordNames[index]}'",
                        span
                    );
            }
        }
        if (target is null)
        {
            throw TypeFault("quantize() missing required argument 'exp' (pos 1)", span);
        }
        if (target is not PythonManagedObjectValue { Payload: DecimalState targetState })
        {
            throw TypeFault(
                $"quantize() argument must be a Decimal, not "
                    + $"'{ManagedObjectProtocols.GetTypeName(target)}'",
                span
            );
        }
        var state = DecimalOf(self);
        if (state.Special == Special.SNaN || targetState.Special == Special.SNaN)
        {
            throw Signal("InvalidOperation", span);
        }
        if (state.Special == Special.NaN)
        {
            return Copy(state);
        }
        if (state.Special == Special.Infinity || targetState.Special != Special.Finite)
        {
            throw Signal("InvalidOperation", span);
        }
        var context = ContextOf(CurrentContext());
        var mode = rounding ?? context.Rounding;
        var rescaled = Rescale(state, targetState.Exponent, mode, span);
        if (Digits(rescaled.Coefficient).Length > context.Prec)
        {
            throw Signal("InvalidOperation", span);
        }
        return Copy(rescaled);
    }

    private static PythonManagedObjectValue SquareRoot(PythonValue self, TextSpan span)
    {
        var state = DecimalOf(self);
        if (state.Special == Special.SNaN)
        {
            throw Signal("InvalidOperation", span);
        }
        if (state.Special == Special.NaN)
        {
            return Copy(state);
        }
        if (state.Special == Special.Infinity)
        {
            return state.Sign == 1 ? throw Signal("InvalidOperation", span) : Copy(state);
        }
        if (state.Coefficient.IsZero)
        {
            return Copy(state);
        }
        if (state.Sign == 1)
        {
            throw Signal("InvalidOperation", span);
        }
        var context = ContextOf(CurrentContext());
        var squareState = state;
        if (squareState.Exponent % 2 != 0)
        {
            squareState = squareState with
            {
                Coefficient = squareState.Coefficient * 10,
                Exponent = squareState.Exponent - 1,
            };
        }
        var exactRoot = IntegerSquareRoot(squareState.Coefficient);
        if (exactRoot * exactRoot == squareState.Coefficient)
        {
            return Copy(RoundToPrecision(0, exactRoot, squareState.Exponent / 2, span));
        }
        var digits = Digits(state.Coefficient);
        var targetDigits = context.Prec + 3;
        var shift = 2 * targetDigits - state.Exponent - digits.Length;
        if (shift < 0)
        {
            shift = 0;
        }
        // Make the exponent even by adding an even number of decimal places.
        if ((state.Exponent - shift) % 2 != 0)
        {
            shift++;
        }
        var scaled = state.Coefficient * BigInteger.Pow(10, (int)shift);
        var root = IntegerSquareRoot(scaled);
        var resultExponent = (state.Exponent - shift) / 2;
        return Copy(RoundToPrecision(0, root, resultExponent, span));
    }

    private static BigInteger IntegerSquareRoot(BigInteger value)
    {
        if (value.IsZero)
        {
            return BigInteger.Zero;
        }
        var bits = (int)value.GetBitLength();
        var estimate = BigInteger.One << ((bits + 1) / 2);
        while (true)
        {
            var next = (estimate + (value / estimate)) >> 1;
            if (next >= estimate)
            {
                return estimate;
            }
            estimate = next;
        }
    }

    private static PythonValue RoundValue(PythonValue self, PythonValue? ndigits)
    {
        var state = DecimalOf(self);
        if (ndigits is null)
        {
            if (state.Special == Special.Infinity)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4060",
                    "cannot round an infinity",
                    default,
                    "OverflowError"
                );
            }
            if (state.Special is Special.NaN or Special.SNaN)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4060",
                    "cannot round a NaN",
                    default,
                    "ValueError"
                );
            }
            var rounded = Rescale(state, 0, "ROUND_HALF_EVEN", default);
            return PythonWholeNumberValue.Create(ToInteger(rounded));
        }
        var digits = (int)RequireInteger(ndigits, default);
        var target = new DecimalState(0, BigInteger.One, -digits, Special.Finite, string.Empty);
        return Quantize(self, [Copy(target)], [], [], default);
    }

    private static PythonManagedObjectValue Extreme(
        PythonValue self,
        PythonValue otherValue,
        bool maximum
    )
    {
        var left = DecimalOf(self);
        var right = DecimalOf(RequireDecimal(otherValue));
        if (left.Special == Special.SNaN || right.Special == Special.SNaN)
        {
            throw Signal("InvalidOperation");
        }
        if (left.Special == Special.NaN && right.Special == Special.NaN)
        {
            return Copy(left);
        }
        if (left.Special == Special.NaN)
        {
            return Copy(right);
        }
        if (right.Special == Special.NaN)
        {
            return Copy(left);
        }
        var order = CompareTotal(left, right);
        var takeLeft = maximum ? order >= 0 : order <= 0;
        return Copy(takeLeft ? left : right);
    }

    /// <summary>`compare_total`: equal values order by sign and exponent.</summary>
    private static int CompareTotal(DecimalState left, DecimalState right)
    {
        if (left.Sign != right.Sign)
        {
            return left.Sign == 1 ? -1 : 1;
        }
        var magnitude = CompareStates(left with { Sign = 0 }, right with { Sign = 0 });
        if (magnitude != 0)
        {
            return left.Sign == 1 ? -magnitude : magnitude;
        }
        if (left.Special != Special.Finite || right.Special != Special.Finite)
        {
            return 0;
        }
        return left.Sign == 0
            ? left.Exponent.CompareTo(right.Exponent)
            : right.Exponent.CompareTo(left.Exponent);
    }

    private static PythonTextValue Format(PythonValue self, PythonValue specValue)
    {
        if (specValue is not PythonTextValue spec)
        {
            throw TypeFault(
                "__format__() argument must be str, not "
                    + ManagedObjectProtocols.GetTypeName(specValue)
            );
        }
        var state = DecimalOf(self);
        if (spec.Value.Length == 0)
        {
            return Text(Str(self));
        }
        return Text(FormatSpec(state, spec.Value));
    }

    private static string FormatSpec(DecimalState state, string spec)
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
        var signOption = '-';
        if (i < n && spec[i] is '-' or '+' or ' ')
        {
            signOption = spec[i];
            i++;
        }
        if (i < n && spec[i] == 'z')
        {
            i++;
        }
        var alternate = false;
        if (i < n && spec[i] == '#')
        {
            alternate = true;
            i++;
        }
        var zeroPad = false;
        if (i + 1 < n && spec[i] == '0' && char.IsAsciiDigit(spec[i + 1]))
        {
            zeroPad = true;
            i++;
        }
        var widthStart = i;
        while (i < n && char.IsAsciiDigit(spec[i]))
        {
            i++;
        }
        var width =
            i > widthStart ? int.Parse(spec[widthStart..i], CultureInfo.InvariantCulture) : 0;
        var grouping = '\0';
        if (i < n && spec[i] is ',' or '_')
        {
            grouping = spec[i];
            i++;
        }
        var precision = -1;
        if (i < n && spec[i] == '.')
        {
            i++;
            var start = i;
            while (i < n && char.IsAsciiDigit(spec[i]))
            {
                i++;
            }
            if (i == start)
            {
                throw InvalidFormatString();
            }
            precision = int.Parse(spec[start..i], CultureInfo.InvariantCulture);
        }
        var type = 'f';
        if (i < n && spec[i] is 'e' or 'E' or 'f' or 'F' or 'g' or 'G' or '%')
        {
            type = spec[i];
            i++;
        }
        else if (i < n)
        {
            // A sign-less trailing character that is not a type: CPython rejects it.
            throw InvalidFormatString();
        }
        if (i != n)
        {
            throw InvalidFormatString();
        }
        var negative = state.Sign == 1;
        var sign =
            negative ? "-"
            : signOption == '-' ? string.Empty
            : signOption.ToString();
        var body = FormatNumberBody(state, type, precision, alternate);
        if (grouping != '\0')
        {
            body = GroupDecimal(body, grouping);
        }
        var padding = Math.Max(0, width - sign.Length - body.Length);
        if (zeroPad && align == '>')
        {
            body = new string('0', padding) + body;
            padding = 0;
        }
        var fillText = new string(fill, padding);
        return align switch
        {
            '<' => sign + body + fillText,
            '^' => fillText[..(padding / 2)] + sign + body + fillText[(padding / 2)..],
            '=' => sign + fillText + body,
            _ => fillText + sign + body,
        };
    }

    private static PythonRuntimeException InvalidFormatString() =>
        ManagedObjectProtocols.Fault("DPY4060", "invalid format string", default, "ValueError");

    private static string FormatNumberBody(
        DecimalState state,
        char type,
        int precision,
        bool alternate
    )
    {
        if (state.Special == Special.Infinity)
        {
            return "Infinity";
        }
        if (state.Special is Special.NaN or Special.SNaN)
        {
            return (state.Special == Special.SNaN ? "sNaN" : "NaN") + state.Payload;
        }
        var magnitude = state with { Sign = 0 };
        if (type is 'f' or 'F' or '%' && precision < 0)
        {
            var exponent = type == '%' ? magnitude.Exponent + 2 : magnitude.Exponent;
            var body = PlainDigits(Digits(magnitude.Coefficient), exponent);
            return type == '%' ? body + "%" : body;
        }
        if (type is 'f' or 'F' or '%')
        {
            var target = type == '%' ? -2L - precision : -precision;
            var rescaled = Rescale(magnitude, target, "ROUND_HALF_EVEN", default);
            var body = PlainDigits(Digits(rescaled.Coefficient), target);
            return type == '%' ? body + "%" : body;
        }
        if (type is 'g' or 'G' && precision < 0)
        {
            var plain = Str(Copy(magnitude)).Replace("E", "e", StringComparison.Ordinal);
            return type == 'G' ? plain.ToUpperInvariant() : plain;
        }
        if (type is 'e' or 'E' && precision < 0)
        {
            var rawDigits = Digits(magnitude.Coefficient);
            var exponent = Adjusted(magnitude);
            var fraction = rawDigits.Length > 1 ? rawDigits[1..] : string.Empty;
            return Scientific(rawDigits[0].ToString(), fraction, exponent, type);
        }
        var figures = type is 'g' or 'G'
            ? Math.Max(precision < 0 ? 6 : precision, 1)
            : (precision < 0 ? 6 : precision) + 1;
        var adjusted = Adjusted(magnitude);
        var roundExponent = adjusted - figures + 1;
        var rounded = Rescale(magnitude, roundExponent, "ROUND_HALF_EVEN", default);
        var digits = Digits(rounded.Coefficient);
        if (type is 'g' or 'G')
        {
            var exponent = (long)digits.Length + roundExponent - 1;
            if (exponent < -4 || exponent >= figures)
            {
                digits = digits.PadRight(figures, '0');
                var fraction = digits.Length > 1 ? digits[1..] : string.Empty;
                if (!alternate)
                {
                    fraction = fraction.TrimEnd('0');
                }
                return Scientific(digits[0].ToString(), fraction, exponent, type);
            }
            var plain = PlainDigits(digits, roundExponent);
            if (!alternate && plain.Contains('.', StringComparison.Ordinal))
            {
                plain = plain.TrimEnd('0').TrimEnd('.');
            }
            return plain;
        }
        digits = digits.PadRight(figures, '0');
        var scientificFraction = digits.Length > 1 ? digits[1..] : string.Empty;
        var scientificExponent = (long)digits.Length + roundExponent - 1;
        return Scientific(digits[0].ToString(), scientificFraction, scientificExponent, type);
    }

    private static string Scientific(string leading, string fraction, long exponent, char type)
    {
        var marker = type is 'E' or 'G' or 'F' ? "E" : "e";
        var text =
            leading
            + (fraction.Length != 0 ? "." + fraction : string.Empty)
            + marker
            + (exponent >= 0 ? "+" : string.Empty)
            + exponent.ToString(CultureInfo.InvariantCulture);
        return text;
    }

    private static string PlainDigits(string digits, long exponent)
    {
        if (exponent >= 0)
        {
            return digits + new string('0', (int)exponent);
        }
        var places = (int)-exponent;
        if (digits.Length <= places)
        {
            return "0." + new string('0', places - digits.Length) + digits;
        }
        return digits[..(digits.Length - places)] + "." + digits[(digits.Length - places)..];
    }

    private static string GroupDecimal(string body, char separator)
    {
        var point = body.IndexOf('.', StringComparison.Ordinal);
        var integerPart = point < 0 ? body : body[..point];
        var fraction = point < 0 ? string.Empty : body[point..];
        var suffix = integerPart.EndsWith('%') ? "%" : string.Empty;
        if (suffix.Length != 0)
        {
            integerPart = integerPart[..^1];
        }
        var digitsOnly = integerPart;
        var first = 1 + ((digitsOnly.Length - 1) % 3);
        var builder = new StringBuilder(digitsOnly.Length + (digitsOnly.Length / 3));
        builder.Append(digitsOnly[..first]);
        for (var index = first; index < digitsOnly.Length; index += 3)
        {
            builder.Append(separator).Append(digitsOnly, index, 3);
        }
        return builder + fraction + suffix;
    }

    // -----------------------------------------------------------------------------
    // Signals and exceptions
    // -----------------------------------------------------------------------------

    private static readonly string[] ExceptionNames =
    [
        "DecimalException",
        "InvalidOperation",
        "DivisionByZero",
        "Overflow",
        "Underflow",
        "Inexact",
        "Rounded",
        "Subnormal",
        "Clamped",
        "FloatOperation",
    ];

    private static readonly Dictionary<string, PythonManagedTypeValue> ExceptionTypes =
        CreateExceptionTypes();

    private static Dictionary<string, PythonManagedTypeValue> CreateExceptionTypes()
    {
        var arithmetic = PythonBuiltinTypes.GetExceptionType("ArithmeticError");
        var zeroDivision = PythonBuiltinTypes.GetExceptionType("ZeroDivisionError");
        var overflowError = PythonBuiltinTypes.GetExceptionType("OverflowError");
        var typeError = PythonBuiltinTypes.GetExceptionType("TypeError");
        var created = new Dictionary<string, PythonManagedTypeValue>(StringComparer.Ordinal);
        PythonManagedTypeValue Make(string name, params PythonValue[] bases)
        {
            var type = new PythonManagedTypeValue(name, exceptionBaseName: "ArithmeticError")
            {
                Module = "decimal",
                LayoutBase = arithmetic,
            };
            type.Attributes["__module__"] = Text("decimal");
            type.Attributes["__qualname__"] = Text(name);
            type.SetDeclaredBases(new PythonTupleValue([.. bases]));
            type.SetResolutionOrder(new PythonTupleValue([.. Linearize(type, bases)]));
            created[name] = type;
            return type;
        }
        var decimalException = Make("DecimalException", arithmetic);
        var inexact = Make("Inexact", decimalException);
        var rounded = Make("Rounded", decimalException);
        var subnormal = Make("Subnormal", decimalException);
        Make("InvalidOperation", decimalException);
        Make("DivisionByZero", decimalException, zeroDivision);
        Make("Overflow", inexact, rounded);
        Make("Underflow", inexact, rounded, subnormal);
        Make("Clamped", decimalException);
        Make("FloatOperation", decimalException, typeError);
        _ = overflowError;
        return created;
    }

    private static List<PythonValue> Linearize(
        PythonManagedTypeValue type,
        IReadOnlyList<PythonValue> bases
    )
    {
        var sequences = new List<List<PythonValue>>();
        foreach (var baseType in bases)
        {
            sequences.Add([.. PythonBuiltinTypes.GetMro(baseType).Elements]);
        }
        sequences.Add([.. bases]);
        var result = new List<PythonValue> { type };
        while (sequences.Any(sequence => sequence.Count != 0))
        {
            PythonValue? candidate = null;
            foreach (var sequence in sequences)
            {
                if (sequence.Count == 0)
                {
                    continue;
                }
                var head = sequence[0];
                if (
                    !sequences.Any(other =>
                        other.Skip(1).Any(entry => ReferenceEquals(entry, head))
                    )
                )
                {
                    candidate = head;
                    break;
                }
            }
            if (candidate is null)
            {
                throw TypeFault("cannot compute the decimal exception hierarchy");
            }
            result.Add(candidate);
            foreach (var sequence in sequences)
            {
                if (sequence.Count != 0 && ReferenceEquals(sequence[0], candidate))
                {
                    sequence.RemoveAt(0);
                }
            }
        }
        return result;
    }

    private static PythonRaisedException Signal(string signal, TextSpan span = default)
    {
        var exception = signal switch
        {
            "DivisionByZero" => "DivisionByZero",
            "Overflow" => "Overflow",
            "Underflow" => "Underflow",
            _ => "InvalidOperation",
        };
        return new PythonRaisedException(
            new PythonExceptionValue(exception, $"[<class 'decimal.{signal}'>]")
            {
                ManagedType = ExceptionTypes[exception],
            }
        );
    }

    // -----------------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------------

    private static PythonValue[] Rest(IReadOnlyList<PythonValue> arguments) =>
        [.. arguments.Skip(1)];

    private static PythonManagedObjectValue NewDecimal(
        PythonValue cls,
        PythonValue[] arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (arguments.Length > 2)
        {
            throw TypeFault(
                $"Decimal() takes at most 2 arguments ({arguments.Length} given)",
                span
            );
        }
        var value = arguments.Length > 0 ? arguments[0] : Text("0");
        for (var index = 0; index < keywordNames.Count; index++)
        {
            switch (keywordNames[index])
            {
                case "value":
                    value = keywordValues[index];
                    break;
                case "context":
                    break;
                default:
                    throw TypeFault(
                        $"Decimal() got an unexpected keyword argument "
                            + $"'{keywordNames[index]}'",
                        span
                    );
            }
        }
        var type = cls as PythonManagedTypeValue ?? throw TypeFault("Decimal requires a class");
        var state = value switch
        {
            PythonTextValue text => ParseDecimalString(text.Value, span),
            PythonWholeNumberValue whole => new DecimalState(
                whole.Value.Sign < 0 ? 1 : 0,
                BigInteger.Abs(whole.Value),
                0,
                Special.Finite,
                string.Empty
            ),
            PythonTruthValue truth => new DecimalState(
                0,
                truth.Value ? BigInteger.One : BigInteger.Zero,
                0,
                Special.Finite,
                string.Empty
            ),
            PythonFloatingPointValue floating => FromDouble(floating.Value),
            PythonManagedObjectValue { Payload: DecimalState existing } => existing,
            PythonTupleValue tuple => FromTuple(tuple.Elements, span),
            PythonListValue list => FromTuple(list.Elements, span),
            _ => throw TypeFault(
                $"conversion from {ManagedObjectProtocols.GetTypeName(value)} to Decimal "
                    + "is not supported",
                span
            ),
        };
        return new PythonManagedObjectValue(type, state);
    }

    private static DecimalState ParseDecimalString(string text, TextSpan span)
    {
        var value = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        var i = 0;
        var sign = 0;
        if (i < value.Length && value[i] is '-' or '+')
        {
            sign = value[i] == '-' ? 1 : 0;
            i++;
        }
        var rest = value[i..];
        var upper = rest.ToUpperInvariant();
        if (upper is "INF" or "INFINITY")
        {
            return new DecimalState(sign, BigInteger.Zero, 0, Special.Infinity, string.Empty);
        }
        if (
            upper.StartsWith("SNAN", StringComparison.Ordinal)
            || upper.StartsWith("NAN", StringComparison.Ordinal)
        )
        {
            var signaling = upper.StartsWith("SNAN", StringComparison.Ordinal);
            var payload = signaling ? rest[4..] : rest[3..];
            if (!payload.All(char.IsAsciiDigit))
            {
                throw Signal("ConversionSyntax", span);
            }
            payload = payload.TrimStart('0');
            return new DecimalState(
                sign,
                BigInteger.Zero,
                0,
                signaling ? Special.SNaN : Special.NaN,
                payload
            );
        }
        var start = i;
        var intStart = i;
        while (i < value.Length && char.IsAsciiDigit(value[i]))
        {
            i++;
        }
        var intPart = value[intStart..i];
        var fracPart = string.Empty;
        if (i < value.Length && value[i] == '.')
        {
            i++;
            var fracStart = i;
            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
            }
            fracPart = value[fracStart..i];
        }
        if (intPart.Length == 0 && fracPart.Length == 0)
        {
            throw Signal("ConversionSyntax", span);
        }
        var exponent = 0L;
        if (i < value.Length && value[i] is 'e' or 'E')
        {
            i++;
            var negative = false;
            if (i < value.Length && value[i] is '-' or '+')
            {
                negative = value[i] == '-';
                i++;
            }
            var expStart = i;
            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
            }
            if (i == expStart)
            {
                throw Signal("ConversionSyntax", span);
            }
            var expText = value[expStart..i];
            exponent =
                expText.Length > 9
                    ? (negative ? long.MinValue / 2 : long.MaxValue / 2)
                    : long.Parse(expText, CultureInfo.InvariantCulture) * (negative ? -1 : 1);
        }
        if (i != value.Length)
        {
            throw Signal("ConversionSyntax", span);
        }
        _ = start;
        var digits = (intPart + fracPart).TrimStart('0');
        var coefficient =
            digits.Length == 0
                ? BigInteger.Zero
                : BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        var resultExponent = exponent - fracPart.Length;
        return new DecimalState(sign, coefficient, resultExponent, Special.Finite, string.Empty);
    }

    private static DecimalState FromDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return new DecimalState(0, BigInteger.Zero, 0, Special.NaN, string.Empty);
        }
        if (double.IsInfinity(value))
        {
            return new DecimalState(
                value < 0 ? 1 : 0,
                BigInteger.Zero,
                0,
                Special.Infinity,
                string.Empty
            );
        }
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var sign = (int)(bits >> 63);
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        var mantissa = bits & ((1UL << 52) - 1);
        if (exponentBits == 0 && mantissa == 0)
        {
            return new DecimalState(sign, BigInteger.Zero, 0, Special.Finite, string.Empty);
        }
        BigInteger coefficient;
        long exponent;
        if (exponentBits == 0)
        {
            coefficient = mantissa;
            exponent = -1074;
        }
        else
        {
            coefficient = mantissa | (1UL << 52);
            exponent = exponentBits - 1075;
        }
        if (exponent >= 0)
        {
            coefficient <<= (int)exponent;
            exponent = 0;
        }
        else
        {
            coefficient *= BigInteger.Pow(5, (int)-exponent);
        }
        while (!coefficient.IsZero && (coefficient % 10).IsZero)
        {
            coefficient /= 10;
            exponent++;
        }
        return new DecimalState(sign, coefficient, exponent, Special.Finite, string.Empty);
    }

    private static DecimalState FromTuple(IReadOnlyList<PythonValue> items, TextSpan span)
    {
        if (items.Count != 3)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "argument must be a sequence of length 3",
                span,
                "ValueError"
            );
        }
        var sign = items[0] switch
        {
            PythonWholeNumberValue whole when whole.Value == 0 || whole.Value == 1 => (int)
                whole.Value,
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "sign must be an integer with the value 0 or 1",
                span,
                "ValueError"
            ),
        };
        var digits = new List<int>();
        foreach (var digit in ManagedObjectProtocols.MaterializeValues(items[1], span))
        {
            if (digit is PythonWholeNumberValue whole && whole.Value >= 0 && whole.Value <= 9)
            {
                digits.Add((int)whole.Value);
            }
            else
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4060",
                    "coefficient must be a tuple of digits",
                    span,
                    "ValueError"
                );
            }
        }
        if (items[2] is PythonTextValue text)
        {
            switch (text.Value)
            {
                case "F":
                    return new DecimalState(
                        sign,
                        BigInteger.Zero,
                        0,
                        Special.Infinity,
                        string.Empty
                    );
                case "n":
                case "N":
                {
                    var payload = string.Concat(digits).TrimStart('0');
                    return new DecimalState(
                        sign,
                        BigInteger.Zero,
                        0,
                        text.Value == "N" ? Special.SNaN : Special.NaN,
                        payload
                    );
                }
                default:
                    throw ManagedObjectProtocols.Fault(
                        "DPY4060",
                        "exponent must be an integer",
                        span,
                        "ValueError"
                    );
            }
        }
        if (items[2] is not (PythonWholeNumberValue or PythonTruthValue))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "exponent must be an integer",
                span,
                "ValueError"
            );
        }
        var exponent =
            items[2] is PythonWholeNumberValue value1 ? (long)value1.Value
            : (items[2] as PythonTruthValue)!.Value ? 1L
            : 0L;
        var digitsText = digits.Count == 0 ? "0" : string.Concat(digits);
        digitsText = digitsText.TrimStart('0');
        return new DecimalState(
            sign,
            digitsText.Length == 0
                ? BigInteger.Zero
                : BigInteger.Parse(digitsText, CultureInfo.InvariantCulture),
            exponent,
            Special.Finite,
            string.Empty
        );
    }

    // -----------------------------------------------------------------------------
    // Reading values and text forms
    // -----------------------------------------------------------------------------

    private static DecimalState DecimalOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: DecimalState state }
            ? state
            : throw TypeFault("the value is not a Decimal");

    private static PythonManagedObjectValue RequireDecimal(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: DecimalState }
            ? (PythonManagedObjectValue)value
            : throw TypeFault("the value is not a Decimal");

    private static PythonManagedObjectValue Copy(DecimalState state) => new(DecimalType, state);

    private static PythonManagedObjectValue CopyTo(PythonValue cls, DecimalState state) =>
        new(cls as PythonManagedTypeValue ?? DecimalType, state);

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

    private static string Digits(BigInteger coefficient) =>
        coefficient.IsZero ? "0" : coefficient.ToString(CultureInfo.InvariantCulture);

    private static long Adjusted(DecimalState state) =>
        state.Exponent + Digits(state.Coefficient).Length - 1;

    internal static string Str(PythonValue value)
    {
        var state = DecimalOf(value);
        var sign = state.Sign == 1 ? "-" : string.Empty;
        switch (state.Special)
        {
            case Special.Infinity:
                return sign + "Infinity";
            case Special.NaN:
                return sign + "NaN" + state.Payload;
            case Special.SNaN:
                return sign + "sNaN" + state.Payload;
        }
        var digits = Digits(state.Coefficient);
        var adjusted = Adjusted(state);
        string body;
        if (state.Exponent <= 0 && adjusted >= -6)
        {
            if (state.Exponent == 0)
            {
                body = digits;
            }
            else if (adjusted >= 0)
            {
                var point = (int)(adjusted + 1);
                body =
                    point >= digits.Length
                        ? digits + new string('0', point - digits.Length)
                        : digits[..point] + "." + digits[point..];
            }
            else
            {
                body = "0." + new string('0', (int)(-adjusted - 1)) + digits;
            }
        }
        else
        {
            body =
                digits[0]
                + (digits.Length > 1 ? "." + digits[1..] : string.Empty)
                + "E"
                + (adjusted >= 0 ? "+" : string.Empty)
                + adjusted.ToString(CultureInfo.InvariantCulture);
        }
        return sign + body;
    }

    private static PythonValue AsTuple(DecimalState state)
    {
        var digitText = state.Special is Special.NaN or Special.SNaN
            ? state.Payload
            : Digits(state.Coefficient);
        var digits =
            digitText.Length == 0
                ? [new BigInteger(0)]
                : digitText.Select(c => new BigInteger(c - '0')).ToArray();
        PythonValue exponent = state.Special switch
        {
            Special.Infinity => Text("F"),
            Special.NaN => Text("n"),
            Special.SNaN => Text("N"),
            _ => PythonWholeNumberValue.Create(state.Exponent),
        };
        return UserObjectProtocols.Dispatcher!.Invoke(
            DecimalTupleType,
            [
                PythonWholeNumberValue.Create(state.Sign),
                new PythonTupleValue([
                    .. digits.Select(digit => PythonWholeNumberValue.Create(digit)),
                ]),
                exponent,
            ],
            default
        );
    }

    private static long Hash(PythonValue owner, DecimalState state)
    {
        if (state.Special == Special.Infinity)
        {
            return state.Sign == 1 ? -314159 : 314159;
        }
        if (state.Special is Special.NaN or Special.SNaN)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner);
        }
        var modulus = (BigInteger.One << 61) - 1;
        BigInteger residue;
        if (state.Exponent >= 0)
        {
            residue =
                (state.Coefficient % modulus)
                * BigInteger.ModPow(10, state.Exponent, modulus)
                % modulus;
        }
        else
        {
            var power = BigInteger.ModPow(10, -state.Exponent, modulus);
            var inverse = ModInverse(power, modulus);
            residue = inverse is null
                ? BigInteger.Zero
                : (state.Coefficient % modulus) * inverse.Value % modulus;
        }
        var signed = state.Sign == 1 ? -residue : residue;
        if (signed == -1)
        {
            signed = -2;
        }
        return (long)signed;
    }

    private static BigInteger? ModInverse(BigInteger value, BigInteger modulus)
    {
        BigInteger oldR = ((value % modulus) + modulus) % modulus,
            remainder = modulus;
        BigInteger oldS = 1,
            coefficient = 0;
        while (!remainder.IsZero)
        {
            var quotient = oldR / remainder;
            (oldR, remainder) = (remainder, oldR - (quotient * remainder));
            (oldS, coefficient) = (coefficient, oldS - (quotient * coefficient));
        }
        return oldR.IsOne ? ((oldS % modulus) + modulus) % modulus : null;
    }

    private static BigInteger ToInteger(DecimalState state)
    {
        if (state.Special == Special.Infinity)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "cannot convert Infinity to integer",
                default,
                "OverflowError"
            );
        }
        if (state.Special is Special.NaN or Special.SNaN)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "cannot convert NaN to integer",
                default,
                "ValueError"
            );
        }
        var magnitude =
            state.Exponent >= 0
                ? state.Coefficient * BigInteger.Pow(10, (int)state.Exponent)
                : state.Coefficient / BigInteger.Pow(10, (int)-state.Exponent);
        return state.Sign == 1 ? -magnitude : magnitude;
    }

    private static double ToDouble(DecimalState state)
    {
        if (state.Special == Special.Infinity)
        {
            return state.Sign == 1 ? double.NegativeInfinity : double.PositiveInfinity;
        }
        if (state.Special is Special.NaN or Special.SNaN)
        {
            return double.NaN;
        }
        var (numerator, denominator) = RatioOf(state);
        try
        {
            var magnitude = PythonFractionsModule.RatioToDouble(
                BigInteger.Abs(numerator),
                denominator
            );
            return state.Sign == 1 ? -magnitude : magnitude;
        }
        catch (PythonRuntimeException)
        {
            return state.Sign == 1 ? double.NegativeInfinity : double.PositiveInfinity;
        }
    }

    private static (BigInteger Numerator, BigInteger Denominator) RatioOf(DecimalState state)
    {
        var signed = state.Sign == 1 ? -state.Coefficient : state.Coefficient;
        if (state.Exponent >= 0)
        {
            return (signed * BigInteger.Pow(10, (int)state.Exponent), BigInteger.One);
        }
        var denominator = BigInteger.Pow(10, (int)-state.Exponent);
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(signed), denominator);
        return (signed / gcd, denominator / gcd);
    }

    private static PythonTupleValue AsIntegerRatio(DecimalState state)
    {
        if (state.Special == Special.Infinity)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "cannot convert Infinity to integer ratio",
                default,
                "OverflowError"
            );
        }
        if (state.Special is Special.NaN or Special.SNaN)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4060",
                "cannot convert NaN to integer ratio",
                default,
                "ValueError"
            );
        }
        var (numerator, denominator) = RatioOf(state);
        return new PythonTupleValue([
            PythonWholeNumberValue.Create(numerator),
            PythonWholeNumberValue.Create(denominator),
        ]);
    }
}
