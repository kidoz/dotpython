using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// Named escapes that resolve. Rejected names, malformed escapes and named sequences
/// are compile-time errors the oracle harness cannot express, so they are covered by
/// the paired execution tests instead.
/// </summary>
public sealed class NamedEscapeCompatibilityTests
{
    [Fact]
    public Task NamedEscapesResolveOrdinaryNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print("\N{SNOWMAN}")
            print(len("\N{SNOWMAN}"), "\N{SNOWMAN}\N{SNOWMAN}")
            print(repr("\N{GRINNING FACE}"), len("\N{GRINNING FACE}"))
            print("\N{LATIN SMALL LETTER A}", "\N{DEGREE SIGN}")
            print("\N{SNOWMAN}" in "a\N{SNOWMAN}b", "\N{SNOWMAN}" == "☃")
            """
        );

    [Fact]
    public Task NamedEscapeMatchingIsCaseInsensitive() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print("\N{snowman}", "\N{SNOWMAN}", "\N{SnowMan}", "\N{SnOwMaN}")
            print("\N{latin small letter a}", "\N{greek small letter alpha}")
            """
        );

    [Fact]
    public Task AliasesAndAlgorithmicNamesResolve() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(repr("\N{LF}"), repr("\N{NUL}"), repr("\N{BYTE ORDER MARK}"))
            print(repr("\N{CJK UNIFIED IDEOGRAPH-4E00}"))
            print(repr("\N{cjk unified ideograph-4e00}"))
            print(repr("\N{HANGUL SYLLABLE GA}"))
            print(repr("\N{TANGUT IDEOGRAPH-17000}"))
            """
        );

    [Fact]
    public Task NamedEscapesWorkInFormattedAndTemplateStrings() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            x = 1
            print(f"a\N{SNOWMAN}b{x}")
            print(f"\N{SNOWMAN}{1 + 1}")
            print(f"\N{SNOWMAN}{{literal}}")
            print(t"a\N{SNOWMAN}b")
            print(r"\N{SNOWMAN}")
            """
        );

    [Fact]
    public Task DecodedNamedEscapesBehaveAsOrdinaryText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            snowman = "\N{SNOWMAN}"
            print(ord(snowman), snowman.encode("utf-8"), len(snowman))
            print("\N{SNOWMAN}".encode("ascii", "namereplace"))
            print([c for c in "\N{SNOWMAN}"], "\N{SNOWMAN}".upper() == "\N{SNOWMAN}")
            print(len("\N{GRINNING FACE}".encode("utf-8")))
            """
        );
}
