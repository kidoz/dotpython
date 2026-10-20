using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CalendarModuleExecutionTests
{
    /// <summary>
    /// The runtime's `calendar`: the leap-year and month-range helpers over the datetime
    /// engine, the `calendar.MONDAY`-style `Day`/`Month` enums, the 13-entry month-name
    /// sequences with their list semantics, the `Calendar` iterators, the
    /// `TextCalendar`/`HTMLCalendar` formatting (including the 3.14 signatures that
    /// dropped `HTMLCalendar`'s width arguments), `timegm`, the exceptions with their
    /// CPython wording and MRO, and the CPython argument-parser messages for bad calls.
    /// The locale subclasses format with the C locale and keep `locale` as given — the
    /// runtime has no locale module — and the iterators are list iterators rather than
    /// generators; those local answers are pinned at the end.
    /// </summary>
    [Fact]
    public void TheCalendarModuleAnswersAsCpythonDoes()
    {
        var output = Run(
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
            print("local-locale", c.LocaleTextCalendar().locale, c.LocaleHTMLCalendar().locale)
            print("local-iter", type(c.Calendar().itermonthdates(2026, 10)).__name__, type(c.Calendar().yeardatescalendar(2026)).__name__)
            """"
        );
        Assert.Equal(
            Lines(
                "consts 0 6 1 12 calendar.MONDAY calendar.JANUARY 0 6 1 0 0",
                "enums True True 1 6 JANUARY 6 Day",
                "mdays [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31] 13",
                "leap True False True False 5 0",
                "weekday 0 5 5 5",
                "monthrange (calendar.THURSDAY, 31) (calendar.THURSDAY, 29) (calendar.SUNDAY, 28)",
                "names  January December 13 Jan Monday Sunday 7 Wed _localized_month _localized_day",
                "slices ['January', 'February', 'March'] ['Sunday', 'Saturday', 'Friday', 'Thursday', 'Wednesday', 'Tuesday', 'Monday'] ['', 'Feb', 'Apr', 'Jun', 'Aug', 'Oct', 'Dec'] %B %a",
                "firstweekday 0 Mo Tu We Th Fr Sa Su Mon Tue Wed Thu Fri Sat Sun   Monday   Tuesday  Wednesday  Thursday   Friday   Saturday   Sunday ",
                "sun 6 Sun Mon Tue Wed Thu Fri Sat [0, 0, 0, 0, 1, 2, 3]",
                "monthcalendar [[0, 0, 0, 1, 2, 3, 4], [5, 6, 7, 8, 9, 10, 11], [12, 13, 14, 15, 16, 17, 18], [19, 20, 21, 22, 23, 24, 25], [26, 27, 28, 29, 30, 31, 0]] [[0, 0, 0, 1, 2, 3, 4], [5, 6, 7, 8, 9, 10, 11], [12, 13, 14, 15, 16, 17, 18], [19, 20, 21, 22, 23, 24, 25], [26, 27, 28, 29, 0, 0, 0]]",
                "timegm 0 1791812706 1767225600",
                "month     October 2026",
                "Mo Tu We Th Fr Sa Su",
                "          1  2  3  4",
                " 5  6  7  8  9 10 11",
                "12 13 14 15 16 17 18",
                "19 20 21 22 23 24 25",
                "26 27 28 29 30 31",
                "",
                "month2    February 2026",
                "Mo Tu We Th Fr Sa Su",
                "                   1",
                " 2  3  4  5  6  7  8",
                " 9 10 11 12 13 14 15",
                "16 17 18 19 20 21 22",
                "23 24 25 26 27 28",
                "",
                "calendar                     2026",
                "",
                "      January                February",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "          1  2  3  4                      1",
                " 5  6  7  8  9 10 11    2  3  4  5  6  7  8",
                "12 13 14 15 16 17 18    9 10 11 12 13 14 15",
                "19 20 21 22 23 24 25   16 17 18 19 20 21 22",
                "26 27 28 29 30 31      23 24 25 26 27 28",
                "",
                "       March                  April",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "                   1          1  2  3  4  5",
                " 2  3  4  5  6  7  8    6  7  8  9 10 11 12",
                " 9 10 11 12 13 14 15   13 14 15 16 17 18 19",
                "16 17 18 19 20 21 22   20 21 22 23 24 25 26",
                "23 24 25 26 27 28 29   27 28 29 30",
                "30 31",
                "",
                "        May                    June",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "             1  2  3    1  2  3  4  5  6  7",
                " 4  5  6  7  8  9 10    8  9 10 11 12 13 14",
                "11 12 13 14 15 16 17   15 16 17 18 19 20 21",
                "18 19 20 21 22 23 24   22 23 24 25 26 27 28",
                "25 26 27 28 29 30 31   29 30",
                "",
                "        July                  August",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "       1  2  3  4  5                   1  2",
                " 6  7  8  9 10 11 12    3  4  5  6  7  8  9",
                "13 14 15 16 17 18 19   10 11 12 13 14 15 16",
                "20 21 22 23 24 25 26   17 18 19 20 21 22 23",
                "27 28 29 30 31         24 25 26 27 28 29 30",
                "                       31",
                "",
                "     September               October",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "    1  2  3  4  5  6             1  2  3  4",
                " 7  8  9 10 11 12 13    5  6  7  8  9 10 11",
                "14 15 16 17 18 19 20   12 13 14 15 16 17 18",
                "21 22 23 24 25 26 27   19 20 21 22 23 24 25",
                "28 29 30               26 27 28 29 30 31",
                "",
                "      November               December",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "                   1       1  2  3  4  5  6",
                " 2  3  4  5  6  7  8    7  8  9 10 11 12 13",
                " 9 10 11 12 13 14 15   14 15 16 17 18 19 20",
                "16 17 18 19 20 21 22   21 22 23 24 25 26 27",
                "23 24 25 26 27 28 29   28 29 30 31",
                "30",
                "",
                "    October 2026",
                "Mo Tu We Th Fr Sa Su",
                "          1  2  3  4",
                " 5  6  7  8  9 10 11",
                "12 13 14 15 16 17 18",
                "19 20 21 22 23 24 25",
                "26 27 28 29 30 31",
                "prmonth None",
                "                    2026",
                "",
                "      January                February",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "          1  2  3  4                      1",
                " 5  6  7  8  9 10 11    2  3  4  5  6  7  8",
                "12 13 14 15 16 17 18    9 10 11 12 13 14 15",
                "19 20 21 22 23 24 25   16 17 18 19 20 21 22",
                "26 27 28 29 30 31      23 24 25 26 27 28",
                "",
                "       March                  April",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "                   1          1  2  3  4  5",
                " 2  3  4  5  6  7  8    6  7  8  9 10 11 12",
                " 9 10 11 12 13 14 15   13 14 15 16 17 18 19",
                "16 17 18 19 20 21 22   20 21 22 23 24 25 26",
                "23 24 25 26 27 28 29   27 28 29 30",
                "30 31",
                "",
                "        May                    June",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "             1  2  3    1  2  3  4  5  6  7",
                " 4  5  6  7  8  9 10    8  9 10 11 12 13 14",
                "11 12 13 14 15 16 17   15 16 17 18 19 20 21",
                "18 19 20 21 22 23 24   22 23 24 25 26 27 28",
                "25 26 27 28 29 30 31   29 30",
                "",
                "        July                  August",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "       1  2  3  4  5                   1  2",
                " 6  7  8  9 10 11 12    3  4  5  6  7  8  9",
                "13 14 15 16 17 18 19   10 11 12 13 14 15 16",
                "20 21 22 23 24 25 26   17 18 19 20 21 22 23",
                "27 28 29 30 31         24 25 26 27 28 29 30",
                "                       31",
                "",
                "     September               October",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "    1  2  3  4  5  6             1  2  3  4",
                " 7  8  9 10 11 12 13    5  6  7  8  9 10 11",
                "14 15 16 17 18 19 20   12 13 14 15 16 17 18",
                "21 22 23 24 25 26 27   19 20 21 22 23 24 25",
                "28 29 30               26 27 28 29 30 31",
                "",
                "      November               December",
                "Mo Tu We Th Fr Sa Su   Mo Tu We Th Fr Sa Su",
                "                   1       1  2  3  4  5  6",
                " 2  3  4  5  6  7  8    7  8  9 10 11 12 13",
                " 9 10 11 12 13 14 15   14 15 16 17 18 19 20",
                "16 17 18 19 20 21 22   21 22 23 24 25 26 27",
                "23 24 25 26 27 28 29   28 29 30 31",
                "30",
                "prcal None",
                "  1   2week ' 1  2   ' None",
                "  a     bbb ",
                "formatstring '  a     bbb     cc ' None",
                "text 6 ' 12' '    ' ' 7'",
                "text-week ' 1  2   ' '  5 '",
                "text-weekday 'Mon' '  Monday ' 'Su'",
                "text-header 'Su Mo Tu We Th Fr Sa' '  Sunday    Monday   Tuesday  Wednesday  Thursday   Friday   Saturday'",
                "text-monthname '    October 2026    ' '      October       ' '      February 2026      '",
                "text-month    February 2024",
                "Su Mo Tu We Th Fr Sa",
                "             1  2  3",
                " 4  5  6  7  8  9 10",
                "11 12 13 14 15 16 17",
                "18 19 20 21 22 23 24",
                "25 26 27 28 29",
                "",
                "text-year                     2026",
                "",
                "      January                February",
                "Su Mo Tu We Th Fr Sa   Su Mo Tu We Th Fr Sa",
                "             1  2  3    1  2  3  4  5  6  7",
                " 4  5  6  7  8  9 10    8  9 10 11 12 13 14",
                "11 12 13 14 15 16 17   15 16 17 18 19 20 21",
                "18 19 20 21 22 23 24   22 23 24 25 26 27 28",
                "25 26 27 28 29 30 31",
                "",
                "       March                  April",
                "Su Mo Tu We Th Fr Sa   Su Mo Tu We Th Fr Sa",
                " 1  2  3  4  5  6  7             1  2  3  4",
                " 8  9 10 11 12 13 14    5  6  7  8  9 10 11",
                "15 16 17 18 19 20 21   12 13 14 15 16 17 18",
                "22 23 24 25 26 27 28   19 20 21 22 23 24 25",
                "29 30 31               26 27 28 29 30",
                "",
                "        May                    June",
                "Su Mo Tu We Th Fr Sa   Su Mo Tu We Th Fr Sa",
                "                1  2       1  2  3  4  5  6",
                " 3  4  5  6  7  8  9    7  8  9 10 11 12 13",
                "10 11 12 13 14 15 16   14 15 16 17 18 19 20",
                "17 18 19 20 21 22 23   21 22 23 24 25 26 27",
                "24 25 26 27 28 29 30   28 29 30",
                "31",
                "",
                "        July                  August",
                "Su Mo Tu We Th Fr Sa   Su Mo Tu We Th Fr Sa",
                "          1  2  3  4                      1",
                " 5  6  7  8  9 10 11    2  3  4  5  6  7  8",
                "12 13 14 15 16 17 18    9 10 11 12 13 14 15",
                "19 20 21 22 23 24 25   16 17 18 19 20 21 22",
                "26 27 28 29 30 31      23 24 25 26 27 28 29",
                "                       30 31",
                "",
                "     September               October",
                "Su Mo Tu We Th Fr Sa   Su Mo Tu We Th Fr Sa",
                "       1  2  3  4  5                1  2  3",
                " 6  7  8  9 10 11 12    4  5  6  7  8  9 10",
                "13 14 15 16 17 18 19   11 12 13 14 15 16 17",
                "20 21 22 23 24 25 26   18 19 20 21 22 23 24",
                "27 28 29 30            25 26 27 28 29 30 31",
                "",
                "      November               December",
                "Su Mo Tu We Th Fr Sa   Su Mo Tu We Th Fr Sa",
                " 1  2  3  4  5  6  7          1  2  3  4  5",
                " 8  9 10 11 12 13 14    6  7  8  9 10 11 12",
                "15 16 17 18 19 20 21   13 14 15 16 17 18 19",
                "22 23 24 25 26 27 28   20 21 22 23 24 25 26",
                "29 30                  27 28 29 30 31",
                "",
                "iterweekdays [0, 1, 2, 3, 4, 5, 6] [6, 0, 1, 2, 3, 4, 5]",
                "itermonthdates [datetime.date(2026, 9, 28), datetime.date(2026, 9, 29), datetime.date(2026, 9, 30), datetime.date(2026, 10, 1), datetime.date(2026, 10, 2), datetime.date(2026, 10, 3), datetime.date(2026, 10, 4), datetime.date(2026, 10, 5), datetime.date(2026, 10, 6), datetime.date(2026, 10, 7), datetime.date(2026, 10, 8), datetime.date(2026, 10, 9), datetime.date(2026, 10, 10), datetime.date(2026, 10, 11), datetime.date(2026, 10, 12), datetime.date(2026, 10, 13), datetime.date(2026, 10, 14), datetime.date(2026, 10, 15), datetime.date(2026, 10, 16), datetime.date(2026, 10, 17), datetime.date(2026, 10, 18), datetime.date(2026, 10, 19), datetime.date(2026, 10, 20), datetime.date(2026, 10, 21), datetime.date(2026, 10, 22), datetime.date(2026, 10, 23), datetime.date(2026, 10, 24), datetime.date(2026, 10, 25), datetime.date(2026, 10, 26), datetime.date(2026, 10, 27), datetime.date(2026, 10, 28), datetime.date(2026, 10, 29), datetime.date(2026, 10, 30), datetime.date(2026, 10, 31), datetime.date(2026, 11, 1)]",
                "itermonthdays [0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 0] [0, 0, 0, 1, 2, 3, 4, 5, 6, 7]",
                "itermonthdays2 [(0, 0), (0, 1), (0, 2), (1, 3), (2, 4), (3, 5), (4, 6), (5, 0), (6, 1), (7, 2), (8, 3), (9, 4), (10, 5), (11, 6), (12, 0), (13, 1), (14, 2), (15, 3), (16, 4), (17, 5), (18, 6), (19, 0), (20, 1), (21, 2), (22, 3), (23, 4), (24, 5), (25, 6), (26, 0), (27, 1), (28, 2), (29, 3), (30, 4), (31, 5), (0, 6)] [(1, 6), (2, 0), (3, 1), (4, 2), (5, 3), (6, 4), (7, 5), (8, 6)]",
                "itermonthdays3 [(2026, 9, 28), (2026, 9, 29), (2026, 9, 30), (2026, 10, 1), (2026, 10, 2), (2026, 10, 3), (2026, 10, 4), (2026, 10, 5), (2026, 10, 6), (2026, 10, 7), (2026, 10, 8), (2026, 10, 9), (2026, 10, 10), (2026, 10, 11), (2026, 10, 12), (2026, 10, 13), (2026, 10, 14), (2026, 10, 15), (2026, 10, 16), (2026, 10, 17), (2026, 10, 18), (2026, 10, 19), (2026, 10, 20), (2026, 10, 21), (2026, 10, 22), (2026, 10, 23), (2026, 10, 24), (2026, 10, 25), (2026, 10, 26), (2026, 10, 27), (2026, 10, 28), (2026, 10, 29), (2026, 10, 30), (2026, 10, 31), (2026, 11, 1)] [(9999, 12, 27), (9999, 12, 28), (9999, 12, 29)]",
                "itermonthdays4 [(2026, 9, 28, 0), (2026, 9, 29, 1), (2026, 9, 30, 2), (2026, 10, 1, 3), (2026, 10, 2, 4), (2026, 10, 3, 5), (2026, 10, 4, 6), (2026, 10, 5, 0), (2026, 10, 6, 1), (2026, 10, 7, 2), (2026, 10, 8, 3), (2026, 10, 9, 4), (2026, 10, 10, 5), (2026, 10, 11, 6), (2026, 10, 12, 0), (2026, 10, 13, 1), (2026, 10, 14, 2), (2026, 10, 15, 3), (2026, 10, 16, 4), (2026, 10, 17, 5), (2026, 10, 18, 6), (2026, 10, 19, 0), (2026, 10, 20, 1), (2026, 10, 21, 2), (2026, 10, 22, 3), (2026, 10, 23, 4), (2026, 10, 24, 5), (2026, 10, 25, 6), (2026, 10, 26, 0), (2026, 10, 27, 1), (2026, 10, 28, 2), (2026, 10, 29, 3), (2026, 10, 30, 4), (2026, 10, 31, 5), (2026, 11, 1, 6)] [(2026, 1, 29, 3), (2026, 1, 30, 4), (2026, 1, 31, 5), (2026, 2, 1, 6), (2026, 2, 2, 0)]",
                "monthcal [[datetime.date(2026, 9, 28), datetime.date(2026, 9, 29), datetime.date(2026, 9, 30), datetime.date(2026, 10, 1), datetime.date(2026, 10, 2), datetime.date(2026, 10, 3), datetime.date(2026, 10, 4)], [datetime.date(2026, 10, 5), datetime.date(2026, 10, 6), datetime.date(2026, 10, 7), datetime.date(2026, 10, 8), datetime.date(2026, 10, 9), datetime.date(2026, 10, 10), datetime.date(2026, 10, 11)], [datetime.date(2026, 10, 12), datetime.date(2026, 10, 13), datetime.date(2026, 10, 14), datetime.date(2026, 10, 15), datetime.date(2026, 10, 16), datetime.date(2026, 10, 17), datetime.date(2026, 10, 18)], [datetime.date(2026, 10, 19), datetime.date(2026, 10, 20), datetime.date(2026, 10, 21), datetime.date(2026, 10, 22), datetime.date(2026, 10, 23), datetime.date(2026, 10, 24), datetime.date(2026, 10, 25)], [datetime.date(2026, 10, 26), datetime.date(2026, 10, 27), datetime.date(2026, 10, 28), datetime.date(2026, 10, 29), datetime.date(2026, 10, 30), datetime.date(2026, 10, 31), datetime.date(2026, 11, 1)]] [[(0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5), (1, 6)], [(2, 0), (3, 1), (4, 2), (5, 3), (6, 4), (7, 5), (8, 6)], [(9, 0), (10, 1), (11, 2), (12, 3), (13, 4), (14, 5), (15, 6)], [(16, 0), (17, 1), (18, 2), (19, 3), (20, 4), (21, 5), (22, 6)], [(23, 0), (24, 1), (25, 2), (26, 3), (27, 4), (28, 5), (0, 6)]] [[0, 0, 0, 1, 2, 3, 4], [5, 6, 7, 8, 9, 10, 11], [12, 13, 14, 15, 16, 17, 18], [19, 20, 21, 22, 23, 24, 25], [26, 27, 28, 29, 0, 0, 0]]",
                "yearcal 4 3 [(28, 0), (29, 1), (30, 2), (31, 3), (0, 4), (0, 5), (0, 6)]",
                "html-day '<td class=\"thu\">12</td>' '<td class=\"noday\">&nbsp;</td>'",
                "html-week '<tr><td class=\"mon\">1</td><td class=\"tue\">2</td><td class=\"noday\">&nbsp;</td></tr>' '<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>'",
                "html-weekday '<th class=\"mon\">Mon</th>' '<th class=\"sun\">Sun</th>'",
                "html-monthname '<tr><th colspan=\"7\" class=\"month\">October 2026</th></tr>' '<tr><th colspan=\"7\" class=\"month\">October</th></tr>'",
                "html-month <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">October 2026</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"thu\">1</td><td class=\"fri\">2</td><td class=\"sat\">3</td></tr>",
                "<tr><td class=\"sun\">4</td><td class=\"mon\">5</td><td class=\"tue\">6</td><td class=\"wed\">7</td><td class=\"thu\">8</td><td class=\"fri\">9</td><td class=\"sat\">10</td></tr>",
                "<tr><td class=\"sun\">11</td><td class=\"mon\">12</td><td class=\"tue\">13</td><td class=\"wed\">14</td><td class=\"thu\">15</td><td class=\"fri\">16</td><td class=\"sat\">17</td></tr>",
                "<tr><td class=\"sun\">18</td><td class=\"mon\">19</td><td class=\"tue\">20</td><td class=\"wed\">21</td><td class=\"thu\">22</td><td class=\"fri\">23</td><td class=\"sat\">24</td></tr>",
                "<tr><td class=\"sun\">25</td><td class=\"mon\">26</td><td class=\"tue\">27</td><td class=\"wed\">28</td><td class=\"thu\">29</td><td class=\"fri\">30</td><td class=\"sat\">31</td></tr>",
                "</table>",
                "",
                "html-month2 <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">October</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"thu\">1</td><td class=\"fri\">2</td><td class=\"sat\">3</td></tr>",
                "<tr><td class=\"sun\">4</td><td class=\"mon\">5</td><td class=\"tue\">6</td><td class=\"wed\">7</td><td class=\"thu\">8</td><td class=\"fri\">9</td><td class=\"sat\">10</td></tr>",
                "<tr><td class=\"sun\">11</td><td class=\"mon\">12</td><td class=\"tue\">13</td><td class=\"wed\">14</td><td class=\"thu\">15</td><td class=\"fri\">16</td><td class=\"sat\">17</td></tr>",
                "<tr><td class=\"sun\">18</td><td class=\"mon\">19</td><td class=\"tue\">20</td><td class=\"wed\">21</td><td class=\"thu\">22</td><td class=\"fri\">23</td><td class=\"sat\">24</td></tr>",
                "<tr><td class=\"sun\">25</td><td class=\"mon\">26</td><td class=\"tue\">27</td><td class=\"wed\">28</td><td class=\"thu\">29</td><td class=\"fri\">30</td><td class=\"sat\">31</td></tr>",
                "</table>",
                "",
                "html-year <table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"year\">",
                "<tr><th colspan=\"2\" class=\"year\">2026</th></tr><tr><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">January</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"thu\">1</td><td class=\"fri\">2</td><td class=\"sat\">3</td></tr>",
                "<tr><td class=\"sun\">4</td><td class=\"mon\">5</td><td class=\"tue\">6</td><td class=\"wed\">7</td><td class=\"thu\">8</td><td class=\"fri\">9</td><td class=\"sat\">10</td></tr>",
                "<tr><td class=\"sun\">11</td><td class=\"mon\">12</td><td class=\"tue\">13</td><td class=\"wed\">14</td><td class=\"thu\">15</td><td class=\"fri\">16</td><td class=\"sat\">17</td></tr>",
                "<tr><td class=\"sun\">18</td><td class=\"mon\">19</td><td class=\"tue\">20</td><td class=\"wed\">21</td><td class=\"thu\">22</td><td class=\"fri\">23</td><td class=\"sat\">24</td></tr>",
                "<tr><td class=\"sun\">25</td><td class=\"mon\">26</td><td class=\"tue\">27</td><td class=\"wed\">28</td><td class=\"thu\">29</td><td class=\"fri\">30</td><td class=\"sat\">31</td></tr>",
                "</table>",
                "</td><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">February</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"sun\">1</td><td class=\"mon\">2</td><td class=\"tue\">3</td><td class=\"wed\">4</td><td class=\"thu\">5</td><td class=\"fri\">6</td><td class=\"sat\">7</td></tr>",
                "<tr><td class=\"sun\">8</td><td class=\"mon\">9</td><td class=\"tue\">10</td><td class=\"wed\">11</td><td class=\"thu\">12</td><td class=\"fri\">13</td><td class=\"sat\">14</td></tr>",
                "<tr><td class=\"sun\">15</td><td class=\"mon\">16</td><td class=\"tue\">17</td><td class=\"wed\">18</td><td class=\"thu\">19</td><td class=\"fri\">20</td><td class=\"sat\">21</td></tr>",
                "<tr><td class=\"sun\">22</td><td class=\"mon\">23</td><td class=\"tue\">24</td><td class=\"wed\">25</td><td class=\"thu\">26</td><td class=\"fri\">27</td><td class=\"sat\">28</td></tr>",
                "</table>",
                "</td></tr><tr><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">March</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"sun\">1</td><td class=\"mon\">2</td><td class=\"tue\">3</td><td class=\"wed\">4</td><td class=\"thu\">5</td><td class=\"fri\">6</td><td class=\"sat\">7</td></tr>",
                "<tr><td class=\"sun\">8</td><td class=\"mon\">9</td><td class=\"tue\">10</td><td class=\"wed\">11</td><td class=\"thu\">12</td><td class=\"fri\">13</td><td class=\"sat\">14</td></tr>",
                "<tr><td class=\"sun\">15</td><td class=\"mon\">16</td><td class=\"tue\">17</td><td class=\"wed\">18</td><td class=\"thu\">19</td><td class=\"fri\">20</td><td class=\"sat\">21</td></tr>",
                "<tr><td class=\"sun\">22</td><td class=\"mon\">23</td><td class=\"tue\">24</td><td class=\"wed\">25</td><td class=\"thu\">26</td><td class=\"fri\">27</td><td class=\"sat\">28</td></tr>",
                "<tr><td class=\"sun\">29</td><td class=\"mon\">30</td><td class=\"tue\">31</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">April</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"wed\">1</td><td class=\"thu\">2</td><td class=\"fri\">3</td><td class=\"sat\">4</td></tr>",
                "<tr><td class=\"sun\">5</td><td class=\"mon\">6</td><td class=\"tue\">7</td><td class=\"wed\">8</td><td class=\"thu\">9</td><td class=\"fri\">10</td><td class=\"sat\">11</td></tr>",
                "<tr><td class=\"sun\">12</td><td class=\"mon\">13</td><td class=\"tue\">14</td><td class=\"wed\">15</td><td class=\"thu\">16</td><td class=\"fri\">17</td><td class=\"sat\">18</td></tr>",
                "<tr><td class=\"sun\">19</td><td class=\"mon\">20</td><td class=\"tue\">21</td><td class=\"wed\">22</td><td class=\"thu\">23</td><td class=\"fri\">24</td><td class=\"sat\">25</td></tr>",
                "<tr><td class=\"sun\">26</td><td class=\"mon\">27</td><td class=\"tue\">28</td><td class=\"wed\">29</td><td class=\"thu\">30</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td></tr><tr><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">May</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"fri\">1</td><td class=\"sat\">2</td></tr>",
                "<tr><td class=\"sun\">3</td><td class=\"mon\">4</td><td class=\"tue\">5</td><td class=\"wed\">6</td><td class=\"thu\">7</td><td class=\"fri\">8</td><td class=\"sat\">9</td></tr>",
                "<tr><td class=\"sun\">10</td><td class=\"mon\">11</td><td class=\"tue\">12</td><td class=\"wed\">13</td><td class=\"thu\">14</td><td class=\"fri\">15</td><td class=\"sat\">16</td></tr>",
                "<tr><td class=\"sun\">17</td><td class=\"mon\">18</td><td class=\"tue\">19</td><td class=\"wed\">20</td><td class=\"thu\">21</td><td class=\"fri\">22</td><td class=\"sat\">23</td></tr>",
                "<tr><td class=\"sun\">24</td><td class=\"mon\">25</td><td class=\"tue\">26</td><td class=\"wed\">27</td><td class=\"thu\">28</td><td class=\"fri\">29</td><td class=\"sat\">30</td></tr>",
                "<tr><td class=\"sun\">31</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">June</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"mon\">1</td><td class=\"tue\">2</td><td class=\"wed\">3</td><td class=\"thu\">4</td><td class=\"fri\">5</td><td class=\"sat\">6</td></tr>",
                "<tr><td class=\"sun\">7</td><td class=\"mon\">8</td><td class=\"tue\">9</td><td class=\"wed\">10</td><td class=\"thu\">11</td><td class=\"fri\">12</td><td class=\"sat\">13</td></tr>",
                "<tr><td class=\"sun\">14</td><td class=\"mon\">15</td><td class=\"tue\">16</td><td class=\"wed\">17</td><td class=\"thu\">18</td><td class=\"fri\">19</td><td class=\"sat\">20</td></tr>",
                "<tr><td class=\"sun\">21</td><td class=\"mon\">22</td><td class=\"tue\">23</td><td class=\"wed\">24</td><td class=\"thu\">25</td><td class=\"fri\">26</td><td class=\"sat\">27</td></tr>",
                "<tr><td class=\"sun\">28</td><td class=\"mon\">29</td><td class=\"tue\">30</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td></tr><tr><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">July</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"wed\">1</td><td class=\"thu\">2</td><td class=\"fri\">3</td><td class=\"sat\">4</td></tr>",
                "<tr><td class=\"sun\">5</td><td class=\"mon\">6</td><td class=\"tue\">7</td><td class=\"wed\">8</td><td class=\"thu\">9</td><td class=\"fri\">10</td><td class=\"sat\">11</td></tr>",
                "<tr><td class=\"sun\">12</td><td class=\"mon\">13</td><td class=\"tue\">14</td><td class=\"wed\">15</td><td class=\"thu\">16</td><td class=\"fri\">17</td><td class=\"sat\">18</td></tr>",
                "<tr><td class=\"sun\">19</td><td class=\"mon\">20</td><td class=\"tue\">21</td><td class=\"wed\">22</td><td class=\"thu\">23</td><td class=\"fri\">24</td><td class=\"sat\">25</td></tr>",
                "<tr><td class=\"sun\">26</td><td class=\"mon\">27</td><td class=\"tue\">28</td><td class=\"wed\">29</td><td class=\"thu\">30</td><td class=\"fri\">31</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">August</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"sat\">1</td></tr>",
                "<tr><td class=\"sun\">2</td><td class=\"mon\">3</td><td class=\"tue\">4</td><td class=\"wed\">5</td><td class=\"thu\">6</td><td class=\"fri\">7</td><td class=\"sat\">8</td></tr>",
                "<tr><td class=\"sun\">9</td><td class=\"mon\">10</td><td class=\"tue\">11</td><td class=\"wed\">12</td><td class=\"thu\">13</td><td class=\"fri\">14</td><td class=\"sat\">15</td></tr>",
                "<tr><td class=\"sun\">16</td><td class=\"mon\">17</td><td class=\"tue\">18</td><td class=\"wed\">19</td><td class=\"thu\">20</td><td class=\"fri\">21</td><td class=\"sat\">22</td></tr>",
                "<tr><td class=\"sun\">23</td><td class=\"mon\">24</td><td class=\"tue\">25</td><td class=\"wed\">26</td><td class=\"thu\">27</td><td class=\"fri\">28</td><td class=\"sat\">29</td></tr>",
                "<tr><td class=\"sun\">30</td><td class=\"mon\">31</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td></tr><tr><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">September</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"tue\">1</td><td class=\"wed\">2</td><td class=\"thu\">3</td><td class=\"fri\">4</td><td class=\"sat\">5</td></tr>",
                "<tr><td class=\"sun\">6</td><td class=\"mon\">7</td><td class=\"tue\">8</td><td class=\"wed\">9</td><td class=\"thu\">10</td><td class=\"fri\">11</td><td class=\"sat\">12</td></tr>",
                "<tr><td class=\"sun\">13</td><td class=\"mon\">14</td><td class=\"tue\">15</td><td class=\"wed\">16</td><td class=\"thu\">17</td><td class=\"fri\">18</td><td class=\"sat\">19</td></tr>",
                "<tr><td class=\"sun\">20</td><td class=\"mon\">21</td><td class=\"tue\">22</td><td class=\"wed\">23</td><td class=\"thu\">24</td><td class=\"fri\">25</td><td class=\"sat\">26</td></tr>",
                "<tr><td class=\"sun\">27</td><td class=\"mon\">28</td><td class=\"tue\">29</td><td class=\"wed\">30</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">October</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"thu\">1</td><td class=\"fri\">2</td><td class=\"sat\">3</td></tr>",
                "<tr><td class=\"sun\">4</td><td class=\"mon\">5</td><td class=\"tue\">6</td><td class=\"wed\">7</td><td class=\"thu\">8</td><td class=\"fri\">9</td><td class=\"sat\">10</td></tr>",
                "<tr><td class=\"sun\">11</td><td class=\"mon\">12</td><td class=\"tue\">13</td><td class=\"wed\">14</td><td class=\"thu\">15</td><td class=\"fri\">16</td><td class=\"sat\">17</td></tr>",
                "<tr><td class=\"sun\">18</td><td class=\"mon\">19</td><td class=\"tue\">20</td><td class=\"wed\">21</td><td class=\"thu\">22</td><td class=\"fri\">23</td><td class=\"sat\">24</td></tr>",
                "<tr><td class=\"sun\">25</td><td class=\"mon\">26</td><td class=\"tue\">27</td><td class=\"wed\">28</td><td class=\"thu\">29</td><td class=\"fri\">30</td><td class=\"sat\">31</td></tr>",
                "</table>",
                "</td></tr><tr><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">November</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"sun\">1</td><td class=\"mon\">2</td><td class=\"tue\">3</td><td class=\"wed\">4</td><td class=\"thu\">5</td><td class=\"fri\">6</td><td class=\"sat\">7</td></tr>",
                "<tr><td class=\"sun\">8</td><td class=\"mon\">9</td><td class=\"tue\">10</td><td class=\"wed\">11</td><td class=\"thu\">12</td><td class=\"fri\">13</td><td class=\"sat\">14</td></tr>",
                "<tr><td class=\"sun\">15</td><td class=\"mon\">16</td><td class=\"tue\">17</td><td class=\"wed\">18</td><td class=\"thu\">19</td><td class=\"fri\">20</td><td class=\"sat\">21</td></tr>",
                "<tr><td class=\"sun\">22</td><td class=\"mon\">23</td><td class=\"tue\">24</td><td class=\"wed\">25</td><td class=\"thu\">26</td><td class=\"fri\">27</td><td class=\"sat\">28</td></tr>",
                "<tr><td class=\"sun\">29</td><td class=\"mon\">30</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td><td><table border=\"0\" cellpadding=\"0\" cellspacing=\"0\" class=\"month\">",
                "<tr><th colspan=\"7\" class=\"month\">December</th></tr>",
                "<tr><th class=\"sun\">Sun</th><th class=\"mon\">Mon</th><th class=\"tue\">Tue</th><th class=\"wed\">Wed</th><th class=\"thu\">Thu</th><th class=\"fri\">Fri</th><th class=\"sat\">Sat</th></tr>",
                "<tr><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td><td class=\"tue\">1</td><td class=\"wed\">2</td><td class=\"thu\">3</td><td class=\"fri\">4</td><td class=\"sat\">5</td></tr>",
                "<tr><td class=\"sun\">6</td><td class=\"mon\">7</td><td class=\"tue\">8</td><td class=\"wed\">9</td><td class=\"thu\">10</td><td class=\"fri\">11</td><td class=\"sat\">12</td></tr>",
                "<tr><td class=\"sun\">13</td><td class=\"mon\">14</td><td class=\"tue\">15</td><td class=\"wed\">16</td><td class=\"thu\">17</td><td class=\"fri\">18</td><td class=\"sat\">19</td></tr>",
                "<tr><td class=\"sun\">20</td><td class=\"mon\">21</td><td class=\"tue\">22</td><td class=\"wed\">23</td><td class=\"thu\">24</td><td class=\"fri\">25</td><td class=\"sat\">26</td></tr>",
                "<tr><td class=\"sun\">27</td><td class=\"mon\">28</td><td class=\"tue\">29</td><td class=\"wed\">30</td><td class=\"thu\">31</td><td class=\"noday\">&nbsp;</td><td class=\"noday\">&nbsp;</td></tr>",
                "</table>",
                "</td></tr></table>",
                "html-page bytes 14973 b'<?xml version=\"1.0\" encoding=\"utf-8\"?>\\n<!DOCTYPE html PUBLIC' b'&nbsp;</td></tr>\\n</table>\\n</td></tr></table></body>\\n</html>\\n'",
                "html-page2 b'<?xml version=\"1.0\" encoding=\"ascii\"?>\\n<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML '",
                "html-page3 b' \"-//W3C//DTD XHTML 1.0 Strict//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">\\n<html>\\n<head>\\n<meta http-equiv=\"Content-Type\" conte'",
                "html-css ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] noday ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] month month year year",
                "locale     October 2026",
                "Mo Tu We Th Fr Sa Su",
                "          1  2  3  4",
                " 5  6  7  8  9 10 11",
                "12 13 14 15 16 17 18",
                "19 20 21 22 23 24 25",
                "26 27 28 29 30 31",
                " '<tr><th colspan=\"7\" class=\"month\">October 2026</th></tr>'",
                "exc bad month number 13; must be 1-12 13 bad weekday number 7; must be 0 (Monday) to 6 (Sunday) 7 True True",
                "bad-month IllegalMonthError bad month number 13; must be 1-12",
                "bad-month0 IllegalMonthError bad month number 0; must be 1-12",
                "bad-weekday IllegalWeekdayError bad weekday number 7; must be 0 (Monday) to 6 (Sunday)",
                "bad-week ValueError month must be in 1..12, not 13",
                "bad-day ValueError day 30 must be in range 1..28 for month 2 in year 2026",
                "bad-timegm ValueError not enough values to unpack (expected 6, got 3)",
                "bad-timegm-year ValueError year must be in 1..9999, not 0",
                "arity-month TypeError TextCalendar.formatmonth() takes from 3 to 5 positional arguments but 6 were given",
                "arity-day TypeError TextCalendar.formatday() missing 2 required positional arguments: 'weekday' and 'width'",
                "arity-week TypeError TextCalendar.formatweek() takes 3 positional arguments but 4 were given",
                "arity-first TypeError setfirstweekday() takes 1 positional argument but 2 were given",
                "arity-iter TypeError Calendar.itermonthdays() missing 2 required positional arguments: 'year' and 'month'",
                "missing-kw TypeError setfirstweekday() got an unexpected keyword argument 'weekday'",
                "bad-encoding LookupError unknown encoding: bogus",
                "bad-pair ValueError too many values to unpack (expected 2, got 3)",
                "bad-pair2 TypeError cannot unpack non-iterable int object",
                "bad-pair3 ValueError not enough values to unpack (expected 2, got 1)",
                "bad-day-type TypeError %i format: a real number is required, not str",
                "surface True 45 ['IllegalMonthError', 'IllegalWeekdayError', 'setfirstweekday'] ['FRIDAY', 'SATURDAY', 'SUNDAY'] False False calendar Day",
                "local-locale None None",
                "local-iter list_iterator list"
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
