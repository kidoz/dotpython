using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `unicodedata` module against CPython 3.14 / UCD 16: every published query and all
/// four normalization forms over composed, decomposed, compatibility, Hangul and astral
/// code points.
/// </summary>
public sealed class UnicodeDataModuleCompatibilityTests
{
    [Fact]
    public Task TheUnicodeDataModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
