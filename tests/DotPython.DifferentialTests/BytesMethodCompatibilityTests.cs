using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BytesMethodCompatibilityTests
{
    [Fact]
    public Task CaseMappingIsAsciiOnly() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'aBc dEf'.upper(), b'aBc dEf'.lower())
            print(b'aBc dEf'.capitalize(), b'aBc dEf'.title())
            print(b'aBc dEf g-h2i'.title(), b'aBc dEf'.swapcase())
            print(b'caf\xe9'.upper(), b'caf\xe9'.lower())
            print(b''.upper(), b''.title(), b''.capitalize(), b''.swapcase())
            print(b'123 abc'.title(), b'MIXED case 99'.swapcase())
            """
        );

    [Fact]
    public Task StrippingUsesTheByteWhitespaceSet() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'  ab  '.strip(), b'  ab  '.lstrip(), b'  ab  '.rstrip())
            print(b'\tab\n '.strip(), b'\x0bab\x0c'.strip(), b'\r\nab\r\n'.strip())
            print(b'xxabxx'.strip(b'x'), b'xxabxx'.lstrip(b'xy'), b'xxabxx'.rstrip(b'y'))
            print(b''.strip(), b'   '.strip(), b'abc'.strip(b'abc'))
            """
        );

    [Fact]
    public Task SearchAndCountAcceptByteValuesAndBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'abcabc'.find(b'b'), b'abc'.find(b'z'), b'abcabc'.rfind(b'b'))
            print(b'abcabc'.find(b'a', 1), b'abcabc'.find(b'a', 1, 3), b'abcabc'.find(b'c', 3))
            print(b'abc'.find(98), b'abc'.find(97), b'abca'.count(97))
            print(b'abc'.find(b''), b'abc'.find(b'', 2), b'abc'.count(b''))
            print(b'abcabc'.count(b'a'), b'aaa'.count(b'a', 1, 2), b'abc'.count(b'z'))
            print(b'abcabc'.rfind(b'', 0, 3), b'abcabc'.rfind(b'a', 0, 2))
            """
        );

    [Fact]
    public Task AffixTestsTakeBytesTuplesAndBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'abc'.startswith(b'ab'), b'abc'.endswith(b'bc'))
            print(b'abc'.startswith((b'z', b'a')), b'abc'.endswith((b'c', b'z')))
            print(b'abc'.startswith(b'b', 1), b'abc'.startswith(b'c', 1, -1))
            print(b'abc'.endswith(b'a', 0, 1), b'abc'.startswith(b'', 1))
            print(b''.startswith(b''), b'abc'.endswith(b'', 1, 2))
            """
        );

    [Fact]
    public Task ReplacementKeepsCountsAndEmptyPatterns() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'abcabc'.replace(b'a', b'X'), b'abcabc'.replace(b'a', b'X', 1))
            print(b'aaa'.replace(b'a', b'bb'), b'a'.replace(b'a', b''))
            print(b'abc'.replace(b'', b'-'), b'abc'.replace(b'', b'-', 0))
            print(b'abc'.replace(b'', b'-', 1), b'abc'.replace(b'', b'-', 2))
            print(b''.replace(b'', b'-'), b'abc'.replace(b'z', b'y'))
            """
        );

    [Fact]
    public Task SplittingAndJoiningFollowTheSeparatorRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'a b  c'.split(), b'a b  c'.split(None, 1), b'a b c'.split(None, 0))
            print(b'a,b,,c'.split(b','), b'a,b,c'.split(b',', 1), b'a,b,c'.rsplit(b',', 1))
            print(b'a b c'.rsplit(None, 1), b'  a  b  '.split(), b'  a  b  '.rsplit())
            print(b''.split(), b''.split(b','), b'abc'.split(b'abc'))
            print(b'a\nb\r\nc'.splitlines(), b'a\nb\r\nc'.splitlines(True))
            print(b'a\n'.splitlines(), b''.splitlines(), b'\n\n'.splitlines())
            print(b','.join([b'a', b'b']), b','.join([]), b''.join([b'a', b'b']))
            """
        );

    [Fact]
    public Task PartitioningPadsOnTheSideThatDidNotSplit() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'a=b'.partition(b'='), b'abc'.partition(b'='))
            print(b'a=b=c'.rpartition(b'='), b'abc'.rpartition(b'='))
            print(b'=a'.partition(b'='), b'a='.partition(b'='), b''.partition(b'='))
            print(b'a'.partition(b'a'), b'a'.rpartition(b'a'))
            """
        );

    [Fact]
    public Task PaddingAndZeroFillMatchCPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'abc'.center(7, b'-'), b'abc'.center(6, b'-'), b'abc'.center(2))
            print(b'abc'.ljust(5, b'.'), b'abc'.rjust(5, b'.'), b'abc'.rjust(5))
            print(b'abc'.ljust(6, b'-'), b'abc'.rjust(7, b'-'))
            print(b'abc'.zfill(5), b'-abc'.zfill(5), b'+abc'.zfill(6), b'abc'.zfill(2))
            print(b''.zfill(3), b'-'.zfill(3), b'a\tb'.expandtabs(), b'a\tb'.expandtabs(4))
            print(b'a\tb'.expandtabs(0), b'a\tb\nc\td'.expandtabs(4), b'\t'.expandtabs(3))
            """
        );

    [Fact]
    public Task PrefixAndSuffixRemovalIsExact() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'abc'.removeprefix(b'a'), b'abc'.removeprefix(b'z'))
            print(b'abc'.removesuffix(b'c'), b'abc'.removesuffix(b'z'))
            print(b'abc'.removeprefix(b'abc'), b'abc'.removesuffix(b'abc'))
            print(b'ab'.removeprefix(b'abc'), b'ab'.removesuffix(b'abc'))
            print(b''.removeprefix(b''), b''.removesuffix(b''))
            """
        );

    [Fact]
    public Task HexRoundTripsThroughFromHex() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(b'ab'.hex(), b'abcd'.hex(':'), b''.hex(), b'\x00\xff'.hex())
            print(bytes.fromhex('41 42'), bytes.fromhex('4142'), bytes.fromhex('4A'))
            print(bytes.fromhex('  41'), bytes.fromhex('41  '), bytes.fromhex('41\t42'))
            print(bytes.fromhex(''), bytes.fromhex('  '), bytes.fromhex(b'41 42'))
            print(bytes.fromhex(b'ab'.hex()), bytes.fromhex(b'abcd'.hex()))
            """
        );

    [Fact]
    public Task TranslateAppliesARoundTrippedTable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            table = bytes.maketrans(b'ab', b'xy')
            print(b'abc'.translate(table), len(table), table[97:100])
            print(b'abc'.translate(bytes.maketrans(b'', b'')), b''.translate(table))
            print(b'abc'.translate(table, b'b'))
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

            probe(lambda: b'abc'.index(b'z'))
            probe(lambda: b'abc'.rindex(b'z'))
            probe(lambda: b'abc'.find('a'))
            probe(lambda: b'abc'.startswith(1))
            probe(lambda: b'abc'.split(b''))
            probe(lambda: b'abc'.partition(b''))
            probe(lambda: b','.join([b'a', 'b']))
            probe(lambda: b'abc'.translate(b'short'))
            probe(lambda: b'abc'.center(5, b''))
            probe(lambda: bytes.fromhex('zz'))
            probe(lambda: bytes.fromhex('abc'))
            probe(lambda: bytes.fromhex('41 4 2'))
            probe(lambda: bytes.fromhex('4z'))
            probe(lambda: bytes.fromhex('0x41'))
            probe(lambda: bytes.fromhex(5))
            probe(lambda: bytes.maketrans(b'ab', b'xyz'))
            probe(lambda: bytes.maketrans('ab', 'xy'))
            probe(lambda: b'abc'.ljust(6, b'ab'))
            probe(lambda: b'abc'.rjust(7, b'ab'))
            probe(lambda: b'abc'.center(7, b'ab'))
            probe(lambda: b'abc'.ljust(6, b''))
            probe(lambda: b'abc'.ljust(2, b'ab'))
            probe(lambda: bytes.fromhex(b'abcd'.hex(':')))
            probe(lambda: b'abc'.find(300))
            probe(lambda: b'abc'.count(300))
            """
        );

    [Fact]
    public Task ArgumentDiagnosticsMatchCPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: b'a'.count('x'))
            probe(lambda: b'a'.startswith('x'))
            probe(lambda: b'a'.strip(1))
            probe(lambda: b'a'.split(1))
            probe(lambda: b'a'.rsplit(1))
            probe(lambda: b'a'.partition(1))
            probe(lambda: b'a'.rpartition(1))
            probe(lambda: b'a'.removeprefix(1))
            probe(lambda: b'a'.removesuffix(None))
            probe(lambda: b'a'.replace(1, b'b'))
            probe(lambda: b'a'.translate('x'))
            probe(lambda: b'a'.center(b'x'))
            probe(lambda: b'a'.center(5, 1))
            probe(lambda: b'a'.center(-1, b'ab'))
            probe(lambda: b'a'.ljust(8, 42))
            probe(lambda: b'a'.rjust(9, b''))
            probe(lambda: b'a'.expandtabs(None))
            probe(lambda: b'a'.expandtabs(b'x'))
            probe(lambda: b'a'.zfill(b'x'))
            probe(lambda: b'a'.join([1]))
            probe(lambda: b'a'.count(b'a', 'x'))
            probe(lambda: b'ab'.hex(',', 1, 2))
            probe(lambda: b'ab'.hex(1))
            probe(lambda: b'a'.decode(5))
            """
        );
}
