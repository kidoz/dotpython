using System.Globalization;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `datetime` module: `timedelta`, `date`, `time`, `datetime`, the `tzinfo` protocol and
/// the fixed-offset `timezone` beside it, over the calendar arithmetic and the
/// `strftime`/`strptime` engine the `time` module carries. The host's own zone answers for
/// local time — there is no time zone database here, so `zoneinfo` and every named zone it
/// would load are the slice's gap, and so is the environment's `TZ`.
/// </summary>
internal static class PythonDateTimeModule
{
    internal const long MinYear = 1;
    internal const long MaxYear = 9999;

    /// <summary>What a `timedelta`'s days may reach, which its overflow wording reports.</summary>
    private const long MaxDeltaDays = 999_999_999;

    private const long MicrosecondsPerSecond = 1_000_000;
    private const long MicrosecondsPerMinute = 60 * MicrosecondsPerSecond;
    private const long MicrosecondsPerHour = 60 * MicrosecondsPerMinute;
    private const long MicrosecondsPerDay = 24 * MicrosecondsPerHour;
    private const long MicrosecondsPerWeek = 7 * MicrosecondsPerDay;
    private const long MicrosecondsPerMillisecond = 1000;

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("MINYEAR", PythonWholeNumberValue.Create(MinYear));
        globals.SetValue("MAXYEAR", PythonWholeNumberValue.Create(MaxYear));
        globals.SetValue("timedelta", TimedeltaType);
        globals.SetValue("date", DateType);
    }

    // -----------------------------------------------------------------------------
    // What an instance carries
    // -----------------------------------------------------------------------------

    /// <summary>A `timedelta`'s three normalized fields.</summary>
    private sealed record Delta(long Days, long Seconds, long Microseconds)
    {
        /// <summary>
        /// The whole span in microseconds, as a big integer: a `timedelta`'s own extremes
        /// reach past what a long holds.
        /// </summary>
        internal System.Numerics.BigInteger Total =>
            (System.Numerics.BigInteger)Days * MicrosecondsPerDay
            + Seconds * MicrosecondsPerSecond
            + Microseconds;

        /// <summary>The fields one microsecond count normalizes to, floored as Python floors.</summary>
        internal static Delta Of(System.Numerics.BigInteger total)
        {
            var days = FloorDiv(total, MicrosecondsPerDay);
            var rest = total - days * MicrosecondsPerDay;
            return new Delta(
                (long)days,
                (long)(rest / MicrosecondsPerSecond),
                (long)(rest % MicrosecondsPerSecond)
            );
        }

        internal Delta Negated() => FromTotal(-Total);

        internal Delta Absolute() => Total.Sign < 0 ? Negated() : this;

        private static Delta FromTotal(System.Numerics.BigInteger total) => Of(total);
    }

    /// <summary>
    /// The fields a `date`, a `time` or a `datetime` carries. A `date` leaves the clock at
    /// midnight, a `time` leaves the calendar at the first of January of year one.
    /// </summary>
    private sealed record Stamp(
        long Year,
        long Month,
        long Day,
        long Hour,
        long Minute,
        long Second,
        long Microsecond,
        PythonValue? Tzinfo,
        int Fold
    )
    {
        internal DateOnly Civil => new((int)Year, (int)Month, (int)Day);

        /// <summary>The clock fields as one microsecond count since local midnight.</summary>
        internal long Clock =>
            Hour * MicrosecondsPerHour
            + Minute * MicrosecondsPerMinute
            + Second * MicrosecondsPerSecond
            + Microsecond;

        internal bool IsAware => Tzinfo is not null;

        /// <summary>The proleptic Gregorian ordinal, where 0001-01-01 is one.</summary>
        internal long OrdinalOf() => Civil.DayNumber + 1;
    }

    // -----------------------------------------------------------------------------
    // Shared wording
    // -----------------------------------------------------------------------------

    private static PythonRuntimeException Fault(string message, TextSpan span, string type) =>
        ManagedObjectProtocols.Fault("DPY4054", message, span, type);

    private static PythonRuntimeException TypeFault(string message, TextSpan span = default) =>
        Fault(message, span, "TypeError");

    private static PythonRuntimeException ValueFault(string message, TextSpan span = default) =>
        Fault(message, span, "ValueError");

    private static PythonRuntimeException OverflowFault(string message) =>
        Fault(message, default, "OverflowError");

    /// <summary>CPython's wording for a field that is not an integer at all.</summary>
    private static long RequireLong(PythonValue value, TextSpan span)
    {
        try
        {
            return (long)PythonBuiltinFunctions.RequireIndex(value, span);
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "TypeError"))
        {
            throw TypeFault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
                span
            );
        }
    }

    private static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static System.Numerics.BigInteger FloorDiv(
        System.Numerics.BigInteger value,
        System.Numerics.BigInteger divisor
    ) => value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static long FloorMod(long value, long divisor) =>
        value - FloorDiv(value, divisor) * divisor;

    private static double ToDouble(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonFloatingPointValue number => number.Value,
            PythonWholeNumberValue number => (double)number.Value,
            PythonTruthValue truth => truth.Value ? 1.0 : 0.0,
            _ => throw TypeFault(
                $"unsupported operand type(s): '{ManagedObjectProtocols.GetTypeName(value)}'",
                span
            ),
        };

    /// <summary>`round()`, which Python rounds to even.</summary>
    private static long RoundMicroseconds(double value)
    {
        if (double.IsNaN(value))
        {
            throw ValueFault("cannot convert NaN to integer ratio");
        }
        if (double.IsInfinity(value))
        {
            throw OverflowFault("cannot convert Infinity to integer ratio");
        }
        return (long)Math.Round(value, MidpointRounding.ToEven);
    }

    private static long DaysInMonth(long year, long month) =>
        DateTime.DaysInMonth((int)year, (int)month);

    private static DateOnly CivilDate(Stamp stamp) =>
        new((int)stamp.Year, (int)stamp.Month, (int)stamp.Day);

    private static long Ordinal(long year, long month, long day) =>
        new DateOnly((int)year, (int)month, (int)day).DayNumber + 1;

    private static DateOnly FromOrdinal(long ordinal) => DateOnly.FromDayNumber((int)(ordinal - 1));

    // -----------------------------------------------------------------------------
    // Validating the fields, in CPython's words
    // -----------------------------------------------------------------------------

    private static void ValidateDate(long year, long month, long day)
    {
        if (year is < MinYear or > MaxYear)
        {
            throw ValueFault($"year must be in 1..9999, not {year}");
        }
        if (month is < 1 or > 12)
        {
            throw ValueFault($"month must be in 1..12, not {month}");
        }
        var days = DaysInMonth(year, month);
        if (day < 1 || day > days)
        {
            throw ValueFault(
                $"day {day} must be in range 1..{days} for month {month} in year {year}"
            );
        }
    }

    private static void ValidateTime(
        long hour,
        long minute,
        long second,
        long microsecond,
        long fold
    )
    {
        if (hour is < 0 or > 23)
        {
            throw ValueFault($"hour must be in 0..23, not {hour}");
        }
        if (minute is < 0 or > 59)
        {
            throw ValueFault($"minute must be in 0..59, not {minute}");
        }
        if (second is < 0 or > 59)
        {
            throw ValueFault($"second must be in 0..59, not {second}");
        }
        if (microsecond is < 0 or > 999_999)
        {
            throw ValueFault($"microsecond must be in 0..999999, not {microsecond}");
        }
        if (fold is < 0 or > 1)
        {
            throw ValueFault($"fold must be either 0 or 1, not {fold}");
        }
    }

    // -----------------------------------------------------------------------------
    // Binding the constructor's arguments
    // -----------------------------------------------------------------------------

    /// <summary>
    /// The argument list `__new__` binds, in CPython's own wording: a name given by keyword
    /// fills the same slot a positional would, a slot left empty without a default is the
    /// first complaint, and the count includes the keyword-only tail.
    /// </summary>
    private static PythonValue?[] BindArguments(
        string[] parameters,
        int positionalOnlyCount,
        int keywordOnlyCount,
        PythonValue?[] defaults,
        PythonValue[] positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        string caller = "function",
        string unknownKeyword = "this function"
    )
    {
        var keywordable = parameters.Length - keywordOnlyCount;
        // A keyword counts with the positional arguments: CPython answers "at most N
        // arguments" whether the extra one was written or named.
        var supplied = positional.Length + keywordNames.Count;
        if (supplied > parameters.Length)
        {
            throw TypeFault(
                $"{caller} takes at most {parameters.Length} argument"
                    + $"{(parameters.Length == 1 ? string.Empty : "s")} ({supplied} given)"
            );
        }
        if (positional.Length > keywordable)
        {
            throw TypeFault(
                $"{caller} takes at most {keywordable} positional argument"
                    + $"{(keywordable == 1 ? string.Empty : "s")} ({positional.Length} given)"
            );
        }
        var slots = new PythonValue?[parameters.Length];
        for (var index = 0; index < positional.Length; index++)
        {
            slots[index] = positional[index];
        }
        string? unknown = null;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0 || slot >= keywordable)
            {
                unknown ??= keywordNames[index];
                continue;
            }
            slots[slot] = keywordValues[index];
        }
        for (var index = 0; index < parameters.Length; index++)
        {
            if (slots[index] is null && (index >= defaults.Length || defaults[index] is null))
            {
                throw TypeFault(
                    $"function missing required argument '{parameters[index]}' (pos {index + 1})"
                );
            }
        }
        if (unknown is not null)
        {
            throw TypeFault($"{unknownKeyword} got an unexpected keyword argument '{unknown}'");
        }
        _ = positionalOnlyCount;
        return slots;
    }

    /// <summary>A method that takes no arguments at all, in CPython's wording.</summary>
    private static void RequireBare(string owner, string name, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count != 0)
        {
            throw TypeFault($"{owner}.{name}() takes no arguments ({arguments.Count} given)");
        }
    }

    /// <summary>
    /// `__new__` is handed the class ahead of its own arguments, the way Python hands it.
    /// </summary>
    private static PythonValue[] WithoutClass(IReadOnlyList<PythonValue> arguments) =>
        arguments.Count > 0 && arguments[0] is PythonManagedTypeValue
            ? [.. arguments.Skip(1)]
            : [.. arguments];

    /// <summary>The class `__new__` was handed, which is the one the call named.</summary>
    private static PythonManagedTypeValue? ClassOf(IReadOnlyList<PythonValue> arguments) =>
        arguments.Count > 0 && arguments[0] is PythonManagedTypeValue type ? type : null;

    private static long Field(
        PythonValue?[] slots,
        int index,
        PythonValue?[] defaults,
        TextSpan span
    )
    {
        var value = slots[index] ?? defaults[index]!;
        return RequireLong(value, span);
    }

    // -----------------------------------------------------------------------------
    // The types
    // -----------------------------------------------------------------------------

    private static readonly PythonManagedTypeValue TimedeltaType = CreateTimedeltaType();
    private static readonly PythonManagedTypeValue DateType = CreateDateType();
    private static readonly PythonManagedTypeValue IsoCalendarDateType =
        CreateIsoCalendarDateType();

    /// <summary>
    /// A member a `date`, a `time` or a `datetime` publishes without letting it be written:
    /// `date.year` reads a field and refuses to be assigned, in CPython's wording.
    /// </summary>
    private static PythonPropertyValue ReadOnlyMember(
        string typeName,
        string name,
        Func<Stamp, PythonValue> read
    ) =>
        new(
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) => read(StampOf(arguments[0], typeName))
            ),
            new PythonProtocolFunctionValue(name, (_, _) => throw NotWritable(typeName, name)),
            new PythonProtocolFunctionValue(name, (_, _) => throw NotWritable(typeName, name))
        );

    private static PythonRuntimeException NotWritable(string typeName, string name) =>
        Fault(
            $"attribute '{name}' of 'datetime.{typeName}' objects is not writable",
            default,
            "AttributeError"
        );

    private static Stamp StampOf(PythonValue value, string typeName) =>
        value is PythonManagedObjectValue { Payload: Stamp stamp }
            ? stamp
            : throw TypeFault(
                $"descriptor for 'datetime.{typeName}' objects doesn't apply to a "
                    + $"'{ManagedObjectProtocols.GetTypeName(value)}' object"
            );

    private static Delta DeltaOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: Delta delta }
            ? delta
            : throw TypeFault(
                "descriptor for 'datetime.timedelta' objects doesn't apply to a "
                    + $"'{ManagedObjectProtocols.GetTypeName(value)}' object"
            );

    private static PythonManagedObjectValue StampValue(PythonManagedTypeValue type, Stamp stamp) =>
        new(type, stamp);

    private static PythonManagedObjectValue DeltaValue(Delta delta) => new(TimedeltaType, delta);

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null,
        string[]? parameters = null,
        PythonValue?[]? defaults = null,
        int positionalOnly = 0
    ) => PythonUserTypes.Method(owner, name, body, keywords, parameters, defaults, positionalOnly);

    private static PythonClassMethodValue ClassMethod(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    ) => new PythonClassMethodValue(Method(owner, name, body, null, parameters, defaults));

    // -----------------------------------------------------------------------------
    // timedelta
    // -----------------------------------------------------------------------------

    private static readonly string[] TimedeltaParameters =
    [
        "days",
        "seconds",
        "microseconds",
        "milliseconds",
        "minutes",
        "hours",
        "weeks",
    ];

    private static readonly PythonValue?[] TimedeltaDefaults =
    [
        PythonNoneValue.Instance,
        PythonNoneValue.Instance,
        PythonNoneValue.Instance,
        PythonNoneValue.Instance,
        PythonNoneValue.Instance,
        PythonNoneValue.Instance,
        PythonNoneValue.Instance,
    ];

    private static PythonManagedTypeValue CreateTimedeltaType()
    {
        var type = new PythonManagedTypeValue("timedelta") { Module = "datetime" };
        type.Attributes["__doc__"] = new PythonTextValue("Difference between two datetime values.");
        type.Attributes["__new__"] = Method(
            "timedelta",
            "__new__",
            (_, arguments) => NewTimedeltaFromPositional(WithoutClass(arguments)),
            (_, positional, names, values) =>
                NewTimedelta(
                    BindArguments(
                        TimedeltaParameters,
                        0,
                        0,
                        TimedeltaDefaults,
                        WithoutClass(positional),
                        names,
                        values,
                        caller: "__new__()",
                        unknownKeyword: "__new__()"
                    ),
                    ClassOf(positional)
                )
        );
        type.Attributes["days"] = DeltaMember("days", delta => delta.Days);
        type.Attributes["seconds"] = DeltaMember("seconds", delta => delta.Seconds);
        type.Attributes["microseconds"] = DeltaMember("microseconds", delta => delta.Microseconds);
        // The field itself is still being assigned here, so the values are built on the
        // type under construction.
        type.Attributes["min"] = new PythonManagedObjectValue(type, new Delta(-MaxDeltaDays, 0, 0));
        type.Attributes["max"] = new PythonManagedObjectValue(
            type,
            new Delta(MaxDeltaDays, 86_399, 999_999)
        );
        type.Attributes["resolution"] = new PythonManagedObjectValue(type, new Delta(0, 0, 1));
        type.Attributes["total_seconds"] = Method(
            "timedelta",
            "total_seconds",
            (self, _) => new PythonFloatingPointValue(TotalSeconds(DeltaOf(self!)))
        );
        type.Attributes["__repr__"] = Method(
            "timedelta",
            "__repr__",
            (self, _) => new PythonTextValue(RepresentDelta(DeltaOf(self!)))
        );
        type.Attributes["__str__"] = Method(
            "timedelta",
            "__str__",
            (self, _) => new PythonTextValue(StrDelta(DeltaOf(self!)))
        );
        type.Attributes["__hash__"] = Method(
            "timedelta",
            "__hash__",
            (self, _) => PythonWholeNumberValue.Create(DeltaOf(self!).Total)
        );
        type.Attributes["__bool__"] = Method(
            "timedelta",
            "__bool__",
            (self, _) => PythonTruthValue.FromBoolean(DeltaOf(self!).Total != 0)
        );
        type.Attributes["__neg__"] = Method(
            "timedelta",
            "__neg__",
            (self, _) => DeltaValue(Decoded(-DeltaOf(self!).Total))
        );
        type.Attributes["__pos__"] = Method("timedelta", "__pos__", (self, _) => self!);
        type.Attributes["__abs__"] = Method(
            "timedelta",
            "__abs__",
            (self, _) => DeltaValue(Decoded(System.Numerics.BigInteger.Abs(DeltaOf(self!).Total)))
        );
        type.Attributes["__add__"] = DeltaArithmetic("__add__", (left, right) => left + right, "+");
        type.Attributes["__radd__"] = type.Attributes["__add__"];
        type.Attributes["__sub__"] = DeltaArithmetic("__sub__", (left, right) => left - right, "-");
        type.Attributes["__rsub__"] = DeltaArithmeticReflected(
            "__rsub__",
            (left, right) => right - left
        );
        type.Attributes["__mul__"] = Method(
            "timedelta",
            "__mul__",
            (self, arguments) => ScaleDelta(self!, arguments)
        );
        type.Attributes["__rmul__"] = type.Attributes["__mul__"];
        type.Attributes["__truediv__"] = Method(
            "timedelta",
            "__truediv__",
            (self, arguments) => DivideDelta(self!, arguments)
        );
        type.Attributes["__floordiv__"] = Method(
            "timedelta",
            "__floordiv__",
            (self, arguments) => FloorDivideDelta(self!, arguments)
        );
        type.Attributes["__mod__"] = Method(
            "timedelta",
            "__mod__",
            (self, arguments) => ModuloDelta(self!, arguments)
        );
        type.Attributes["__divmod__"] = Method(
            "timedelta",
            "__divmod__",
            (self, arguments) => DivmodDelta(self!, arguments)
        );
        type.Attributes["__eq__"] = DeltaComparison("__eq__", (left, right) => left == right);
        type.Attributes["__ne__"] = DeltaComparison("__ne__", (left, right) => left != right);
        type.Attributes["__lt__"] = DeltaComparison("__lt__", (left, right) => left < right);
        type.Attributes["__le__"] = DeltaComparison("__le__", (left, right) => left <= right);
        type.Attributes["__gt__"] = DeltaComparison("__gt__", (left, right) => left > right);
        type.Attributes["__ge__"] = DeltaComparison("__ge__", (left, right) => left >= right);
        return type;
    }

    private static PythonPropertyValue DeltaMember(string name, Func<Delta, long> read) =>
        new(
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) => PythonWholeNumberValue.Create(read(DeltaOf(arguments[0])))
            ),
            new PythonProtocolFunctionValue(name, (_, _) => throw NotWritable("timedelta", name)),
            new PythonProtocolFunctionValue(name, (_, _) => throw NotWritable("timedelta", name))
        );

    private static PythonProtocolFunctionValue DeltaComparison(
        string name,
        Func<System.Numerics.BigInteger, System.Numerics.BigInteger, bool> compare
    ) =>
        Method(
            "timedelta",
            name,
            (self, arguments) =>
                arguments.Count == 1
                && arguments[0] is PythonManagedObjectValue { Payload: Delta other }
                    ? PythonTruthValue.FromBoolean(compare(DeltaOf(self!).Total, other.Total))
                    : PythonNotImplementedValue.Instance
        );

    private static PythonProtocolFunctionValue DeltaArithmetic(
        string name,
        Func<
            System.Numerics.BigInteger,
            System.Numerics.BigInteger,
            System.Numerics.BigInteger
        > combine,
        string symbol
    ) =>
        Method(
            "timedelta",
            name,
            (self, arguments) =>
                arguments.Count == 1
                && arguments[0] is PythonManagedObjectValue { Payload: Delta other }
                    ? DeltaValue(Decoded(combine(DeltaOf(self!).Total, other.Total)))
                    : PythonNotImplementedValue.Instance
        );

    private static PythonProtocolFunctionValue DeltaArithmeticReflected(
        string name,
        Func<
            System.Numerics.BigInteger,
            System.Numerics.BigInteger,
            System.Numerics.BigInteger
        > combine
    ) =>
        Method(
            "timedelta",
            name,
            (self, arguments) =>
                arguments.Count == 1
                && arguments[0] is PythonManagedObjectValue { Payload: Delta other }
                    ? DeltaValue(Decoded(combine(DeltaOf(self!).Total, other.Total)))
                    : PythonNotImplementedValue.Instance
        );

    /// <summary>One microsecond count as a `timedelta`, refusing a year's worth of days.</summary>
    private static Delta Decoded(System.Numerics.BigInteger total)
    {
        var delta = Delta.Of(total);
        if (Math.Abs(delta.Days) > MaxDeltaDays)
        {
            throw OverflowFault($"days={delta.Days}; must have magnitude <= 999999999");
        }
        return delta;
    }

    private static PythonManagedObjectValue NewTimedeltaFromPositional(PythonValue[] arguments) =>
        NewTimedelta([.. arguments.Cast<PythonValue?>()], ClassOf(arguments));

    private static PythonManagedObjectValue NewTimedelta(
        PythonValue?[] arguments,
        PythonValue? cls = null
    )
    {
        if (arguments.Length > TimedeltaParameters.Length)
        {
            throw TypeFault(
                $"function takes at most {TimedeltaParameters.Length} arguments "
                    + $"({arguments.Length} given)"
            );
        }
        long[] scales =
        [
            MicrosecondsPerDay,
            MicrosecondsPerSecond,
            1,
            MicrosecondsPerMillisecond,
            MicrosecondsPerMinute,
            MicrosecondsPerHour,
            MicrosecondsPerWeek,
        ];
        var total = 0.0;
        for (var index = 0; index < arguments.Length; index++)
        {
            var value = arguments[index];
            if (value is null)
            {
                continue;
            }
            if (
                value
                is not (PythonWholeNumberValue or PythonFloatingPointValue or PythonTruthValue)
            )
            {
                throw TypeFault(
                    $"unsupported type for timedelta {TimedeltaParameters[index]} component: "
                        + $"{ManagedObjectProtocols.GetTypeName(value)}"
                );
            }
            total += ToDouble(value, default) * scales[index];
        }
        var days = (long)Math.Truncate(total / MicrosecondsPerDay);
        if (Math.Abs(days) > MaxDeltaDays)
        {
            throw OverflowFault($"days={days}; must have magnitude <= 999999999");
        }
        return Instantiate(cls, TimedeltaType, Delta.Of(RoundMicroseconds(total)));
    }

    private static PythonValue ScaleDelta(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count != 1 || arguments[0] is PythonTextValue)
        {
            return PythonNotImplementedValue.Instance;
        }
        var scale = ToDouble(arguments[0], default);
        var total = (double)DeltaOf(self).Total;
        if (double.IsNaN(total * scale))
        {
            throw ValueFault("cannot convert NaN to integer ratio");
        }
        if (double.IsInfinity(total * scale))
        {
            throw OverflowFault("cannot convert Infinity to integer ratio");
        }
        return DeltaValue(Decoded(RoundMicroseconds(total * scale)));
    }

    private static PythonValue DivideDelta(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count != 1)
        {
            return PythonNotImplementedValue.Instance;
        }
        var left = DeltaOf(self).Total;
        if (arguments[0] is PythonManagedObjectValue { Payload: Delta other })
        {
            if (other.Total == 0)
            {
                throw Fault("division by zero", default, "ZeroDivisionError");
            }
            return new PythonFloatingPointValue((double)left / (double)other.Total);
        }
        var divisor = ToDouble(arguments[0], default);
        if (divisor == 0)
        {
            throw Fault("division by zero", default, "ZeroDivisionError");
        }
        return DeltaValue(Decoded(RoundMicroseconds((double)left / divisor)));
    }

    private static PythonValue FloorDivideDelta(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
        {
            return PythonNotImplementedValue.Instance;
        }
        var left = DeltaOf(self).Total;
        if (arguments[0] is PythonManagedObjectValue { Payload: Delta other })
        {
            if (other.Total == 0)
            {
                throw Fault("division by zero", default, "ZeroDivisionError");
            }
            return PythonWholeNumberValue.Create(FloorDiv(left, other.Total));
        }
        if (arguments[0] is not PythonWholeNumberValue whole)
        {
            throw TypeFault(
                "unsupported operand type(s) for //: 'datetime.timedelta' and "
                    + $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}'"
            );
        }
        if (whole.Value.IsZero)
        {
            throw Fault("division by zero", default, "ZeroDivisionError");
        }
        return DeltaValue(Decoded(left / (long)whole.Value));
    }

    private static PythonValue ModuloDelta(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        if (
            arguments.Count != 1
            || arguments[0] is not PythonManagedObjectValue { Payload: Delta other }
        )
        {
            return PythonNotImplementedValue.Instance;
        }
        if (other.Total == 0)
        {
            throw Fault("division by zero", default, "ZeroDivisionError");
        }
        var left = DeltaOf(self).Total;
        return DeltaValue(Decoded(left - FloorDiv(left, other.Total) * other.Total));
    }

    private static PythonValue DivmodDelta(PythonValue self, IReadOnlyList<PythonValue> arguments)
    {
        if (
            arguments.Count != 1
            || arguments[0] is not PythonManagedObjectValue { Payload: Delta other }
        )
        {
            return PythonNotImplementedValue.Instance;
        }
        if (other.Total == 0)
        {
            throw Fault("division by zero", default, "ZeroDivisionError");
        }
        var left = DeltaOf(self).Total;
        var quotient = FloorDiv(left, other.Total);
        return new PythonTupleValue([
            PythonWholeNumberValue.Create(quotient),
            DeltaValue(Decoded(left - quotient * other.Total)),
        ]);
    }

    /// <summary>
    /// `total_seconds()` as CPython computes it: days and seconds are exact in a double,
    /// which keeps the extremes exact where one microsecond count would round.
    /// </summary>
    private static double TotalSeconds(Delta delta) =>
        delta.Days * 86_400.0 + delta.Seconds + delta.Microseconds / 1_000_000.0;

    /// <summary>`repr()`: the fields that are not zero, and `0` when none of them is.</summary>
    private static string RepresentDelta(Delta delta)
    {
        var parts = new List<string>();
        if (delta.Days != 0)
        {
            parts.Add($"days={delta.Days}");
        }
        if (delta.Seconds != 0)
        {
            parts.Add($"seconds={delta.Seconds}");
        }
        if (delta.Microseconds != 0)
        {
            parts.Add($"microseconds={delta.Microseconds}");
        }
        return parts.Count == 0
            ? "datetime.timedelta(0)"
            : "datetime.timedelta(" + string.Join(", ", parts) + ")";
    }

    private static string StrDelta(Delta delta)
    {
        var clock =
            $"{delta.Seconds / 3600}:{delta.Seconds % 3600 / 60:00}:{delta.Seconds % 60:00}";
        if (delta.Microseconds != 0)
        {
            clock += $".{delta.Microseconds:000000}";
        }
        if (delta.Days == 0)
        {
            return clock;
        }
        var days = Math.Abs(delta.Days);
        return $"{(delta.Days < 0 ? "-" : string.Empty)}{days} "
            + $"day{(days == 1 ? string.Empty : "s")}, {clock}";
    }

    // -----------------------------------------------------------------------------
    // date
    // -----------------------------------------------------------------------------

    private static readonly string[] DateParameters = ["year", "month", "day"];

    private static readonly PythonValue?[] DateDefaults = [null, null, null];

    private static PythonManagedTypeValue CreateDateType()
    {
        var type = new PythonManagedTypeValue("date") { Module = "datetime" };
        type.Attributes["__doc__"] = new PythonTextValue("date(year, month, day) --> date object");
        type.Attributes["__new__"] = Method(
            "date",
            "__new__",
            (_, arguments) => NewDate(ClassOf(arguments), WithoutClass(arguments)),
            (_, positional, names, values) =>
                NewDate(
                    ClassOf(positional),
                    BindArguments(
                            DateParameters,
                            0,
                            0,
                            DateDefaults,
                            WithoutClass(positional),
                            names,
                            values
                        )
                        .Select(slot => slot ?? PythonNoneValue.Instance)
                        .ToArray()
                )
        );
        type.Attributes["year"] = ReadOnlyMember(
            "date",
            "year",
            stamp => PythonWholeNumberValue.Create(stamp.Year)
        );
        type.Attributes["month"] = ReadOnlyMember(
            "date",
            "month",
            stamp => PythonWholeNumberValue.Create(stamp.Month)
        );
        type.Attributes["day"] = ReadOnlyMember(
            "date",
            "day",
            stamp => PythonWholeNumberValue.Create(stamp.Day)
        );
        type.Attributes["min"] = new PythonManagedObjectValue(
            type,
            new Stamp(1, 1, 1, 0, 0, 0, 0, null, 0)
        );
        type.Attributes["max"] = new PythonManagedObjectValue(
            type,
            new Stamp(9999, 12, 31, 0, 0, 0, 0, null, 0)
        );
        type.Attributes["resolution"] = DeltaValue(new Delta(1, 0, 0));
        type.Attributes["today"] = ClassMethod(
            "date",
            "today",
            (cls, _) => Instantiate(cls, DateType, Today())
        );
        type.Attributes["fromtimestamp"] = ClassMethod(
            "date",
            "fromtimestamp",
            (cls, arguments) => DateFromTimestamp(cls!, arguments)
        );
        type.Attributes["fromordinal"] = ClassMethod(
            "date",
            "fromordinal",
            (cls, arguments) => DateFromOrdinal(cls!, arguments)
        );
        type.Attributes["fromisoformat"] = ClassMethod(
            "date",
            "fromisoformat",
            (cls, arguments) => DateFromIsoFormat(cls!, arguments)
        );
        type.Attributes["fromisocalendar"] = ClassMethod(
            "date",
            "fromisocalendar",
            (cls, arguments) => DateFromIsoCalendar(cls!, arguments)
        );
        type.Attributes["strptime"] = ClassMethod(
            "date",
            "strptime",
            (cls, arguments) => DateStrptime(cls!, arguments)
        );
        type.Attributes["toordinal"] = Method(
            "date",
            "toordinal",
            (self, arguments) =>
                Bare(
                    "date",
                    "toordinal",
                    arguments,
                    PythonWholeNumberValue.Create(StampOf(self!, "date").OrdinalOf())
                )
        );
        type.Attributes["timetuple"] = Method(
            "date",
            "timetuple",
            (self, arguments) =>
                Bare("date", "timetuple", arguments, BrokenTimeOf(StampOf(self!, "date")).ToValue())
        );
        type.Attributes["weekday"] = Method(
            "date",
            "weekday",
            (self, arguments) =>
                Bare(
                    "date",
                    "weekday",
                    arguments,
                    PythonWholeNumberValue.Create(WeekdayOf(StampOf(self!, "date")))
                )
        );
        type.Attributes["isoweekday"] = Method(
            "date",
            "isoweekday",
            (self, arguments) =>
                Bare(
                    "date",
                    "isoweekday",
                    arguments,
                    PythonWholeNumberValue.Create(WeekdayOf(StampOf(self!, "date")) + 1)
                )
        );
        type.Attributes["isocalendar"] = Method(
            "date",
            "isocalendar",
            (self, arguments) =>
                Bare("date", "isocalendar", arguments, IsoCalendarOf(StampOf(self!, "date")))
        );
        type.Attributes["isoformat"] = Method(
            "date",
            "isoformat",
            (self, arguments) =>
                Bare(
                    "date",
                    "isoformat",
                    arguments,
                    new PythonTextValue(IsoDate(StampOf(self!, "date")))
                )
        );
        type.Attributes["ctime"] = Method(
            "date",
            "ctime",
            (self, arguments) =>
                Bare(
                    "date",
                    "ctime",
                    arguments,
                    new PythonTextValue(CtimeOf(StampOf(self!, "date")))
                )
        );
        type.Attributes["strftime"] = Method(
            "date",
            "strftime",
            (self, arguments) =>
                new PythonTextValue(FormatStamp(StampOf(self!, "date"), RequireFormat(arguments)))
        );
        type.Attributes["replace"] = Method(
            "date",
            "replace",
            (self, arguments) => ReplaceDate(self!, arguments, [], []),
            (self, _, names, values) => ReplaceDate(self!, [], names, values)
        );
        type.Attributes["__repr__"] = Method(
            "date",
            "__repr__",
            (self, _) =>
                new PythonTextValue(
                    $"{RepresentTypeName(self!)}" + $"({RepresentDate(StampOf(self!, "date"))})"
                )
        );
        type.Attributes["__str__"] = Method(
            "date",
            "__str__",
            (self, _) => new PythonTextValue(IsoDate(StampOf(self!, "date")))
        );
        type.Attributes["__format__"] = Method(
            "date",
            "__format__",
            (self, arguments) => FormatDate(self!, arguments)
        );
        type.Attributes["__hash__"] = Method(
            "date",
            "__hash__",
            (self, _) => PythonWholeNumberValue.Create(StampOf(self!, "date").OrdinalOf())
        );
        type.Attributes["__add__"] = Method(
            "date",
            "__add__",
            (self, arguments) => AddToDate(self!, arguments, subtract: false, reflected: false)
        );
        type.Attributes["__eq__"] = DateComparison(
            "date",
            "__eq__",
            (left, right) => left == right
        );
        type.Attributes["__ne__"] = DateComparison(
            "date",
            "__ne__",
            (left, right) => left != right
        );
        type.Attributes["__lt__"] = DateComparison("date", "__lt__", (left, right) => left < right);
        type.Attributes["__le__"] = DateComparison(
            "date",
            "__le__",
            (left, right) => left <= right
        );
        type.Attributes["__gt__"] = DateComparison("date", "__gt__", (left, right) => left > right);
        type.Attributes["__ge__"] = DateComparison(
            "date",
            "__ge__",
            (left, right) => left >= right
        );
        type.Attributes["__radd__"] = type.Attributes["__add__"];
        type.Attributes["__sub__"] = Method(
            "date",
            "__sub__",
            (self, arguments) => AddToDate(self!, arguments, subtract: true, reflected: false)
        );
        return type;
    }

    /// <summary>Dates compare by their ordinal; a `datetime` or anything else does not.</summary>
    private static PythonProtocolFunctionValue DateComparison(
        string owner,
        string name,
        Func<long, long, bool> compare
    ) =>
        Method(
            owner,
            name,
            (self, arguments) =>
                arguments.Count == 1
                && arguments[0] is PythonManagedObjectValue { Payload: Stamp other }
                && IsPlainDate(arguments[0])
                    ? PythonTruthValue.FromBoolean(
                        compare(StampOf(self!, "date").OrdinalOf(), other.OrdinalOf())
                    )
                    : PythonNotImplementedValue.Instance
        );

    /// <summary>A method of no arguments, checking its arity before it answers.</summary>
    private static PythonValue Bare(
        string owner,
        string name,
        IReadOnlyList<PythonValue> arguments,
        PythonValue answer
    )
    {
        RequireBare(owner, name, arguments);
        return answer;
    }

    private static PythonManagedObjectValue DateValue(Stamp stamp) => new(DateType, stamp);

    private static PythonManagedObjectValue NewDate(PythonValue? cls, PythonValue[] arguments)
    {
        if (arguments.Length > DateParameters.Length)
        {
            throw TypeFault(
                $"function takes at most {DateParameters.Length} arguments ({arguments.Length} given)"
            );
        }
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] is PythonNoneValue)
            {
                throw TypeFault(
                    $"function missing required argument '{DateParameters[index]}' "
                        + $"(pos {index + 1})"
                );
            }
        }
        if (arguments.Length < DateParameters.Length)
        {
            throw TypeFault(
                $"function missing required argument '{DateParameters[arguments.Length]}' "
                    + $"(pos {arguments.Length + 1})"
            );
        }
        var year = RequireLong(arguments[0], default);
        var month = RequireLong(arguments[1], default);
        var day = RequireLong(arguments[2], default);
        ValidateDate(year, month, day);
        var stamp = new Stamp(year, month, day, 0, 0, 0, 0, null, 0);
        return Instantiate(cls, DateType, stamp);
    }

    /// <summary>A construction answers the class it was called on, which may be a subclass.</summary>
    private static PythonManagedObjectValue Instantiate(
        PythonValue? cls,
        PythonManagedTypeValue declaring,
        object payload
    )
    {
        var type = cls as PythonManagedTypeValue ?? declaring;
        var value = new PythonManagedObjectValue(type, payload);
        value.Attributes["__class__"] = type;
        return value;
    }

    private static Stamp Today()
    {
        var now = DateTime.Now;
        return new Stamp(now.Year, now.Month, now.Day, 0, 0, 0, 0, null, 0);
    }

    // -----------------------------------------------------------------------------
    // date: reading the calendar out of a stamp
    // -----------------------------------------------------------------------------

    /// <summary>`repr` names the base module-qualified and a subclass by its own name.</summary>
    private static string RepresentTypeName(PythonValue self) =>
        self is PythonManagedObjectValue { Type: { } type } && !ReferenceEquals(type, DateType)
            ? type.Name
            : "datetime.date";

    private static string IsoDate(Stamp stamp) =>
        $"{stamp.Year:0000}-{stamp.Month:00}-{stamp.Day:00}";

    private static string RepresentDate(Stamp stamp) => $"{stamp.Year}, {stamp.Month}, {stamp.Day}";

    /// <summary>Python's weekday, Monday zero.</summary>
    private static long WeekdayOf(Stamp stamp) => ((int)stamp.Civil.DayOfWeek + 6) % 7;

    private static string CtimeOf(Stamp stamp) =>
        $"{ShortWeekdays[WeekdayOf(stamp)]} {ShortMonths[stamp.Month - 1]} {stamp.Day:00} "
        + $"{stamp.Hour:00}:{stamp.Minute:00}:{stamp.Second:00} {stamp.Year:0000}";

    private static readonly string[] ShortWeekdays =
    [
        "Mon",
        "Tue",
        "Wed",
        "Thu",
        "Fri",
        "Sat",
        "Sun",
    ];

    private static readonly string[] ShortMonths =
    [
        "Jan",
        "Feb",
        "Mar",
        "Apr",
        "May",
        "Jun",
        "Jul",
        "Aug",
        "Sep",
        "Oct",
        "Nov",
        "Dec",
    ];

    /// <summary>
    /// A stamp as the broken-down time `strftime` and `timetuple` work with. A `date` has
    /// no zone, so `%Z`/`%z` write nothing and the daylight-saving flag is unknown.
    /// </summary>
    private static PythonTimeModule.BrokenTime BrokenTimeOf(Stamp stamp) =>
        new(
            stamp.Year,
            stamp.Month,
            stamp.Day,
            stamp.Hour,
            stamp.Minute,
            stamp.Second,
            WeekdayOf(stamp),
            stamp.Civil.DayOfYear,
            -1
        );

    private static string FormatStamp(Stamp stamp, string format) =>
        PythonTimeModule.Format(format, BrokenTimeOf(stamp));

    private static string RequireFormat(IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count == 0)
        {
            throw TypeFault("strftime() missing required argument 'format' (pos 1)");
        }
        if (arguments.Count > 1)
        {
            throw TypeFault($"strftime() takes at most 1 argument ({arguments.Count} given)");
        }
        return arguments[0] is PythonTextValue text
            ? text.Value
            : throw TypeFault(
                $"strftime() argument 1 must be str, not "
                    + $"{ManagedObjectProtocols.GetTypeName(arguments[0])}"
            );
    }

    private static PythonTextValue FormatDate(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var spec = arguments.Count == 1 && arguments[0] is PythonTextValue text ? text.Value : null;
        if (spec is null)
        {
            throw TypeFault(
                "__format__() argument 1 must be str, not "
                    + (
                        arguments.Count != 1 ? "nothing"
                        : arguments[0] is PythonNoneValue ? "None"
                        : ManagedObjectProtocols.GetTypeName(arguments[0])
                    )
            );
        }
        var stamp = StampOf(self, "date");
        return new PythonTextValue(spec.Length == 0 ? IsoDate(stamp) : FormatStamp(stamp, spec));
    }

    /// <summary>`date.isocalendar()`: the ISO year, week and weekday, as a three-tuple.</summary>
    private static PythonManagedObjectValue IsoCalendarOf(Stamp stamp) =>
        new(
            IsoCalendarDateType,
            new PythonTupleValue([
                PythonWholeNumberValue.Create(ISOWeek.GetYear(stamp.Civil)),
                PythonWholeNumberValue.Create(ISOWeek.GetWeekOfYear(stamp.Civil)),
                PythonWholeNumberValue.Create(WeekdayOf(stamp) + 1),
            ])
        );

    /// <summary>`datetime.IsoCalendarDate`: what `isocalendar()` answers, a tuple of three.</summary>
    private static PythonManagedTypeValue CreateIsoCalendarDateType()
    {
        var tuple = PythonBuiltinTypes.Tuple;
        var type = new PythonManagedTypeValue("IsoCalendarDate")
        {
            Module = "datetime",
            LayoutBase = tuple,
        };
        type.Attributes["__module__"] = new PythonTextValue("datetime");
        type.Attributes["__qualname__"] = new PythonTextValue("IsoCalendarDate");
        type.Attributes["__doc__"] = new PythonTextValue("IsoCalendarDate(year, week, weekday)");
        var fields = new (string Name, string Doc)[]
        {
            ("year", "year"),
            ("week", "week"),
            ("weekday", "weekday"),
        };
        for (var index = 0; index < fields.Length; index++)
        {
            type.Attributes[fields[index].Name] = new PythonTupleGetterValue(
                fields[index].Name,
                index,
                fields[index].Doc
            );
        }
        type.Attributes["__repr__"] = Method(
            "IsoCalendarDate",
            "__repr__",
            (self, _) => new PythonTextValue(RepresentIsoCalendar(self!))
        );
        type.SetDeclaredBases(new PythonTupleValue([tuple]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, tuple, PythonBuiltinFunctions.ObjectType])
        );
        return type;
    }

    private static string RepresentIsoCalendar(PythonValue value)
    {
        var tuple = value is PythonManagedObjectValue { Payload: PythonTupleValue payload }
            ? payload
            : throw TypeFault("IsoCalendarDate fields are missing");
        return $"datetime.IsoCalendarDate(year={tuple.Elements[0].ToRepresentationString()}, "
            + $"week={tuple.Elements[1].ToRepresentationString()}, "
            + $"weekday={tuple.Elements[2].ToRepresentationString()})";
    }

    /// <summary>The positional-or-keyword slots `replace` binds, CPython's own wording aside.</summary>
    private static PythonValue?[] ReplaceBind(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        string[] parameters
    )
    {
        var slots = new PythonValue?[parameters.Length];
        if (arguments.Count > parameters.Length)
        {
            throw TypeFault(
                $"replace() takes at most {parameters.Length} arguments ({arguments.Count} given)"
            );
        }
        for (var index = 0; index < arguments.Count; index++)
        {
            slots[index] = arguments[index];
        }
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(parameters, names[index]);
            if (slot < 0)
            {
                throw TypeFault($"replace() got an unexpected keyword argument '{names[index]}'");
            }
            slots[slot] = values[index];
        }
        return slots;
    }

    private static PythonManagedObjectValue ReplaceDate(
        PythonValue self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var stamp = StampOf(self, "date");
        var fields = ReplaceBind(positional, names, values, DateParameters);
        var year = fields[0] is null ? stamp.Year : RequireLong(fields[0]!, default);
        var month = fields[1] is null ? stamp.Month : RequireLong(fields[1]!, default);
        var day = fields[2] is null ? stamp.Day : RequireLong(fields[2]!, default);
        ValidateDate(year, month, day);
        return new PythonManagedObjectValue(
            self is PythonManagedObjectValue { Type: { } type } ? type : DateType,
            new Stamp(year, month, day, 0, 0, 0, 0, null, 0)
        );
    }

    // -----------------------------------------------------------------------------
    // date: the classmethods
    // -----------------------------------------------------------------------------

    private static PythonManagedObjectValue DateFromTimestamp(
        PythonValue cls,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
        {
            throw TypeFault(
                $"date.fromtimestamp() takes exactly one argument ({arguments.Count} given)"
            );
        }
        var local = LocalFromUnixMicroseconds(ToUnixMicroseconds(arguments[0]));
        return Instantiate(
            cls,
            DateType,
            new Stamp(local.Year, local.Month, local.Day, 0, 0, 0, 0, null, 0)
        );
    }

    /// <summary>One instant, as the host's own zone breaks it down.</summary>
    private static DateTime LocalFromUnixMicroseconds(long micros)
    {
        var seconds = FloorDiv(micros, MicrosecondsPerSecond);
        var remainder = micros - seconds * MicrosecondsPerSecond;
        return DateTimeOffset
            .FromUnixTimeSeconds(seconds)
            .UtcDateTime.AddTicks(remainder * 10)
            .ToLocalTime();
    }

    private static PythonManagedObjectValue DateFromOrdinal(
        PythonValue cls,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 1)
        {
            throw TypeFault("fromordinal() takes exactly one argument");
        }
        var ordinal = RequireLong(arguments[0], default);
        if (ordinal < 1)
        {
            throw ValueFault("ordinal must be >= 1");
        }
        if (ordinal > Ordinal(MaxYear, 12, 31))
        {
            throw ValueFault("year must be in 1..9999, not 10000");
        }
        var date = FromOrdinal(ordinal);
        return Instantiate(
            cls,
            DateType,
            new Stamp(date.Year, date.Month, date.Day, 0, 0, 0, 0, null, 0)
        );
    }

    private static PythonManagedObjectValue DateFromIsoFormat(
        PythonValue cls,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var text = RequireTextArgument(arguments, "fromisoformat");
        var (year, month, day) = ParseIsoDate(text);
        return Instantiate(cls, DateType, new Stamp(year, month, day, 0, 0, 0, 0, null, 0));
    }

    private static PythonManagedObjectValue DateFromIsoCalendar(
        PythonValue cls,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 3)
        {
            throw TypeFault(
                $"fromisocalendar() takes exactly 3 arguments ({arguments.Count} given)"
            );
        }
        var year = RequireLong(arguments[0], default);
        var week = RequireLong(arguments[1], default);
        var weekday = RequireLong(arguments[2], default);
        if (week is < 1 or > 53)
        {
            throw ValueFault($"Invalid week: {week}");
        }
        if (weekday is < 1 or > 7)
        {
            throw ValueFault($"Invalid weekday: {weekday} (range is [1, 7])");
        }
        var date = ISOWeek.ToDateTime((int)year, (int)week, (DayOfWeek)(weekday % 7));
        if (date.Year is < (int)MinYear or > (int)MaxYear)
        {
            throw ValueFault($"Invalid week: {week}");
        }
        return Instantiate(
            cls,
            DateType,
            new Stamp(date.Year, date.Month, date.Day, 0, 0, 0, 0, null, 0)
        );
    }

    private static PythonManagedObjectValue DateStrptime(
        PythonValue cls,
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count != 2)
        {
            throw TypeFault("strptime() takes exactly 2 arguments");
        }
        if (arguments[0] is not PythonTextValue text || arguments[1] is not PythonTextValue format)
        {
            throw TypeFault("strptime() argument 1 must be str");
        }
        var parsed = PythonTimeModule.Parse(text.Value, format.Value, default);
        ValidateDate(parsed.Year, parsed.Month, parsed.Day);
        return Instantiate(
            cls,
            DateType,
            new Stamp(parsed.Year, parsed.Month, parsed.Day, 0, 0, 0, 0, null, 0)
        );
    }

    private static string RequireTextArgument(IReadOnlyList<PythonValue> arguments, string name)
    {
        if (arguments.Count != 1)
        {
            throw TypeFault($"{name}() takes exactly one argument");
        }
        return arguments[0] is PythonTextValue text
            ? text.Value
            : throw TypeFault($"{name}() argument must be str");
    }

    /// <summary>The date forms `fromisoformat` reads: `2026-10-12`, `20261012`, `2026-W42-1`.</summary>
    private static (long Year, long Month, long Day) ParseIsoDate(string text)
    {
        if (
            text.Length == 8
            && !text.Contains('-', StringComparison.Ordinal)
            && text[4] is not ('W' or 'w')
        )
        {
            return ReadIsoFields(text[..4], text[4..6], text[6..], text);
        }
        if (text.Length == 10 && text[4] == '-' && text[7] == '-' && text[5] != 'W')
        {
            return ReadIsoFields(text[..4], text[5..7], text[8..], text);
        }
        // An ISO week: `2026-W42`, `2026-W42-1`, and the compact `2026W421`.
        if (text.Length is 8 or 10 && (text[4] is 'W' or 'w' || text[5] is 'W' or 'w'))
        {
            var compact = text[4] is 'W' or 'w';
            var year = ReadIsoNumber(text[..4], text);
            var weekStart = compact ? 5 : 6;
            var week = ReadIsoNumber(text[weekStart..(weekStart + 2)], text);
            var weekday = compact || text.Length == 8 ? 1 : ReadIsoNumber(text[9..], text);
            try
            {
                var date = ISOWeek.ToDateTime((int)year, (int)week, (DayOfWeek)(weekday % 7));
                return (date.Year, date.Month, date.Day);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw ValueFault($"Invalid isoformat string: '{text}'");
            }
        }
        throw ValueFault($"Invalid isoformat string: '{text}'");
    }

    private static (long Year, long Month, long Day) ReadIsoFields(
        string yearText,
        string monthText,
        string dayText,
        string whole
    )
    {
        var year = ReadIsoNumber(yearText, whole);
        var month = ReadIsoNumber(monthText, whole);
        var day = ReadIsoNumber(dayText, whole);
        ValidateDate(year, month, day);
        return (year, month, day);
    }

    private static long ReadIsoNumber(string text, string whole) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw ValueFault($"Invalid isoformat string: '{whole}'");

    /// <summary>The same reading, but a field out of range is the whole string's fault.</summary>
    private static long ReadIsoWeekPart(string text, string whole) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : throw ValueFault($"Invalid isoformat string: '{whole}'");

    private static long ToUnixMicroseconds(PythonValue value) =>
        value switch
        {
            PythonFloatingPointValue floating => (long)(floating.Value * 1_000_000),
            PythonWholeNumberValue whole => (long)(whole.Value * 1_000_000),
            PythonTruthValue truth => truth.Value ? 1_000_000 : 0,
            _ => throw TypeFault(
                $"an integer is required (got type {ManagedObjectProtocols.GetTypeName(value)})"
            ),
        };

    /// <summary>Whether a type is `date` itself or one a program derived from it.</summary>
    private static bool IsPlainDate(PythonValue value) =>
        value is PythonManagedObjectValue { Type: { } type }
        && type.Mro.Any(entry => ReferenceEquals(entry, DateType))
        && type.Name != "datetime";

    /// <summary>
    /// `date + timedelta` and `date - timedelta`: only the timedelta's days count, and a
    /// date outside year 1..9999 is the overflow CPython reports.
    /// </summary>
    private static PythonValue AddToDate(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        bool subtract,
        bool reflected
    )
    {
        if (arguments.Count != 1)
        {
            return PythonNotImplementedValue.Instance;
        }
        // `date - date` is the days between them; a `datetime` is a different type and
        // answers NotImplemented, which is the TypeError Python reports.
        if (
            subtract
            && !reflected
            && arguments[0] is PythonManagedObjectValue other
            && other.Payload is Stamp otherStamp
            && IsPlainDate(arguments[0])
        )
        {
            var own = StampOf(self, "date");
            return DeltaValue(
                Decoded((own.OrdinalOf() - otherStamp.OrdinalOf()) * MicrosecondsPerDay)
            );
        }
        if (arguments[0] is not PythonManagedObjectValue { Payload: Delta delta })
        {
            return PythonNotImplementedValue.Instance;
        }
        var stamp = StampOf(self, "date");
        var days = subtract ? -delta.Days : delta.Days;
        if (reflected)
        {
            days = -days;
        }
        var ordinal = stamp.OrdinalOf() + days;
        if (ordinal < 1 || ordinal > Ordinal(MaxYear, 12, 31))
        {
            throw OverflowFault("date value out of range");
        }
        var moved = FromOrdinal(ordinal);
        return new PythonManagedObjectValue(
            self is PythonManagedObjectValue { Type: { } type } ? type : DateType,
            new Stamp(moved.Year, moved.Month, moved.Day, 0, 0, 0, 0, null, 0)
        );
    }
}
