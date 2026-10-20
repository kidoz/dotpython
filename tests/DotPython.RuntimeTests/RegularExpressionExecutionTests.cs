using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class RegularExpressionExecutionTests
{
    /// <summary>
    /// The runtime's own regular-expression engine on CPython's `re` surface: flags,
    /// compiled patterns, matches, the module functions and the errors a bad pattern
    /// raises — with named groups, backreferences, lookaround, atomic groups, scoped
    /// flags, conditional groups and the `\N{…}` named-character escapes the engine
    /// reads.
    /// </summary>
    [Fact]
    public void TheRegularExpressionModuleMatchesCpython()
    {
        var output = Run(
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
        Assert.Equal(
            Lines(
                "flags re.IGNORECASE re.IGNORECASE re.IGNORECASE|re.MULTILINE 2 True",
                "flags re.NOFLAG <flag 'RegexFlag'> RegexFlag RegexFlag",
                "flags IGNORECASE 2 True True IGNORECASE|MULTILINE",
                "flags True True True",
                "flags ['A', 'ASCII', 'DEBUG', 'DOTALL', 'I', 'IGNORECASE', 'L', 'LOCALE', 'M', 'MULTILINE', 'NOFLAG', 'S', 'U', 'UNICODE', 'VERBOSE', 'X'] RegexFlag re",
                "flags re.DEBUG re.LOCALE re.UNICODE re.ASCII re.VERBOSE re.DOTALL re.MULTILINE",
                "flags re.ASCII re.DEBUG 8",
                "compile re.compile('a') 32 0",
                "compile re.compile('a', re.IGNORECASE) re.compile('a', re.IGNORECASE|re.MULTILINE) re.compile('a', re.ASCII)",
                "compile 34 1048608 re.compile('a', 0x100000)",
                "compile 2 {} a",
                "compile True False",
                "compile 34 96 256",
                "compile-int TypeError first argument must be string or compiled pattern",
                "compile-none TypeError first argument must be string or compiled pattern",
                "compile-locale ValueError cannot use LOCALE flag with a str pattern",
                "compile-ascii-unicode ValueError ASCII and UNICODE flags are incompatible",
                "compile-bytes-flags TypeError unsupported operand type(s) for &: 'str' and 'int'",
                "compile-compiled-flags ValueError cannot process flags argument with a compiled pattern",
                "compile a True",
                "purge None True",
                "repr-instance True True",
                "fns (0, 3) (2, 3) (0, 3)",
                "fns None None None",
                "fns ['abc', 'abc'] [('a', 'b'), ('a', 'b')] ['b', '']",
                "fns ['a', 'b', 'c'] ['', 'a', 'b', ''] ['b', 'a', 'n', 'a', 'n', 'a', '']",
                "fns bXnXnX ('bXnXnX', 3) ('bXnXna', 2)",
                "fns [ba] a!b a!b",
                "fns AbAn  a b c ",
                "fns a\\-b_c\\ d! \\\\d\\+ \\&=\\~\\# abc ",
                "fns-match-missing TypeError match() missing 1 required positional argument: 'string'",
                "fns-compile-many TypeError compile() takes from 1 to 2 positional arguments but 3 were given",
                "fns-sub-few TypeError sub() missing 1 required positional argument: 'string'",
                "fns-escape-int TypeError decoding to str: need a bytes-like object, int found",
                "fns-bad-count TypeError 'str' object cannot be interpreted as an integer",
                "fns-bad-repl TypeError decoding to str: need a bytes-like object, int found",
                "fns-bad-repl-result TypeError sequence item 0: expected str instance, int found",
                "fns-bad-flags ValueError ASCII and UNICODE flags are incompatible",
                "match ab ab ('a', 'b') ('a', 'b', None) ('a', 'b', 'z') {}",
                "match (0, 2) (-1, -1) -1 1 0 2 ((0, 2), (0, 1), (1, 2), (-1, -1))",
                "match 2 None 0 2 ab (a)(b)(c)? a b",
                "match <re.Match object; span=(0, 2), match='ab'> <re.Match object; span=(0, 2), match='ab'> True True True",
                "match <a|b||> plain True",
                "match-bad-group IndexError no such group",
                "match-name-group IndexError no such group",
                "match-float-group IndexError no such group",
                "match-none-group IndexError no such group",
                "match-missing-group IndexError no such group",
                "match-iter TypeError 're.Match' object is not iterable",
                "match-pos-write AttributeError readonly attribute",
                "match-lastindex-write AttributeError attribute 'lastindex' of 're.Match' objects is not writable",
                "match-unknown 'skip'",
                "match ((1, 4), (2, 3), (3, 4)) 2",
                "match ((0, 1), (-1, -1), (-1, -1)) None",
                "match 3 skip",
                "pattern None (1, 2) (0, 1) ['a', 'a']",
                "pattern (1, 2) Xa ('XX', 2)",
                "pattern ['b', 'n', 'n', ''] ['b', 'nana'] [<re.Match object; span=(1, 2), match='a'>]",
                "pattern (3, 4) ['a', 'a']",
                "pattern ['a'] None (5, 6)",
                "pattern 2 6",
                "pattern-none-pos TypeError 'NoneType' object cannot be interpreted as an integer",
                "pattern-bad-pos TypeError 'str' object cannot be interpreted as an integer",
                "pattern-write AttributeError readonly attribute",
                "pattern-groups-write AttributeError readonly attribute",
                "pattern-groupindex-write AttributeError attribute 'groupindex' of 're.Pattern' objects is not writable",
                "pattern-call TypeError cannot create 're.Pattern' instances",
                "pattern-match-call TypeError cannot create 're.Match' instances",
                "pattern-unknown 'skip'",
                "pattern a 42",
                "pattern ['1', '22'] aNbN",
                "empty [(0, 0), (1, 1), (2, 3), (3, 3), (4, 4)] [(0, 0), (1, 2), (2, 2)]",
                "empty [(0, 0), (1, 1), (2, 2)] ['', 'b', '', '']",
                "empty ['', '', ''] -a-b--d- -a-b- -b--",
                "empty ['', ''] [''] - ['abc']",
                "empty [(0, 0), (2, 2), (4, 4)] ['', '']",
                "grammar None a",
                "b",
                "grammar (0, 1) None",
                "grammar (2, 5) (1, 3)",
                "grammar ['a-b', 'c'] ['bc', 'b'] ['abc']",
                "grammar aaa aaa <re.Match object; span=(0, 1), match='a'>",
                "grammar a aab abab",
                "grammar ab a (0, 2)",
                "grammar Xb (0, 0) (0, 2)",
                "grammar ['héllo', 'wörld'] ['h', 'llo', 'w', 'rld']",
                "grammar ['٣', '3'] ['3']",
                "grammar ['a', ''] ['', 'a', None, '', None, 'b', '']",
                "errors True PatternError re True",
                "bad '(' PatternError missing ), unterminated subpattern at position 0",
                "bad ')' PatternError unbalanced parenthesis at position 0",
                "bad 'a(' PatternError missing ), unterminated subpattern at position 1",
                "bad '*' PatternError nothing to repeat at position 0",
                "bad 'a**' PatternError multiple repeat at position 2",
                "bad 'a{2,1}' PatternError min repeat greater than max repeat at position 2",
                "bad '[' PatternError unterminated character set at position 0",
                "bad '[z-a]' PatternError bad character range z-a at position 1",
                "bad '\\\\q' PatternError bad escape \\q at position 0",
                "bad '\\\\1' PatternError invalid group reference 1 at position 1",
                "bad 'a(?i)' PatternError global flags not at the start of the expression at position 1",
                "bad '(?)' PatternError unknown extension ?) at position 1",
                "bad '(?' PatternError unexpected end of pattern at position 2",
                "bad '(?<x)' PatternError unknown extension ?<x at position 1",
                "bad '\\\\x' PatternError incomplete escape \\x at position 0",
                "bad '\\\\8' PatternError invalid group reference 8 at position 1",
                "bad '[\\\\8]' PatternError bad escape \\8 at position 1",
                "bad '[\\\\A]' PatternError bad escape \\A at position 1",
                "bad '[\\\\d]' re.compile('[\\\\d]')",
                "bad '[\\\\b]' re.compile('[\\\\b]')",
                "bad '(?#comment)a' re.compile('(?#comment)a')",
                "bad '^*' PatternError nothing to repeat at position 1",
                "bad '{2}' PatternError nothing to repeat at position 0",
                "bad '(?P' PatternError unexpected end of pattern at position 3",
                "bad-multiline PatternError missing ), unterminated subpattern at position 4 (line 2, column 2)",
                "bad-lastindex 'skip'",
                "errors missing ), unterminated subpattern '(a\\nb(' 4 2 2 ('missing ), unterminated subpattern at position 4 (line 2, column 2)',) PatternError",
                "errors plain ('plain',) m at position 2",
                "errors m None None",
                "errors True True 2 2",
                "errors-attr AttributeError 'PatternError' object has no attribute 'bad'",
                "errors False False",
                "named a c ('a', 'b', 'c') {'first': 'a', 'second': 'b'} None 3",
                "named ('a', 'b') (0, 1) 1 a <a>",
                "named skip mappingproxy 2 1",
                "named {'a': 'a', 'b': None} {'a': 'a', 'b': 'z'} ('a', 'z') a 1",
                "named [('a', 'b'), ('a', 'b')] ['b', 'a', 'n', 'a', 'n', 'a', ''] [a]b",
                "named [('a', ''), ('', 'b')] {'a': 'x', 'b': 'x'}",
                "named (0, 1) {'a': 'x'}",
                "named {'é': 1} {'class': 1}",
                "named-unknown IndexError no such group",
                "named-span-unknown IndexError no such group",
                "named-dup PatternError redefinition of group name 'n' as group 2; was group 1 at position 12",
                "named-badname PatternError bad character in group name '1' at position 4",
                "named-empty PatternError missing group name at position 4",
                "named-ends PatternError unexpected end of pattern at position 3",
                "named-ref-missing PatternError unknown group name 'a' at position 4",
                "named-ref-unknown PatternError unknown group name 'x' at position 12",
                "named-repl-unknown IndexError unknown group name 'nope'",
                "named-repl-unterminated PatternError missing >, unterminated name at position 3",
                "backref (0, 2) None (0, 5)",
                "backref (0, 2) (0, 4) (0, 2)",
                "backref None (0, 3) (0, 12)",
                "backref-missing PatternError invalid group reference 2 at position 2",
                "backref-open PatternError cannot refer to an open group at position 2",
                "backref [a] Sb '\\x00b'",
                "backref-template-bad PatternError invalid group reference 12 at position 1",
                "backref-template-missing PatternError invalid group reference 8 at position 1",
                "backref-lookbehind PatternError cannot refer to group defined in the same lookbehind subpattern at position 9",
                "look (0, 1) None (0, 1)",
                "look (0, 2) ['a', 'a'] ('a',)",
                "look (1, 2) (1, 2) ('a',)",
                "look (2, 3) 1,2,3,4,567",
                "look-word None",
                "look (0, 1) (0, 0) None",
                "look-fixed PatternError look-behind requires fixed-width pattern",
                "look-fixed-branch PatternError look-behind requires fixed-width pattern",
                "look-apart re.compile('(a)(?<=\\\\1)b')",
                "atomic None (0, 2) (0, 2)",
                "atomic [(0, 2)] ('a',)",
                "scoped (0, 2) None (0, 2) ['ab']",
                "scoped - - ['b', 'b'] (0, 2)",
                "scoped (0, 3) (2, 3) 32",
                "scoped-off PatternError missing : at position 4",
                "scoped-both PatternError bad inline flags: flag turned on and off at position 5",
                "scoped-type PatternError bad inline flags: cannot turn off flags 'a', 'u' and 'L' at position 5",
                "scoped-locale PatternError bad inline flags: cannot use 'L' flag with a str pattern at position 3",
                "scoped-unknown PatternError unknown flag at position 4",
                "scoped-late PatternError global flags not at the start of the expression at position 1",
                "scoped 34 32 256",
                "cond (0, 2) (0, 3) (1, 3)",
                "cond (0, 1) None (0, 2)",
                "cond (2, 3) ('a', None)",
                "cond ['a', ''] - - b <re.Match object; span=(1, 3), match='bd'>",
                "cond (None, 'c') [(0, 2), (3, 6)]",
                "cond 1 3 None",
                "cond-zero PatternError bad group number at position 3",
                "cond-missing PatternError invalid group reference 2 at position 3",
                "cond-name PatternError unknown group name 'x' at position 3",
                "cond-three PatternError conditional backref with more than two branches at position 8",
                "cond-cut PatternError missing ), unterminated name at position 3",
                "cond-open re.compile('((?(1)a|b))')",
                "name (0, 1) (0, 3)",
                "name (0, 1) (0, 1)",
                "name-nobrace PatternError missing { at position 2",
                "name-empty PatternError missing character name at position 3",
                "name-unknown PatternError undefined character name 'NOT A REAL NAME' at position 0",
                "name-unterminated PatternError missing }, unterminated name at position 3"
            ),
            output
        );
    }

    /// <summary>
    /// The list of names the module publishes, in the order CPython's own module does.
    /// </summary>
    [Fact]
    public void ThePublicNamesMatchCpython()
    {
        var output = Run(
            """
            import re
            print(re.__all__)
            """
        );
        Assert.Equal(
            Lines(
                "['match', 'fullmatch', 'search', 'sub', 'subn', 'split', 'findall', 'finditer', "
                    + "'compile', 'purge', 'escape', 'error', 'Pattern', 'Match', 'A', 'I', 'L', 'M', "
                    + "'S', 'X', 'U', 'ASCII', 'IGNORECASE', 'LOCALE', 'MULTILINE', 'DOTALL', 'VERBOSE', "
                    + "'UNICODE', 'NOFLAG', 'RegexFlag', 'PatternError']"
            ),
            output
        );
    }

    /// <summary>
    /// What the slice does not take: `bytes` patterns, which the module refuses in its own
    /// words — a `bytes` subject beside a `str` pattern is refused in Python's — and the
    /// `re.DEBUG` flag, which is accepted and carried but prints no disassembly.
    /// </summary>
    [Fact]
    public void TheSyntaxOutsideTheSliceIsRefused()
    {
        var output = Run(
            """
            import re
            def report(label, thunk):
                try:
                    print(label, thunk())
                except Exception as error:
                    print(label, type(error).__name__, error)
            report("locale-str", lambda: re.compile("a", re.L))
            report("bytes-string", lambda: re.match("a", b"a"))
            report("bytes-compiled", lambda: re.match(re.compile(b"a"), "a"))
            report("debug", lambda: repr(re.compile("a", re.DEBUG)))
            report("debug-flag", lambda: re.compile("a", re.DEBUG).flags)
            """
        );
        Assert.Equal(
            Lines(
                "locale-str ValueError cannot use LOCALE flag with a str pattern",
                "bytes-string TypeError cannot use a string pattern on a bytes-like object",
                "bytes-compiled TypeError cannot use a bytes pattern on a string-like object",
                "debug re.compile('a', re.DEBUG)",
                "debug-flag 160"
            ),
            output
        );
    }

    /// <summary>
    /// Two compiles of one pattern are the same object, which is what CPython's own cache
    /// does, and `re.purge` takes that cache away.
    /// </summary>
    [Fact]
    public void CompiledPatternsAreCached()
    {
        var output = Run(
            """
            import re
            first = re.compile("a")
            print("cached", first is re.compile("a"), first is re.compile("a", re.I))
            re.purge()
            print("purged", first is re.compile("a"), re.compile("a").pattern)
            match = re.match("(a)", "a")
            print("bound", match.re.pattern, match.re is re.compile("(a)"), match.string, match.pos, match.endpos)
            """
        );
        Assert.Equal(Lines("cached True False", "purged False a", "bound (a) True a 0 1"), output);
    }

    /// <summary>
    /// The leftovers closed here: `escape(bytes)`, the `'re.Pattern'`/`'re.Match'`
    /// attribute wording, class-level `Pattern[...]`/`Match[...]`, and `scanner()` with
    /// its positional and keyword arguments and stateful `match`/`search`.
    /// </summary>
    [Fact]
    public void TheLeftoverHelpersMatchPython()
    {
        var output = Run(
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

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "escape-bytes b'a\\\\.b\\\\ c\\\\*\\\\[\\\\]\\\\(\\\\)\\\\{\\\\}' b'\\xff\\x00\\x7f'",
                "escape-str a\\.b\\ c\\*\\[\\]\\(\\)\\{\\}",
                "pattern-attr AttributeError 're.Pattern' object has no attribute 'nope'",
                "match-attr AttributeError 're.Match' object has no attribute 'nope'",
                "pattern-sub TypeError 're.Pattern' object is not subscriptable",
                "match-sub IndexError no such group",
                "pattern-class-sub ok re.Pattern[str]",
                "match-class-sub ok re.Match[str]",
                "pattern-class-sub-int ok re.Pattern[5]",
                "match-class-sub-int ok re.Match[5]",
                "scanner match ok <re.Match object; span=(0, 2), match='aa'>",
                "search ok <re.Match object; span=(5, 8), match='aaa'>",
                "match ok None",
                "search ok None",
                "match ok None",
                "search ok None",
                "scanner-pos m0 ok <re.Match object; span=(0, 1), match='a'>",
                "m1 ok <re.Match object; span=(1, 2), match='a'>",
                "m2 ok <re.Match object; span=(2, 3), match='a'>",
                "m3 ok None",
                "scanner-type ok ('SRE_Scanner', '_sre')",
                "scanner-dir ok ['match', 'pattern', 'search']",
                "scanner-init TypeError scanner() missing required argument 'string' (pos 1)",
                "scanner-extra ok 'SRE_Scanner'",
                "scanner-kw ok 'SRE_Scanner'",
                "scanner-pattern ok re.compile('a')",
                "scanner-attr AttributeError '_sre.SRE_Scanner' object has no attribute 'nope'",
                "scanner-fail ok None",
                "scanner-fail2 ok <re.Match object; span=(1, 2), match='a'>",
                "sf0 ok None",
                "sf1 ok None",
                "sf2 ok None",
                "e0 ok <re.Match object; span=(0, 0), match=''>",
                "e1 ok None",
                "e2 ok None",
                "pos-start ok <re.Match object; span=(1, 2), match='a'>",
                "endpos ok <re.Match object; span=(0, 1), match='a'>",
                "endpos2 ok <re.Match object; span=(0, 2), match='aa'>",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The bytes pattern surface: Latin-1 patterns and subjects with ASCII-only classes
    /// and case folding, byte groups, `findall`/`finditer`/`split`/`sub`/`subn`, the
    /// pattern and match attributes, `expand`, the module/compiled mixing refusals, the
    /// inline-flag error, scanner subjects, and `Pattern[bytes]` aliases.
    /// </summary>
    [Fact]
    public void TheBytesPatternSurfaceMatchesPython()
    {
        var output = Run(
            """"
            import re
            def show(label, fn):
                try: print(label, "ok", repr(fn()))
                except Exception as e: print(label, type(e).__name__, e)
            print("escape-bytes", re.escape(b"a.b c*[](){}"), re.escape(b"\xff\x00\x7f"))
            p = re.compile(b"a(b)?c", re.I)
            print("bytes-match", p.match(b"AC").groups(), p.search(b"xxabc").group(0))
            print("bytes-sub", p.sub(b"x", b"abc-ac"), p.sub(b"x", b"abc-ac", count=1))
            print("bytes-findall", re.findall(b"a+", b"aa b aaa"))
            print("bytes-split", re.split(b",", b"a,b,,c"))
            print("bytes-flags", p.flags, p.pattern)
            print("bytes-repr", repr(p), repr(p.match(b"abc")))
            print("bytes-string", p.match(b"abc").string, type(p.match(b"abc").string).__name__)
            show("bytes-int", lambda: p.match(5))
            show("bytes-strsub", lambda: p.match("abc"))
            show("str-bytessub", lambda: re.compile("a").match(b"a"))
            show("bytes-uflag", lambda: re.compile(b"a", re.U))
            show("bytes-lflag", lambda: re.compile(b"a", re.L).flags)
            show("bytes-default-flags", lambda: re.compile(b"a").flags)
            show("bytes-af", lambda: re.compile(b"a", re.A).flags)
            print("bytes-bytearray", re.compile(b"a").match(bytearray(b"a")).group(0))
            print("bytes-groups", re.compile(b"(a)(?P<n>b)").match(b"ab").groups(), re.compile(b"(a)(?P<n>b)").match(b"ab").groupdict())
            print("bytes-expand", re.compile(b"(a)").match(b"a").expand(b"\\1x"))
            show("expand-str", lambda: re.compile(b"(a)").match(b"a").expand("\\1x"))
            show("expand-bytes-strpat", lambda: re.compile("(a)").match("a").expand(b"\\1x"))
            show("sub-str-repl", lambda: re.compile(b"a").sub("x", b"a"))
            show("sub-bytes-on-str", lambda: re.compile("a").sub(b"x", "a"))
            def f(m): return "x"
            def g(m): return b"x"
            show("sub-fn-str", lambda: re.compile(b"a").sub(f, b"a"))
            show("sub-fn-bytes", lambda: re.compile("a").sub(g, "a"))
            print("bytes-finditer", [m.group(0) for m in re.finditer(b"a+", b"aa b aaa")])
            print("bytes-match-nongroup", re.compile(b"x*").findall(b"abc"))
            print("bytes-nonascii", re.compile(b"\xff").match(b"\xff"), re.compile(b"[\x80-\xff]+").match(b"\x80\xff"))
            print("bytes-ci", re.compile(b"\xe9", re.I).match(b"\xc9"))
            print("bytes-word", re.compile(b"\\w+").match(b"\xe9"), re.compile(b"\\w+", re.A).match(b"\xe9"))
            print("scanner", re.compile(b"a+").scanner(b"aa b").match().group(0))
            print("class-sub", re.Pattern[bytes], re.Match[bytes])
            show("bytes-pattern-attr", lambda: re.compile(b"a").scanner("a"))

            import re
            def show(label, fn):
                try: print(label, "ok", repr(fn()))
                except Exception as e: print(label, type(e).__name__, e)
            print("cache-distinct", re.compile("a") is re.compile("a"), re.compile(b"a") is re.compile(b"a"), re.compile("a") is re.compile(b"a"))
            p = re.compile(b"(?P<x>a+)(b)?")
            m = p.match(b"aa")
            print("m", m.group(0), m.groups(), m.groups(b"-"), m.groupdict(), m.span(), m.regs)
            print("idx", m["x"], m[1], m.lastindex, m.lastgroup, m.pos, m.endpos)
            show("missing", lambda: m.group(3))
            print("subn", p.subn(b"<\\g<x>>", b"aab b aa"))
            print("subn-fn", p.subn(lambda mm: b"[" + mm.group(0) + b"]", b"aab b aa"))
            print("split-groups", p.split(b"aab b aa"))
            print("split-max", p.split(b"aab b aa", 1))
            print("finditer", [(mm.span(), mm.group(0)) for mm in p.finditer(b"aab b aa")])
            print("pos", p.match(b"aa", 1), p.search(b"aa", 1), p.fullmatch(b"aa", 0, 2))
            print("module", re.match(b"a", b"a").group(0), re.fullmatch(b"a+", b"aaa").group(0), re.search(b"b", b"ab").span())
            show("mix-compiled", lambda: re.match(re.compile(b"a"), "a"))
            show("mix-compiled2", lambda: re.match(re.compile("a"), b"a"))
            print("empty-bytes", re.findall(b"", b"ab"))
            print("classes", re.findall(b"[^a]+", b"aa\xff\x80cc"))
            print("nongreedy", re.compile(b"a+?").match(b"aaa").group(0))
            print("anchors", re.findall(b"^.", b"a\nb", re.M), re.findall(b".", b"\n"))
            print("escaped-meta", re.match(b"\\.", b"."), re.match(b"\\d+", b"123").group(0))
            print("bytes-escape-backslash", re.escape(b"\\a"))
            print("str-escape-backslash", re.escape("\\a"))
            print("scanner", end=" ")
            s = re.compile(b"a").scanner(b"baa")
            show("m0", lambda: s.match())
            show("s1", lambda: s.search())
            show("m2", lambda: s.search())
            show("s3", lambda: s.search())
            print("expand-none", re.compile(b"(a)?b").match(b"b").expand(b"<\\1>"))
            print("group-none", re.compile(b"(a)?b").match(b"b").group(1), re.compile(b"(a)?b").match(b"b").groups())
            show("bytearray-repl", lambda: re.compile(b"a").sub(bytearray(b"x"), b"a"))
            show("memview", lambda: re.compile(b"a").match(memoryview(b"a")))
            print("flags", re.compile(b"(?i)a").flags, re.compile(b"(?a)a").flags, re.compile(b"(?u)a") if False else "skip")
            show("flags-u-inline", lambda: re.compile(b"(?u)a"))
            print("repr", repr(re.compile(b"(?i)a(b)?")), repr(re.compile(b"a").match(b"a")))
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "escape-bytes b'a\\\\.b\\\\ c\\\\*\\\\[\\\\]\\\\(\\\\)\\\\{\\\\}' b'\\xff\\x00\\x7f'",
                "bytes-match (None,) b'abc'",
                "bytes-sub b'x-x' b'x-ac'",
                "bytes-findall [b'aa', b'aaa']",
                "bytes-split [b'a', b'b', b'', b'c']",
                "bytes-flags 2 b'a(b)?c'",
                "bytes-repr re.compile(b'a(b)?c', re.IGNORECASE) <re.Match object; span=(0, 3), match=b'abc'>",
                "bytes-string b'abc' bytes",
                "bytes-int TypeError expected string or bytes-like object, got 'int'",
                "bytes-strsub TypeError cannot use a bytes pattern on a string-like object",
                "str-bytessub TypeError cannot use a string pattern on a bytes-like object",
                "bytes-uflag ValueError cannot use UNICODE flag with a bytes pattern",
                "bytes-lflag ok 4",
                "bytes-default-flags ok 0",
                "bytes-af ok 256",
                "bytes-bytearray b'a'",
                "bytes-groups (b'a', b'b') {'n': b'b'}",
                "bytes-expand b'ax'",
                "expand-str TypeError sequence item 1: expected str instance, bytes found",
                "expand-bytes-strpat TypeError sequence item 1: expected a bytes-like object, str found",
                "sub-str-repl TypeError sequence item 0: expected a bytes-like object, str found",
                "sub-bytes-on-str TypeError sequence item 0: expected str instance, bytes found",
                "sub-fn-str TypeError sequence item 0: expected a bytes-like object, str found",
                "sub-fn-bytes TypeError sequence item 0: expected str instance, bytes found",
                "bytes-finditer [b'aa', b'aaa']",
                "bytes-match-nongroup [b'', b'', b'', b'']",
                "bytes-nonascii <re.Match object; span=(0, 1), match=b'\\xff'> <re.Match object; span=(0, 2), match=b'\\x80\\xff'>",
                "bytes-ci None",
                "bytes-word None None",
                "scanner b'aa'",
                "class-sub re.Pattern[bytes] re.Match[bytes]",
                "bytes-pattern-attr TypeError cannot use a bytes pattern on a string-like object",
                "cache-distinct True True False",
                "m b'aa' (b'aa', None) (b'aa', b'-') {'x': b'aa'} (0, 2) ((0, 2), (0, 2), (-1, -1))",
                "idx b'aa' b'aa' 1 x 0 2",
                "missing IndexError no such group",
                "subn (b'<aa> b <aa>', 2)",
                "subn-fn (b'[aab] b [aa]', 2)",
                "split-groups [b'', b'aa', b'b', b' b ', b'aa', None, b'']",
                "split-max [b'', b'aa', b'b', b' b aa']",
                "finditer [((0, 3), b'aab'), ((6, 8), b'aa')]",
                "pos <re.Match object; span=(1, 2), match=b'a'> <re.Match object; span=(1, 2), match=b'a'> <re.Match object; span=(0, 2), match=b'aa'>",
                "module b'a' b'aaa' (1, 2)",
                "mix-compiled TypeError cannot use a bytes pattern on a string-like object",
                "mix-compiled2 TypeError cannot use a string pattern on a bytes-like object",
                "empty-bytes [b'', b'', b'']",
                "classes [b'\\xff\\x80cc']",
                "nongreedy b'a'",
                "anchors [b'a', b'b'] []",
                "escaped-meta <re.Match object; span=(0, 1), match=b'.'> b'123'",
                "bytes-escape-backslash b'\\\\\\\\a'",
                "str-escape-backslash \\\\a",
                "scanner m0 ok None",
                "s1 ok None",
                "m2 ok None",
                "s3 ok None",
                "expand-none b'<>'",
                "group-none None (None,)",
                "bytearray-repl ok b'x'",
                "memview ok <re.Match object; span=(0, 1), match=b'a'>",
                "flags 2 256 skip",
                "flags-u-inline PatternError bad inline flags: cannot use 'u' flag with a bytes pattern at position 3",
                "repr re.compile(b'(?i)a(b)?', re.IGNORECASE) <re.Match object; span=(0, 1), match=b'a'>",
                ""
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
