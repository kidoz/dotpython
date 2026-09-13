using Xunit;

namespace DotPython.DifferentialTests;

public sealed class EncodingErrorHandlerCompatibilityTests
{
    [Fact]
    public Task UnknownHandlersAreNotLookedUpForValidOrEmptyText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding in ('ascii', 'latin1', 'utf8', 'utf16', 'utf-16-le', 'utf-16-be'):
                for source in ('', 'ABC'):
                    print(encoding, list(source.encode(encoding, 'missing-handler')))
                    print(list(bytes(source, encoding, 'missing-handler')))
                print(list(source.encode(encoding, 'namereplace')))
            """
        );

    [Fact]
    public Task UnknownHandlersFailOnlyWhenTheCodecFindsUnencodableText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding in ('ascii', 'latin1', 'utf8', 'utf16', 'utf-16-le', 'utf-16-be'):
                source = 'A€Z' if encoding in ('ascii', 'latin1') else 'A' + chr(0xd800) + 'Z'
                for convert in (lambda: source.encode(encoding, 'missing-handler'), lambda: bytes(source, encoding, 'missing-handler')):
                    try: convert()
                    except LookupError as error: print(str(error))
            """
        );

    [Fact]
    public Task HandlerNamesRemainCaseSensitiveAndAreNotNormalized() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for handler in ('STRICT', 'Ignore', ' replace ', '', 'backslash_replace'):
                print(list('A'.encode('ascii', handler)))
                try: 'é'.encode('ascii', handler)
                except LookupError as error: print(str(error))
            """
        );

    [Fact]
    public Task EmbeddedNullHandlerNamesAreRejectedBeforeScanningEvenEmptySources() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ('', 'A', 'é'):
                for handler in ('strict' + chr(0), chr(0) + 'ignore', 'unknown' + chr(0)):
                    for convert in (lambda: source.encode('ascii', handler), lambda: bytes(source, 'ascii', handler)):
                        try: convert()
                        except ValueError as error: print(str(error))
            """
        );

    [Fact]
    public Task EncodingLookupAndEmbeddedNullValidationHaveConsistentPrecedence() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ('', 'A', 'é'):
                for encoding, handler in (('unknown-codec', 'missing-handler'), ('unknown-codec', 'strict' + chr(0)), ('ascii' + chr(0), 'missing-handler')):
                    for convert in (lambda: source.encode(encoding, handler), lambda: bytes(source, encoding, handler)):
                        try: convert()
                        except (LookupError, ValueError) as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task AsciiIgnoreAndReplaceHandleEveryInvalidCodePointInTheRun() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ('Aé€😀Z', 'é€😀', 'AéB€C😀D', chr(0xd800) + 'A' + chr(0xdcff)):
                for handler in ('ignore', 'replace'):
                    print(source.encode('ascii', handler))
            """
        );

    [Fact]
    public Task AsciiBackslashReplacementChoosesEscapesByUnicodeCodePointWidth() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A' + chr(128) + chr(255) + chr(256) + '€😀' + chr(0x10ffff) + chr(0xd800) + 'Z'
            print(source.encode('ascii', 'backslashreplace'))
            print(bytes(source, 'ascii', 'backslashreplace'))
            """
        );

    [Fact]
    public Task AsciiXmlReplacementUsesDecimalReferencesForEveryInvalidCodePoint() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'Aé€😀' + chr(0xd800) + chr(0xd801) + 'Z'
            print(source.encode('ascii', 'xmlcharrefreplace'))
            print(bytes(source, 'ascii', 'xmlcharrefreplace'))
            """
        );

    [Fact]
    public Task ReplacementDoesNotEscapeAlreadyRepresentableSlashesOrReferenceText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = chr(92) + 'u20ac &#8364; <>&é'
            for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                print(source.encode('ascii', handler))
            """
        );

    [Fact]
    public Task LatinOnePreservesRepresentableHighBytesAndReplacesOnlyInvalidPoints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'Aéÿ€😀' + chr(0xd800) + 'Z'
            for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                print(list(source.encode('ISO-8859-1', handler)))
            """
        );

    [Fact]
    public Task Utf8IgnoreAndReplaceSkipOrReplaceEveryLoneSurrogate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ('A' + chr(0xd800) + 'B', chr(0xd800) + chr(0xd801), '😀' + chr(0xdcff) + 'é', chr(0xdc00) + 'X' + chr(0xdfff)):
                for handler in ('ignore', 'replace'):
                    print(list(source.encode('utf8', handler)))
            """
        );

    [Fact]
    public Task Utf8EscapedReplacementPreservesValidMultibyteTextAroundSurrogates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'é😀' + chr(0xd800) + chr(0xd801) + 'Z'
            for handler in ('backslashreplace', 'xmlcharrefreplace'):
                print(list(source.encode('UTF_8', handler)))
            """
        );

    [Fact]
    public Task Utf16IgnoreAndReplaceEmitTheNativeBomAndEncodeQuestionMarksInTargetEndian() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A😀' + chr(0xd800) + 'B' + chr(0xdcff)
            for encoding in ('utf16', 'utf-16-le', 'utf-16-be'):
                for handler in ('ignore', 'replace'):
                    print(encoding, handler, list(source.encode(encoding, handler)))
            """
        );

    [Fact]
    public Task Utf16EscapedReplacementTextIsEncodedInTheRequestedEndian() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A' + chr(0xd800) + chr(0xd801) + 'B'
            for encoding in ('utf16', 'utf-16-le', 'utf-16-be'):
                for handler in ('backslashreplace', 'xmlcharrefreplace'):
                    print(encoding, handler, list(source.encode(encoding, handler)))
            """
        );

    [Fact]
    public Task Utf16EmitsOnePreambleAcrossMultipleReplacementRunsIncludingAllInvalidInput() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in (chr(0xd800) + 'A' + chr(0xdcff) + 'B' + chr(0xd801), chr(0xd800) + chr(0xd801), ''):
                for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                    value = source.encode('utf16', handler)
                    print(list(value))
            """
        );

    [Fact]
    public Task ExistingBomCharactersRemainDataBesidesTheNativeUtf16Preamble() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = chr(0xfeff) + 'A' + chr(0xd800) + chr(0xfeff)
            for encoding in ('utf16', 'utf-16-le', 'utf-16-be'):
                for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                    print(list(source.encode(encoding, handler)))
            """
        );

    [Fact]
    public Task EncodeAndBytesConstructorAgreeAcrossSupportedCodecsAndHandlers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding in ('ascii', 'latin1', 'utf8', 'utf16', 'utf-16-le', 'utf-16-be'):
                source = 'Aé€😀' + chr(0xd800) + 'Z'
                for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                    first = source.encode(encoding, handler)
                    second = bytes(source, encoding, handler)
                    print(first == second, list(first))
            """
        );

    [Fact]
    public Task KeywordFormsShareTheSameRecoveryAndLazyLookupRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'AéZ'
            print(source.encode(encoding='ascii', errors='backslashreplace'))
            print(source.encode('ascii', errors='xmlcharrefreplace'))
            print(bytes(source=source, encoding='ascii', errors='ignore'))
            print(bytes(source, 'ascii', errors='replace'))
            print('ABC'.encode(errors='missing-handler'))
            print(bytes(source='ABC', encoding='ascii', errors='missing-handler'))
            """
        );

    [Fact]
    public Task SavedEncodeMethodsKeepTheirSourceAndDoNotChangeItDuringRecovery() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'Aé€😀Z'
            alias = source
            encode = source.encode
            source = 'changed'
            for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                print(encode('ascii', handler))
            print(source, alias)
            """
        );

    [Fact]
    public Task StrictErrorsRemainStructuredAfterSuccessfulRecoveryCalls() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'Aé€😀Z'
            for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                source.encode('ascii', handler)
            target = 'original'
            try: target = source.encode('ascii', 'strict')
            except UnicodeEncodeError as error:
                print(target, error.encoding, error.start, error.end, error.reason, error.object is source)
                print(str(error))
            """
        );

    [Fact]
    public Task UnknownHandlerFailuresDoNotPublishPartialOutputOrChangeTheSource() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'ABCé'
            alias = source
            target = 'original'
            try: target = source.encode('ascii', 'missing-handler')
            except LookupError as error: print(str(error), target, source is alias)
            print(source.encode('ascii', 'backslashreplace'))
            """
        );

    [Fact]
    public Task LongReplacementRunsPreserveCountsAndSurroundingRepresentableText() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'start:' + ('é😀' * 100) + ':end'
            for handler in ('ignore', 'replace', 'backslashreplace', 'xmlcharrefreplace'):
                value = source.encode('ascii', handler)
                print(len(value), value[:20], value[-20:])
            """
        );

    [Fact]
    public Task SurrogatePassEncodesLoneSurrogatesInUtf8AndEitherUtf16Endian() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source = 'A' + chr(0xd800) + 'B' + chr(0xdcff) + chr(0xdc80) + '😀'
            for encoding in ('utf8', 'utf16', 'utf-16-le', 'utf-16-be'):
                value = source.encode(encoding, 'surrogatepass')
                print(encoding, list(value), value == bytes(source, encoding, 'surrogatepass'))
            """
        );

    [Fact]
    public Task SurrogatePassReraisesOriginalStructuredErrorsForAsciiAndLatinOne() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding, source in (('ascii', 'Aé€' + chr(0xd800) + 'Z'), ('latin1', 'A€😀' + chr(0xdc80) + 'Z')):
                try: source.encode(encoding, 'surrogatepass')
                except UnicodeEncodeError as error:
                    print(error.encoding, error.start, error.end, error.reason, error.object is source)
                    print(str(error))
            """
        );

    [Fact]
    public Task SurrogateEscapeEmitsTheRawByteRangeInAsciiLatinOneAndUtf8() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            escaped = ''.join(chr(0xdc00 + value) for value in range(128, 256))
            for encoding in ('ascii', 'latin1', 'utf8'):
                source = 'A' + escaped + 'Z'
                value = source.encode(encoding, 'surrogateescape')
                print(encoding, list(value), value == bytes(source, encoding, 'surrogateescape'))
            """
        );

    [Fact]
    public Task SurrogateEscapeAdvancesTheErrorStartPastConsumedEscapesButRetainsRunEnd() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding, invalid in (('ascii', 'é'), ('latin1', '€'), ('utf8', chr(0xdd00))):
                source = 'A' + chr(0xdc80) + invalid + chr(0xdcff) + 'Z'
                try: source.encode(encoding, 'surrogateescape')
                except UnicodeEncodeError as error:
                    print(error.encoding, error.start, error.end, error.reason, error.object is source)
                    print(str(error))
            for source in (chr(0xdc7f), chr(0xd800), chr(0xdd00)):
                try: source.encode('utf8', 'surrogateescape')
                except UnicodeEncodeError as error: print(error.start, error.end, str(error))
            """
        );

    [Fact]
    public Task SurrogateEscapeDoesNotEmitRawBytesInsideUtf16CodeUnits() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding in ('utf16', 'utf-16-le', 'utf-16-be'):
                for source in ('A' + chr(0xdc80) + chr(0xdcff) + 'Z', '😀' + chr(0xd800) + 'Z'):
                    try: source.encode(encoding, 'surrogateescape')
                    except UnicodeEncodeError as error:
                        print(error.encoding, error.start, error.end, error.reason, error.object is source)
                        print(str(error))
            """
        );
}
