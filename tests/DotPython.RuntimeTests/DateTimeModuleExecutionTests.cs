using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DateTimeModuleExecutionTests
{
    /// <summary>
    /// The runtime's `timedelta` and `date`: CPython's normalized fields and string
    /// forms, arithmetic and comparison rules, the ordinal and ISO calendars, the shared
    /// `strftime`/`strptime` engine, every accepted `fromisoformat` form and CPython's
    /// own error wording. The operand-type messages name the types unqualified where
    /// CPython writes `datetime.date`, which is the runtime's general naming of managed
    /// types rather than a date-specific rule.
    /// </summary>
    [Fact]
    public void TimedeltaAndDateAnswerAsCpythonDoes()
    {
        var output = Run(
            """"
            from datetime import date, timedelta, MINYEAR, MAXYEAR
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            print("const", MINYEAR, MAXYEAR)
            print("date", repr(date(2026, 10, 12)), str(date(2026, 10, 12)), date(2026, 10, 12).isoformat(), date(2026, 10, 12).ctime())
            print("bounds", repr(date.min), repr(date.max), repr(date.resolution))
            print("delta", repr(timedelta(1, 3600, 500000)), repr(timedelta(microseconds=1)), repr(timedelta(days=-1, seconds=1)), repr(timedelta(0)))
            print("delta-str", str(timedelta(2, 3)), str(timedelta(seconds=-1)), str(-timedelta(days=1)), str(timedelta(seconds=1) * 0.5))
            print("delta-fields", timedelta(1, 3661, 500000).total_seconds(), timedelta.max.total_seconds(), timedelta.min.total_seconds(), timedelta(days=2).days, timedelta(seconds=1).seconds, timedelta(days=1).microseconds)
            print("delta-bounds", repr(timedelta.min), repr(timedelta.max), repr(timedelta.resolution))
            print("delta-arith", timedelta(days=1) + timedelta(seconds=-1), timedelta(seconds=1) * 2.5, timedelta(seconds=3) / 2, timedelta(seconds=5) // 2, timedelta(seconds=5) % timedelta(seconds=2))
            print("delta-arith2", divmod(timedelta(seconds=5), timedelta(seconds=2)), abs(timedelta(days=-1)), -timedelta(days=1), +timedelta(days=1), bool(timedelta(0)), timedelta(days=1) > timedelta(hours=23))
            print("delta-cmp", timedelta(seconds=1) == timedelta(seconds=1), timedelta(days=1) == 86400, timedelta(seconds=1) != "x")
            print("date-ordinal", date.fromordinal(1), date(2026, 10, 12).toordinal(), date.fromordinal(739000).isoformat())
            print("date-calendar", date(2026, 10, 12).weekday(), date(2026, 10, 12).isoweekday(), date(2026, 10, 12).isocalendar())
            print("date-iso", date.fromisoformat('2026-10-12'), date.fromisoformat('20261012'), date.fromisoformat('2026-W42-1'), date.fromisoformat('2026-W42'), date.fromisoformat('2026W421'))
            print("date-fromisocal", date.fromisocalendar(2026, 42, 1), date.fromisocalendar(2024, 1, 1))
            print("date-tuple", date(2026, 10, 12).timetuple(), len(date(1, 1, 1).timetuple()), tuple(date(2026, 10, 12).timetuple()))
            print("date-strftime", date(2026, 10, 12).strftime('%Y-%m-%d %j %a %b %c %x %X %U %W %p %e'))
            print("date-strptime", date.strptime('2026-10-12', '%Y-%m-%d'))
            print("date-replace", date(2026, 10, 12).replace(), date(2026, 10, 12).replace(2020), date(2026, 10, 12).replace(year=2020, day=5))
            print("date-arith", date(2026, 10, 12) + timedelta(days=10), date(2026, 10, 12) - timedelta(days=1), date(2026, 10, 12) - date(2026, 10, 1), timedelta(1) + date(2026, 10, 12))
            print("date-cmp", date(2026, 10, 12) == date(2026, 10, 12), date(2026, 10, 12) < date(2026, 10, 13), date(2026, 1, 1) == None, bool(date(2026, 1, 1)))
            print("date-format", format(date(2026, 10, 12), '%d/%m/%Y'), format(date(2026, 10, 12), ''))
            print("date-ts", date.fromtimestamp(0), date.fromtimestamp(1791801900.0))
            class D(date):
                pass
            print("subclass", repr(D(2026, 1, 1)), str(D(2026, 1, 1)), repr(D(2026, 1, 1) + timedelta(1)), D(2026, 1, 1).isoformat(), D.fromisoformat('2026-10-12').replace(day=2))
            report("date-float", lambda: date(2026.0, 1, 1))
            report("date-big", lambda: date(10000, 1, 1))
            report("date-month", lambda: date(2026, 0, 1))
            report("date-day", lambda: date(2026, 1, 32))
            report("date-feb", lambda: date(2026, 2, 29))
            report("date-arity", lambda: date(2026, 1, 1, 1))
            report("date-missing", lambda: date())
            report("date-kw-missing", lambda: date(year=2026))
            report("date-kw-count", lambda: date(2026, 1, 1, foo=2))
            report("date-kw-unknown", lambda: date(foo=1))
            report("date-dupe", lambda: date(2026, 1, 1, day=1))
            report("td-bad-kw", lambda: timedelta(moon=1))
            report("td-none", lambda: timedelta(days=None))
            report("td-str", lambda: timedelta(days="1"))
            report("td-huge", lambda: timedelta(days=1000000000))
            report("td-overflow", lambda: timedelta.max + timedelta(days=1))
            report("td-nan", lambda: timedelta(seconds=1) * float('nan'))
            report("td-inf", lambda: timedelta(seconds=1) * float('inf'))
            report("td-fdiv-float", lambda: timedelta(seconds=5) // 2.0)
            report("td-div-zero", lambda: timedelta(seconds=1) / 0)
            report("min-minus", lambda: date.min - timedelta(1))
            report("max-plus", lambda: date.max + timedelta(1))
            report("fromordinal-zero", lambda: date.fromordinal(0))
            report("fromordinal-big", lambda: date.fromordinal(3652060))
            report("iso-bad", lambda: date.fromisoformat("hello"))
            report("iso-month", lambda: date.fromisoformat("2026-13-01"))
            report("iso-week54", lambda: date.fromisoformat("2026-W54-1"))
            report("isocal-week", lambda: date.fromisocalendar(2026, 54, 1))
            report("isocal-day", lambda: date.fromisocalendar(2026, 1, 8))
            report("strptime-bad", lambda: date.strptime("hello", "%Y"))
            report("strftime-args", lambda: date(2026, 1, 1).strftime('%Y', 2))
            report("weekday-args", lambda: date(2026, 1, 1).weekday(1))
            report("replace-bad", lambda: date(2026, 1, 1).replace(month=13))
            report("replace-unknown", lambda: date(2026, 1, 1).replace(hour=1))
            report("format-none", lambda: date(2026, 1, 1).__format__(None))
            report("date-plus-int", lambda: date(2026, 10, 12) + 1)
            report("date-rsub", lambda: timedelta(1) - date(2026, 10, 12))
            print("hash", date(2026, 10, 12).__hash__() == date(2026, 10, 12).__hash__(), hash(timedelta(days=1)) == hash(timedelta(days=1)))
            print("dir-date", sorted(n for n in dir(date) if not n.startswith('_')))
            print("dir-delta", sorted(n for n in dir(timedelta) if not n.startswith('_')))
            print("module", sorted(n for n in dir(__import__('datetime')) if not n.startswith('_')))
            """"
        );
        Assert.Equal(
            Lines(
                "const 1 9999",
                "date datetime.date(2026, 10, 12) 2026-10-12 2026-10-12 Mon Oct 12 00:00:00 2026",
                "bounds datetime.date(1, 1, 1) datetime.date(9999, 12, 31) datetime.timedelta(days=1)",
                "delta datetime.timedelta(days=1, seconds=3600, microseconds=500000) datetime.timedelta(microseconds=1) datetime.timedelta(days=-1, seconds=1) datetime.timedelta(0)",
                "delta-str 2 days, 0:00:03 -1 day, 23:59:59 -1 day, 0:00:00 0:00:00.500000",
                "delta-fields 90061.5 86400000000000.0 -86399999913600.0 2 1 0",
                "delta-bounds datetime.timedelta(days=-999999999) datetime.timedelta(days=999999999, seconds=86399, microseconds=999999) datetime.timedelta(microseconds=1)",
                "delta-arith 23:59:59 0:00:02.500000 0:00:01.500000 0:00:02.500000 0:00:01",
                "delta-arith2 (2, datetime.timedelta(seconds=1)) 1 day, 0:00:00 -1 day, 0:00:00 1 day, 0:00:00 False True",
                "delta-cmp True False True",
                "date-ordinal 0001-01-01 739901 2024-04-24",
                "date-calendar 0 1 datetime.IsoCalendarDate(year=2026, week=42, weekday=1)",
                "date-iso 2026-10-12 2026-10-12 2026-10-12 2026-10-12 2026-10-12",
                "date-fromisocal 2026-10-12 2024-01-01",
                "date-tuple time.struct_time(tm_year=2026, tm_mon=10, tm_mday=12, tm_hour=0, tm_min=0, tm_sec=0, tm_wday=0, tm_yday=285, tm_isdst=-1) 9 (2026, 10, 12, 0, 0, 0, 0, 285, -1)",
                "date-strftime 2026-10-12 285 Mon Oct Mon Oct 12 00:00:00 2026 10/12/26 00:00:00 41 41 AM 12",
                "date-strptime 2026-10-12",
                "date-replace 2026-10-12 2020-10-12 2020-10-05",
                "date-arith 2026-10-22 2026-10-11 11 days, 0:00:00 2026-10-13",
                "date-cmp True True False True",
                "date-format 12/10/2026 2026-10-12",
                "date-ts 1970-01-01 2026-10-12",
                "subclass D(2026, 1, 1) 2026-01-01 D(2026, 1, 2) 2026-01-01 2026-10-02",
                "date-float TypeError 'float' object cannot be interpreted as an integer",
                "date-big ValueError year must be in 1..9999, not 10000",
                "date-month ValueError month must be in 1..12, not 0",
                "date-day ValueError day 32 must be in range 1..31 for month 1 in year 2026",
                "date-feb ValueError day 29 must be in range 1..28 for month 2 in year 2026",
                "date-arity TypeError function takes at most 3 arguments (4 given)",
                "date-missing TypeError function missing required argument 'year' (pos 1)",
                "date-kw-missing TypeError function missing required argument 'month' (pos 2)",
                "date-kw-count TypeError function takes at most 3 arguments (4 given)",
                "date-kw-unknown TypeError function missing required argument 'year' (pos 1)",
                "date-dupe TypeError function takes at most 3 arguments (4 given)",
                "td-bad-kw TypeError __new__() got an unexpected keyword argument 'moon'",
                "td-none TypeError unsupported type for timedelta days component: NoneType",
                "td-str TypeError unsupported type for timedelta days component: str",
                "td-huge OverflowError days=1000000000; must have magnitude <= 999999999",
                "td-overflow OverflowError days=1000000000; must have magnitude <= 999999999",
                "td-nan ValueError cannot convert NaN to integer ratio",
                "td-inf OverflowError cannot convert Infinity to integer ratio",
                "td-fdiv-float TypeError unsupported operand type(s) for //: 'datetime.timedelta' and 'float'",
                "td-div-zero ZeroDivisionError division by zero",
                "min-minus OverflowError date value out of range",
                "max-plus OverflowError date value out of range",
                "fromordinal-zero ValueError ordinal must be >= 1",
                "fromordinal-big ValueError year must be in 1..9999, not 10000",
                "iso-bad ValueError Invalid isoformat string: 'hello'",
                "iso-month ValueError month must be in 1..12, not 13",
                "iso-week54 ValueError Invalid isoformat string: '2026-W54-1'",
                "isocal-week ValueError Invalid week: 54",
                "isocal-day ValueError Invalid weekday: 8 (range is [1, 7])",
                "strptime-bad ValueError time data 'hello' does not match format '%Y'",
                "strftime-args TypeError strftime() takes at most 1 argument (2 given)",
                "weekday-args TypeError date.weekday() takes no arguments (1 given)",
                "replace-bad ValueError month must be in 1..12, not 13",
                "replace-unknown TypeError replace() got an unexpected keyword argument 'hour'",
                "format-none TypeError __format__() argument 1 must be str, not None",
                "date-plus-int TypeError unsupported operand type(s) for +: 'date' and 'int'",
                "date-rsub TypeError unsupported operand type(s) for -: 'timedelta' and 'date'",
                "hash True True",
                "dir-date ['ctime', 'day', 'fromisocalendar', 'fromisoformat', 'fromordinal', 'fromtimestamp', 'isocalendar', 'isoformat', 'isoweekday', 'max', 'min', 'month', 'replace', 'resolution', 'strftime', 'strptime', 'timetuple', 'today', 'toordinal', 'weekday', 'year']",
                "dir-delta ['days', 'max', 'microseconds', 'min', 'resolution', 'seconds', 'total_seconds']",
                "module ['MAXYEAR', 'MINYEAR', 'date', 'timedelta']"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
