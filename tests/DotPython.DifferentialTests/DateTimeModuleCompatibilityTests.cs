using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DateTimeModuleCompatibilityTests
{
    /// <summary>
    /// The `datetime` module over the calendar engine the `time` module carries:
    /// `timedelta` and `date`, `time`, `datetime`, the `tzinfo` protocol and the
    /// fixed-offset `timezone`. The cases cover CPython's normalized fields and string
    /// forms, arithmetic and comparison rules, the ordinal and ISO calendars,
    /// `strftime`/`strptime`, every accepted `fromisoformat` form, `timestamp` and
    /// `astimezone` over the host's zone, and CPython's own refusal wording for every
    /// constructor and field error.
    /// </summary>
    [Fact]
    public Task TheDateTimeModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            """"
        );
}
