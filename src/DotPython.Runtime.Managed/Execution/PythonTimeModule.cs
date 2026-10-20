using System.Diagnostics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The managed `time` module: the clocks a program asks the operating system for, the
/// `struct_time` breakdown every calendar function works on, and `strftime`/`strptime` over
/// the C locale's directives. The local zone comes from the host's own setting; there is no
/// timezone database.
/// </summary>
internal static class PythonTimeModule
{
    private const string Module = "time";

    private static readonly string[] WeekdayShort =
    [
        "Mon",
        "Tue",
        "Wed",
        "Thu",
        "Fri",
        "Sat",
        "Sun",
    ];
    private static readonly string[] WeekdayLong =
    [
        "Monday",
        "Tuesday",
        "Wednesday",
        "Thursday",
        "Friday",
        "Saturday",
        "Sunday",
    ];
    private static readonly string[] MonthShort =
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
    private static readonly string[] MonthLong =
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

    /// <summary>The macOS clock ids `clock_gettime` answers to.</summary>
    private static readonly (string Name, long Id)[] ClockIds =
    [
        ("CLOCK_REALTIME", 0),
        ("CLOCK_MONOTONIC", 6),
        ("CLOCK_MONOTONIC_RAW", 4),
        ("CLOCK_MONOTONIC_RAW_APPROX", 5),
        ("CLOCK_UPTIME_RAW", 8),
        ("CLOCK_UPTIME_RAW_APPROX", 9),
        ("CLOCK_PROCESS_CPUTIME_ID", 12),
        ("CLOCK_THREAD_CPUTIME_ID", 16),
    ];

    private static readonly PythonManagedTypeValue StructTimeType = CreateStructTimeType();

    private static readonly PythonManagedTypeValue ClockInfoType = CreateClockInfoType();

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("struct_time", StructTimeType);
        foreach (var (name, id) in ClockIds)
        {
            globals.SetValue(name, PythonWholeNumberValue.Create(id));
        }
        // `timezone` and `altzone` are the *negations* of the offsets Python reports,
        // which is what the C library's globals carry.
        globals.SetValue("timezone", PythonWholeNumberValue.Create((long)-LocalStandardOffset()));
        globals.SetValue("altzone", PythonWholeNumberValue.Create((long)-LocalDaylightOffset()));
        globals.SetValue("daylight", PythonWholeNumberValue.Create(LocalDaylight()));
        globals.SetValue(
            "tzname",
            new PythonTupleValue([
                new PythonTextValue(LocalZoneName(daylight: false)),
                new PythonTextValue(LocalZoneName(daylight: true)),
            ])
        );
        globals.SetValue("time", Function("time", Time));
        globals.SetValue("time_ns", Function("time_ns", TimeNs));
        globals.SetValue("monotonic", Function("monotonic", Monotonic));
        globals.SetValue("monotonic_ns", Function("monotonic_ns", MonotonicNs));
        globals.SetValue("perf_counter", Function("perf_counter", Monotonic));
        globals.SetValue("perf_counter_ns", Function("perf_counter_ns", MonotonicNs));
        globals.SetValue("process_time", Function("process_time", ProcessTime));
        globals.SetValue("process_time_ns", Function("process_time_ns", ProcessTimeNs));
        globals.SetValue("thread_time", Function("thread_time", ProcessTime));
        globals.SetValue("thread_time_ns", Function("thread_time_ns", ProcessTimeNs));
        globals.SetValue("sleep", Function("sleep", Sleep, ["secs"]));
        globals.SetValue(
            "gmtime",
            Function("gmtime", (arguments, span) => BreakDown(arguments, span, utc: true), ["secs"])
        );
        globals.SetValue(
            "localtime",
            Function(
                "localtime",
                (arguments, span) => BreakDown(arguments, span, utc: false),
                ["secs"]
            )
        );
        globals.SetValue("mktime", Function("mktime", MakeTime, ["t"]));
        globals.SetValue("asctime", Function("asctime", Asctime, ["t"]));
        globals.SetValue("ctime", Function("ctime", Ctime, ["secs"]));
        globals.SetValue("strftime", Function("strftime", Strftime, ["format", "t"]));
        globals.SetValue("strptime", Function("strptime", Strptime, ["string", "format"]));
        globals.SetValue("tzset", Function("tzset", (_, _) => PythonNoneValue.Instance));
        globals.SetValue("clock_gettime", Function("clock_gettime", ClockGettime, ["clk_id"]));
        globals.SetValue(
            "clock_gettime_ns",
            Function("clock_gettime_ns", ClockGettimeNs, ["clk_id"])
        );
        globals.SetValue("clock_getres", Function("clock_getres", ClockGetres, ["clk_id"]));
        globals.SetValue(
            "clock_settime",
            Function("clock_settime", ClockSettime, ["clk_id", "time"])
        );
        globals.SetValue(
            "clock_settime_ns",
            Function("clock_settime_ns", ClockSettime, ["clk_id", "time_ns"])
        );
        globals.SetValue("get_clock_info", Function("get_clock_info", GetClockInfo, ["name"]));
    }

    // -----------------------------------------------------------------------------
    // struct_time
    // -----------------------------------------------------------------------------

    /// <summary>
    /// `time.struct_time`: the nine fields of a broken-down time, as the tuple subclass
    /// Python's own C type is — its fields are reads of the tuple it wraps.
    /// </summary>
    private static PythonManagedTypeValue CreateStructTimeType()
    {
        var tuple = PythonBuiltinTypes.Tuple;
        var type = new PythonManagedTypeValue("struct_time")
        {
            Module = Module,
            QualName = "struct_time",
            LayoutBase = tuple,
        };
        type.Attributes["__module__"] = new PythonTextValue(Module);
        type.Attributes["__qualname__"] = new PythonTextValue("struct_time");
        type.Attributes["__doc__"] = new PythonTextValue(
            "The time value as returned by gmtime(), localtime(), and strptime(), and "
                + "accepted by asctime(), mktime() and strftime().  May be considered as a "
                + "sequence of 9 integers.\n\nNote that several fields' values are not the "
                + "same as those defined by the C standard. Operating system-related fields "
                + "(e.g. tm_isdst) may have different ranges."
        );
        var fields = new (string Name, string Doc)[]
        {
            ("tm_year", "year, for example, 1993"),
            ("tm_mon", "month of year, range 1 - 12"),
            ("tm_mday", "day of month, range 1 - 31"),
            ("tm_hour", "hours, range 0 - 23"),
            ("tm_min", "minutes, range 0 - 59"),
            ("tm_sec", "seconds, range 0 - 61"),
            ("tm_wday", "day of week, range 0 - 6, Monday is 0"),
            ("tm_yday", "day of year, range 1 - 366"),
            ("tm_isdst", "daylight savings flag, -1 means unknown"),
        };
        for (var index = 0; index < fields.Length; index++)
        {
            type.Attributes[fields[index].Name] = new PythonTupleGetterValue(
                fields[index].Name,
                index,
                fields[index].Doc
            );
        }
        // The C struct carries two more fields beside the nine a sequence shows.
        type.Attributes["tm_zone"] = ReadOnlyMember(
            "tm_zone",
            self =>
                self is PythonManagedObjectValue { Attributes: var attributes }
                && attributes.TryGetValue("tm_zone", out var zone)
                    ? zone
                    : PythonNoneValue.Instance
        );
        type.Attributes["tm_gmtoff"] = ReadOnlyMember(
            "tm_gmtoff",
            self =>
                self is PythonManagedObjectValue { Attributes: var attributes }
                && attributes.TryGetValue("tm_gmtoff", out var offset)
                    ? offset
                    : PythonWholeNumberValue.Create(0)
        );
        type.Attributes["n_fields"] = PythonWholeNumberValue.Create(fields.Length + 2);
        type.Attributes["n_sequence_fields"] = PythonWholeNumberValue.Create(fields.Length);
        type.Attributes["n_unnamed_fields"] = PythonWholeNumberValue.Create(0);
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, arguments) => NewStructTime(arguments),
            (_, arguments, _, _) => throw StructTimeKeywords()
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => new PythonTextValue(Represent(self!))
        );
        type.SetDeclaredBases(new PythonTupleValue([tuple]));
        type.SetResolutionOrder(new PythonTupleValue([type, tuple, PythonBuiltinFunctions.Object]));
        return type;
    }

    private static PythonManagedObjectValue NewStructTime(IReadOnlyList<PythonValue> arguments)
    {
        // `__new__` is handed the class first, then the sequence the call named.
        if (arguments.Count != 2)
        {
            throw Fault(
                $"time.struct_time() takes an at least 9-sequence ({arguments.Count - 1}-sequence given)",
                default,
                "TypeError"
            );
        }
        var sequence = arguments[1];
        if (!IsSequence(sequence))
        {
            throw Fault("constructor requires a sequence", default, "TypeError");
        }
        var items = ManagedObjectProtocols.MaterializeValues(sequence, default, null);
        if (items.Count < 9)
        {
            throw Fault(
                $"time.struct_time() takes an at least 9-sequence ({items.Count}-sequence given)",
                default,
                "TypeError"
            );
        }
        // The fields are held as they were given: a structseq asks nothing of them
        // until a function that reads them does.
        return new PythonManagedObjectValue(
            StructTimeType,
            new PythonTupleValue([.. items.Take(9)])
        );
    }

    private static PythonPropertyValue ReadOnlyMember(
        string name,
        Func<PythonValue, PythonValue> get
    ) =>
        new(
            new PythonProtocolFunctionValue(name, (_, arguments) => get(arguments[0])),
            new PythonProtocolFunctionValue(name, (_, _) => throw StructTimeReadOnly(name)),
            new PythonProtocolFunctionValue(name, (_, _) => throw StructTimeReadOnly(name))
        );

    private static PythonRuntimeException StructTimeReadOnly(string name) =>
        Fault($"readonly attribute '{name}'", default, "AttributeError");

    private static PythonRuntimeException StructTimeKeywords() =>
        Fault("structseq() takes at most 2 keyword arguments", default, "TypeError");

    private static string Represent(PythonValue self) =>
        $"time.struct_time(tm_year={Field(self, 0)}, tm_mon={Field(self, 1)}, "
        + $"tm_mday={Field(self, 2)}, tm_hour={Field(self, 3)}, tm_min={Field(self, 4)}, "
        + $"tm_sec={Field(self, 5)}, tm_wday={Field(self, 6)}, tm_yday={Field(self, 7)}, "
        + $"tm_isdst={Field(self, 8)})";

    private static string Field(PythonValue self, int index) =>
        self is PythonManagedObjectValue { Payload: PythonTupleValue tuple }
            ? tuple.Elements[index].ToRepresentationString()
            : "0";

    /// <summary>One broken-down time, as the module's own functions work with it.</summary>
    /// <summary>
    /// A time broken into the fields `struct_time` carries. The `datetime` module reads
    /// and writes the same fields through this, so `strftime` and `strptime` answer the
    /// same way for both modules.
    /// </summary>
    internal sealed record BrokenTime(
        long Year,
        long Month,
        long Day,
        long Hour,
        long Minute,
        long Second,
        long Weekday,
        long YearDay,
        long IsDst,
        string? Zone = null,
        long? Offset = null
    )
    {
        internal PythonManagedObjectValue ToValue()
        {
            var value = new PythonManagedObjectValue(
                StructTimeType,
                new PythonTupleValue([
                    PythonWholeNumberValue.Create(Year),
                    PythonWholeNumberValue.Create(Month),
                    PythonWholeNumberValue.Create(Day),
                    PythonWholeNumberValue.Create(Hour),
                    PythonWholeNumberValue.Create(Minute),
                    PythonWholeNumberValue.Create(Second),
                    PythonWholeNumberValue.Create(Weekday),
                    PythonWholeNumberValue.Create(YearDay),
                    PythonWholeNumberValue.Create(IsDst),
                ])
            );
            // The zone a calendar call worked in is not one of the nine fields, but
            // `tm_zone`/`tm_gmtoff` and `%Z`/`%z` read it.
            if (Zone is not null)
            {
                value.Attributes["tm_zone"] = new PythonTextValue(Zone);
                value.Attributes["tm_gmtoff"] = PythonWholeNumberValue.Create(ZoneOffset());
            }
            return value;
        }

        /// <summary>The offset the zone stands at for this time's daylight-saving flag.</summary>
        internal long ZoneOffset() =>
            Zone == "UTC"
                ? 0
                : Offset ?? (IsDst == 1 ? LocalDaylightOffset() : LocalStandardOffset());

        /// <summary>The name `%Z` writes, and the offset `%z` writes.</summary>
        internal (string Name, string Offset) ZoneText()
        {
            // A datetime carries the offset its own zone reports, which is what a
            // `datetime.timezone` hands out; a `time` module call leaves it unsaid and
            // the host's own zone answers.
            if (Offset is { } offset)
            {
                return (Zone ?? string.Empty, OffsetText(offset));
            }
            if (IsDst == 1)
            {
                return (Zone ?? LocalZoneName(daylight: true), OffsetText(LocalDaylightOffset()));
            }
            if (IsDst == 0)
            {
                return (Zone ?? LocalZoneName(daylight: false), OffsetText(LocalStandardOffset()));
            }
            // A time whose daylight-saving flag nobody knows names no zone at all.
            return (string.Empty, string.Empty);
        }
    }

    internal static string OffsetText(long seconds)
    {
        var sign = seconds < 0 ? '-' : '+';
        var absolute = Math.Abs(seconds);
        return $"{sign}{absolute / 3600:00}{(absolute % 3600) / 60:00}";
    }

    /// <summary>The nine fields of a `struct_time`, or of anything shaped like one.</summary>
    private static BrokenTime ReadTime(PythonValue value, TextSpan span)
    {
        if (value is PythonManagedObjectValue { Payload: BrokenTime broken })
        {
            return broken;
        }
        var zone =
            value is PythonManagedObjectValue { Attributes: var attributes }
            && attributes.TryGetValue("tm_zone", out var zoneName)
            && zoneName is PythonTextValue zoneText
                ? zoneText.Value
                : null;
        var items = ManagedObjectProtocols.MaterializeValues(value, span, null);
        if (items.Count < 9)
        {
            throw Fault(
                $"time.struct_time() takes an at least 9-sequence ({items.Count}-sequence given)",
                span,
                "TypeError"
            );
        }
        return new BrokenTime(
            (long)RequireIndex(items[0], span),
            (long)RequireIndex(items[1], span),
            (long)RequireIndex(items[2], span),
            (long)RequireIndex(items[3], span),
            (long)RequireIndex(items[4], span),
            (long)RequireIndex(items[5], span),
            (long)RequireIndex(items[6], span),
            (long)RequireIndex(items[7], span),
            (long)RequireIndex(items[8], span),
            zone
        );
    }

    // -----------------------------------------------------------------------------
    // The clocks
    // -----------------------------------------------------------------------------

    private static long UnixMicroseconds() =>
        (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    private static long Nanoseconds() =>
        (long)(Stopwatch.GetTimestamp() * 1_000_000_000.0 / Stopwatch.Frequency);

    private static PythonValue Time(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        new PythonFloatingPointValue(UnixMicroseconds() / 1e6);

    private static PythonValue TimeNs(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        new PythonWholeNumberValue(UnixMicroseconds() * 1000);

    private static PythonValue Monotonic(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        new PythonFloatingPointValue(Nanoseconds() / 1e9);

    private static PythonValue MonotonicNs(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        new PythonWholeNumberValue(Nanoseconds());

    private static PythonValue ProcessTime(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        new PythonFloatingPointValue(ProcessCpuNanoseconds() / 1e9);

    private static PythonValue ProcessTimeNs(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        new PythonWholeNumberValue(ProcessCpuNanoseconds());

    private static long ProcessCpuNanoseconds() =>
        (long)(Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds * 1e9);

    private static PythonValue Sleep(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var seconds = RequireNumber(arguments[0], span);
        if (seconds < 0)
        {
            throw Fault("sleep length must be non-negative", span, "ValueError");
        }
        if (seconds > 0)
        {
            var milliseconds = (int)Math.Min(Math.Ceiling(seconds * 1000), int.MaxValue);
            Thread.Sleep(milliseconds);
        }
        return PythonNoneValue.Instance;
    }

    private static PythonValue ClockGettime(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var id = (long)RequireIndex(arguments[0], span);
        return id switch
        {
            0 => new PythonFloatingPointValue(UnixMicroseconds() / 1e6),
            12 or 16 => new PythonFloatingPointValue(ProcessCpuNanoseconds() / 1e9),
            _ => new PythonFloatingPointValue(Nanoseconds() / 1e9),
        };
    }

    private static PythonValue ClockGettimeNs(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var id = (long)RequireIndex(arguments[0], span);
        return id switch
        {
            0 => new PythonWholeNumberValue(UnixMicroseconds() * 1000),
            12 or 16 => new PythonWholeNumberValue(ProcessCpuNanoseconds()),
            _ => new PythonWholeNumberValue(Nanoseconds()),
        };
    }

    private static PythonValue ClockGetres(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        _ = RequireIndex(arguments[0], span);
        return new PythonFloatingPointValue(1.0000000000000002e-06);
    }

    /// <summary>
    /// The resolution the platform's clocks report, as CPython reads it: a microsecond
    /// for `clock_getres`, and the mach tick for `get_clock_info("monotonic")`.
    /// </summary>
    private static double ClockResolution(long id) =>
        id == 6 ? 4.166666666666666e-08 : 1.0000000000000002e-06;

    private static PythonValue ClockSettime(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        _ = RequireIndex(arguments[0], span);
        throw Fault(
            "clock_settime(CLOCK_REALTIME): Operation not permitted",
            span,
            "PermissionError"
        );
    }

    private static PythonValue GetClockInfo(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var name = arguments[0] is PythonTextValue text ? text.Value : string.Empty;
        var info = name switch
        {
            "time" => (
                Implementation: "clock_gettime(CLOCK_REALTIME)",
                Monotonic: false,
                Adjustable: true,
                Resolution: ClockResolution(0)
            ),
            "monotonic" or "perf_counter" => (
                Implementation: "mach_absolute_time()",
                Monotonic: true,
                Adjustable: false,
                Resolution: ClockResolution(6)
            ),
            "process_time" or "thread_time" => (
                Implementation: "getrusage(RUSAGE_SELF)",
                Monotonic: true,
                Adjustable: false,
                Resolution: ClockResolution(0)
            ),
            _ => throw Fault($"unknown clock {name}", span, "ValueError"),
        };
        var value = new PythonManagedObjectValue(ClockInfoType);
        value.Attributes["implementation"] = new PythonTextValue(info.Implementation);
        value.Attributes["monotonic"] = PythonTruthValue.FromBoolean(info.Monotonic);
        value.Attributes["adjustable"] = PythonTruthValue.FromBoolean(info.Adjustable);
        value.Attributes["resolution"] = new PythonFloatingPointValue(info.Resolution);
        return value;
    }

    /// <summary>`types.SimpleNamespace`, whose repr `get_clock_info` reports with.</summary>
    private static PythonManagedTypeValue CreateClockInfoType()
    {
        var type = new PythonManagedTypeValue("SimpleNamespace")
        {
            Module = "types",
            QualName = "SimpleNamespace",
        };
        type.Attributes["__module__"] = new PythonTextValue("types");
        type.Attributes["__qualname__"] = new PythonTextValue("SimpleNamespace");
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) =>
            {
                var instance = (PythonManagedObjectValue)self!;
                var parts = instance.Attributes.Select(pair =>
                    $"{pair.Key}={pair.Value.ToRepresentationString()}"
                );
                return new PythonTextValue($"namespace({string.Join(", ", parts)})");
            }
        );
        return type;
    }

    // -----------------------------------------------------------------------------
    // Breaking a timestamp down
    // -----------------------------------------------------------------------------

    private static PythonManagedObjectValue BreakDown(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span,
        bool utc
    )
    {
        var seconds =
            arguments.Count > 0 && arguments[0] is not PythonNoneValue
                ? RequireNumber(arguments[0], span)
                : UnixMicroseconds() / 1e6;
        var micros = (long)Math.Floor(seconds * 1e6);
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime;
        var zone = TimeZoneInfo.Local;
        var offset = utc ? TimeSpan.Zero : zone.GetUtcOffset(instant);
        var local = instant + offset;
        var isDst =
            utc ? 0
            : zone.IsDaylightSavingTime(instant) ? 1
            : 0;
        return new BrokenTime(
            local.Year,
            local.Month,
            local.Day,
            local.Hour,
            local.Minute,
            local.Second,
            ((int)local.DayOfWeek + 6) % 7,
            local.DayOfYear,
            isDst,
            // The zone a call worked in travels with the time it produced.
            utc ? "UTC" : LocalZoneName(isDst == 1)
        ).ToValue();
    }

    private static PythonValue MakeTime(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var broken = ReadTime(arguments[0], span);
        var naive = new DateTime(
            (int)broken.Year,
            (int)broken.Month,
            (int)broken.Day,
            (int)broken.Hour,
            (int)broken.Minute,
            (int)broken.Second,
            DateTimeKind.Unspecified
        );
        var offset = TimeZoneInfo.Local.GetUtcOffset(naive);
        var utc = naive - offset;
        var unix = (utc - DateTime.UnixEpoch).TotalSeconds;
        return new PythonFloatingPointValue(Math.Round(unix, 6));
    }

    private static PythonValue Asctime(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var broken =
            arguments.Count > 0 && arguments[0] is not PythonNoneValue
                ? ReadTime(arguments[0], span)
                : ReadTime(BreakDown([], span, utc: false), span);
        return new PythonTextValue(AsctimeText(broken));
    }

    private static string AsctimeText(BrokenTime broken) =>
        $"{WeekdayShort[broken.Weekday % 7]} {MonthShort[(broken.Month - 1 + 12) % 12]} "
        + $"{broken.Day, 2} {broken.Hour:00}:{broken.Minute:00}:{broken.Second:00} {broken.Year}";

    private static PythonValue Ctime(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var broken =
            arguments.Count > 0 && arguments[0] is not PythonNoneValue
                ? ReadTime(BreakDown(arguments, span, utc: false), span)
                : ReadTime(BreakDown([], span, utc: false), span);
        return new PythonTextValue(AsctimeText(broken));
    }

    // -----------------------------------------------------------------------------
    // strftime
    // -----------------------------------------------------------------------------

    private static PythonValue Strftime(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var format = RequireText(arguments[0], span);
        var broken =
            arguments.Count > 1 && arguments[1] is not PythonNoneValue
                ? ReadTime(arguments[1], span)
                : ReadTime(BreakDown([], span, utc: false), span);
        return new PythonTextValue(Format(format, broken));
    }

    /// <summary>
    /// The C locale's `strftime`. `%z` and `%Z` name the local zone whatever instant the
    /// broken-down time came from, which is what the C library does.
    /// </summary>
    internal static string Format(string format, BrokenTime t)
    {
        var output = new StringBuilder(format.Length + 16);
        for (var index = 0; index < format.Length; index++)
        {
            var value = format[index];
            if (value != '%' || index + 1 >= format.Length)
            {
                output.Append(value);
                continue;
            }
            index++;
            switch (format[index])
            {
                case 'a':
                    output.Append(WeekdayShort[t.Weekday % 7]);
                    break;
                case 'A':
                    output.Append(WeekdayLong[t.Weekday % 7]);
                    break;
                case 'b' or 'h':
                    output.Append(MonthShort[FloorMod(t.Month - 1, 12)]);
                    break;
                case 'B':
                    output.Append(MonthLong[FloorMod(t.Month - 1, 12)]);
                    break;
                case 'c':
                    output.Append(Format("%a %b %e %H:%M:%S %Y", t));
                    break;
                case 'C':
                    output.Append(Pad(FloorDiv(t.Year, 100), "00"));
                    break;
                case 'd':
                    output.Append(Pad(t.Day, "00"));
                    break;
                case 'D':
                    output.Append(Format("%m/%d/%y", t));
                    break;
                case 'e':
                    output.Append(t.Day < 10 ? $" {t.Day}" : Pad(t.Day, "00"));
                    break;
                case 'f':
                    output.Append("000000");
                    break;
                case 'F':
                    output.Append(Format("%Y-%m-%d", t));
                    break;
                case 'g':
                    output.Append(Pad(FloorMod(IsoWeekYear(t), 100), "00"));
                    break;
                case 'G':
                    output.Append(Pad(IsoWeekYear(t), "0000"));
                    break;
                case 'H':
                    output.Append(Pad(t.Hour, "00"));
                    break;
                case 'I':
                    output.Append(Pad(t.Hour % 12 == 0 ? 12 : t.Hour % 12, "00"));
                    break;
                case 'j':
                    output.Append(Pad(t.YearDay, "000"));
                    break;
                case 'm':
                    output.Append(Pad(t.Month, "00"));
                    break;
                case 'M':
                    output.Append(Pad(t.Minute, "00"));
                    break;
                case 'n':
                    output.Append('\n');
                    break;
                case 'p':
                    output.Append(t.Hour < 12 ? "AM" : "PM");
                    break;
                case 'r':
                    output.Append(Format("%I:%M:%S %p", t));
                    break;
                case 'R':
                    output.Append(Format("%H:%M", t));
                    break;
                case 'S':
                    output.Append(Pad(t.Second, "00"));
                    break;
                case 't':
                    output.Append('\t');
                    break;
                case 'T':
                    output.Append(Format("%H:%M:%S", t));
                    break;
                case 'u':
                    output.Append(t.Weekday + 1);
                    break;
                case 'U':
                    output.Append(Pad(FloorDiv(t.YearDay + 6 - (t.Weekday + 1) % 7, 7), "00"));
                    break;
                case 'V':
                    output.Append(Pad(IsoWeekNumber(t), "00"));
                    break;
                case 'w':
                    output.Append((t.Weekday + 1) % 7);
                    break;
                case 'W':
                    output.Append(Pad(FloorDiv(t.YearDay + 6 - t.Weekday, 7), "00"));
                    break;
                case 'x':
                    output.Append(Format("%m/%d/%y", t));
                    break;
                case 'X':
                    output.Append(Format("%H:%M:%S", t));
                    break;
                case 'y':
                    output.Append(Pad(FloorMod(t.Year, 100), "00"));
                    break;
                case 'Y':
                    output.Append(Pad(t.Year, "0000"));
                    break;
                case 'z':
                    output.Append(t.ZoneText().Offset);
                    break;
                case 'Z':
                    output.Append(t.ZoneText().Name);
                    break;
                case '%':
                    output.Append('%');
                    break;
                default:
                    // The C library drops the `%` of a directive it does not know.
                    output.Append(format[index]);
                    break;
            }
        }
        return output.ToString();
    }

    /// <summary>A field written to a fixed width, with the host's culture kept out of it.</summary>
    internal static string Pad(long value, string format) =>
        value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    internal static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    internal static long FloorMod(long value, long divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static long IsoWeekNumber(BrokenTime t)
    {
        var date = new DateTime((int)t.Year, (int)t.Month, (int)t.Day);
        return System.Globalization.ISOWeek.GetWeekOfYear(date);
    }

    private static long IsoWeekYear(BrokenTime t)
    {
        var date = new DateTime((int)t.Year, (int)t.Month, (int)t.Day);
        return System.Globalization.ISOWeek.GetYear(date);
    }

    // -----------------------------------------------------------------------------
    // strptime
    // -----------------------------------------------------------------------------

    private static PythonValue Strptime(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var text = RequireText(arguments[0], span);
        var format = RequireText(arguments[1], span);
        return Parse(text, format, span).ToValue();
    }

    /// <summary>
    /// `_strptime` under the C locale: the directives' own matching, then the way Python
    /// settles the ambiguities between a weekday, a week of the year and a Julian day.
    /// </summary>
    internal static BrokenTime Parse(string text, string format, TextSpan span)
    {
        var found = new Dictionary<char, string>();
        var position = 0;
        for (var index = 0; index < format.Length; index++)
        {
            var value = format[index];
            if (char.IsWhiteSpace(value))
            {
                while (index + 1 < format.Length && char.IsWhiteSpace(format[index + 1]))
                {
                    index++;
                }
                while (position < text.Length && char.IsWhiteSpace(text[position]))
                {
                    position++;
                }
                continue;
            }
            if (value != '%')
            {
                if (position >= text.Length || text[position] != value)
                {
                    throw NotMatching(text, format, span);
                }
                position++;
                continue;
            }
            index++;
            if (index >= format.Length)
            {
                throw Fault($"stray %% in format '{format}'", span, "ValueError");
            }
            var directive = format[index];
            var consumed = MatchDirective(directive, text, position, format, span, found);
            if (consumed is null)
            {
                throw NotMatching(text, format, span);
            }
            if (consumed.Length > 0)
            {
                found[directive] = consumed;
            }
            position += consumed.Length;
        }
        if (position != text.Length)
        {
            throw Fault($"unconverted data remains: {text[position..]}", span, "ValueError");
        }

        long year = -1,
            isoYear = -1,
            month = 1,
            day = 1,
            hour = 0,
            minute = 0,
            second = 0,
            isDst = -1;
        long? julian = null,
            weekday = null,
            weekOfYear = null,
            weekStart = null;
        if (found.TryGetValue('y', out var shortYear))
        {
            var value = long.Parse(shortYear, System.Globalization.CultureInfo.InvariantCulture);
            year = found.TryGetValue('C', out var century)
                ? value
                    + long.Parse(century, System.Globalization.CultureInfo.InvariantCulture) * 100
                : value + (value <= 68 ? 2000 : 1900);
        }
        if (found.TryGetValue('Y', out var fullYear))
        {
            year = long.Parse(fullYear, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('G', out var isoYearText))
        {
            isoYear = long.Parse(isoYearText, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('m', out var monthText))
        {
            month = long.Parse(monthText, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('B', out var monthName))
        {
            month = IndexOfName(MonthLong, monthName) + 1;
        }
        if (found.TryGetValue('b', out var shortMonth))
        {
            month = IndexOfName(MonthShort, shortMonth) + 1;
        }
        if (found.TryGetValue('d', out var dayText))
        {
            day = long.Parse(dayText, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('H', out var hourText))
        {
            hour = long.Parse(hourText, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('I', out var twelveHour))
        {
            hour = long.Parse(twelveHour, System.Globalization.CultureInfo.InvariantCulture);
            var ampm = found.TryGetValue('p', out var meridian) ? meridian : string.Empty;
            if (ampm.Length == 0 || string.Equals(ampm, "am", StringComparison.OrdinalIgnoreCase))
            {
                if (hour == 12)
                {
                    hour = 0;
                }
            }
            else if (string.Equals(ampm, "pm", StringComparison.OrdinalIgnoreCase) && hour != 12)
            {
                hour += 12;
            }
        }
        if (found.TryGetValue('M', out var minuteText))
        {
            minute = long.Parse(minuteText, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('S', out var secondText))
        {
            second = long.Parse(secondText, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('A', out var weekdayName))
        {
            weekday = IndexOfName(WeekdayLong, weekdayName);
        }
        if (found.TryGetValue('a', out var shortWeekday))
        {
            weekday = IndexOfName(WeekdayShort, shortWeekday);
        }
        if (found.TryGetValue('w', out var weekdayText))
        {
            var value = long.Parse(weekdayText, System.Globalization.CultureInfo.InvariantCulture);
            weekday = value == 0 ? 6 : value - 1;
        }
        if (found.TryGetValue('u', out var isoWeekday))
        {
            weekday = long.Parse(isoWeekday, System.Globalization.CultureInfo.InvariantCulture) - 1;
        }
        if (found.TryGetValue('j', out var julianText))
        {
            julian = long.Parse(julianText, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (found.TryGetValue('U', out var sundayWeek))
        {
            weekOfYear = long.Parse(sundayWeek, System.Globalization.CultureInfo.InvariantCulture);
            weekStart = 6;
        }
        if (found.TryGetValue('W', out var mondayWeek))
        {
            weekOfYear = long.Parse(mondayWeek, System.Globalization.CultureInfo.InvariantCulture);
            weekStart = 0;
        }
        if (found.TryGetValue('Z', out var zone))
        {
            // A zone the local one names settles whether daylight saving was in force.
            // `utc`, `gmt` and the zone's standard name mean standard time; its
            // daylight name means daylight saving, where the zone has one.
            if (
                string.Equals(zone, "utc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(zone, "gmt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    zone,
                    LocalZoneName(daylight: false),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                isDst = 0;
            }
            else if (
                LocalDaylight() == 1
                && string.Equals(
                    zone,
                    LocalZoneName(daylight: true),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                isDst = 1;
            }
        }

        if (year < 0)
        {
            year = month == 2 && day == 29 ? 1904 : 1900;
        }
        if (julian is null && weekday is not null)
        {
            if (weekOfYear is not null)
            {
                julian = JulianFromWeek(year, weekOfYear.Value, weekday.Value, weekStart == 0);
            }
            if (julian is <= 0)
            {
                year--;
                julian += IsLeap(year) ? 366 : 365;
            }
        }
        if (julian is null)
        {
            julian = new DateTime((int)year, (int)month, (int)day).DayOfYear;
        }
        else
        {
            var date = new DateTime((int)year, 1, 1).AddDays(julian.Value - 1);
            year = date.Year;
            month = date.Month;
            day = date.Day;
        }
        weekday ??= ((int)new DateTime((int)year, (int)month, (int)day).DayOfWeek + 6) % 7;
        return new BrokenTime(
            year,
            month,
            day,
            hour,
            minute,
            second,
            weekday.Value,
            julian.Value,
            isDst
        );
    }

    /// <summary>`_calc_julian_from_U_or_W`: the day of the year a week and a weekday name.</summary>
    private static long JulianFromWeek(long year, long week, long dayOfWeek, bool weekStartsMonday)
    {
        var firstWeekday = ((int)new DateTime((int)year, 1, 1).DayOfWeek + 6) % 7;
        if (!weekStartsMonday)
        {
            firstWeekday = (firstWeekday + 1) % 7;
            dayOfWeek = (dayOfWeek + 1) % 7;
        }
        var weekZeroLength = (7 - firstWeekday) % 7;
        return week == 0
            ? 1 + dayOfWeek - firstWeekday
            : 1 + weekZeroLength + 7 * (week - 1) + dayOfWeek;
    }

    internal static bool IsLeap(long year) => DateTime.IsLeapYear((int)year);

    private static int IndexOfName(string[] names, string value)
    {
        for (var index = 0; index < names.Length; index++)
        {
            if (string.Equals(names[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>What one directive matched, or null where the text does not match it.</summary>
    private static string? MatchDirective(
        char directive,
        string text,
        int position,
        string format,
        TextSpan span,
        Dictionary<char, string> found
    )
    {
        switch (directive)
        {
            case 'Y' or 'G':
                return MatchNumber(text, position, 4, 4);
            case 'y' or 'C' or 'U' or 'W' or 'V':
                return MatchNumber(text, position, 1, 2) is { Length: > 0 } two
                    ? two.PadLeft(2, '0')
                    : MatchNumber(text, position, 1, 2);
            case 'm' or 'd' or 'H' or 'I' or 'M' or 'S':
                return MatchNumber(text, position, 1, 2);
            case 'j':
                return MatchNumber(text, position, 3, 3);
            case 'w' or 'u':
                return MatchNumber(text, position, 1, 1);
            case 'a':
                return MatchName(text, position, WeekdayShort);
            case 'A':
                return MatchName(text, position, WeekdayLong);
            case 'b' or 'h':
                return MatchName(text, position, MonthShort);
            case 'B':
                return MatchName(text, position, MonthLong);
            case 'p':
                return MatchName(text, position, ["AM", "PM"]);
            case 'f':
                return MatchNumber(text, position, 1, 6);
            case 'z':
                return MatchZoneOffset(text, position);
            case 'Z':
                return MatchName(
                    text,
                    position,
                    [LocalZoneName(daylight: false), LocalZoneName(daylight: true), "UTC", "GMT"]
                );
            case 'c':
                return MatchExpansion(text, position, "%a %b %e %H:%M:%S %Y", format, span, found);
            case 'x':
                return MatchExpansion(text, position, "%m/%d/%y", format, span, found);
            case 'X':
                return MatchExpansion(text, position, "%H:%M:%S", format, span, found);
            case 'r':
                return MatchExpansion(text, position, "%I:%M:%S %p", format, span, found);
            case '%':
                return position < text.Length && text[position] == '%' ? "%" : null;
            default:
                throw Fault(
                    $"'{directive}' is a bad directive in format '{format}'",
                    span,
                    "ValueError"
                );
        }
    }

    /// <summary>The text one of the format's own expansions matches, with its fields recorded.</summary>
    private static string? MatchExpansion(
        string text,
        int position,
        string expansion,
        string format,
        TextSpan span,
        Dictionary<char, string> found
    )
    {
        var cursor = position;
        for (var index = 0; index < expansion.Length; index++)
        {
            var value = expansion[index];
            if (value == ' ')
            {
                while (cursor < text.Length && text[cursor] == ' ')
                {
                    cursor++;
                }
                continue;
            }
            if (value != '%')
            {
                // The expansion's own punctuation matches the text as written.
                if (cursor >= text.Length || text[cursor] != value)
                {
                    return null;
                }
                cursor++;
                continue;
            }
            index++;
            var directive = expansion[index];
            var matched = MatchDirective(directive, text, cursor, format, span, found);
            if (matched is null)
            {
                return null;
            }
            if (matched.Length > 0)
            {
                found[directive] = matched;
            }
            cursor += matched.Length;
        }
        return text[position..cursor];
    }

    private static string? MatchNumber(string text, int position, int minimum, int maximum)
    {
        var length = 0;
        while (
            length < maximum
            && position + length < text.Length
            && char.IsAsciiDigit(text[position + length])
        )
        {
            length++;
        }
        return length < minimum ? null : text.Substring(position, length);
    }

    private static string? MatchName(string text, int position, string[] names)
    {
        foreach (var name in names)
        {
            if (
                position + name.Length <= text.Length
                && string.Compare(
                    text,
                    position,
                    name,
                    0,
                    name.Length,
                    StringComparison.OrdinalIgnoreCase
                ) == 0
            )
            {
                return text.Substring(position, name.Length);
            }
        }
        return null;
    }

    private static string? MatchZoneOffset(string text, int position)
    {
        if (position < text.Length && text[position] == 'Z')
        {
            return "Z";
        }
        var cursor = position;
        if (cursor < text.Length && text[cursor] is '+' or '-')
        {
            cursor++;
        }
        else
        {
            return null;
        }
        var digits = 0;
        while (cursor < text.Length && (char.IsAsciiDigit(text[cursor]) || text[cursor] == ':'))
        {
            if (char.IsAsciiDigit(text[cursor]))
            {
                digits++;
            }
            cursor++;
        }
        return digits >= 4 ? text[position..cursor] : null;
    }

    private static PythonRuntimeException NotMatching(string text, string format, TextSpan span) =>
        Fault($"time data '{text}' does not match format '{format}'", span, "ValueError");

    // -----------------------------------------------------------------------------
    // The local zone
    // -----------------------------------------------------------------------------

    private static int LocalOffsetNow() => (int)-LocalStandardOffsetLong();

    /// <summary>The zone's offset outside its daylight-saving period.</summary>
    private static long LocalStandardOffsetLong()
    {
        var january = new DateTime(DateTime.UtcNow.Year, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        return (long)TimeZoneInfo.Local.GetUtcOffset(january).TotalSeconds;
    }

    internal static long LocalStandardOffset() => LocalStandardOffsetLong();

    /// <summary>The zone's offset inside its daylight-saving period.</summary>
    internal static long LocalDaylightOffset()
    {
        var zone = TimeZoneInfo.Local;
        if (!zone.SupportsDaylightSavingTime)
        {
            return LocalStandardOffset();
        }
        var july = new DateTime(DateTime.UtcNow.Year, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        return (long)zone.GetUtcOffset(july).TotalSeconds;
    }

    private static int LocalAltZone()
    {
        // The offset daylight saving would put the zone at, for the rules the host knows.
        var zone = TimeZoneInfo.Local;
        if (zone.SupportsDaylightSavingTime)
        {
            var date = new DateTime(DateTime.UtcNow.Year, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            return (int)-zone.GetUtcOffset(date).TotalSeconds;
        }
        return LocalOffsetNow();
    }

    /// <summary>
    /// Whether daylight saving is in force this year, which is what the C library's
    /// `daylight` reports — a zone whose rules once had it does not count.
    /// </summary>
    internal static int LocalDaylight() => LocalDaylightOffset() != LocalStandardOffset() ? 1 : 0;

    internal static string LocalZoneName(bool daylight)
    {
        var zone = TimeZoneInfo.Local;
        return daylight && LocalDaylight() == 1 ? zone.DaylightName : zone.StandardName;
    }

    // -----------------------------------------------------------------------------
    // Values
    // -----------------------------------------------------------------------------

    private static bool IsSequence(PythonValue value) =>
        value
            is PythonTextValue
                or PythonListValue
                or PythonTupleValue
                or PythonByteSequenceValue
                or PythonRangeValue
        || (
            value is PythonManagedObjectValue instance
            && UserObjectProtocols.DefinesSpecialMethod(instance, "__getitem__")
            && UserObjectProtocols.DefinesSpecialMethod(instance, "__len__")
        );

    private static PythonBuiltinFunctionValue Function(
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body,
        string[]? parameters = null
    ) =>
        parameters is null
            ? new PythonBuiltinFunctionValue(name, body)
            : new PythonBuiltinFunctionValue(name, body).WithSignature(
                parameters,
                [.. parameters.Select(_ => (PythonValue?)null)]
            );

    private static string RequireText(PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                $"strptime() argument must be str, not {ManagedObjectProtocols.GetTypeName(value)}",
                span,
                "TypeError"
            );

    private static double RequireNumber(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonTruthValue truth => truth.Value ? 1.0 : 0.0,
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floating => floating.Value,
            _ => throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
                span,
                "TypeError"
            ),
        };

    private static System.Numerics.BigInteger RequireIndex(PythonValue value, TextSpan span) =>
        PythonBuiltinFunctions.RequireIndex(value, span);

    private static PythonRuntimeException Fault(string message, TextSpan span, string type) =>
        ManagedObjectProtocols.Fault("DPY4053", message, span, type);
}
