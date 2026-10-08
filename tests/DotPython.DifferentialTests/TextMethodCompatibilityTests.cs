using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TextMethodCompatibilityTests
{
    [Fact]
    public Task PaddingAndZeroFillPlaceTheirExtraCharacter() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print('abc'.center(7, '-'), 'abc'.center(6, '-'), 'abc'.center(2), 'abc'.center(2, '*'))
            print('abc'.ljust(5, '.'), 'abc'.rjust(5), ''.center(4), ''.ljust(3, 'x'))
            print('abc'.zfill(5), '-abc'.zfill(6), '+abc'.zfill(6), 'abc'.zfill(2), ''.zfill(3))
            print('a\U0001F600'.zfill(5), 'ab'.center(6, '\U0001F600'), 'ab'.ljust(5, 'é'))
            """
        );

    [Fact]
    public Task TabExpansionCountsCharactersAndResetsOnNewlines() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(repr('a\tb'.expandtabs()), repr('a\tb'.expandtabs(4)), repr('a\tb'.expandtabs(0)))
            print(repr('a\tb\rc\td'.expandtabs(4)), repr('\t\t'.expandtabs(3)))
            print(repr('ab\tc'.expandtabs(4)), repr('a\U0001F600\tb'.expandtabs(4)))
            """
        );

    [Fact]
    public Task SplitLinesAndReverseSplittingFollowTheirBoundaries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print('a\nb\r\nc\rd\x0be\x0cf\x1cg\x1dh\x1ei\x85j\u2028k\u2029l'.splitlines())
            print('a\nb\r\nc'.splitlines(True), ''.splitlines(), 'a\n'.splitlines())
            print('a,b,c'.rsplit(',', 1), 'a,b,c'.rsplit(','), 'a b c'.rsplit(None, 1))
            print('  a  b  '.rsplit(), 'a b c'.rsplit(None, 0), ' a '.rsplit())
            """
        );

    [Fact]
    public Task PartitioningAndAffixRemovalKeepTheirEdges() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print('a=b'.partition('='), 'abc'.partition('='), 'abc'.rpartition('='))
            print('=a'.partition('='), 'a='.rpartition('='), 'a=b=c'.rpartition('='))
            print('abc'.removeprefix('a'), 'abc'.removesuffix('c'), 'abc'.removeprefix('z'))
            print('abc'.removeprefix(''), 'abc'.removesuffix(''), 'ab'.removeprefix('abc'))
            """
        );

    [Fact]
    public Task ReverseSearchFindsTheLastMatchWithinBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print('abcabc'.rfind('b'), 'abc'.rfind('z'), 'abcabc'.rfind('a', 0, 2))
            print('abcabc'.rfind(''), 'abcabc'.rfind('', 0, 0), 'abcabc'.rindex('c', 0, 3))
            print('aaaa'.rfind('aa'), 'aaaa'.rfind('aa', 0, 3), 'abc'.find('b'), 'abc'.index('b'))
            """
        );

    [Fact]
    public Task TranslationTablesCarryOrdinalsDeletionsAndText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(str.maketrans({'a': 'b'}), str.maketrans('ab', 'xy'), str.maketrans('ab', 'xy', 'c'))
            print(str.maketrans({97: 98}), str.maketrans({'a': None}), str.maketrans({'a': 98}))
            print('abc'.translate({97: 'x'}), 'abc'.translate({97: None}), 'abc'.translate({97: 98}))
            print('abc'.translate({122: 'x'}), 'abc'.translate({97: 'xyz'}), ''.translate({97: 'x'}))
            print('abc'.translate(str.maketrans('ab', 'xy', 'c')))
            """
        );

    [Fact]
    public Task PredicatesAnswerFromThePinnedUnicodeProperties() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            samples = ['', 'a', 'A', '1', 'abc', 'a1', 'a b', '_', 'Ⅰ', 'Ⅷ', 'ß', 'ǅ', '²', '½',
                       '１２３', '١٢٣', '一二三', 'Ⅻ', '\t', ' ', '\u2028', '　', '\n',
                       '\x7f', '\x1c', 'é', 'ﬁ', 'α', 'あ', 'def', '1a', '_a1', "They'Re", 'a-b']
            for s in samples:
                print(repr(s), s.isalpha(), s.isdecimal(), s.isdigit(), s.isnumeric(), s.isspace(),
                      s.isprintable(), s.isascii(), s.islower(), s.isupper(), s.istitle(),
                      s.isidentifier(), s.isalnum())
            """
        );

    [Fact]
    public Task MethodErrorsMatchCPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(thunk())
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: 'abc'.center(10, 'xy'))
            probe(lambda: 'abc'.center(10, ''))
            probe(lambda: 'abc'.ljust(1, 'xy'))
            probe(lambda: 'abc'.partition(''))
            probe(lambda: 'abc'.rpartition(''))
            probe(lambda: 'abc'.rsplit(''))
            probe(lambda: 'abc'.rindex('z'))
            probe(lambda: str.maketrans('ab', 'xyz'))
            probe(lambda: str.maketrans({'ab': 'x'}))
            probe(lambda: str.maketrans({'': 'x'}))
            probe(lambda: str.maketrans({1.5: 'x'}))
            probe(lambda: str.maketrans(['a']))
            probe(lambda: 'abc'.translate({97: 1.5}))
            """
        );
}
