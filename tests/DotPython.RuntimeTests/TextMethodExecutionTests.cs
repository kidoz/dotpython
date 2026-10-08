using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TextMethodExecutionTests
{
    [Fact]
    public void PaddingAndZeroFillPlaceTheirExtraCharacter()
    {
        var output = Run(
            """
            print('abc'.center(7, '-'), 'abc'.center(6, '-'), 'abc'.center(2))
            print('abc'.ljust(5, '.'), 'abc'.rjust(5))
            print('abc'.zfill(5), '-abc'.zfill(6), '+abc'.zfill(6), 'abc'.zfill(2))
            print('a\U0001F600'.zfill(5), 'ab'.center(6, '\U0001F600'))
            """
        );

        Assert.Equal(
            Lines(
                "--abc-- -abc-- abc",
                "abc..   abc",
                "00abc -00abc +00abc abc",
                "000a\U0001F600 \U0001F600\U0001F600ab\U0001F600\U0001F600"
            ),
            output
        );
    }

    [Fact]
    public void TabsAndLinesFollowTheStrBoundaries()
    {
        var output = Run(
            """
            print(repr('a\tb'.expandtabs()), repr('a\tb'.expandtabs(4)), repr('a\tb'.expandtabs(0)))
            print(repr('a\tb\rc\td'.expandtabs(4)))
            print('a\nb\r\nc\rd\x0be\x0cf\x1cg\x1dh\x1ei\x85j\u2028k\u2029l'.splitlines())
            print('a\nb\r\nc'.splitlines(True))
            """
        );

        Assert.Equal(
            Lines(
                "'a       b' 'a   b' 'ab'",
                "'a   b\\rc   d'",
                "['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l']",
                "['a\\n', 'b\\r\\n', 'c']"
            ),
            output
        );
    }

    [Fact]
    public void SplittingAndPartitioningHandleSeparatorsAndBounds()
    {
        var output = Run(
            """
            print('a=b'.partition('='), 'abc'.partition('='), 'abc'.rpartition('='))
            print('a,b,c'.rsplit(',', 1), 'a,b,c'.rsplit(','), 'a b c'.rsplit(None, 1))
            print('  a  b  '.rsplit(), 'a b c'.rsplit(None, 0))
            print('abc'.removeprefix('a'), 'abc'.removesuffix('c'), 'abc'.removeprefix('z'))
            print('abcabc'.rfind('b'), 'abc'.rfind('z'), 'abcabc'.rindex('b'))
            """
        );

        Assert.Equal(
            Lines(
                "('a', '=', 'b') ('abc', '', '') ('', '', 'abc')",
                "['a,b', 'c'] ['a', 'b', 'c'] ['a b', 'c']",
                "['a', 'b'] ['a b c']",
                "bc ab abc",
                "4 -1 4"
            ),
            output
        );
    }

    [Fact]
    public void TranslationTablesCarryOrdinalsAndDeletions()
    {
        var output = Run(
            """
            print(str.maketrans({'a': 'b'}), str.maketrans('ab', 'xy'), str.maketrans('ab', 'xy', 'c'))
            print('abc'.translate({97: 'x'}), 'abc'.translate({97: None}), 'abc'.translate({97: 98}))
            print('abc'.translate({122: 'x'}), 'abc'.translate({97: 'xyz'}))
            """
        );

        Assert.Equal(
            Lines(
                "{97: 'b'} {97: 120, 98: 121} {97: 120, 98: 121, 99: None}",
                "xbc bc bbc",
                "abc xyzbc"
            ),
            output
        );
    }

    [Fact]
    public void PredicatesAnswerFromThePinnedProperties()
    {
        var output = Run(
            """
            print('abc'.isalpha(), 'a1'.isalpha(), ''.isalpha(), 'Ⅰ'.isalpha())
            print('123'.isdecimal(), '½'.isdecimal(), '²'.isdigit(), '½'.isnumeric())
            print('123'.isdigit(), 'Ⅷ'.isnumeric(), ' \t'.isspace(), '\xa0'.isspace())
            print('a b'.isprintable(), '\n'.isprintable(), ''.isprintable(), 'a\x7f'.isprintable())
            print('abc'.islower(), 'aBc'.islower(), '123'.islower(), 'ABC'.isupper())
            print('Abc'.istitle(), 'ABc'.istitle(), "They'Re".istitle(), '123'.istitle())
            print('def'.isidentifier(), '1a'.isidentifier(), '_a1'.isidentifier(), 'a b'.isidentifier())
            print('a'.isascii(), 'é'.isascii(), ''.isascii())
            """
        );

        Assert.Equal(
            Lines(
                "True False False False",
                "True False True True",
                "True True True True",
                "True False True False",
                "True False False True",
                "True False True False",
                "True False True False",
                "True False True"
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
