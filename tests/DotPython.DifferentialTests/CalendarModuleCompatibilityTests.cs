using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CalendarModuleCompatibilityTests
{
    /// <summary>
    /// The `calendar` module: the month and weekday enums, the localized name sequences,
    /// the leap-year, weekday and month-range helpers, `timegm`, the `Calendar` iterators,
    /// the `TextCalendar`/`HTMLCalendar` formatters and their printers, the module-level
    /// bound methods, the exceptions, and CPython's own argument-parser wording, all
    /// matching the pinned interpreter.
    /// </summary>
    [Fact]
    public Task TheCalendarModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            import calendar as c
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            print("consts", c.MONDAY, c.SUNDAY, c.JANUARY, c.DECEMBER, repr(c.MONDAY), repr(c.JANUARY), str(c.MONDAY), int(c.SUNDAY), c.MONDAY + 1, "%d" % c.MONDAY, f"{c.MONDAY}")
            print("enums", c.Day.MONDAY is c.MONDAY, c.Month.JANUARY is c.JANUARY, c.Month(1), c.Day(6), c.Month(1).name, c.Day(6).value, type(c.weekday(2026, 10, 12)).__name__)
            print("mdays", c.mdays, len(c.mdays))
            print("leap", c.isleap(2024), c.isleap(2026), c.isleap(2000), c.isleap(1900), c.leapdays(2000, 2020), c.leapdays(2019, 2020))
            print("weekday", c.weekday(2026, 10, 12), c.weekday(2000, 1, 1), c.weekday(-400, 1, 1), c.weekday(10000, 1, 1))
            print("monthrange", c.monthrange(2026, 10), c.monthrange(2024, 2), c.monthrange(2026, 2))
            print("names", c.month_name[0], c.month_name[1], c.month_name[12], len(c.month_name), c.month_abbr[1], c.day_name[0], c.day_name[6], len(c.day_name), c.day_abbr[2], type(c.month_name).__name__, type(c.day_abbr).__name__)
            print("slices", c.month_name[1:4], c.day_name[::-1], c.month_abbr[::2], c.month_name.format, c.day_abbr.format)
            print("firstweekday", c.firstweekday(), c.weekheader(2), c.weekheader(3), c.weekheader(9))
            c.setfirstweekday(c.SUNDAY)
            print("sun", c.firstweekday(), c.weekheader(3), c.monthcalendar(2026, 10)[0])
            c.setfirstweekday(0)
            print("monthcalendar", c.monthcalendar(2026, 10), c.monthcalendar(2024, 2))
            print("timegm", c.timegm((1970, 1, 1, 0, 0, 0)), c.timegm((2026, 10, 12, 13, 45, 6)), c.timegm((2026, 1, 1, 0, 0, 0, 99)))
            print("month", c.month(2026, 10))
            print("month2", c.month(2026, 2, 2, 1))
            print("calendar", c.calendar(2026, 2, 1, 3, 2))
            print("prmonth", repr(c.prmonth(2026, 10)))
            print("prcal", repr(c.prcal(2026, 2, 1, 3, 2)))
            print("week", repr(c.week([(1, 0), (2, 1), (0, 6)], 2)), repr(c.prweek([(1, 0), (2, 1)], 3)))
            print("formatstring", repr(c.formatstring(["a", "bbb", "cc"], 5, 2)), repr(c.format(["a", "bbb"], 5, 2)))
            tc = c.TextCalendar(c.SUNDAY)
            print("text", tc.firstweekday, repr(tc.formatday(12, 3, 3)), repr(tc.formatday(0, 0, 4)), repr(tc.formatday(7, 6, 2)))
            print("text-week", repr(tc.formatweek([(1, 0), (2, 1), (0, 3)], 2)), repr(tc.formatweek([(5, 6)], 4)))
            print("text-weekday", repr(tc.formatweekday(0, 3)), repr(tc.formatweekday(0, 9)), repr(tc.formatweekday(-1, 2)))
            print("text-header", repr(tc.formatweekheader(2)), repr(tc.formatweekheader(9)))
            print("text-monthname", repr(tc.formatmonthname(2026, 10, 20)), repr(tc.formatmonthname(2026, 10, 20, False)), repr(tc.formatmonthname(2026, 2, 25)))
            print("text-month", tc.formatmonth(2024, 2))
            print("text-year", tc.formatyear(2026, 2, 1, 3, 2))
            cal = c.Calendar()
            print("iterweekdays", list(cal.iterweekdays()), list(c.Calendar(6).iterweekdays()))
            print("itermonthdates", list(cal.itermonthdates(2026, 10)))
            print("itermonthdays", list(cal.itermonthdays(2026, 10)), list(cal.itermonthdays(2024, 2))[:10])
            print("itermonthdays2", list(cal.itermonthdays2(2026, 10)), list(c.Calendar(6).itermonthdays2(2026, 2))[:8])
            print("itermonthdays3", list(cal.itermonthdays3(2026, 10)), list(cal.itermonthdays3(10000, 1))[:3])
            print("itermonthdays4", list(cal.itermonthdays4(2026, 10)), list(c.Calendar(3).itermonthdays4(2026, 2))[:5])
            print("monthcal", cal.monthdatescalendar(2026, 10), cal.monthdays2calendar(2026, 2), cal.monthdayscalendar(2024, 2))
            print("yearcal", len(cal.yeardatescalendar(2026)), len(cal.yeardayscalendar(2026, 4)), cal.yeardays2calendar(2026, 4)[-1][-1][-1])
            h = c.HTMLCalendar(c.SUNDAY)
            print("html-day", repr(h.formatday(12, 3)), repr(h.formatday(0, 3)))
            print("html-week", repr(h.formatweek([(1, 0), (2, 1), (0, 3)])), repr(h.formatweekheader()))
            print("html-weekday", repr(h.formatweekday(0)), repr(h.formatweekday(6)))
            print("html-monthname", repr(h.formatmonthname(2026, 10)), repr(h.formatmonthname(2026, 10, False)))
            print("html-month", h.formatmonth(2026, 10))
            print("html-month2", h.formatmonth(2026, 10, withyear=False))
            print("html-year", h.formatyear(2026, 2))
            page = h.formatyearpage(2026, 2, "calendar.css", "utf-8")
            print("html-page", type(page).__name__, len(page), page[:60], page[-60:])
            print("html-page2", h.formatyearpage(2026, encoding="ascii")[:80])
            print("html-page3", h.formatyearpage(2026, 3, None)[60:200])
            print("html-css", h.cssclasses, h.cssclass_noday, h.cssclasses_weekday_head, h.cssclass_month_head, h.cssclass_month, h.cssclass_year, h.cssclass_year_head)
            print("locale", c.LocaleTextCalendar().formatmonth(2026, 10), repr(c.LocaleHTMLCalendar().formatmonthname(2026, 10)))
            print("exc", str(c.IllegalMonthError(13)), c.IllegalMonthError(13).month, str(c.IllegalWeekdayError(7)), c.IllegalWeekdayError(7).weekday, isinstance(c.IllegalMonthError(13), IndexError), issubclass(c.IllegalWeekdayError, c.error))
            report("bad-month", lambda: c.monthrange(2026, 13))
            report("bad-month0", lambda: c.monthcalendar(2026, 0))
            report("bad-weekday", lambda: c.setfirstweekday(7))
            report("bad-week", lambda: c.weekday(2026, 13, 1))
            report("bad-day", lambda: c.weekday(2026, 2, 30))
            report("bad-timegm", lambda: c.timegm((2026, 1, 1)))
            report("bad-timegm-year", lambda: c.timegm((0, 1, 1, 0, 0, 0)))
            report("arity-month", lambda: c.month(2026, 2, 2, 1, 6))
            report("arity-day", lambda: c.TextCalendar().formatday(1))
            report("arity-week", lambda: c.week([(1, 0)], 2, 9))
            report("arity-first", lambda: c.setfirstweekday(1, 2))
            report("arity-iter", lambda: list(c.Calendar().itermonthdays()))
            report("missing-kw", lambda: c.setfirstweekday(weekday=3))
            report("bad-encoding", lambda: c.HTMLCalendar().formatyearpage(2026, encoding="bogus"))
            report("bad-pair", lambda: c.TextCalendar().formatweek([(1, 2, 3)], 2))
            report("bad-pair2", lambda: c.TextCalendar().formatweek([5], 2))
            report("bad-pair3", lambda: c.HTMLCalendar().formatweek([(1,)]))
            report("bad-day-type", lambda: c.TextCalendar().formatday("1", 2, 3))
            print("surface", c.error is ValueError, len(c.__all__), c.__all__[:3], c.__all__[-3:], hasattr(c, "mktime"), hasattr(c, "March"), c.Month.__module__, c.Day.__qualname__)
            """"
        );
}
