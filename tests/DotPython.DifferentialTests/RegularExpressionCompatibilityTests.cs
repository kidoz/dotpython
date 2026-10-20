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

            # --- named groups and backreferences ---------------------------------------
            m = re.match(r"(?P<first>a)(?P<second>b)(c)", "abc")
            print("named", m.group("first"), m.group(3), m.groups(), m.groupdict(), m.lastgroup, m.lastindex)
            print("named", m.group(1, "second"), m.span("first"), m.start("second"), m["first"], m.expand(r"<\g<first>>"))
            p = re.compile(r"(?P<a>x)(?P<b>y)?")
            print("named", dict(p.groupindex) if False else "skip", type(p.groupindex).__name__, p.groups, p.groupindex["a"])
            u = re.match(r"(?P<a>a)(?P<b>b)?", "a")
            print("named", u.groupdict(), u.groupdict("z"), u.groups("z"), u.lastgroup, u.lastindex)
            print("named", re.findall(r"(?P<a>a)(?P<b>b)", "abab"), re.split(r"(?P<a>a)", "banana"), re.sub(r"(?P<a>a)", r"[\g<a>]", "ab"))
            print("named", re.findall(r"(?P<x>a)|(?P<y>b)", "ab"), re.match(r"(?P<a>(?P<b>x))", "x").groupdict())
            print("named", re.search(r"(?=(?P<a>x))(?P=a)", "x").span(), re.search(r"(?=(?P<a>x))(?P=a)", "x").groupdict())
            print("named", re.compile(r"(?P<é>x)").groupindex, re.compile(r"(?P<class>x)").groupindex)
            report("named-unknown", lambda: m.group("nope"))
            report("named-span-unknown", lambda: m.span("nope"))
            report("named-dup", lambda: re.compile(r"(?P<n>a)(?P<n>b)"))
            report("named-badname", lambda: re.compile(r"(?P<1>a)"))
            report("named-empty", lambda: re.compile(r"(?P<>a)"))
            report("named-ends", lambda: re.compile(r"(?P"))
            report("named-ref-missing", lambda: re.compile(r"(?P=a)"))
            report("named-ref-unknown", lambda: re.compile(r"(?P<n>a)(?P=x)"))
            report("named-repl-unknown", lambda: re.sub(r"(?P<a>a)", r"\g<nope>", "ab"))
            report("named-repl-unterminated", lambda: re.sub(r"(?P<a>a)", r"\g<a", "ab"))
            print("backref", re.search(r"(a)\1", "aa").span(), re.search(r"(a)\1", "ab"), re.search(r"(\w+) \1", "hi hi there").span())
            print("backref", re.search(r"(?i)(a)\1", "aA").span(), re.search(r"(a+)\1", "aaaa").span(), re.search(r"(?P<n>a)(?P=n)", "aa").span())
            print("backref", re.search(r"(x)?(a)\1", "a"), re.search(r"(x)?(a)\1", "xax").span(), re.search(r"(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)\11", "abcdefghijkk").span())
            report("backref-missing", lambda: re.compile(r"a\2"))
            report("backref-open", lambda: re.compile(r"(a\1)"))
            print("backref", re.sub(r"(?P<n>a)(?P=n)", r"[\g<n>]", "aa"), re.sub(r"(a)", r"\123", "ab"), repr(re.sub(r"(a)", r"\0", "ab")))
            report("backref-template-bad", lambda: re.sub(r"(a)", r"\12", "ab"))
            report("backref-template-missing", lambda: re.sub(r"(a)", r"\8", "ab"))
            report("backref-lookbehind", lambda: re.compile(r"(?<=(a)\1)b"))
            print("look", re.search(r"a(?=b)", "ab").span(), re.search(r"a(?=b)", "ac"), re.search(r"a(?!b)", "ac").span())
            print("look", re.search(r"(?=(a))a\1", "aa").span(), re.findall(r"(?=(a))", "aa"), re.search(r"(a)(?=b)", "ab").groups())
            print("look", re.search(r"(?<=a)b", "ab").span(), re.search(r"(?<!a)b", "xb").span(), re.search(r"(?<=(a))b", "ab").groups())
            print("look", re.search(r"(?<=a{2})b", "aab").span(), re.sub(r"(?<=\d)(?=(\d\d\d))", ",", "1234567"))
            report("look-word", lambda: re.search(r"(?=a+)(a+)\b", "aaab"))
            print("look", re.search(r"(?=a)a", "a").span(), re.search(r"(?=a)", "a").span(), re.search(r"(?=a)", "b"))
            report("look-fixed", lambda: re.compile(r"(?<=a+)b"))
            report("look-fixed-branch", lambda: re.compile(r"(?<=ab|c)b"))
            report("look-apart", lambda: re.compile(r"(a)(?<=\1)b"))
            print("atomic", re.search(r"(?>ab|a)b", "ab"), re.search(r"(?>a|ab)b", "ab").span(), re.search(r"a++b", "ab").span())
            print("atomic", [x.span() for x in re.finditer(r"(?>a|ab)b", "abb")], re.search(r"(?>(a))b", "ab").groups())
            print("scoped", re.search(r"(?i:ab)", "AB").span(), re.search(r"(?i)a(?-i:B)", "Ab"), re.search(r"(?i)a(?-i:B)", "AB").span(), re.findall(r"(?i:a)b", "AB ab"))
            print("scoped", re.sub(r"(?i:ab)", "-", "AB ab"), re.split(r"(?i:a)", "bAb"), re.search(r"(?x:a b)", "ab").span())
            print("scoped", re.search(r"(?s:a.b)", "a\nb").span(), re.search(r"(?m:^b)", "a\nb").span(), sp_span := re.compile(r"(?i:a)").flags)
            report("scoped-off", lambda: re.compile(r"(?-i)a"))
            report("scoped-both", lambda: re.compile(r"(?i-i:a)"))
            report("scoped-type", lambda: re.compile(r"(?a-u:a)"))
            report("scoped-locale", lambda: re.compile(r"(?L:a)"))
            report("scoped-unknown", lambda: re.compile(r"(?i-z:a)"))
            report("scoped-late", lambda: re.compile(r"a(?i)"))
            print("scoped", re.compile(r"(?i)(?-i:a)").flags, re.compile(r"(?i:a)").flags, re.compile(r"(?a)a").flags)

            # --- conditional groups and named escapes ----------------------------------
            print("cond", re.search(r"(a)?b(?(1)c|d)", "bd").span(), re.search(r"(a)?b(?(1)c|d)", "abc").span(), re.search(r"(a)?b(?(1)c|d)", "abd").span())
            print("cond", re.search(r"(a)?b(?(1)c)", "bd").span(), re.search(r"(?P<n>a)?b(?(n)c|d)", "bc"), re.search(r"(a)(?(1)b|c)", "ab").span())
            print("cond", re.search(r"(a)?(?(1)(b)c)(d)", "acd").span(), re.search(r"(a)?(?(1)(b)?(?(2)c|d)|e)", "ad").groups())
            print("cond", re.findall(r"(a)?(?(1)b|c)", "ab c b"), re.sub(r"(a)?(?(1)b|c)", "-", "ab c b"), re.search(r"(a)?b(?(1)c|d)", "abd"))
            print("cond", re.search(r"(a)?(?(1)b|(c))", "c").groups(), [m.span() for m in re.finditer(r"(a)?b(?(1)c|d)", "bd abc")])
            print("cond", re.compile(r"(a)?(?(1)ab|c)").groups, re.compile(r"(a)?(?(1)b|(c))(d)").groups, re.search(r"(?(1)a|b)(x)", "ax"))
            report("cond-zero", lambda: re.compile(r"(?(0)a|b)"))
            report("cond-missing", lambda: re.compile(r"(?(2)a|b)"))
            report("cond-name", lambda: re.compile(r"(?(x)a|b)"))
            report("cond-three", lambda: re.compile(r"(?(1)a|b|c)"))
            report("cond-cut", lambda: re.compile(r"(?(1"))
            report("cond-open", lambda: re.compile(r"((?(1)a|b))"))
            print("name", re.match(r"\N{EM DASH}", "—").span(), re.match(r"[\N{LATIN SMALL LETTER A}-\N{LATIN SMALL LETTER C}]+", "abc").span())
            print("name", re.match(r"\N{bullet}", "•").span(), re.match(r"\N{Latin small letter a}", "a").span())
            report("name-nobrace", lambda: re.compile(r"\N"))
            report("name-empty", lambda: re.compile(r"\N{}"))
            report("name-unknown", lambda: re.compile(r"\N{NOT A REAL NAME}"))
            report("name-unterminated", lambda: re.compile(r"\N{EM DASH"))
            """
        );

    [Fact]
    public Task TheLeftoverHelpersMatchCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            import re
            def show(label, fn):
                try: print(label, "ok", repr(fn()))
                except Exception as e: print(label, type(e).__name__, e)
            print("escape-bytes", re.escape(b"a.b c*[](){}"), re.escape(b"\xff\x00\x7f"))
            print("escape-str", re.escape("a.b c*[](){}"))
            show("pattern-attr", lambda: re.compile("a").nope)
            show("match-attr", lambda: re.match("a","a").nope)
            show("pattern-sub", lambda: re.compile("a")[str])
            show("match-sub", lambda: re.match("a","a")[str])
            show("pattern-class-sub", lambda: re.Pattern[str])
            show("match-class-sub", lambda: re.Match[str])
            show("pattern-class-sub-int", lambda: re.Pattern[5])
            show("match-class-sub-int", lambda: re.Match[5])
            print("scanner", end=" ")
            s = re.compile("a+").scanner("aa b aaa")
            for call in ("match", "search", "match", "search", "match", "search"):
                show(call, lambda call=call: getattr(s, call)())
            print("scanner-pos", end=" ")
            s2 = re.compile("a").scanner("aaa")
            for i in range(4):
                show("m%d" % i, lambda: s2.match())
            show("scanner-type", lambda: (type(s).__name__, type(s).__module__))
            show("scanner-dir", lambda: sorted(n for n in dir(s) if not n.startswith("_")))
            show("scanner-init", lambda: re.compile("a").scanner())
            show("scanner-extra", lambda: type(re.compile("a").scanner("a", 0, 1)).__name__)
            show("scanner-kw", lambda: type(re.compile("a").scanner(string="a")).__name__)
            show("scanner-pattern", lambda: re.compile("a").scanner("a").pattern)
            show("scanner-attr", lambda: re.compile("a").scanner("a").nope)
            show("scanner-fail", lambda: re.compile("a").scanner("ba").match())
            show("scanner-fail2", lambda: re.compile("a").scanner("ba").search())
            s3 = re.compile("a").scanner("ba")
            show("sf0", lambda: s3.match())
            show("sf1", lambda: s3.search())
            show("sf2", lambda: s3.search())
            s4 = re.compile("").scanner("ab")
            for i in range(3):
                show("e%d" % i, lambda: s4.match())
            s5 = re.compile("a").scanner("aaa")
            show("pos-start", lambda: re.compile("a").scanner("aaa", 1).match())
            show("endpos", lambda: re.compile("a").scanner("aaa", 0, 1).match())
            show("endpos2", lambda: re.compile("a+").scanner("aaa", 0, 2).search())
            """"
        );
}
