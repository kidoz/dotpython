using Xunit;

namespace DotPython.DifferentialTests;

public sealed class NamedEncodingReplacementCompatibilityTests
{
    [Fact]
    public Task AsciiReplacesOrdinaryNamedCharactersWithoutChangingRepresentableText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'AéΩ€中Z'
            print(source.encode('ascii', 'namereplace'))
            print(bytes(source, 'ascii', 'namereplace'))
            print(source)
            """
        );

    [Fact]
    public Task LatinOneOnlyNamesCharactersOutsideItsRepresentableRange() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A' + chr(0x80) + 'éÿΩ€中Z'
            print(source.encode('latin1', 'namereplace'))
            print(bytes(source, 'latin1', 'namereplace'))
            """
        );

    [Fact]
    public Task SupplementaryCharactersUseTheirNamesRatherThanSurrogateUnitNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0x10000, 0x10300, 0x10400, 0x1d11e, 0x1d400, 0x1f600, 0x1f680):
                source = chr(point)
                print(point, source.encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task NewUnicodeSixteenScriptsAndSymbolsHavePinnedNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0x105c0, 0x11380, 0x11bf0, 0x16100, 0x16d40, 0x1e5d0, 0x1fa89, 0x1fa8f):
                print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task HangulSyllablesComposeInitialVowelAndFinalNamesAtBoundaries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0xabff, 0xac00, 0xac01, 0xac1b, 0xac1c, 0xac38, 0xae4b, 0xae4c, 0xb098, 0xc544, 0xd788, 0xd7a2, 0xd7a3, 0xd7a4):
                print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task BasicAndExtensionACjkIdeographsUseUppercaseHexadecimalNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for start, stop in ((0x3400, 0x4dbf), (0x4e00, 0x9fff)):
                for point in (start - 1, start, start + 1, stop - 1, stop, stop + 1):
                    print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task SupplementaryCjkExtensionBoundariesMatchThePinnedDatabase() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for start, stop in ((0x20000, 0x2a6df), (0x2a700, 0x2b739), (0x2b740, 0x2b81d), (0x2b820, 0x2cea1), (0x2ceb0, 0x2ebe0), (0x2ebf0, 0x2ee5d), (0x30000, 0x3134a), (0x31350, 0x323af)):
                for point in (start - 1, start, start + 1, stop - 1, stop, stop + 1):
                    print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task CompatibilityIdeographsRetainTheirDistinctNamesAndUnassignedGaps() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0xf900, 0xfa6d, 0xfa6e, 0xfa70, 0xfad9, 0xfada, 0x2f800, 0x2fa1d, 0x2fa1e):
                print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task TangutIdeographsRemainUnnamedInPythonThreeFourteen() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0x16fff, 0x17000, 0x17001, 0x187f7, 0x187f8, 0x18800, 0x18aff, 0x18d00, 0x18d08, 0x18d09):
                print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task NushuAndKhitanNamesRespectAssignedRangeBoundaries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0x1b16f, 0x1b170, 0x1b171, 0x1b2fa, 0x1b2fb, 0x1b2fc, 0x18aff, 0x18b00, 0x18b01, 0x18cd4, 0x18cd5, 0x18cd6):
                print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task ControlAliasesAreNotSubstitutedForMissingCanonicalNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = ''.join(chr(point) for point in (0, 9, 10, 13, 0x7f, 0x80, 0x85, 0x8e, 0x9f))
            print(source.encode('ascii', 'namereplace'))
            print(list(source.encode('latin1', 'namereplace')))
            """
        );

    [Fact]
    public Task UnassignedPrivateUseAndNoncharactersUseWidthSpecificBackslashEscapes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0x378, 0x380, 0xe000, 0xf8ff, 0xfdd0, 0xfdef, 0xfffe, 0xffff, 0x1000c, 0x1fffe, 0xf0000, 0xffffd, 0x100000, 0x10fffd, 0x10ffff):
                print(point, chr(point).encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task LoneSurrogatesUseLowercaseFourDigitEscapes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0xd800, 0xdbff, 0xdc00, 0xdc80, 0xdcff, 0xdfff):
                for encoding in ('ascii', 'latin1', 'utf8'):
                    print(point, encoding, chr(point).encode(encoding, 'namereplace'))
            """
        );

    [Fact]
    public Task NamedCombiningCharactersAreNotNormalizedBeforeReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ('é', 'e' + chr(0x301), chr(0x212b), 'A' + chr(0x30a), chr(0x200d), chr(0xfe0f), chr(0xe0100)):
                print(source.encode('ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task LiteralNameEscapeSyntaxAndBackslashesRemainRepresentableData() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = '\\N{FAKE NAME} \\u00e9 ' + 'é' + ' {Ω} \\end'
            print(source.encode('ascii', 'namereplace'))
            print(bytes(source, encoding='ascii', errors='namereplace'))
            """
        );

    [Fact]
    public Task Utf8PreservesNamedCharactersAndEscapesOnlyInvalidSurrogates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'é😀' + chr(0xd800) + '中' + chr(0xdcff) + 'Z'
            value = source.encode('utf8', 'namereplace')
            print(list(value))
            print(value == bytes(source, 'utf8', 'namereplace'))
            print(len(source), source.encode('utf8', 'surrogatepass'))
            """
        );

    [Fact]
    public Task Utf16EncodesReplacementTextInTheRequestedEndianWithOneNativeBom() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A' + chr(0xd800) + '😀' + chr(0xdcff) + 'Z'
            for encoding in ('utf16', 'utf-16-le', 'utf-16-be'):
                value = source.encode(encoding, 'namereplace')
                print(encoding, list(value), value == bytes(source, encoding, 'namereplace'))
            """
        );

    [Fact]
    public Task EmptyAndEntirelyInvalidUtf16SourcesHaveTheCorrectPreamble() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding in ('utf16', 'utf-16-le', 'utf-16-be'):
                for source in ('', chr(0xd800), chr(0xdc00) + chr(0xdcff), chr(0xfeff) + chr(0xdfff)):
                    print(encoding, list(source.encode(encoding, 'namereplace')))
            """
        );

    [Fact]
    public Task NamedReplacementKeywordsAndNormalizedCodecAliasesShareTheSameResults() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'éΩ' + chr(0xd800)
            print(source.encode(errors='namereplace'))
            print(source.encode(errors='namereplace', encoding='ASCII'))
            print(bytes(errors='namereplace', source=source, encoding='LaTiN_1'))
            print(bytes(source, errors='namereplace', encoding='utf 8'))
            """
        );

    [Fact]
    public Task HandlerLookupRemainsLazyAndCaseSensitive() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for handler in ('namereplace', 'NameReplace', 'NAMEREPLACE', 'name_replace', ' namereplace '):
                print(handler, list('ABC'.encode('ascii', handler)))
                try: print('é'.encode('ascii', handler))
                except LookupError as error: print(str(error))
            """
        );

    [Fact]
    public Task SavedEncodeMethodsPreserveTheirSourceAcrossNamedAndUnnamedRuns() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'Aé' + chr(0x378) + '😀' + chr(0xd800) + 'Z'
            saved = source.encode
            original = source
            for encoding in ('ascii', 'latin1', 'utf8'):
                print(saved(encoding, 'namereplace'))
                print(source is original, source.encode('utf8', 'surrogatepass'))
            """
        );

    [Fact]
    public Task LongMixedReplacementRunsPreserveOrderAndConstructorParity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            unit = 'é' + chr(0x378) + '😀' + chr(0xe000) + '中'
            source = 'start:' + unit * 128 + ':end'
            for encoding in ('ascii', 'latin1'):
                value = source.encode(encoding, 'namereplace')
                print(encoding, len(value), value[:100], value[-100:])
                print(value == bytes(source, encoding, 'namereplace'))
            print(len(source), source[:6], source[-4:])
            """
        );

    [Fact]
    public Task InternalAliasPseudoEntriesHavePinnedNamesAndPrivateUseBoundaries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0xeffff, 0xf0000, 0xf0001, 0xf0002, 0xf01db, 0xf01dc, 0xf01dd, 0xf01ff, 0xf03cd, 0xffffd, 0x100000):
                source = chr(point)
                value = source.encode('ascii', 'namereplace')
                print(point, value, value == bytes(source, 'ascii', 'namereplace'))
            """
        );

    [Fact]
    public Task InternalNamedSequencePseudoEntriesUseTheNameWithoutExpandingTheSequence() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0xf01ff, 0xf0200, 0xf0201, 0xf0202, 0xf03cb, 0xf03cc, 0xf03cd):
                print(point, chr(point).encode('ascii', 'namereplace'))
            source = 'A' + chr(0xf0200) + '#' + chr(0xfe0f) + chr(0x20e3) + chr(0xf0000) + chr(0xf03cc) + 'Z'
            print(source.encode('ascii', 'namereplace'))
            print(source.encode('latin1', 'namereplace'))
            print([ord(value) for value in source])
            """
        );
}
