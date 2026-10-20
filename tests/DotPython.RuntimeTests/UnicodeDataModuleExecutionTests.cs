using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class UnicodeDataModuleExecutionTests
{
    /// <summary>
    /// The `unicodedata` surface over UCD 16: version, name and lookup (aliases and
    /// named sequences included), category, combining class, bidirectional class,
    /// east-asian width, mirrored, decomposition (compatibility tags included), numeric
    /// values, and the four normalization forms with canonical ordering, Hangul and
    /// compatibility composition.
    /// </summary>
    [Fact]
    public void TheUnicodeDataModuleMatchesPython()
    {
        var output = Run(
            """"
            import unicodedata as u
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            print("ver", u.unidata_version)
            show("name", lambda: u.name("A"))
            show("name-default", lambda: (u.name("͸", "dflt"), u.name("͸", None)))
            show("name-err", lambda: u.name("͸"))
            show("name-int", lambda: u.name(5))
            show("name-multi", lambda: u.name("AB"))
            show("lookup", lambda: (u.lookup("LATIN CAPITAL LETTER A"), u.lookup("SNOWMAN"), u.lookup("NULL")))
            show("lookup-lower", lambda: u.lookup("latin capital letter a"))
            show("lookup-alias", lambda: u.lookup("LINE FEED"))
            show("lookup-seq", lambda: u.lookup("KEYCAP DIGIT ONE"))
            show("lookup-err", lambda: u.lookup("NOPE"))
            show("category", lambda: [u.category(c) for c in "A1 é́中\U0001F600\u0000"])
            show("category-bad", lambda: u.category(5))
            show("combining", lambda: [u.combining(c) for c in "á̀⃐"])
            show("bidi", lambda: [u.bidirectional(c) for c in "A1 אا中\U0001F600"])
            show("eaw", lambda: [u.east_asian_width(c) for c in "A中́\U0001F600"])
            show("mirrored", lambda: [u.mirrored(c) for c in "(Aא"])
            show("decomp", lambda: (u.decomposition("A"), u.decomposition("é"), u.decomposition(" "), u.decomposition("ﬁ")))
            show("norm", lambda: [u.normalize(f, "é") for f in ("NFC","NFD","NFKC","NFKD")])
            show("norm2", lambda: [u.normalize(f, "ﬁ ") for f in ("NFC","NFD","NFKC","NFKD")])
            show("norm-bad", lambda: u.normalize("XX", "a"))
            show("norm-int", lambda: u.normalize("NFC", 5))
            show("isnorm", lambda: (u.is_normalized("NFC", "é"), u.is_normalized("NFC", "é")))
            show("numeric", lambda: (u.decimal("1"), u.decimal("٣"), u.digit("1"), u.numeric("1"), u.numeric("½")))
            show("digit-super", lambda: u.digit("²"))
            show("decimal-err", lambda: u.decimal("a"))
            show("decimal-none", lambda: u.decimal("a", None))
            show("num-err", lambda: u.numeric("a"))
            show("arith", lambda: (u.decimal("1") + 1, u.numeric("½")))
            show("norm-hangul", lambda: (u.normalize("NFC", "가"), u.normalize("NFD", "가"), u.normalize("NFC", u.normalize("NFD", "각"))))
            show("norm-order", lambda: (u.normalize("NFC", "à́"), u.normalize("NFD", "ǻ")))
            show("norm-cjk", lambda: (u.normalize("NFKC", "①"), u.normalize("NFKD", "①"), u.normalize("NFKC", "㍿")))
            show("decomp-recursive", lambda: (u.decomposition("ǻ"), u.normalize("NFD", "ǻ"), u.normalize("NFC", u.normalize("NFD", "ǻ"))))
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "ver 16.0.0",
                "name ok 'LATIN CAPITAL LETTER A'",
                "name-default ok ('dflt', None)",
                "name-err ValueError no such name",
                "name-int TypeError name() argument 1 must be a unicode character, not int",
                "name-multi TypeError name(): argument 1 must be a unicode character, not a string of length 2",
                "lookup ok ('A', '☃', '\\x00')",
                "lookup-lower ok 'A'",
                "lookup-alias ok '\\n'",
                "lookup-seq ok '1️⃣'",
                "lookup-err KeyError \"undefined character name 'NOPE'\"",
                "category ok ['Lu', 'Nd', 'Zs', 'Ll', 'Mn', 'Lo', 'So', 'Cc']",
                "category-bad TypeError category() argument must be a unicode character, not int",
                "combining ok [0, 230, 230, 230]",
                "bidi ok ['L', 'EN', 'WS', 'R', 'AL', 'L', 'ON']",
                "eaw ok ['Na', 'W', 'A', 'W']",
                "mirrored ok [1, 0, 0]",
                "decomp ok ('', '0065 0301', '', '<compat> 0066 0069')",
                "norm ok ['é', 'é', 'é', 'é']",
                "norm2 ok ['ﬁ ', 'ﬁ ', 'fi ', 'fi ']",
                "norm-bad ValueError invalid normalization form",
                "norm-int TypeError normalize() argument 2 must be str, not int",
                "isnorm ok (True, True)",
                "numeric ok (1, 3, 1, 1.0, 0.5)",
                "digit-super ok 2",
                "decimal-err ValueError not a decimal",
                "decimal-none ok None",
                "num-err ValueError not a numeric character",
                "arith ok (2, 0.5)",
                "norm-hangul ok ('가', '가', '각')",
                "norm-order ok ('à́', 'ǻ')",
                "norm-cjk ok ('1', '1', '株式会社')",
                "decomp-recursive ok ('00E5 0301', 'ǻ', 'ǻ')",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The recorded gaps: the `UCD` and `ucd_3_2_0` objects are not published, and name
    /// lookup covers the explicit names, aliases and named sequences but not the
    /// algorithmic CJK and Hangul names (`name()` still answers them).
    /// </summary>
    [Fact]
    public void TheRuntimePinsItsOwnUnicodeDataGaps()
    {
        var output = Run(
            """"
            import unicodedata as u
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            print(sorted(n for n in dir(u) if not n.startswith("_")))
            show("ucd", lambda: u.UCD)
            show("ucd32", lambda: u.ucd_3_2_0)
            show("cjk-lookup", lambda: u.lookup("CJK UNIFIED IDEOGRAPH-4E00"))
            show("hangul-lookup", lambda: u.lookup("HANGUL SYLLABLE GA"))
            print("name-cjk", u.name("一"), u.name("가"))
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "['bidirectional', 'category', 'combining', 'decimal', 'decomposition', 'digit', 'east_asian_width', 'is_normalized', 'lookup', 'mirrored', 'name', 'normalize', 'numeric', 'unidata_version']",
                "ucd AttributeError module 'unicodedata' has no attribute 'UCD'",
                "ucd32 AttributeError module 'unicodedata' has no attribute 'ucd_3_2_0'",
                "cjk-lookup KeyError \"undefined character name 'CJK UNIFIED IDEOGRAPH-4E00'\"",
                "hangul-lookup KeyError \"undefined character name 'HANGUL SYLLABLE GA'\"",
                "name-cjk CJK UNIFIED IDEOGRAPH-4E00 HANGUL SYLLABLE GA",
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
}
