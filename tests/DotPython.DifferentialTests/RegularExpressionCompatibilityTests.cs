using Xunit;

namespace DotPython.DifferentialTests;

public sealed class RegularExpressionCompatibilityTests
{
    [Fact]
    public Task TheRegularExpressionModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import re


            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)


            # --- flags -----------------------------------------------------------------
            print("flags", repr(re.I), str(re.I), repr(re.I | re.M), int(re.I), re.I == 2)
            print("flags", repr(re.NOFLAG), repr(re.RegexFlag), type(re.I).__name__, type(re.I | re.M).__name__)
            print("flags", re.I.name, re.I.value, re.MULTILINE is re.M, re.A is re.ASCII, (re.I | re.M).name)
            print("flags", isinstance(re.I, int), isinstance(re.I, re.RegexFlag), re.RegexFlag(2) is re.I)
            print("flags", sorted(re.RegexFlag.__members__), re.RegexFlag.__name__, re.RegexFlag.__module__)
            print("flags", re.DEBUG, re.LOCALE, re.UNICODE, re.ASCII, re.VERBOSE, re.DOTALL, re.MULTILINE)
            print("flags", list(re.RegexFlag)[0], list(re.RegexFlag)[-1], len(list(re.RegexFlag)))

            # --- compiling -------------------------------------------------------------
            print("compile", repr(re.compile("a")), re.compile("a").flags, re.compile("a").groups)
            print("compile", repr(re.compile("a", re.I)), repr(re.compile("a", re.I | re.M)), repr(re.compile("a", re.A)))
            print("compile", re.compile("a", 2).flags, re.compile("a", 1 << 20).flags, repr(re.compile("a", 1 << 20)))
            print("compile", re.compile("(a)(b)").groups, re.compile("a").groupindex, re.compile("a").pattern)
            print("compile", re.compile("a") is re.compile("a"), re.compile("a") is re.compile("a", re.I))
            print("compile", re.compile("(?i)a").flags, re.compile("(?x)a").flags, re.compile("(?a)a").flags)
            report("compile-int", lambda: re.compile(3))
            report("compile-none", lambda: re.compile(None))
            report("compile-locale", lambda: re.compile("a", re.L))
            report("compile-ascii-unicode", lambda: re.compile("a", re.U | re.A))
            report("compile-bytes-flags", lambda: re.compile("a", "x"))
            report("compile-compiled-flags", lambda: re.compile(re.compile("a"), re.I))
            print("compile", re.compile(re.compile("a")).pattern, re.compile("a") is re.compile(re.compile("a")))
            print("purge", re.purge(), re.purge() is None)
            print("repr-instance", str(re.compile("a")) == repr(re.compile("a")), bool(re.compile("")))

            # --- module-level functions ------------------------------------------------
            print("fns", re.match("a.c", "abc").span(), re.search("c", "abc").span(), re.fullmatch("a.c", "abc").span())
            print("fns", re.fullmatch("a", "ab"), re.match("b", "abc"), re.search("z", "abc"))
            print("fns", re.findall("a.c", "abcabc"), re.findall("(a)(b)", "abab"), re.findall("a(b)?", "ab a"))
            print("fns", re.split(":", "a:b:c"), re.split("", "ab"), re.split("(a)", "banana"))
            print("fns", re.sub("a", "X", "banana"), re.subn("a", "X", "banana"), re.subn("a", "X", "banana", count=2))
            print("fns", re.sub("(a)(b)", r"[\2\1]", "ab"), re.sub("(a)", r"\g<1>!", "ab"), re.sub("a", r"\g<0>!", "ab"))
            print("fns", re.sub("a", lambda m: m.group().upper(), "aban"), re.sub(r"\s+", " ", " a  b\nc "))
            print("fns", re.escape("a-b_c d!"), re.escape(r"\d+"), re.escape("&=~#"), re.escape("abc"), re.escape(""))
            report("fns-match-missing", lambda: re.match("a"))
            report("fns-compile-many", lambda: re.compile("a", 1, 2))
            report("fns-sub-few", lambda: re.sub("a", "b"))
            report("fns-escape-int", lambda: re.escape(5))
            report("fns-bad-count", lambda: re.sub("a", "x", "ab", count="no"))
            report("fns-bad-repl", lambda: re.sub("a", 1, "ab"))
            report("fns-bad-repl-result", lambda: re.sub("a", lambda m: 5, "ab"))
            report("fns-bad-flags", lambda: re.match("a", "a", 1 << 8 | 1 << 5))

            # --- match objects ---------------------------------------------------------
            m = re.match(r"(a)(b)(c)?", "ab")
            print("match", m.group(), m.group(0), m.group(1, 2), m.groups(), m.groups("z"), m.groupdict())
            print("match", m.span(), m.span(3), m.start(3), m.end(1), m.start(), m.end(), m.regs)
            print("match", m.lastindex, m.lastgroup, m.pos, m.endpos, m.string, m.re.pattern, m[1], m[2])
            print("match", repr(m), str(m), bool(m), type(m) is re.Match, isinstance(m, re.Match))
            print("match", m.expand(r"<\1|\2|\3|>"), m.expand("plain"), m.group() == "ab")
            report("match-bad-group", lambda: m.group(4))
            report("match-name-group", lambda: m.group("x"))
            report("match-float-group", lambda: m.group(1.0))
            report("match-none-group", lambda: m.group(None))
            report("match-missing-group", lambda: re.match("a", "a").group(1))
            report("match-iter", lambda: list(m))
            report("match-pos-write", lambda: setattr(m, "pos", 1))
            report("match-lastindex-write", lambda: setattr(m, "lastindex", 1))
            report("match-unknown", lambda: m.unknown if False else "skip")
            print("match", re.search("b(c)(d)?", "abcd").regs, re.search("(a)|(b)", "b").lastindex)
            print("match", re.search("(x)?(y)?z", "z").regs, re.search("(x)?(y)?z", "z").lastindex)
            print("match", re.match("a", "abc").endpos, re.match("a", "abc", 1).pos if False else "skip")

            # --- pattern methods -------------------------------------------------------
            p = re.compile("a")
            print("pattern", p.match("ba"), p.search("ba").span(), p.fullmatch("a").span(), p.findall("aa"))
            print("pattern", p.finditer("ba").__next__().span(), p.sub("X", "aa", count=1), p.subn("X", "aa"))
            print("pattern", p.split("banana"), p.split("banana", maxsplit=1), list(p.finditer("ba")))
            print("pattern", re.compile("a").search("banana", 2).span(), re.compile("a").findall("banana", 2))
            print("pattern", re.compile("a").findall("banana", 2, 4), re.compile("a").match("banana", 4), re.compile("a").match("banana", 5).span())
            print("pattern", re.compile("a").search("banana", 2).pos, re.compile("a").search("banana", 2).endpos)
            report("pattern-none-pos", lambda: re.compile("a").search("banana", None))
            report("pattern-bad-pos", lambda: re.compile("a").search("banana", "x"))
            report("pattern-write", lambda: setattr(p, "pattern", 1))
            report("pattern-groups-write", lambda: setattr(p, "groups", 1))
            report("pattern-groupindex-write", lambda: setattr(p, "groupindex", 1))
            report("pattern-call", lambda: re.Pattern("a"))
            report("pattern-match-call", lambda: re.Match())
            report("pattern-unknown", lambda: p.unknown if False else "skip")
            print("pattern", re.compile("a", re.I).pattern, re.compile("a", re.I | re.M).flags)
            print("pattern", re.compile(r"\d+").findall("a1b22"), re.compile(r"\d+").sub("N", "a1b22"))

            # --- empty matches ---------------------------------------------------------
            print("empty", [x.span() for x in re.finditer("x*", "abxd")], [x.span() for x in re.finditer("a*", "ba")])
            print("empty", [x.span() for x in re.finditer("", "ab")], [x.group() for x in re.finditer("b*", "abc")])
            print("empty", re.findall("", "ab"), re.sub("x*", "-", "abxd"), re.sub("", "-", "ab"), re.sub("a*", "-", "ba"))
            print("empty", re.split("", ""), re.findall("", ""), re.sub("", "-", ""), re.split("x", "abc"))
            print("empty", [m.span() for m in re.finditer(r"^", "a\nb\n", re.M)], re.findall(r"$", "ab\n"))

            # --- anchors, classes, quantifiers ----------------------------------------
            print("grammar", re.search(r"a.b", "a\nb"), re.search(r"(?s)a.b", "a\nb").group())
            print("grammar", re.search(r"a$", "a\n").span(), re.search(r"a$", "a\nb").span() if re.search(r"a$", "a\nb") else None)
            print("grammar", re.search(r"\bfoo\b", "a foo b").span(), re.search(r"o\Bo", "foo").span())
            print("grammar", re.findall(r"[\w-]+", "a-b c"), re.findall(r"[^a]+", "abcab"), re.findall(r"[a-c]+", "abcd", re.I))
            print("grammar", re.match(r"a{1,3}", "aaaa").group(), re.match(r"a{2,}", "aaa").group(), re.match(r"a{,2}", "a{,2}"))
            print("grammar", re.match(r"a+?", "aaa").group(), re.match(r"a*?b", "aab").group(), re.match(r"(?:ab)+", "ababx").group())
            print("grammar", re.match(r"ab|a", "ab").group(), re.match(r"a|ab", "ab").group(), re.fullmatch("a|ab", "ab").span())
            print("grammar", re.sub("a|ab", "X", "ab"), re.match(r"(a?)+", "b").span(), re.match(r"(a*)*", "aa").span())
            print("grammar", re.findall(r"\w+", "héllo wörld"), re.findall(r"\w+", "héllo wörld", re.A))
            print("grammar", re.findall(r"\d+", "٣ 3"), re.findall(r"\d+", "٣ 3", re.A))
            print("grammar", re.findall(r"(a)?b", "ab b"), re.split(r"(a)|(b)", "ab"))

            # --- errors ----------------------------------------------------------------
            print("errors", re.error is re.PatternError, re.error.__name__, re.error.__module__, issubclass(re.error, Exception))
            for pattern in ["(", ")", "a(", "*", "a**", "a{2,1}", "[", "[z-a]", r"\q", r"\1", "a(?i)", "(?)", "(?", "(?<x)", r"\x",
                            r"\8", r"[\8]", r"[\A]", r"[\d]", r"[\b]", "(?#comment)a", "^*", "{2}", "(?P"]:
                report("bad " + repr(pattern), lambda pattern=pattern: re.compile(pattern))
            report("bad-multiline", lambda: re.compile("(a\nb("))
            report("bad-lastindex", lambda: re.error("m", "p", 2).msg if False else "skip")
            e = None
            try:
                re.compile("(a\nb(")
            except re.error as error:
                e = error
            print("errors", e.msg, repr(e.pattern), e.pos, e.lineno, e.colno, e.args, type(e).__name__)
            print("errors", str(re.error("plain")), re.error("plain").args, str(re.error("m", "p", 2)))
            print("errors", re.error("m").msg, re.error("m").pattern, re.error("m").pos)
            print("errors", isinstance(e, re.error), isinstance(e, Exception), e.lineno, e.colno)
            report("errors-attr", lambda: e.bad)
            print("errors", re.error("x") == re.error("x"), re.error("x") is re.error("x"))
            """
        );
}
