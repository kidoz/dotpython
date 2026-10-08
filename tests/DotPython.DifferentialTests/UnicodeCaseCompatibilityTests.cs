using Xunit;

namespace DotPython.DifferentialTests;

public sealed class UnicodeCaseCompatibilityTests
{
    [Fact]
    public Task FullCaseMappingExpandsWhereCPythonExpands() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print('ß'.upper(), 'MAßE'.upper(), 'ﬁ'.upper(), 'ﬄ'.upper())
            print('ß'.casefold(), 'ẞ'.casefold(), 'K'.casefold(), 'ﬁ'.casefold())
            print('İ'.lower(), 'ǅ'.upper(), 'ǅ'.lower(), 'ǅ'.title())
            print('ΣΣ'.lower(), 'Σ'.lower(), 'ΟΔΟΣ'.lower(), 'ΟΔΟΣ'.upper())
            print('ﬀ'.upper(), 'ŉ'.upper(), 'ǰ'.upper(), 'ΐ'.upper())
            """
        );

    [Fact]
    public Task FinalSigmaFollowsTheCasedNeighbourRule() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for text in ('Σ', 'ΣΣ', 'ΣΣΣ', 'ΑΣ', 'ΑΣΒ', 'ΑΣ ', 'ΑΣ.', ' ΑΣ',
                         'ΣΑ', 'ΑΣ́', 'ΑΣ́Β', 'ΆΣ', "'Σ", "'Σ'",
                         'ΑΣ1', '1Σ', 'ΑΣ_', 'ΑΣΑ', 'ΣΣΣΣ'):
                print(repr(text), '->', text.lower())
            print('ΟΔΟΣ'.lower(), 'ΟΔΟΣ'.title(), 'ΟΔΟΣ'.capitalize(), 'ΟΔΟΣ'.casefold())
            """
        );

    [Fact]
    public Task TitleAndSwapCaseUseTheUnicodeMappings() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print('they\'re'.title(), 'x-y'.title(), 'hello world'.title())
            print('ß'.title(), 'ǅ'.title(), 'ǆ'.title(), 'ǳ'.title())
            print('aBc dEf'.swapcase(), 'ß'.swapcase(), 'ǅ'.swapcase(), 'ǆ'.swapcase())
            print('ﬁX'.swapcase(), 'ΑΣ'.swapcase(), '123 abc'.title())
            print('ǅungla'.title(), 'ǅungla'.capitalize(), 'ǅ'.capitalize())
            """
        );

    [Fact]
    public Task NonAsciiScriptsMapThroughThePinnedTables() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print('ĳsselmeer'.upper(), 'ĲSSELMEER'.lower())
            print('ᏣᎳᎩ'.upper(), 'ᎠᏂᏴᏫ'.lower())
            print('𐐀𐐨'.lower(), '𐐨'.upper())
            print('ⰀⰁ'.lower(), 'ⰰⰱ'.upper())
            print('ᲐᲑ'.lower(), 'ა'.upper())
            print('Ⲁⲁ'.upper(), 'Ⲁ'.lower())
            print('ª'.upper(), 'º'.lower(), 'ǅ'.swapcase())
            """
        );

    [Fact]
    public Task CaseOperationsRoundTripAndCompose() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            samples = ['', 'a', 'A', 'ä', 'Ä', 'ß', 'ﬁ', 'Σ', 'ς', 'ΣΣ', 'ǅ', 'ǆ', 'ǳ',
                       'İ', 'ı', 'ſ', 'ᾀ', 'ΐ', 'ﬅ', '𐐨', 'ᏣᎳᎩ', 'hello WORLD 123',
                       'Ə', 'ə', 'Ა', 'ᲐᲑ', 'K', 'K', 'Å', 'å', 'K']
            for text in samples:
                print(repr(text.upper()), repr(text.lower()), repr(text.title()))
                print(repr(text.capitalize()), repr(text.swapcase()), repr(text.casefold()))
            """
        );
}
