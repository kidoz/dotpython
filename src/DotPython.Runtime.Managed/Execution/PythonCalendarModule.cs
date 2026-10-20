using System.Globalization;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `calendar` module over the `datetime` engine: the leap-year and month-range
/// helpers, the weekday and month-name sequences, the `Day`/`Month` IntEnums, the
/// `Calendar` iterators and the `TextCalendar`/`HTMLCalendar` formatting. `timegm` is
/// exact calendar arithmetic; `mktime` is absent, as 3.14 removed it. The
/// `LocaleTextCalendar`/`LocaleHTMLCalendar` classes accept a locale but format with the
/// C locale — the runtime has no locale module — and the deprecated module `__getattr__`
/// aliases are absent.
/// </summary>
internal static class PythonCalendarModule
{
    private static readonly string[] MonthNameSequence = [""];

    private static readonly string[] MonthNames =
    [
        "January",
        "February",
        "March",
        "April",
        "May",
        "June",
        "July",
        "August",
        "September",
        "October",
        "November",
        "December",
    ];

    private static readonly string[] MonthAbbreviations =
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

    private static readonly string[] DayNames =
    [
        "Monday",
        "Tuesday",
        "Wednesday",
        "Thursday",
        "Friday",
        "Saturday",
        "Sunday",
    ];

    private static readonly string[] DayAbbreviations =
    [
        "Mon",
        "Tue",
        "Wed",
        "Thu",
        "Fri",
        "Sat",
        "Sun",
    ];

    /// <summary>`mdays`, with the placeholder at index zero.</summary>
    private static readonly string[] CssClassNames =
    [
        "mon",
        "tue",
        "wed",
        "thu",
        "fri",
        "sat",
        "sun",
    ];

    private static readonly int[] MonthLengths =
    [
        0,
        31,
        28,
        31,
        30,
        31,
        30,
        31,
        31,
        30,
        31,
        30,
        31,
    ];

    private const int EpochOrdinal = 719163; // datetime.date(1970, 1, 1).toordinal()

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        var errorType = PythonBuiltinTypes.GetExceptionType("ValueError");
        globals.SetValue("error", errorType);
        globals.SetValue("IllegalMonthError", IllegalMonthErrorType);
        globals.SetValue("IllegalWeekdayError", IllegalWeekdayErrorType);
        globals.SetValue("Month", MonthType);
        globals.SetValue("Day", DayType);
        for (var index = 0; index < MonthNames.Length; index++)
        {
            globals.SetValue(MonthNames[index].ToUpperInvariant(), MemberOf(MonthType, index + 1));
        }
        string[] daySymbols =
        [
            "MONDAY",
            "TUESDAY",
            "WEDNESDAY",
            "THURSDAY",
            "FRIDAY",
            "SATURDAY",
            "SUNDAY",
        ];
        for (var index = 0; index < daySymbols.Length; index++)
        {
            globals.SetValue(daySymbols[index], MemberOf(DayType, index));
        }
        globals.SetValue(
            "mdays",
            new PythonListValue([
                .. MonthLengths.Select(length =>
                    (PythonValue)PythonWholeNumberValue.Create(length)
                ),
            ])
        );
        globals.SetValue(
            "month_name",
            NamesValue(MonthNamesType, [.. MonthNameSequence, .. MonthNames], "%B")
        );
        globals.SetValue(
            "month_abbr",
            NamesValue(MonthNamesType, [.. MonthNameSequence, .. MonthAbbreviations], "%b")
        );
        globals.SetValue("day_name", NamesValue(DayNamesType, DayNames, "%A"));
        globals.SetValue("day_abbr", NamesValue(DayNamesType, DayAbbreviations, "%a"));

        foreach (var (name, body) in Functions)
        {
            globals.SetValue(name, body);
        }

        globals.SetValue("Calendar", CalendarType);
        globals.SetValue("TextCalendar", TextCalendarType);
        globals.SetValue("HTMLCalendar", HtmlCalendarType);
        globals.SetValue("LocaleTextCalendar", LocaleTextCalendarType);
        globals.SetValue("LocaleHTMLCalendar", LocaleHtmlCalendarType);
        // The module-level `c` is a TextCalendar, which is where the module functions
        // `monthcalendar`, `weekheader`, `month`, `calendar`, `prmonth`, `prcal`, `week`
        // and `prweek` are bound methods of.
        var calendar = new PythonManagedObjectValue(TextCalendarType);
        globals.SetValue("c", calendar);
        globals.SetValue(
            "monthcalendar",
            ManagedObjectProtocols.GetAttribute(calendar, "monthdayscalendar", default)
        );
        globals.SetValue(
            "weekheader",
            ManagedObjectProtocols.GetAttribute(calendar, "formatweekheader", default)
        );
        globals.SetValue(
            "month",
            ManagedObjectProtocols.GetAttribute(calendar, "formatmonth", default)
        );
        globals.SetValue(
            "calendar",
            ManagedObjectProtocols.GetAttribute(calendar, "formatyear", default)
        );
        globals.SetValue(
            "prmonth",
            ManagedObjectProtocols.GetAttribute(calendar, "prmonth", default)
        );
        globals.SetValue("prcal", ManagedObjectProtocols.GetAttribute(calendar, "pryear", default));
        globals.SetValue(
            "week",
            ManagedObjectProtocols.GetAttribute(calendar, "formatweek", default)
        );
        globals.SetValue(
            "prweek",
            ManagedObjectProtocols.GetAttribute(calendar, "prweek", default)
        );
        globals.SetValue(
            "__all__",
            new PythonListValue([
                new PythonTextValue("IllegalMonthError"),
                new PythonTextValue("IllegalWeekdayError"),
                new PythonTextValue("setfirstweekday"),
                new PythonTextValue("firstweekday"),
                new PythonTextValue("isleap"),
                new PythonTextValue("leapdays"),
                new PythonTextValue("weekday"),
                new PythonTextValue("monthrange"),
                new PythonTextValue("monthcalendar"),
                new PythonTextValue("prmonth"),
                new PythonTextValue("month"),
                new PythonTextValue("prcal"),
                new PythonTextValue("calendar"),
                new PythonTextValue("timegm"),
                new PythonTextValue("month_name"),
                new PythonTextValue("month_abbr"),
                new PythonTextValue("day_name"),
                new PythonTextValue("day_abbr"),
                new PythonTextValue("Calendar"),
                new PythonTextValue("TextCalendar"),
                new PythonTextValue("HTMLCalendar"),
                new PythonTextValue("LocaleTextCalendar"),
                new PythonTextValue("LocaleHTMLCalendar"),
                new PythonTextValue("weekheader"),
                new PythonTextValue("Day"),
                new PythonTextValue("Month"),
                new PythonTextValue("JANUARY"),
                new PythonTextValue("FEBRUARY"),
                new PythonTextValue("MARCH"),
                new PythonTextValue("APRIL"),
                new PythonTextValue("MAY"),
                new PythonTextValue("JUNE"),
                new PythonTextValue("JULY"),
                new PythonTextValue("AUGUST"),
                new PythonTextValue("SEPTEMBER"),
                new PythonTextValue("OCTOBER"),
                new PythonTextValue("NOVEMBER"),
                new PythonTextValue("DECEMBER"),
                new PythonTextValue("MONDAY"),
                new PythonTextValue("TUESDAY"),
                new PythonTextValue("WEDNESDAY"),
                new PythonTextValue("THURSDAY"),
                new PythonTextValue("FRIDAY"),
                new PythonTextValue("SATURDAY"),
                new PythonTextValue("SUNDAY"),
            ])
        );
    }

    private static readonly (string Name, PythonValue Body)[] Functions =
    [
        ("setfirstweekday", Function("setfirstweekday", SetFirstWeekday, ["firstweekday"], [null])),
        ("firstweekday", Function("firstweekday", FirstWeekday, [], [])),
        ("isleap", Function("isleap", IsLeap, ["year"], [null])),
        ("leapdays", Function("leapdays", LeapDays, ["y1", "y2"], [null, null])),
        ("weekday", Function("weekday", Weekday, ["year", "month", "day"], [null, null, null])),
        ("monthrange", Function("monthrange", MonthRange, ["year", "month"], [null, null])),
        ("timegm", Function("timegm", TimeGm, ["tuple"], [null])),
        (
            "formatstring",
            Function(
                "formatstring",
                FormatString,
                ["cols", "colwidth", "spacing"],
                [null, PythonWholeNumberValue.Create(20), PythonWholeNumberValue.Create(6)]
            )
        ),
        (
            "format",
            Function(
                "format",
                FormatColumns,
                ["cols", "colwidth", "spacing"],
                [null, PythonWholeNumberValue.Create(20), PythonWholeNumberValue.Create(6)]
            )
        ),
    ];

    private static PythonBuiltinFunctionValue Function(
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body,
        string[] parameters,
        PythonValue?[] defaults
    )
    {
        var required = RequiredCount(parameters, defaults);
        return new PythonBuiltinFunctionValue(
            name,
            (arguments, span) =>
            {
                CheckArity(name, arguments, parameters, required, method: false);
                return body(arguments, span);
            }
        ).WithSignature(parameters, defaults);
    }

    /// <summary>The `date` type the month iterators build their values with.</summary>
    private static PythonValue DateType() =>
        ManagedObjectProtocols.GetAttribute(
            UserObjectProtocols.Dispatcher!.ImportModule("datetime", string.Empty, default),
            "date",
            default
        );

    /// <summary>Writes to the interpreter's own output through its `print` builtin.</summary>
    private static void WriteOutput(string text)
    {
        var builtins = UserObjectProtocols.Dispatcher!.ImportModule(
            "builtins",
            string.Empty,
            default
        );
        var print = ManagedObjectProtocols.GetAttribute(builtins, "print", default);
        UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
            print,
            [new PythonTextValue(text)],
            ["end"],
            [new PythonTextValue(string.Empty)],
            default
        );
    }

    /// <summary>`global_enum`'s repr: `calendar.MONDAY`.</summary>
    private static PythonProtocolFunctionValue MemberRepr(string enumName)
    {
        _ = enumName;
        return Method(
            enumName,
            "__repr__",
            (self, _) =>
            {
                var name =
                    self is PythonManagedObjectValue member
                    && member.Attributes.TryGetValue("_name_", out var declared)
                    && declared is PythonTextValue text
                        ? text.Value
                        : string.Empty;
                return new PythonTextValue($"calendar.{name}");
            }
        );
    }

    /// <summary>A `%i`/`%d` conversion: integers, booleans and finite floats.</summary>
    private static long FormatInt(PythonValue value, string spec)
    {
        switch (value)
        {
            case PythonWholeNumberValue whole:
                return (long)whole.Value;
            case PythonTruthValue truth:
                return truth.Value ? 1 : 0;
            case PythonFloatingPointValue floating when double.IsFinite(floating.Value):
                return (long)Math.Truncate(floating.Value);
            case PythonFloatingPointValue floating when double.IsNaN(floating.Value):
                throw Fault("cannot convert float NaN to integer", default, "ValueError");
            case PythonFloatingPointValue:
                throw Fault("cannot convert float infinity to integer", default, "OverflowError");
            default:
                throw TypeFault(
                    $"{spec} format: a real number is required, not "
                        + ManagedObjectProtocols.GetTypeName(value)
                );
        }
    }

    private static PythonValue MemberOf(PythonManagedTypeValue type, int value) =>
        UserObjectProtocols.Dispatcher!.Invoke(
            type,
            [PythonWholeNumberValue.Create(value)],
            default
        );

    // -----------------------------------------------------------------------------
    // Wording and numbers
    // -----------------------------------------------------------------------------

    private static PythonRuntimeException Fault(string message, TextSpan span, string type) =>
        ManagedObjectProtocols.Fault("DPY4056", message, span, type);

    private static PythonRuntimeException TypeFault(string message, TextSpan span = default) =>
        Fault(message, span, "TypeError");

    private static long Integer(PythonValue value, TextSpan span) =>
        (long)PythonBuiltinFunctions.RequireIndex(value, span);

    private static long Require(long value, int minimum, int maximum, string name) =>
        value >= minimum && value <= maximum
            ? value
            : throw Fault(
                $"{name} must be in {minimum}..{maximum}, not {value}",
                default,
                "ValueError"
            );

    private static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static long FloorMod(long value, long divisor) =>
        value - FloorDiv(value, divisor) * divisor;

    // -----------------------------------------------------------------------------
    // Exceptions
    // -----------------------------------------------------------------------------

    private static readonly PythonManagedTypeValue IllegalMonthErrorType =
        CreateIllegalMonthError();

    private static readonly PythonManagedTypeValue IllegalWeekdayErrorType =
        CreateIllegalWeekdayError();

    private static PythonManagedTypeValue CreateIllegalMonthError()
    {
        var baseType = PythonBuiltinTypes.GetExceptionType("ValueError");
        var indexType = PythonBuiltinTypes.GetExceptionType("IndexError");
        var type = new PythonManagedTypeValue("IllegalMonthError", exceptionBaseName: "ValueError")
        {
            Module = "calendar",
            LayoutBase = baseType,
        };
        type.Attributes["__module__"] = new PythonTextValue("calendar");
        type.Attributes["__qualname__"] = new PythonTextValue("IllegalMonthError");
        type.Attributes["__init__"] = Method(
            "IllegalMonthError",
            "__init__",
            (self, arguments) => InitializeIllegalMonth(self, arguments)
        );
        type.Attributes["__str__"] = Method(
            "IllegalMonthError",
            "__str__",
            (self, _) => new PythonTextValue(DescribeIllegalMonth(self!))
        );
        type.SetDeclaredBases(new PythonTupleValue([baseType, indexType]));
        // C3 of (ValueError, IndexError): the two share Exception and its bases, and
        // IndexError's own ancestor LookupError sits between them.
        type.SetResolutionOrder(
            new PythonTupleValue([
                type,
                baseType,
                indexType,
                PythonBuiltinTypes.GetExceptionType("LookupError"),
                PythonBuiltinTypes.GetExceptionType("Exception"),
                PythonBuiltinTypes.GetExceptionType("BaseException"),
                PythonBuiltinFunctions.Object,
            ])
        );
        return type;
    }

    private static PythonNoneValue InitializeIllegalMonth(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (target, rest) = PythonUserTypes.Bound(receiver, arguments);
        var month = rest.Count > 0 ? rest[0] : PythonNoneValue.Instance;
        switch (target)
        {
            case PythonExceptionValue exception:
                exception.Attributes["month"] = month;
                exception.Message = DescribeIllegalMonth(exception);
                break;
            case PythonManagedObjectValue instance:
                instance.Attributes["month"] = month;
                break;
        }
        return PythonNoneValue.Instance;
    }

    private static string DescribeIllegalMonth(PythonValue value)
    {
        var attributes = value switch
        {
            PythonExceptionValue exception => exception.Attributes,
            PythonManagedObjectValue instance => instance.Attributes,
            _ => null,
        };
        return "bad month number "
            + (
                attributes is not null && attributes.TryGetValue("month", out var month)
                    ? month.ToRepresentationString()
                    : "None"
            )
            + "; must be 1-12";
    }

    private static PythonNoneValue InitializeIllegalWeekday(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (target, rest) = PythonUserTypes.Bound(receiver, arguments);
        var weekday = rest.Count > 0 ? rest[0] : PythonNoneValue.Instance;
        switch (target)
        {
            case PythonExceptionValue exception:
                exception.Attributes["weekday"] = weekday;
                exception.Message = DescribeIllegalWeekday(exception);
                break;
            case PythonManagedObjectValue instance:
                instance.Attributes["weekday"] = weekday;
                break;
        }
        return PythonNoneValue.Instance;
    }

    private static string DescribeIllegalWeekday(PythonValue value)
    {
        var attributes = value switch
        {
            PythonExceptionValue exception => exception.Attributes,
            PythonManagedObjectValue instance => instance.Attributes,
            _ => null,
        };
        return "bad weekday number "
            + (
                attributes is not null && attributes.TryGetValue("weekday", out var weekday)
                    ? weekday.ToRepresentationString()
                    : "None"
            )
            + "; must be 0 (Monday) to 6 (Sunday)";
    }

    private static PythonManagedTypeValue CreateIllegalWeekdayError()
    {
        var baseType = PythonBuiltinTypes.GetExceptionType("ValueError");
        var type = new PythonManagedTypeValue(
            "IllegalWeekdayError",
            exceptionBaseName: "ValueError"
        )
        {
            Module = "calendar",
            LayoutBase = baseType,
        };
        type.Attributes["__module__"] = new PythonTextValue("calendar");
        type.Attributes["__qualname__"] = new PythonTextValue("IllegalWeekdayError");
        type.Attributes["__init__"] = Method(
            "IllegalWeekdayError",
            "__init__",
            (self, arguments) => InitializeIllegalWeekday(self, arguments)
        );
        type.Attributes["__str__"] = Method(
            "IllegalWeekdayError",
            "__str__",
            (self, _) => new PythonTextValue(DescribeIllegalWeekday(self!))
        );
        type.SetDeclaredBases(new PythonTupleValue([baseType]));
        type.SetResolutionOrder(
            new PythonTupleValue([type, .. PythonBuiltinTypes.GetMro(baseType).Elements])
        );
        return type;
    }

    // -----------------------------------------------------------------------------
    // The enums and the name sequences
    // -----------------------------------------------------------------------------

    private static readonly PythonManagedTypeValue MonthType = CreateMonthEnum();

    private static readonly PythonManagedTypeValue DayType = CreateDayEnum();

    private static PythonManagedTypeValue CreateMonthEnum()
    {
        var members = new PythonDictionaryValue([]);
        for (var index = 0; index < MonthNames.Length; index++)
        {
            ManagedObjectProtocols.SetItem(
                members,
                new PythonTextValue(MonthNames[index].ToUpperInvariant()),
                PythonWholeNumberValue.Create(index + 1)
            );
        }
        var type = PythonEnum.CreateIntEnum("Month", "calendar", members);
        type.Attributes["__repr__"] = MemberRepr("Month");
        foreach (var (name, body) in Functions)
        {
            if (name is "isleap" or "leapdays" or "weekday" or "monthrange")
            {
                type.Attributes[name] = body;
            }
        }
        return type;
    }

    private static PythonManagedTypeValue CreateDayEnum()
    {
        var members = new PythonDictionaryValue([]);
        string[] symbols =
        [
            "MONDAY",
            "TUESDAY",
            "WEDNESDAY",
            "THURSDAY",
            "FRIDAY",
            "SATURDAY",
            "SUNDAY",
        ];
        for (var index = 0; index < symbols.Length; index++)
        {
            ManagedObjectProtocols.SetItem(
                members,
                new PythonTextValue(symbols[index]),
                PythonWholeNumberValue.Create(index)
            );
        }
        var type = PythonEnum.CreateIntEnum("Day", "calendar", members);
        type.Attributes["__repr__"] = MemberRepr("Day");
        foreach (var (name, body) in Functions)
        {
            if (name is "isleap" or "leapdays" or "weekday" or "monthrange")
            {
                type.Attributes[name] = body;
            }
        }
        return type;
    }

    private static readonly PythonManagedTypeValue MonthNamesType = CreateNamesType(
        "_localized_month"
    );

    private static readonly PythonManagedTypeValue DayNamesType = CreateNamesType("_localized_day");

    private static PythonManagedObjectValue NamesValue(
        PythonManagedTypeValue type,
        string[] names,
        string format
    )
    {
        var value = new PythonManagedObjectValue(type, new NamesState(names));
        value.Attributes["format"] = new PythonTextValue(format);
        return value;
    }

    private sealed record NamesState(string[] Names);

    /// <summary>
    /// `_localized_month`/`_localized_day`: the C-locale name sequences, indexable with
    /// Python's list semantics (negative indices and slices included). The month sequence
    /// carries the empty string at index zero, as CPython's does.
    /// </summary>
    private static PythonManagedTypeValue CreateNamesType(string name)
    {
        var type = new PythonManagedTypeValue(name) { Module = "calendar" };
        type.Attributes["__module__"] = new PythonTextValue("calendar");
        type.Attributes["__qualname__"] = new PythonTextValue(name);
        type.Attributes["__getitem__"] = Method(
            name,
            "__getitem__",
            (self, arguments) =>
            {
                var names = NamesOf(self!);
                if (arguments[0] is PythonSliceValue slice)
                {
                    var (start, stop, step) = SliceBounds(slice, names.Length);
                    var result = new List<PythonValue>();
                    for (var index = start; step > 0 ? index < stop : index > stop; index += step)
                    {
                        result.Add(new PythonTextValue(TextAt(names, index)));
                    }
                    return new PythonListValue(result);
                }
                return new PythonTextValue(TextAt(names, (int)Integer(arguments[0], default)));
            }
        );
        type.Attributes["__len__"] = Method(
            name,
            "__len__",
            (self, _) => PythonWholeNumberValue.Create(NamesOf(self!).Length)
        );
        return type;
    }

    private static string TextAt(string[] names, int index)
    {
        var offset = index < 0 ? index + names.Length : index;
        if (offset < 0 || offset >= names.Length)
        {
            throw Fault("list index out of range", default, "IndexError");
        }
        return names[offset];
    }

    private static (int Start, int Stop, int Step) SliceBounds(PythonSliceValue slice, int length)
    {
        var step = slice.Step is PythonNoneValue ? 1 : (int)Integer(slice.Step, default);
        int Bound(PythonValue value, int fallback) =>
            value is PythonNoneValue ? fallback : (int)Integer(value, default);
        var start = Bound(slice.Start, step > 0 ? 0 : length - 1);
        var stop = Bound(slice.Stop, step > 0 ? length : -1);
        if (start < 0)
        {
            start += length;
        }
        if (stop < 0 && slice.Stop is not PythonNoneValue)
        {
            stop += length;
        }
        start = Math.Clamp(start, step > 0 ? 0 : -1, step > 0 ? length : length - 1);
        stop = Math.Clamp(stop, step > 0 ? 0 : -1, step > 0 ? length : length - 1);
        return (start, stop, step);
    }

    private static string[] NamesOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: NamesState state }
            ? state.Names
            : throw TypeFault("the value is not a name sequence");

    // -----------------------------------------------------------------------------
    // The functions
    // -----------------------------------------------------------------------------

    private static PythonValue SetFirstWeekday(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var weekday = Integer(arguments[0], span);
        if (weekday is < 0 or > 6)
        {
            throw RaiseIllegalWeekday(arguments[0], span);
        }
        SetFirstWeekdayOf(ModuleCalendar().Calendar, weekday);
        return PythonNoneValue.Instance;
    }

    private static PythonValue FirstWeekday(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        _ = arguments;
        _ = span;
        return PythonWholeNumberValue.Create(FirstWeekdayOf(ModuleCalendar().Calendar));
    }

    private static PythonValue IsLeap(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var year = Integer(arguments[0], span);
        return PythonTruthValue.FromBoolean(IsLeapYear(year));
    }

    private static bool IsLeapYear(long year) =>
        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    private static PythonValue LeapDays(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var first = Integer(arguments[0], span) - 1;
        var second = Integer(arguments[1], span) - 1;
        return PythonWholeNumberValue.Create(
            (second / 4 - first / 4) - (second / 100 - first / 100) + (second / 400 - first / 400)
        );
    }

    private static PythonValue Weekday(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var year = Integer(arguments[0], span);
        var month = Integer(arguments[1], span);
        var day = Integer(arguments[2], span);
        if (year is < 1 or > 9999)
        {
            year = 2000 + FloorMod(year, 400);
        }
        RequireDateTimeMonth(month);
        var length = MonthLength(year, month);
        if (day < 1 || day > length)
        {
            throw Fault(
                $"day {day} must be in range 1..{length} for month {month} in year {year}",
                span,
                "ValueError"
            );
        }
        return MemberOf(DayType, (int)WeekdayOf(year, month, day));
    }

    private static void RequireDateTimeMonth(long month)
    {
        if (month is < 1 or > 12)
        {
            throw Fault($"month must be in 1..12, not {month}", default, "ValueError");
        }
    }

    private static PythonValue MonthRange(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var year = Integer(arguments[0], span);
        var month = Integer(arguments[1], span);
        ValidateMonth(month, span);
        var first = WeekdayOf(year, month, 1);
        var days = MonthLengths[month] + (month == 2 && IsLeapYear(year) ? 1 : 0);
        return new PythonTupleValue([
            MemberOf(DayType, (int)first),
            PythonWholeNumberValue.Create(days),
        ]);
    }

    private static void ValidateMonth(long month, TextSpan span)
    {
        if (month is < 1 or > 12)
        {
            throw RaiseIllegalMonth(month, span);
        }
    }

    private static PythonRaisedException RaiseIllegalMonth(long month, TextSpan span)
    {
        _ = span;
        var value = new PythonExceptionValue(
            "IllegalMonthError",
            $"bad month number {month.ToString(CultureInfo.InvariantCulture)}; must be 1-12"
        )
        {
            ManagedType = IllegalMonthErrorType,
        };
        value.Attributes["month"] = PythonWholeNumberValue.Create(month);
        return new PythonRaisedException(value);
    }

    private static PythonRaisedException RaiseIllegalWeekday(PythonValue weekday, TextSpan span)
    {
        _ = span;
        var value = new PythonExceptionValue(
            "IllegalWeekdayError",
            "bad weekday number "
                + weekday.ToRepresentationString()
                + "; must be 0 (Monday) to 6 (Sunday)"
        )
        {
            ManagedType = IllegalWeekdayErrorType,
        };
        value.Attributes["weekday"] = weekday;
        return new PythonRaisedException(value);
    }

    private static long WeekdayOf(long year, long month, long day)
    {
        if (year is < 1 or > 9999)
        {
            year = 2000 + FloorMod(year, 400);
        }
        var date = new DateOnly((int)year, (int)month, (int)day);
        return ((int)date.DayOfWeek + 6) % 7;
    }

    private static PythonValue TimeGm(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var fields = ManagedObjectProtocols.MaterializeValues(arguments[0], span);
        if (fields.Count < 6)
        {
            throw Fault(
                $"not enough values to unpack (expected 6, got {fields.Count})",
                span,
                "ValueError"
            );
        }
        var year = Integer(fields[0], span);
        var month = Integer(fields[1], span);
        var day = Integer(fields[2], span);
        var hour = Integer(fields[3], span);
        var minute = Integer(fields[4], span);
        var second = Integer(fields[5], span);
        var days = CivilOrdinal(year, month) + 1 - EpochOrdinal + day - 1;
        return PythonWholeNumberValue.Create(((days * 24 + hour) * 60 + minute) * 60 + second);
    }

    /// <summary>`datetime.date(year, month, 1).toordinal()`, with date()'s errors.</summary>
    private static long CivilOrdinal(long year, long month)
    {
        if (year is < 1 or > 9999)
        {
            throw Fault($"year must be in 1..9999, not {year}", default, "ValueError");
        }
        RequireDateTimeMonth(month);
        return new DateOnly((int)year, (int)month, 1).DayNumber;
    }

    private static PythonValue FormatString(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var columns = ManagedObjectProtocols.MaterializeValues(arguments[0], span);
        var width = arguments.Count > 1 ? (int)Integer(arguments[1], span) : 20;
        var spacing = arguments.Count > 2 ? (int)Integer(arguments[2], span) : 6;
        return new PythonTextValue(JoinColumns(columns, width, spacing));
    }

    private static PythonValue FormatColumns(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var columns = ManagedObjectProtocols.MaterializeValues(arguments[0], span);
        var width = arguments.Count > 1 ? (int)Integer(arguments[1], span) : 20;
        var spacing = arguments.Count > 2 ? (int)Integer(arguments[2], span) : 6;
        WriteOutput(JoinColumns(columns, width, spacing) + "\n");
        return PythonNoneValue.Instance;
    }

    private static string JoinColumns(IReadOnlyList<PythonValue> columns, int width, int spacing) =>
        string.Join(
            new string(' ', spacing),
            columns.Select(column => Center(Text(column), width))
        );

    private static string Text(PythonValue value) =>
        value is PythonTextValue text
            ? text.Value
            : throw TypeFault(
                $"a string is required, not '{ManagedObjectProtocols.GetTypeName(value)}'"
            );

    /// <summary>Python's `str.center`.</summary>
    private static string Center(string text, int width)
    {
        if (text.Length >= width)
        {
            return text;
        }
        var total = width - text.Length;
        // CPython's str.center adds one to the left padding when the margin, the width,
        // and 1 are all odd.
        var left = (total / 2) + (total & width & 1);
        var right = total - left;
        return new string(' ', Math.Max(left, 0)) + text + new string(' ', Math.Max(right, 0));
    }

    private static (
        PythonGlobalNamespace? Globals,
        PythonManagedObjectValue Calendar
    ) ModuleCalendar()
    {
        // The module-level `c` lives in the module's globals; the functions reach it
        // through the engine's module table.
        var module = UserObjectProtocols.Dispatcher!.ImportModule(
            "calendar",
            string.Empty,
            default
        );
        var calendar = ManagedObjectProtocols.GetAttribute(module, "c", default);
        return (
            null,
            calendar as PythonManagedObjectValue ?? throw TypeFault("calendar.c is missing")
        );
    }

    // -----------------------------------------------------------------------------
    // Calendar
    // -----------------------------------------------------------------------------

    /// <summary>The raw `self._firstweekday`; the property reports it modulo seven.</summary>
    private static long FirstWeekdayOf(PythonValue value) =>
        FloorMod(
            value is PythonManagedObjectValue instance
            && instance.Attributes.TryGetValue("_firstweekday", out var stored)
            && stored is PythonWholeNumberValue number
                ? (long)number.Value
                : 0,
            7
        );

    private static void SetFirstWeekdayOf(PythonValue value, long firstweekday)
    {
        if (value is not PythonManagedObjectValue instance)
        {
            throw TypeFault("the value is not a calendar");
        }
        instance.Attributes["_firstweekday"] = PythonWholeNumberValue.Create(firstweekday);
    }

    private static PythonManagedTypeValue CreateCalendarBase()
    {
        var type = new PythonManagedTypeValue("Calendar") { Module = "calendar" };
        type.Attributes["__module__"] = new PythonTextValue("calendar");
        type.Attributes["__qualname__"] = new PythonTextValue("Calendar");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Base calendar class. This class doesn't do any formatting. It simply provides "
                + "data to subclasses."
        );
        type.Attributes["__init__"] = BoundMethod(
            "Calendar",
            "__init__",
            (self, arguments) =>
            {
                SetFirstWeekdayOf(self!, arguments.Count > 0 ? Integer(arguments[0], default) : 0);
                return PythonNoneValue.Instance;
            },
            ["firstweekday"],
            [PythonWholeNumberValue.Create(0)]
        );
        type.Attributes["firstweekday"] = new PythonPropertyValue(
            new PythonProtocolFunctionValue(
                "firstweekday",
                (_, arguments) => PythonWholeNumberValue.Create(FirstWeekdayOf(arguments[0]))
            ),
            new PythonProtocolFunctionValue(
                "firstweekday",
                (_, arguments) =>
                {
                    SetFirstWeekdayOf(arguments[0], Integer(arguments[1], default));
                    return PythonNoneValue.Instance;
                }
            ),
            null
        );
        AddCalendarMethods(type);
        return type;
    }

    private static void AddCalendarMethods(PythonManagedTypeValue type)
    {
        type.Attributes["iterweekdays"] = Method(
            "Calendar",
            "iterweekdays",
            (self, _) =>
            {
                var first = FirstWeekdayOf(self!);
                return IteratorOf([
                    .. Enumerable
                        .Range(0, 7)
                        .Select(offset =>
                            (PythonValue)PythonWholeNumberValue.Create(FloorMod(first + offset, 7))
                        ),
                ]);
            },
            [],
            []
        );
        type.Attributes["itermonthdates"] = Method(
            "Calendar",
            "itermonthdates",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var month = Integer(arguments[1], default);
                var dateType = DateType();
                return IteratorOf([
                    .. MonthDays3(FirstWeekdayOf(self!), year, month)
                        .Select(entry =>
                            (PythonValue)
                                UserObjectProtocols.Dispatcher!.Invoke(
                                    dateType,
                                    [
                                        PythonWholeNumberValue.Create(entry.Item1),
                                        PythonWholeNumberValue.Create(entry.Item2),
                                        PythonWholeNumberValue.Create(entry.Item3),
                                    ],
                                    default
                                )
                        ),
                ]);
            },
            ["year", "month"],
            [null, null]
        );
        type.Attributes["itermonthdays"] = Method(
            "Calendar",
            "itermonthdays",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var month = Integer(arguments[1], default);
                return IteratorOf([
                    .. MonthDayNumbers(FirstWeekdayOf(self!), year, month)
                        .Select(day => (PythonValue)PythonWholeNumberValue.Create(day)),
                ]);
            },
            ["year", "month"],
            [null, null]
        );
        type.Attributes["itermonthdays2"] = Method(
            "Calendar",
            "itermonthdays2",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var month = Integer(arguments[1], default);
                var first = FirstWeekdayOf(self!);
                return IteratorOf([
                    .. MonthDayNumbers(first, year, month)
                        .Select(
                            (day, index) =>
                                (PythonValue)
                                    new PythonTupleValue([
                                        PythonWholeNumberValue.Create(day),
                                        PythonWholeNumberValue.Create(FloorMod(first + index, 7)),
                                    ])
                        ),
                ]);
            },
            ["year", "month"],
            [null, null]
        );
        type.Attributes["itermonthdays3"] = Method(
            "Calendar",
            "itermonthdays3",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var month = Integer(arguments[1], default);
                return IteratorOf([
                    .. MonthDays3(FirstWeekdayOf(self!), year, month)
                        .Select(entry =>
                            (PythonValue)
                                new PythonTupleValue([
                                    PythonWholeNumberValue.Create(entry.Item1),
                                    PythonWholeNumberValue.Create(entry.Item2),
                                    PythonWholeNumberValue.Create(entry.Item3),
                                ])
                        ),
                ]);
            },
            ["year", "month"],
            [null, null]
        );
        type.Attributes["itermonthdays4"] = Method(
            "Calendar",
            "itermonthdays4",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var month = Integer(arguments[1], default);
                var first = FirstWeekdayOf(self!);
                return IteratorOf([
                    .. MonthDays3(first, year, month)
                        .Select(
                            (entry, index) =>
                                (PythonValue)
                                    new PythonTupleValue([
                                        PythonWholeNumberValue.Create(entry.Item1),
                                        PythonWholeNumberValue.Create(entry.Item2),
                                        PythonWholeNumberValue.Create(entry.Item3),
                                        PythonWholeNumberValue.Create(FloorMod(first + index, 7)),
                                    ])
                        ),
                ]);
            },
            ["year", "month"],
            [null, null]
        );
        type.Attributes["monthdatescalendar"] = Method(
            "Calendar",
            "monthdatescalendar",
            (self, arguments) => Weeks(CallOn(self!, "itermonthdates", arguments), 7),
            ["year", "month"],
            [null, null]
        );
        type.Attributes["monthdays2calendar"] = Method(
            "Calendar",
            "monthdays2calendar",
            (self, arguments) => Weeks(CallOn(self!, "itermonthdays2", arguments), 7),
            ["year", "month"],
            [null, null]
        );
        type.Attributes["monthdayscalendar"] = Method(
            "Calendar",
            "monthdayscalendar",
            (self, arguments) => Weeks(CallOn(self!, "itermonthdays", arguments), 7),
            ["year", "month"],
            [null, null]
        );
        type.Attributes["yeardatescalendar"] = Method(
            "Calendar",
            "yeardatescalendar",
            (self, arguments) => YearWeeks(self!, arguments, "monthdatescalendar"),
            ["year", "width"],
            [null, PythonWholeNumberValue.Create(3)]
        );
        type.Attributes["yeardays2calendar"] = Method(
            "Calendar",
            "yeardays2calendar",
            (self, arguments) => YearWeeks(self!, arguments, "monthdays2calendar"),
            ["year", "width"],
            [null, PythonWholeNumberValue.Create(3)]
        );
        type.Attributes["yeardayscalendar"] = Method(
            "Calendar",
            "yeardayscalendar",
            (self, arguments) => YearWeeks(self!, arguments, "monthdayscalendar"),
            ["year", "width"],
            [null, PythonWholeNumberValue.Create(3)]
        );
    }

    private static PythonIteratorValue IteratorOf(List<PythonValue> items) =>
        ManagedObjectProtocols.GetIterator(new PythonListValue(items), default);

    private static PythonValue CallOn(
        PythonValue receiver,
        string method,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var callable = ManagedObjectProtocols.GetAttribute(receiver, method, default);
        return UserObjectProtocols.Dispatcher!.Invoke(callable, [.. arguments], default);
    }

    private static List<PythonValue> Materialize(PythonValue iterator) =>
        ManagedObjectProtocols.MaterializeValues(iterator, default);

    private static PythonListValue Weeks(PythonValue iterator, int size)
    {
        var items = Materialize(iterator);
        var rows = new List<PythonValue>();
        for (var index = 0; index < items.Count; index += size)
        {
            rows.Add(new PythonListValue([.. items.Skip(index).Take(size)]));
        }
        return new PythonListValue(rows);
    }

    private static PythonListValue YearWeeks(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        string monthMethod
    )
    {
        var year = Integer(arguments[0], default);
        var width = arguments.Count > 1 ? (int)Integer(arguments[1], default) : 3;
        var months = new List<PythonValue>();
        for (var month = 1; month <= 12; month++)
        {
            months.Add(
                CallOn(self, monthMethod, [arguments[0], PythonWholeNumberValue.Create(month)])
            );
        }
        var rows = new List<PythonValue>();
        for (var index = 0; index < months.Count; index += width)
        {
            rows.Add(new PythonListValue([.. months.Skip(index).Take(width)]));
        }
        return new PythonListValue(rows);
    }

    private static IEnumerable<(long, long, long)> MonthDays3(
        long firstWeekday,
        long year,
        long month
    )
    {
        ValidateMonth(month, default);
        var (day1, totalDays) = MonthRangeValues(year, month);
        var daysBefore = FloorMod(day1 - firstWeekday % 7, 7);
        var daysAfter = FloorMod(firstWeekday % 7 - day1 - totalDays, 7);
        var (previousYear, previousMonth) = PreviousMonth(year, month);
        var end = MonthLength(previousYear, previousMonth) + 1;
        for (var day = end - daysBefore; day < end; day++)
        {
            yield return (previousYear, previousMonth, day);
        }
        for (var day = 1; day <= totalDays; day++)
        {
            yield return (year, month, day);
        }
        var (nextYear, nextMonth) = NextMonth(year, month);
        for (var day = 1; day <= daysAfter; day++)
        {
            yield return (nextYear, nextMonth, day);
        }
    }

    private static IEnumerable<long> MonthDayNumbers(long firstWeekday, long year, long month)
    {
        ValidateMonth(month, default);
        var (day1, totalDays) = MonthRangeValues(year, month);
        var daysBefore = FloorMod(day1 - firstWeekday % 7, 7);
        var daysAfter = FloorMod(firstWeekday % 7 - day1 - totalDays, 7);
        for (var index = 0; index < daysBefore; index++)
        {
            yield return 0;
        }
        for (var day = 1; day <= totalDays; day++)
        {
            yield return day;
        }
        for (var index = 0; index < daysAfter; index++)
        {
            yield return 0;
        }
    }

    private static (long, long) MonthRangeValues(long year, long month) =>
        (WeekdayOf(year, month, 1), MonthLengths[month] + (month == 2 && IsLeapYear(year) ? 1 : 0));

    private static long MonthLength(long year, long month) =>
        MonthLengths[month] + (month == 2 && IsLeapYear(year) ? 1 : 0);

    private static (long, long) PreviousMonth(long year, long month) =>
        month == 1 ? (year - 1, 12) : (year, month - 1);

    private static (long, long) NextMonth(long year, long month) =>
        month == 12 ? (year + 1, 1) : (year, month + 1);

    // -----------------------------------------------------------------------------
    // TextCalendar
    // -----------------------------------------------------------------------------

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    )
    {
        if (parameters is not null)
        {
            body = GuardArity($"{owner}.{name}", body, parameters, defaults);
        }
        return PythonUserTypes.Method(owner, name, body, null, parameters, defaults);
    }

    private static PythonProtocolFunctionValue BoundMethod(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    ) => Method(owner, name, body, parameters, defaults);

    /// <summary>
    /// The positional call path bypasses the signature binding, so the wrapper checks the
    /// counts itself and reports them the way CPython's argument parser does.
    /// </summary>
    private static Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> GuardArity(
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[] parameters,
        PythonValue?[]? defaults
    )
    {
        var required = RequiredCount(parameters, defaults);
        return (self, arguments) =>
        {
            CheckArity(name, arguments, parameters, required);
            return body(self, arguments);
        };
    }

    private static int RequiredCount(string[] parameters, PythonValue?[]? defaults)
    {
        var required = 0;
        for (var index = 0; index < parameters.Length; index++)
        {
            if (defaults is null || index >= defaults.Length || defaults[index] is null)
            {
                required++;
            }
        }
        return required;
    }

    private static void CheckArity(
        string name,
        IReadOnlyList<PythonValue> arguments,
        string[] parameters,
        int required,
        bool method = true
    )
    {
        var receiver = method ? 1 : 0;
        if (arguments.Count >= required && arguments.Count <= parameters.Length)
        {
            return;
        }
        if (arguments.Count < required)
        {
            var missing = new List<string>();
            for (var index = arguments.Count; index < required; index++)
            {
                missing.Add($"'{parameters[index]}'");
            }
            throw TypeFault(
                $"{name}() missing {missing.Count} required positional argument"
                    + (missing.Count == 1 ? "" : "s")
                    + ": "
                    + JoinNames(missing)
            );
        }
        var given = arguments.Count + receiver;
        var maximum = parameters.Length + receiver;
        throw TypeFault(
            required == parameters.Length
                ? $"{name}() takes {maximum} positional argument"
                    + (maximum == 1 ? "" : "s")
                    + $" but {given} were given"
                : $"{name}() takes from {required + receiver} to {maximum} positional arguments "
                    + $"but {given} were given"
        );
    }

    private static string JoinNames(List<string> names) =>
        names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + $", and {names[^1]}",
        };

    private static PythonManagedTypeValue CreateTextCalendar()
    {
        var type = new PythonManagedTypeValue(
            "TextCalendar",
            bases: [CalendarType],
            linearizedBases: CalendarType.Mro
        )
        {
            Module = "calendar",
        };
        type.Attributes["__module__"] = new PythonTextValue("calendar");
        type.Attributes["__qualname__"] = new PythonTextValue("TextCalendar");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Subclass of Calendar that outputs a calendar as a simple plain text similar "
                + "to the UNIX program cal."
        );
        type.Attributes["formatday"] = Method(
            "TextCalendar",
            "formatday",
            (_, arguments) =>
            {
                var day = FormatInt(arguments[0], "%i");
                var width = (int)Integer(arguments[2], default);
                var text =
                    day == 0 ? string.Empty : day.ToString(CultureInfo.InvariantCulture).PadLeft(2);
                return new PythonTextValue(Center(text, width));
            },
            ["day", "weekday", "width"],
            [null, null, null]
        );
        type.Attributes["formatweek"] = Method(
            "TextCalendar",
            "formatweek",
            (self, arguments) =>
            {
                var week = Materialize(arguments[0]);
                var width = (int)Integer(arguments[1], default);
                return new PythonTextValue(
                    string.Join(" ", week.Select(entry => FormatDayOf(self!, entry, width)))
                );
            },
            ["theweek", "width"],
            [null, null]
        );
        type.Attributes["formatweekday"] = Method(
            "TextCalendar",
            "formatweekday",
            (_, arguments) => FormatWeekdayText(arguments[0], arguments[1]),
            ["day", "width"],
            [null, null]
        );
        type.Attributes["formatweekheader"] = Method(
            "TextCalendar",
            "formatweekheader",
            (self, arguments) =>
            {
                var width = (int)Integer(arguments[0], default);
                var first = FirstWeekdayOf(self!);
                return new PythonTextValue(
                    string.Join(
                        " ",
                        Enumerable
                            .Range(0, 7)
                            .Select(offset =>
                                FormatWeekdayOf(self!, FloorMod(first + offset, 7), width)
                            )
                    )
                );
            },
            ["width"],
            [null]
        );
        type.Attributes["formatmonthname"] = Method(
            "TextCalendar",
            "formatmonthname",
            (_, arguments) =>
                FormatMonthNameText(
                    arguments[0],
                    arguments[1],
                    arguments[2],
                    arguments.Count < 4 ? PythonTruthValue.True : arguments[3]
                ),
            ["theyear", "themonth", "width", "withyear"],
            [null, null, null, PythonTruthValue.True]
        );
        type.Attributes["prweek"] = Method(
            "TextCalendar",
            "prweek",
            (self, arguments) =>
            {
                WriteOutput(FormatWeekOf(self!, arguments[0], Integer(arguments[1], default)));
                return PythonNoneValue.Instance;
            },
            ["theweek", "width"],
            [null, null]
        );
        type.Attributes["prmonth"] = Method(
            "TextCalendar",
            "prmonth",
            (self, arguments) =>
            {
                WriteOutput(
                    FormatMonthOf(
                        self!,
                        arguments[0],
                        arguments[1],
                        arguments.Count > 2 ? Integer(arguments[2], default) : 0,
                        arguments.Count > 3 ? Integer(arguments[3], default) : 0
                    )
                );
                return PythonNoneValue.Instance;
            },
            ["theyear", "themonth", "w", "l"],
            [null, null, PythonWholeNumberValue.Create(0), PythonWholeNumberValue.Create(0)]
        );
        type.Attributes["formatmonth"] = Method(
            "TextCalendar",
            "formatmonth",
            (self, arguments) =>
                new PythonTextValue(
                    FormatMonthOf(
                        self!,
                        arguments[0],
                        arguments[1],
                        arguments.Count > 2 ? Integer(arguments[2], default) : 0,
                        arguments.Count > 3 ? Integer(arguments[3], default) : 0
                    )
                ),
            ["theyear", "themonth", "w", "l"],
            [null, null, PythonWholeNumberValue.Create(0), PythonWholeNumberValue.Create(0)]
        );
        type.Attributes["formatyear"] = Method(
            "TextCalendar",
            "formatyear",
            (self, arguments) =>
                new PythonTextValue(
                    FormatYearOf(
                        self!,
                        arguments[0],
                        arguments.Count > 1 ? Integer(arguments[1], default) : 2,
                        arguments.Count > 2 ? Integer(arguments[2], default) : 1,
                        arguments.Count > 3 ? Integer(arguments[3], default) : 6,
                        arguments.Count > 4 ? Integer(arguments[4], default) : 3
                    )
                ),
            ["theyear", "w", "l", "c", "m"],
            [
                null,
                PythonWholeNumberValue.Create(2),
                PythonWholeNumberValue.Create(1),
                PythonWholeNumberValue.Create(6),
                PythonWholeNumberValue.Create(3),
            ]
        );
        type.Attributes["pryear"] = Method(
            "TextCalendar",
            "pryear",
            (self, arguments) =>
            {
                WriteOutput(
                    FormatYearOf(
                        self!,
                        arguments[0],
                        arguments.Count > 1 ? Integer(arguments[1], default) : 0,
                        arguments.Count > 2 ? Integer(arguments[2], default) : 0,
                        arguments.Count > 3 ? Integer(arguments[3], default) : 6,
                        arguments.Count > 4 ? Integer(arguments[4], default) : 3
                    )
                );
                return PythonNoneValue.Instance;
            },
            ["theyear", "w", "l", "c", "m"],
            [
                null,
                PythonWholeNumberValue.Create(0),
                PythonWholeNumberValue.Create(0),
                PythonWholeNumberValue.Create(6),
                PythonWholeNumberValue.Create(3),
            ]
        );
        return type;
    }

    private static PythonTextValue FormatWeekdayText(PythonValue dayValue, PythonValue widthValue)
    {
        var day = Integer(dayValue, default);
        var width = (int)Integer(widthValue, default);
        var names = width >= 9 ? DayNames : DayAbbreviations;
        var name = At(names, day);
        return new PythonTextValue(Center(name[..Math.Min(name.Length, width)], width));
    }

    private static PythonTextValue FormatMonthNameText(
        PythonValue yearValue,
        PythonValue monthValue,
        PythonValue widthValue,
        PythonValue withYearValue
    )
    {
        var year = Integer(yearValue, default);
        var month = Integer(monthValue, default);
        var width = (int)Integer(widthValue, default);
        ValidateMonth(month, default);
        var text = MonthNames[month - 1];
        if (ManagedObjectProtocols.IsTrue(withYearValue))
        {
            text = $"{text} {yearValue.ToRepresentationString()}";
        }
        return new PythonTextValue(Center(text, width));
    }

    /// <summary>A list index with Python's negative wrapping and IndexError.</summary>
    private static string At(string[] items, long index)
    {
        var offset = index < 0 ? index + items.Length : index;
        if (offset < 0 || offset >= items.Length)
        {
            throw Fault("list index out of range", default, "IndexError");
        }
        return items[(int)offset];
    }

    private static string FormatDayOf(PythonValue self, PythonValue dayEntry, int width)
    {
        var fields = UnpackPair(dayEntry);
        var call = ManagedObjectProtocols.GetAttribute(self, "formatday", default);
        return Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                call,
                [fields[0], fields[1], PythonWholeNumberValue.Create(width)],
                default
            )
        );
    }

    /// <summary>`for (day, weekday) in theweek`: a two-item unpack with CPython's errors.</summary>
    private static List<PythonValue> UnpackPair(PythonValue entry)
    {
        List<PythonValue> fields;
        try
        {
            fields = Materialize(entry);
        }
        catch (PythonRuntimeException exception)
            when (exception.PythonExceptionTypeName == "TypeError")
        {
            throw TypeFault(
                $"cannot unpack non-iterable {ManagedObjectProtocols.GetTypeName(entry)} object"
            );
        }
        if (fields.Count < 2)
        {
            throw Fault(
                $"not enough values to unpack (expected 2, got {fields.Count})",
                default,
                "ValueError"
            );
        }
        if (fields.Count > 2)
        {
            throw Fault(
                $"too many values to unpack (expected 2, got {fields.Count})",
                default,
                "ValueError"
            );
        }
        return fields;
    }

    private static string FormatWeekdayOf(PythonValue self, long day, int width)
    {
        var call = ManagedObjectProtocols.GetAttribute(self, "formatweekday", default);
        return Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                call,
                [PythonWholeNumberValue.Create(day), PythonWholeNumberValue.Create(width)],
                default
            )
        );
    }

    private static string FormatWeekOf(PythonValue self, PythonValue week, long width) =>
        Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "formatweek", default),
                [week, PythonWholeNumberValue.Create(width)],
                default
            )
        );

    private static string FormatMonthNameOf(
        PythonValue self,
        long year,
        long month,
        long width,
        bool withYear
    ) =>
        Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "formatmonthname", default),
                [
                    PythonWholeNumberValue.Create(year),
                    PythonWholeNumberValue.Create(month),
                    PythonWholeNumberValue.Create(width),
                    PythonTruthValue.FromBoolean(withYear),
                ],
                default
            )
        );

    private static int Max(int first, int second) => Math.Max(first, second);

    private static long Max(long first, long second) => Math.Max(first, second);

    private static string FormatMonthOf(
        PythonValue self,
        PythonValue yearValue,
        PythonValue monthValue,
        long w,
        long l
    )
    {
        var year = Integer(yearValue, default);
        var month = Integer(monthValue, default);
        w = Math.Max(2, w);
        l = Math.Max(1, l);
        var text = FormatMonthNameOf(self, year, month, 7 * (w + 1) - 1, true).TrimEnd();
        text += string.Concat(Enumerable.Repeat("\n", (int)l));
        text += HeaderOf(self, w).TrimEnd();
        text += string.Concat(Enumerable.Repeat("\n", (int)l));
        var weeks = ManagedObjectProtocols.MaterializeValues(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "monthdays2calendar", default),
                [PythonWholeNumberValue.Create(year), PythonWholeNumberValue.Create(month)],
                default
            ),
            default
        );
        foreach (var week in weeks)
        {
            text += FormatWeekOf(self, week, w).TrimEnd();
            text += string.Concat(Enumerable.Repeat("\n", (int)l));
        }
        return text;
    }

    private static string HeaderOf(PythonValue self, long width) =>
        Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "formatweekheader", default),
                [PythonWholeNumberValue.Create(width)],
                default
            )
        );

    private static string FormatYearOf(
        PythonValue self,
        PythonValue yearValue,
        long w,
        long l,
        long c,
        long m
    )
    {
        var year = Integer(yearValue, default);
        w = Math.Max(2, w);
        l = Math.Max(1, l);
        c = Math.Max(2, c);
        var colwidth = (w + 1) * 7 - 1;
        var parts = new List<string>
        {
            Center(year.ToString(CultureInfo.InvariantCulture), (int)(colwidth * m + c * (m - 1)))
                .TrimEnd(),
        };
        parts.Add(string.Concat(Enumerable.Repeat("\n", (int)l)));
        var header = HeaderOf(self, w);
        var rows = ManagedObjectProtocols.MaterializeValues(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "yeardays2calendar", default),
                [PythonWholeNumberValue.Create(year), PythonWholeNumberValue.Create(m)],
                default
            ),
            default
        );
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = ManagedObjectProtocols.MaterializeValues(rows[rowIndex], default);
            parts.Add(string.Concat(Enumerable.Repeat("\n", (int)l)));
            var months = Enumerable
                .Range(
                    (int)(m * rowIndex + 1),
                    (int)(Math.Min(m * (rowIndex + 1) + 1, 13) - (m * rowIndex + 1))
                )
                .ToList();
            parts.Add(
                JoinColumns(
                        [
                            .. months.Select(month =>
                                (PythonValue)
                                    new PythonTextValue(
                                        FormatMonthNameOf(self, year, month, colwidth, false)
                                    )
                            ),
                        ],
                        (int)colwidth,
                        (int)c
                    )
                    .TrimEnd()
            );
            parts.Add(string.Concat(Enumerable.Repeat("\n", (int)l)));
            parts.Add(
                JoinColumns(
                        [.. months.Select(_ => (PythonValue)new PythonTextValue(header))],
                        (int)colwidth,
                        (int)c
                    )
                    .TrimEnd()
            );
            parts.Add(string.Concat(Enumerable.Repeat("\n", (int)l)));
            var height = row.Select(calendar =>
                    ManagedObjectProtocols.MaterializeValues(calendar, default).Count
                )
                .Max();
            for (var weekIndex = 0; weekIndex < height; weekIndex++)
            {
                var weeks = new List<PythonValue>();
                foreach (var calendar in row)
                {
                    var weeksOfMonth = ManagedObjectProtocols.MaterializeValues(calendar, default);
                    weeks.Add(
                        weekIndex >= weeksOfMonth.Count
                            ? new PythonTextValue(string.Empty)
                            : new PythonTextValue(FormatWeekOf(self, weeksOfMonth[weekIndex], w))
                    );
                }
                parts.Add(JoinColumns(weeks, (int)colwidth, (int)c).TrimEnd());
                parts.Add(string.Concat(Enumerable.Repeat("\n", (int)l)));
            }
        }
        return string.Concat(parts);
    }

    // -----------------------------------------------------------------------------
    // HTMLCalendar
    // -----------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateHtmlCalendar()
    {
        var type = new PythonManagedTypeValue(
            "HTMLCalendar",
            bases: [CalendarType],
            linearizedBases: CalendarType.Mro
        )
        {
            Module = "calendar",
        };
        type.Attributes["__module__"] = new PythonTextValue("calendar");
        type.Attributes["__qualname__"] = new PythonTextValue("HTMLCalendar");
        type.Attributes["__doc__"] = new PythonTextValue(
            "This calendar returns complete HTML pages."
        );
        type.Attributes["cssclasses"] = new PythonListValue([
            .. CssClassNames.Select(name => (PythonValue)new PythonTextValue(name)),
        ]);
        type.Attributes["cssclasses_weekday_head"] = type.Attributes["cssclasses"];
        type.Attributes["cssclass_noday"] = new PythonTextValue("noday");
        type.Attributes["cssclass_month_head"] = new PythonTextValue("month");
        type.Attributes["cssclass_month"] = new PythonTextValue("month");
        type.Attributes["cssclass_year_head"] = new PythonTextValue("year");
        type.Attributes["cssclass_year"] = new PythonTextValue("year");
        type.Attributes["formatday"] = Method(
            "HTMLCalendar",
            "formatday",
            (self, arguments) =>
            {
                var day = FormatInt(arguments[0], "%d");
                var weekday = (int)Integer(arguments[1], default);
                if (day == 0)
                {
                    return new PythonTextValue(
                        $"<td class=\"{AttributeText(self!, "cssclass_noday")}\">&nbsp;</td>"
                    );
                }
                return new PythonTextValue(
                    $"<td class=\"{At(CssClasses(self!), weekday)}\">{day}</td>"
                );
            },
            ["day", "weekday"],
            [null, null]
        );
        type.Attributes["formatweek"] = Method(
            "HTMLCalendar",
            "formatweek",
            (self, arguments) =>
            {
                var week = Materialize(arguments[0]);
                var call = ManagedObjectProtocols.GetAttribute(self!, "formatday", default);
                var cells = string.Concat(
                    week.Select(entry =>
                    {
                        var fields = UnpackPair(entry);
                        return Text(
                            UserObjectProtocols.Dispatcher!.Invoke(
                                call,
                                [fields[0], fields[1]],
                                default
                            )
                        );
                    })
                );
                return new PythonTextValue($"<tr>{cells}</tr>");
            },
            ["theweek"],
            [null]
        );
        type.Attributes["formatweekday"] = Method(
            "HTMLCalendar",
            "formatweekday",
            (self, arguments) => FormatHtmlWeekday(self!, arguments[0]),
            ["day"],
            [null]
        );
        type.Attributes["formatweekheader"] = Method(
            "HTMLCalendar",
            "formatweekheader",
            (self, arguments) =>
            {
                _ = arguments;
                var days = Materialize(
                    UserObjectProtocols.Dispatcher!.Invoke(
                        ManagedObjectProtocols.GetAttribute(self!, "iterweekdays", default),
                        [],
                        default
                    )
                );
                var call = ManagedObjectProtocols.GetAttribute(self!, "formatweekday", default);
                var cells = string.Concat(
                    days.Select(day =>
                        Text(UserObjectProtocols.Dispatcher!.Invoke(call, [day], default))
                    )
                );
                return new PythonTextValue($"<tr>{cells}</tr>");
            },
            [],
            []
        );
        type.Attributes["formatmonthname"] = Method(
            "HTMLCalendar",
            "formatmonthname",
            (self, arguments) =>
                FormatHtmlMonthName(
                    self!,
                    arguments[0],
                    arguments[1],
                    arguments.Count < 3 ? PythonTruthValue.True : arguments[2]
                ),
            ["theyear", "themonth", "withyear"],
            [null, null, PythonTruthValue.True]
        );
        type.Attributes["formatmonth"] = Method(
            "HTMLCalendar",
            "formatmonth",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var month = Integer(arguments[1], default);
                var withYear = arguments.Count < 4 || ManagedObjectProtocols.IsTrue(arguments[3]);
                var text =
                    $"<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" "
                    + $"class=\"{AttributeText(self!, "cssclass_month")}\">\n";
                text += FormatMonthNameHtml(self!, year, month, withYear) + "\n";
                text += FormatWeekHeaderHtml(self!) + "\n";
                var weeks = ManagedObjectProtocols.MaterializeValues(
                    UserObjectProtocols.Dispatcher!.Invoke(
                        ManagedObjectProtocols.GetAttribute(self!, "monthdays2calendar", default),
                        [PythonWholeNumberValue.Create(year), PythonWholeNumberValue.Create(month)],
                        default
                    ),
                    default
                );
                foreach (var week in weeks)
                {
                    text += FormatWeekHtml(self!, week) + "\n";
                }
                text += "</table>\n";
                return new PythonTextValue(text);
            },
            ["theyear", "themonth", "width", "withyear"],
            [null, null, PythonWholeNumberValue.Create(0), PythonTruthValue.True]
        );
        type.Attributes["formatyear"] = Method(
            "HTMLCalendar",
            "formatyear",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var width = arguments.Count > 1 ? (int)Integer(arguments[1], default) : 3;
                width = Math.Max(width, 1);
                var text =
                    "<table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" "
                    + $"class=\"{AttributeText(self!, "cssclass_year")}\">\n";
                text +=
                    $"<tr><th colspan=\"{width}\" class=\""
                    + $"{AttributeText(self!, "cssclass_year_head")}\">"
                    + $"{year.ToString(CultureInfo.InvariantCulture)}</th></tr>";
                for (var start = 1; start <= 12; start += width)
                {
                    var months = Enumerable.Range(start, Math.Min(start + width, 13) - start);
                    text += "<tr>";
                    foreach (var month in months)
                    {
                        text += "<td>";
                        text += Text(
                            UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
                                ManagedObjectProtocols.GetAttribute(self!, "formatmonth", default),
                                [
                                    PythonWholeNumberValue.Create(year),
                                    PythonWholeNumberValue.Create(month),
                                ],
                                ["withyear"],
                                [PythonTruthValue.False],
                                default
                            )
                        );
                        text += "</td>";
                    }
                    text += "</tr>";
                }
                text += "</table>";
                return new PythonTextValue(text);
            },
            ["theyear", "width"],
            [null, PythonWholeNumberValue.Create(3)]
        );
        type.Attributes["formatyearpage"] = Method(
            "HTMLCalendar",
            "formatyearpage",
            (self, arguments) =>
            {
                var year = Integer(arguments[0], default);
                var width = arguments.Count > 1 ? (int)Integer(arguments[1], default) : 3;
                var cssValue =
                    arguments.Count > 2 ? arguments[2] : new PythonTextValue("calendar.css");
                var css = cssValue is PythonNoneValue ? null : Text(cssValue);
                var encoding =
                    arguments.Count > 3 && arguments[3] is not PythonNoneValue
                        ? Text(arguments[3])
                        : "utf-8";
                var text = $"<?xml version=\"1.0\" encoding=\"{encoding}\"?>\n";
                text +=
                    "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\" "
                    + "\"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">\n";
                text += "<html>\n<head>\n";
                text +=
                    "<meta http-equiv=\"Content-Type\" content=\"text/html; "
                    + $"charset={encoding}\" />\n";
                if (css is not null)
                {
                    text += $"<link rel=\"stylesheet\" type=\"text/css\" href=\"{css}\" />\n";
                }
                text += $"<title>Calendar for {year}</title>\n";
                text += "</head>\n<body>\n";
                text += Text(
                    UserObjectProtocols.Dispatcher!.Invoke(
                        ManagedObjectProtocols.GetAttribute(self!, "formatyear", default),
                        [PythonWholeNumberValue.Create(year), PythonWholeNumberValue.Create(width)],
                        default
                    )
                );
                text += "</body>\n</html>\n";
                Encoding system;
                try
                {
                    system = Encoding.GetEncoding(
                        encoding,
                        new EncoderReplacementFallback(),
                        new DecoderReplacementFallback()
                    );
                }
                catch (ArgumentException)
                {
                    throw Fault($"unknown encoding: {encoding}", default, "LookupError");
                }
                return new PythonByteSequenceValue([.. system.GetBytes(text)]);
            },
            ["theyear", "width", "css", "encoding"],
            [
                null,
                PythonWholeNumberValue.Create(3),
                new PythonTextValue("calendar.css"),
                PythonNoneValue.Instance,
            ]
        );
        return type;
    }

    private static PythonTextValue FormatHtmlWeekday(PythonValue self, PythonValue dayValue)
    {
        var day = Integer(dayValue, default);
        return new PythonTextValue(
            $"<th class=\"{At(WeekdayHeadClasses(self), day)}\">{At(DayAbbreviations, day)}</th>"
        );
    }

    private static PythonTextValue FormatHtmlMonthName(
        PythonValue self,
        PythonValue yearValue,
        PythonValue monthValue,
        PythonValue withYearValue
    )
    {
        var year = Integer(yearValue, default);
        var month = Integer(monthValue, default);
        ValidateMonth(month, default);
        var text = ManagedObjectProtocols.IsTrue(withYearValue)
            ? $"{MonthNames[month - 1]} {yearValue.ToRepresentationString()}"
            : MonthNames[month - 1];
        return new PythonTextValue(
            $"<tr><th colspan=\"7\" class=\""
                + $"{AttributeText(self, "cssclass_month_head")}\">{text}</th></tr>"
        );
    }

    private static string AttributeText(PythonValue self, string name) =>
        ManagedObjectProtocols.GetAttribute(self, name, default) is PythonTextValue text
            ? text.Value
            : throw TypeFault($"'{name}' must be a string");

    private static string[] WeekdayHeadClasses(PythonValue self) =>
        [
            .. ManagedObjectProtocols
                .MaterializeValues(
                    ManagedObjectProtocols.GetAttribute(self, "cssclasses_weekday_head", default),
                    default
                )
                .Select(Text),
        ];

    private static string[] CssClasses(PythonValue self) =>
        [
            .. ManagedObjectProtocols
                .MaterializeValues(
                    ManagedObjectProtocols.GetAttribute(self, "cssclasses", default),
                    default
                )
                .Select(Text),
        ];

    private static string FormatMonthNameHtml(
        PythonValue self,
        long year,
        long month,
        bool withYear
    ) =>
        Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "formatmonthname", default),
                [
                    PythonWholeNumberValue.Create(year),
                    PythonWholeNumberValue.Create(month),
                    PythonTruthValue.FromBoolean(withYear),
                ],
                default
            )
        );

    private static string FormatWeekHeaderHtml(PythonValue self) =>
        Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "formatweekheader", default),
                [],
                default
            )
        );

    private static string FormatWeekHtml(PythonValue self, PythonValue week) =>
        Text(
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(self, "formatweek", default),
                [week],
                default
            )
        );

    // -----------------------------------------------------------------------------
    // The locale subclasses and the type table
    // -----------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateLocaleTextCalendar() =>
        CreateLocaleSubclass("LocaleTextCalendar", TextCalendarType);

    private static PythonManagedTypeValue CreateLocaleHtmlCalendar() =>
        CreateLocaleSubclass("LocaleHTMLCalendar", HtmlCalendarType);

    /// <summary>
    /// The deprecated locale subclasses: the `locale` argument is accepted, and the C
    /// locale is what they format with — the runtime has no locale module.
    /// </summary>
    private static PythonManagedTypeValue CreateLocaleSubclass(
        string name,
        PythonManagedTypeValue baseType
    )
    {
        var type = new PythonManagedTypeValue(
            name,
            bases: [baseType],
            linearizedBases: baseType.Mro
        )
        {
            Module = "calendar",
        };
        type.Attributes["__module__"] = new PythonTextValue("calendar");
        type.Attributes["__qualname__"] = new PythonTextValue(name);
        type.Attributes["__init__"] = Method(
            name,
            "__init__",
            (self, arguments) =>
            {
                SetFirstWeekdayOf(self!, arguments.Count > 0 ? Integer(arguments[0], default) : 0);
                if (self is PythonManagedObjectValue instance)
                {
                    instance.Attributes["locale"] =
                        arguments.Count > 1 ? arguments[1] : PythonNoneValue.Instance;
                }
                return PythonNoneValue.Instance;
            },
            ["firstweekday", "locale"],
            [PythonWholeNumberValue.Create(0), PythonNoneValue.Instance]
        );
        if (baseType == TextCalendarType)
        {
            type.Attributes["formatweekday"] = Method(
                name,
                "formatweekday",
                (_, arguments) => FormatWeekdayText(arguments[0], arguments[1]),
                ["day", "width"],
                [null, null]
            );
            type.Attributes["formatmonthname"] = Method(
                name,
                "formatmonthname",
                (_, arguments) =>
                    FormatMonthNameText(
                        arguments[0],
                        arguments[1],
                        arguments[2],
                        arguments.Count < 4 ? PythonTruthValue.True : arguments[3]
                    ),
                ["theyear", "themonth", "width", "withyear"],
                [null, null, null, PythonTruthValue.True]
            );
        }
        else
        {
            type.Attributes["formatweekday"] = Method(
                name,
                "formatweekday",
                (self, arguments) => FormatHtmlWeekday(self!, arguments[0]),
                ["day"],
                [null]
            );
            type.Attributes["formatmonthname"] = Method(
                name,
                "formatmonthname",
                (self, arguments) =>
                    FormatHtmlMonthName(
                        self!,
                        arguments[0],
                        arguments[1],
                        arguments.Count < 3 ? PythonTruthValue.True : arguments[2]
                    ),
                ["theyear", "themonth", "withyear"],
                [null, null, PythonTruthValue.True]
            );
        }
        return type;
    }

    private static readonly PythonManagedTypeValue CalendarType = CreateCalendarBase();

    private static readonly PythonManagedTypeValue TextCalendarType = CreateTextCalendar();

    private static readonly PythonManagedTypeValue HtmlCalendarType = CreateHtmlCalendar();

    private static readonly PythonManagedTypeValue LocaleTextCalendarType =
        CreateLocaleTextCalendar();

    private static readonly PythonManagedTypeValue LocaleHtmlCalendarType =
        CreateLocaleHtmlCalendar();
}
