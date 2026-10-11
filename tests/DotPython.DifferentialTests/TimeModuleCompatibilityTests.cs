using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TimeModuleCompatibilityTests
{
    [Fact]
    public Task TheTimeModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
