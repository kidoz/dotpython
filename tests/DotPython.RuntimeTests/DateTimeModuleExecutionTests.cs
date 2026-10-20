using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DateTimeModuleExecutionTests
{
    /// <summary>
    /// The runtime's `datetime` module: `timedelta` and `date` over the shared calendar
    /// engine, `time`, `datetime`, the `tzinfo` protocol and the fixed-offset `timezone`,
    /// with CPython's normalized fields and string forms, arithmetic and comparison
    /// rules, the ordinal and ISO calendars, `strftime`/`strptime`, every accepted
    /// `fromisoformat` form, `timestamp`/`astimezone` over the host's zone, and CPython's
    /// own error wording. The runtime names managed types unqualified where CPython
    /// writes `datetime.date`, does not emit the 3.14 deprecation warnings for the UTC
    /// constructors, and exposes no `datetime_CAPI`; those local answers are pinned at
    /// the end.
    /// </summary>
    [Fact]
    public void TheDateTimeModuleAnswersAsCpythonDoes()
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
            from datetime import date, time, datetime, timedelta, timezone, tzinfo
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            print("iso-time", time(13, 45).isoformat(), time(13, 45, 6).isoformat(), time(13, 45, 6, 789).isoformat(), time(1).isoformat('hours'), time(1, 2, 3, 400000).isoformat('milliseconds'))
            print("iso-dt", datetime(2026, 10, 12, 1, 2, 3, 4, timezone.utc).isoformat(), datetime(2026, 10, 12, 1, 2, 3, 4).isoformat(' '), datetime(2026, 10, 12).isoformat('X', 'minutes'))
            print("iso-ts", time(1, 2, 3, 4).isoformat('microseconds'), datetime(2026, 10, 12, 1, 2, 3, 4, timezone(timedelta(hours=-5, minutes=-30))).isoformat())
            report("iso-bad-ts", lambda: time(1).isoformat('days'))
            report("iso-bad-sep", lambda: datetime(2026, 1, 1).isoformat('XY'))
            report("iso-arity", lambda: time(1).isoformat('auto', 'x'))
            print("time-fromiso", time.fromisoformat('13:45'), time.fromisoformat('13:45:06.000789+05:30'), time.fromisoformat('T13:45'), time.fromisoformat('1345'), time.fromisoformat('13:45:06Z'), time.fromisoformat('13:45-05:00'))
            print("dt-fromiso", datetime.fromisoformat('2026-10-12'), datetime.fromisoformat('2026-10-12T13:45'), datetime.fromisoformat('20261012T134506'), datetime.fromisoformat('2026-10-12 13:45:06.000789+05:30'), datetime.fromisoformat('2026-10-12T13:45:06Z'))
            report("dt-fromiso-bad", lambda: datetime.fromisoformat('2026-10-12T25:00'))
            report("dt-fromiso-bad2", lambda: datetime.fromisoformat('hello'))
            print("combine", datetime.combine(date(2026, 10, 12), time(1, 2, 3)), datetime.combine(date(2026, 10, 12), time(1, 2, 3, tzinfo=timezone.utc)), datetime.combine(date(2026, 10, 12), time(1), timezone.utc))
            report("combine-bad", lambda: datetime.combine(5, time(1)))
            report("combine-bad2", lambda: datetime.combine(date(2026, 1, 1), 5))
            print("replace", time(1, 2, 3).replace(minute=5), time(1, 2, 3).replace(4, 5, 6), time(1, tzinfo=timezone.utc).replace(tzinfo=None), datetime(2026, 10, 12, 1, 2, 3).replace(2020, 2, 3, 4, 5, 6, 7), datetime(2026, 10, 12, 1).replace(hour=2, fold=1))
            report("time-replace-bad", lambda: time(1).replace(hour=24))
            print("time-cmp", time(1, 2) < time(1, 3), time(1, 2) == time(1, 2), time(1, 2) != time(1, 2), time(1, 2) <= time(1, 2), time(1) < time(2) < time(3))
            print("time-arith", hasattr(time(1), '__add__'), time(1).min, time(1).max, time(1).resolution)
            print("dt-arith", datetime(2026, 10, 12, 1, 2, 3, 4) + timedelta(microseconds=1), datetime(2026, 10, 12, 1) - timedelta(days=1, microseconds=1), datetime(2026, 10, 12, 1, 2, 3, tzinfo=timezone.utc) - timedelta(hours=1))
            print("dt-sub", datetime(2026, 10, 12, 2) - datetime(2026, 10, 12, 1), datetime(2026, 10, 12, 2, tzinfo=timezone(timedelta(hours=1))) - datetime(2026, 10, 12, 1, tzinfo=timezone.utc))
            print("dt-attrs", datetime(2026, 10, 12, 13, 45, 6, 789, timezone.utc, fold=1).hour, datetime(2026, 10, 12, fold=1).fold, datetime(2026, 10, 12).year, datetime(2026, 10, 12, tzinfo=timezone.utc).utcoffset())
            print("dt-misc", datetime(2026, 10, 12, 13, 45).date(), datetime(2026, 10, 12, 13, 45, 6, 7).time(), datetime(2026, 10, 12, 13, 45, tzinfo=timezone.utc).timetz(), datetime(2026, 10, 12, 13, 45, 6, 7).ctime())
            print("dt-tt", datetime(2026, 10, 12, 13, 45, 6, tzinfo=timezone.utc).utctimetuple(), datetime(2026, 10, 12, 13, 45).timetuple())
            print("dt-strftime", datetime(2026, 10, 12, 13, 45, 6, tzinfo=timezone(timedelta(hours=2))).strftime('%Y-%m-%d %H:%M:%S %Z %z %j %U %W %c'))
            print("dt-strptime", datetime.strptime('2026-10-12 13:45:06', '%Y-%m-%d %H:%M:%S'), time.strptime('13:45:06', '%H:%M:%S'))
            print("dt-format", format(datetime(2026, 10, 12, 1, 2, 3), '%d/%m/%Y %H:%M'), format(datetime(2026, 10, 12, 1, 2, 3), ''))
            print("dt-bounds", repr(datetime.min), repr(datetime.max), repr(datetime.resolution), repr(time.min), repr(time.max), repr(time.resolution))
            print("tz", timezone.utc.utcoffset(None), timezone.utc.dst(None), timezone.utc.tzname(None), repr(timezone.utc), str(timezone(timedelta(hours=2))), timezone(timedelta(hours=2)).tzname(None))
            tz2 = timezone(timedelta(hours=2))
            print("tz-fromutc", tz2.fromutc(datetime(2026, 10, 12, 1, tzinfo=tz2)))
            print("tz-bounds", repr(timezone.min), repr(timezone.max))
            print("cls", issubclass(datetime, date), isinstance(datetime(2026,1,1), date), issubclass(timezone, tzinfo), isinstance(timezone.utc, tzinfo))
            print("dt-compare-date", datetime(2026,1,1) == date(2026,1,1), date(2026,1,1) == datetime(2026,1,1))
            print("dt-hash", hash(datetime(2026, 10, 12, 1)) == hash(datetime(2026, 10, 12, 1)), hash(time(1, 2)) == hash(time(1, 2)))
            print("dt-ts", datetime(1970, 1, 1, tzinfo=timezone.utc).timestamp(), datetime(1970, 1, 1).timestamp())
            print("dt-astz", datetime(1970, 1, 1, tzinfo=timezone.utc).astimezone(timezone(timedelta(hours=1))))
            print("dt-utcnow-ts", round(datetime(1970, 1, 1, tzinfo=timezone.utc).timestamp()))
            print("dt-repr-sub", repr(datetime(2026, 10, 12, 1, 2, 3, 4, timezone.utc, fold=1)))

            print("cmp2", time(1, tzinfo=timezone.utc) == time(1, tzinfo=timezone(timedelta(hours=1))), time(1, tzinfo=timezone.utc) < time(1, tzinfo=timezone(timedelta(hours=1))))
            report("local-date-plus-int", lambda: date(2026, 10, 12) + 1)
            report("local-dt-lt-date", lambda: datetime(2026, 10, 12) < date(2026, 10, 12))
            report("local-td-float-ops", lambda: timedelta(seconds=1) * timedelta(seconds=1))
            report("local-tz-isoformat", lambda: timezone.utc.isoformat())
            print("local-utcnow", datetime.utcnow().tzinfo is None, datetime.utcfromtimestamp(0), datetime.fromtimestamp(0))
            print("local-module", sorted(n for n in dir(__import__('datetime')) if not n.startswith('_')))

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
                "iso-time 13:45:00 13:45:06 13:45:06.000789 01 01:02:03.400",
                "iso-dt 2026-10-12T01:02:03.000004+00:00 2026-10-12 01:02:03.000004 2026-10-12X00:00",
                "iso-ts 01:02:03.000004 2026-10-12T01:02:03.000004-05:30",
                "iso-bad-ts ValueError Unknown timespec value",
                "iso-bad-sep TypeError isoformat() argument 1 must be a unicode character, not a string of length 2",
                "iso-arity TypeError isoformat() takes at most 1 argument (2 given)",
                "time-fromiso 13:45:00 13:45:06.000789+05:30 13:45:00 13:45:00 13:45:06+00:00 13:45:00-05:00",
                "dt-fromiso 2026-10-12 00:00:00 2026-10-12 13:45:00 2026-10-12 13:45:06 2026-10-12 13:45:06.000789+05:30 2026-10-12 13:45:06+00:00",
                "dt-fromiso-bad ValueError hour must be in 0..23, not 25",
                "dt-fromiso-bad2 ValueError Invalid isoformat string: 'hello'",
                "combine 2026-10-12 01:02:03 2026-10-12 01:02:03+00:00 2026-10-12 01:00:00+00:00",
                "combine-bad TypeError combine() argument 1 must be datetime.date, not int",
                "combine-bad2 TypeError combine() argument 2 must be datetime.time, not int",
                "replace 01:05:03 04:05:06 01:00:00 2020-02-03 04:05:06.000007 2026-10-12 02:00:00",
                "time-replace-bad ValueError hour must be in 0..23, not 24",
                "time-cmp True True False True True",
                "time-arith False 00:00:00 23:59:59.999999 0:00:00.000001",
                "dt-arith 2026-10-12 01:02:03.000005 2026-10-11 00:59:59.999999 2026-10-12 00:02:03+00:00",
                "dt-sub 1:00:00 0:00:00",
                "dt-attrs 13 1 2026 0:00:00",
                "dt-misc 2026-10-12 13:45:06.000007 13:45:00+00:00 Mon Oct 12 13:45:06 2026",
                "dt-tt time.struct_time(tm_year=2026, tm_mon=10, tm_mday=12, tm_hour=13, tm_min=45, tm_sec=6, tm_wday=0, tm_yday=285, tm_isdst=0) time.struct_time(tm_year=2026, tm_mon=10, tm_mday=12, tm_hour=13, tm_min=45, tm_sec=0, tm_wday=0, tm_yday=285, tm_isdst=-1)",
                "dt-strftime 2026-10-12 13:45:06 UTC+02:00 +0200 285 41 41 Mon Oct 12 13:45:06 2026",
                "dt-strptime 2026-10-12 13:45:06 13:45:06",
                "dt-format 12/10/2026 01:02 2026-10-12 01:02:03",
                "dt-bounds datetime.datetime(1, 1, 1, 0, 0) datetime.datetime(9999, 12, 31, 23, 59, 59, 999999) datetime.timedelta(microseconds=1) datetime.time(0, 0) datetime.time(23, 59, 59, 999999) datetime.timedelta(microseconds=1)",
                "tz 0:00:00 None UTC datetime.timezone.utc UTC+02:00 UTC+02:00",
                "tz-fromutc 2026-10-12 03:00:00+02:00",
                "tz-bounds datetime.timezone(datetime.timedelta(days=-1, seconds=60)) datetime.timezone(datetime.timedelta(seconds=86340))",
                "cls True True True True",
                "dt-compare-date False False",
                "dt-hash True True",
                "dt-ts 0.0 -10800.0",
                "dt-astz 1970-01-01 01:00:00+01:00",
                "dt-utcnow-ts 0",
                "dt-repr-sub datetime.datetime(2026, 10, 12, 1, 2, 3, 4, fold=1, tzinfo=datetime.timezone.utc)",
                "cmp2 False False",
                "local-date-plus-int TypeError unsupported operand type(s) for +: 'date' and 'int'",
                "local-dt-lt-date TypeError '<' not supported between instances of 'datetime' and 'date'",
                "local-td-float-ops TypeError unsupported operand type(s) for *: 'timedelta' and 'timedelta'",
                "local-tz-isoformat AttributeError 'timezone' object has no attribute 'isoformat'",
                "local-utcnow True 1970-01-01 00:00:00 1970-01-01 03:00:00",
                "local-module ['MAXYEAR', 'MINYEAR', 'UTC', 'date', 'datetime', 'time', 'timedelta', 'timezone', 'tzinfo']"
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
