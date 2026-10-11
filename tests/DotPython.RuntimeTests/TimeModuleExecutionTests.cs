using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TimeModuleExecutionTests
{
    /// <summary>
    /// The runtime's `time` module: the clocks it asks the host for, the `struct_time`
    /// tuple subclass every calendar function works on, and `strftime`/`strptime` over
    /// the C locale's directives.
    /// </summary>
    [Fact]
    public void TheTimeModuleMatchesCpython()
    {
        var output = Run(
            """
            import time


            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)


            def fields(value):
                return tuple(value) if value is not None else None


            st = time.gmtime(0)
            print("gmtime", st, time.gmtime(1_000_000_000), time.gmtime(2_147_483_647), time.gmtime(-1))
            print("fields", st.tm_year, st.tm_mon, st.tm_mday, st.tm_hour, st.tm_min, st.tm_sec, st.tm_wday, st.tm_yday, st.tm_isdst)
            print("seq", len(st), st[0], st[8], st[-1], st[1:3], tuple(st), list(st), st == (1970, 1, 1, 0, 0, 0, 3, 1, 0), isinstance(st, tuple))
            print("meta", st.n_fields, st.n_sequence_fields, st.n_unnamed_fields, type(st).__name__, type(st).__module__, time.struct_time is type(st))
            print("ctor", time.struct_time((2026, 10, 11, 1, 2, 3, 6, 284, 0)), time.struct_time([2026, 10, 11, 1, 2, 3, 6, 284, 0]))
            print("zone-attrs", st.tm_gmtoff, st.tm_zone, time.localtime(0).tm_gmtoff)
            print("asctime", time.asctime(time.gmtime(0)), time.ctime(0) == time.asctime(time.localtime(0)), time.asctime((2026, 10, 11, 1, 2, 3, 6, 284, 0)))
            print("asctime2", time.asctime((1, 1, 1, 1, 1, 1, 0, 1, 0)), time.ctime(86400))
            print("strftime", time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime(0)), time.strftime("a%Ab%Bc%jd%%e", time.gmtime(0)))
            print("strftime2", time.strftime("%y %C %e %H %I %j %m %M %p %S %U %w %W %x %X %z %Z", time.gmtime(0)))
            print("strftime3", time.strftime("%d/%m/%y %H:%M:%S %j %w %W %U %C %e %I %p %y", (2026, 10, 11, 13, 14, 15, 6, 284, 0)))
            print("strftime4", time.strftime("%G %V %u %g", (2026, 10, 11, 0, 0, 0, 6, 284, 0)), time.strftime("", st), time.strftime("%Q", st))
            print("mktime", time.mktime(time.gmtime(0)), time.mktime(time.localtime(0)), type(time.mktime(st)).__name__)
            print("zone", type(time.timezone).__name__, time.timezone <= 0, type(time.tzname).__name__, len(time.tzname), time.tzname[0] == time.tzname[1], type(time.daylight).__name__)
            print("clocks", type(time.time()).__name__, type(time.time_ns()).__name__, type(time.monotonic()).__name__, type(time.perf_counter_ns()).__name__)
            print("now", 1_700_000_000 < time.time() < 2_000_000_000, time.time_ns() % 1000, time.monotonic_ns() > 0, time.perf_counter_ns() > 0)
            print("proc", time.process_time() >= 0, time.thread_time() >= 0, time.process_time_ns() >= 0)
            print("sleep", time.sleep(0), time.sleep(0.0005))
            print("clock", time.clock_gettime(time.CLOCK_MONOTONIC) > 0, time.clock_gettime_ns(time.CLOCK_REALTIME) % 1000, time.CLOCK_REALTIME, time.CLOCK_MONOTONIC)
            print("clockres", time.clock_getres(time.CLOCK_MONOTONIC), type(time.clock_getres(time.CLOCK_REALTIME)).__name__)
            print("tzset", time.tzset())
            info = time.get_clock_info("monotonic")
            print("clockinfo", info.implementation, info.monotonic, info.adjustable, info.resolution > 0, repr(info).startswith("namespace("))
            print("strptime", fields(time.strptime("2026-10-11", "%Y-%m-%d")), fields(time.strptime("2026-10-11 01:02:03", "%Y-%m-%d %H:%M:%S")))
            print("strptime2", fields(time.strptime("Oct 11 2026", "%b %d %Y")), fields(time.strptime("10/11/26", "%x")), fields(time.strptime("01:02:03 PM", "%I:%M:%S %p")))
            print("strptime3", fields(time.strptime("2026 284", "%Y %j")), fields(time.strptime("Sun Oct 11 2026", "%a %b %d %Y")), fields(time.strptime("2026-10-11 +0300", "%Y-%m-%d %z")))
            print("strptime4", fields(time.strptime("2026-10-11 UTC", "%Y-%m-%d %Z")), fields(time.strptime("11 Oct 2026 01:02:03 PM", "%d %b %Y %I:%M:%S %p")))
            print("strptime5", fields(time.strptime("2026-284", "%Y-%j")), fields(time.strptime("26 41", "%y %U")), fields(time.strptime("10/11/26 13:14:15", "%x %X")))
            print("strptime6", fields(time.strptime("  2026-10-11  ", " %Y-%m-%d ")), fields(time.strptime("2026\t10", "%Y\t%m")), fields(time.strptime("Jan 1 2026 00:00:00", "%b %d %Y %H:%M:%S")))
            report("bad-fields", lambda: time.struct_time((1, 2)))
            report("bad-type", lambda: time.struct_time(5))
            report("bad-sec", lambda: time.struct_time(("a", 1, 1, 0, 0, 0, 0, 0, 0)))
            report("bad-str", lambda: time.strptime("nope", "%Y"))
            report("bad-format", lambda: time.strptime("2026", "nope"))
            report("bad-remain", lambda: time.strptime("2026-10-11 extra", "%Y-%m-%d"))
            report("bad-float", lambda: time.gmtime("x"))
            report("bad-sleep", lambda: time.sleep(-1))
            """
        );
        Assert.Equal(
            Lines(
                "gmtime time.struct_time(tm_year=1970, tm_mon=1, tm_mday=1, tm_hour=0, tm_min=0, tm_sec=0, tm_wday=3, tm_yday=1, tm_isdst=0) time.struct_time(tm_year=2001, tm_mon=9, tm_mday=9, tm_hour=1, tm_min=46, tm_sec=40, tm_wday=6, tm_yday=252, tm_isdst=0) time.struct_time(tm_year=2038, tm_mon=1, tm_mday=19, tm_hour=3, tm_min=14, tm_sec=7, tm_wday=1, tm_yday=19, tm_isdst=0) time.struct_time(tm_year=1969, tm_mon=12, tm_mday=31, tm_hour=23, tm_min=59, tm_sec=59, tm_wday=2, tm_yday=365, tm_isdst=0)",
                "fields 1970 1 1 0 0 0 3 1 0",
                "seq 9 1970 0 0 (1, 1) (1970, 1, 1, 0, 0, 0, 3, 1, 0) [1970, 1, 1, 0, 0, 0, 3, 1, 0] True True",
                "meta 11 9 0 struct_time time True",
                "ctor time.struct_time(tm_year=2026, tm_mon=10, tm_mday=11, tm_hour=1, tm_min=2, tm_sec=3, tm_wday=6, tm_yday=284, tm_isdst=0) time.struct_time(tm_year=2026, tm_mon=10, tm_mday=11, tm_hour=1, tm_min=2, tm_sec=3, tm_wday=6, tm_yday=284, tm_isdst=0)",
                "zone-attrs 0 UTC 10800",
                "asctime Thu Jan  1 00:00:00 1970 True Sun Oct 11 01:02:03 2026",
                "asctime2 Mon Jan  1 01:01:01 1 Fri Jan  2 03:00:00 1970",
                "strftime 1970-01-01 00:00:00 aThursdaybJanuaryc001d%e",
                "strftime2 70 19  1 00 12 001 01 00 AM 00 00 4 00 01/01/70 00:00:00 +0300 UTC",
                "strftime3 11/10/26 13:14:15 284 0 40 41 20 11 01 PM 26",
                "strftime4 2026 41 7 26  Q",
                "mktime -10800.0 0.0 float",
                "zone int True tuple 2 True int",
                "clocks float int float int",
                "now True 0 True True",
                "proc True True True",
                "sleep None None",
                "clock True 0 0 6",
                "clockres 1.0000000000000002e-06 float",
                "tzset None",
                "clockinfo mach_absolute_time() True False True True",
                "strptime (2026, 10, 11, 0, 0, 0, 6, 284, -1) (2026, 10, 11, 1, 2, 3, 6, 284, -1)",
                "strptime2 (2026, 10, 11, 0, 0, 0, 6, 284, -1) (2026, 10, 11, 0, 0, 0, 6, 284, -1) (1900, 1, 1, 13, 2, 3, 0, 1, -1)",
                "strptime3 (2026, 10, 11, 0, 0, 0, 6, 284, -1) (2026, 10, 11, 0, 0, 0, 6, 284, -1) (2026, 10, 11, 0, 0, 0, 6, 284, -1)",
                "strptime4 (2026, 10, 11, 0, 0, 0, 6, 284, 0) (2026, 10, 11, 13, 2, 3, 6, 284, -1)",
                "strptime5 (2026, 10, 11, 0, 0, 0, 6, 284, -1) (2026, 1, 1, 0, 0, 0, 3, 1, -1) (2026, 10, 11, 13, 14, 15, 6, 284, -1)",
                "strptime6 (2026, 10, 11, 0, 0, 0, 6, 284, -1) (2026, 10, 1, 0, 0, 0, 3, 274, -1) (2026, 1, 1, 0, 0, 0, 3, 1, -1)",
                "bad-fields TypeError time.struct_time() takes an at least 9-sequence (2-sequence given)",
                "bad-type TypeError constructor requires a sequence",
                "bad-sec time.struct_time(tm_year='a', tm_mon=1, tm_mday=1, tm_hour=0, tm_min=0, tm_sec=0, tm_wday=0, tm_yday=0, tm_isdst=0)",
                "bad-str ValueError time data 'nope' does not match format '%Y'",
                "bad-format ValueError time data '2026' does not match format 'nope'",
                "bad-remain ValueError unconverted data remains:  extra",
                "bad-float TypeError 'str' object cannot be interpreted as an integer",
                "bad-sleep ValueError sleep length must be non-negative"
            ),
            output
        );
    }

    /// <summary>
    /// The clocks answer in the order their names promise, and a timestamp round-trips
    /// through the calendar functions with the zone the call worked in.
    /// </summary>
    [Fact]
    public void TheClocksAreOrdered()
    {
        var output = Run(
            """
            import time
            first = time.time_ns()
            second = time.time_ns()
            print("wall", first <= second, len(str(first)) == 19)
            print("monotonic", time.monotonic() <= time.monotonic(), time.perf_counter_ns() <= time.perf_counter_ns())
            broken = time.localtime(0)
            print("round-trip", time.mktime(broken) == 0, time.localtime(time.mktime(broken)).tm_hour == broken.tm_hour)
            print("zone", time.localtime(0).tm_gmtoff == -time.timezone, time.gmtime(0).tm_gmtoff, time.gmtime(0).tm_zone)
            print("shape", time.localtime(0).tm_isdst in (0, 1), isinstance(time.localtime(0), tuple), type(time.localtime(0)).__name__)
            """
        );
        Assert.Equal(
            Lines(
                "wall True True",
                "monotonic True True",
                "round-trip True True",
                "zone True 0 UTC",
                "shape True True struct_time"
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
