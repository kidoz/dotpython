using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BytesTextCompatibilityTests
{
    [Fact]
    public Task EveryByteHasMatchingDirectStringAndRepresentationOutput() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = bytes(range(256))
            print(value)
            print(str(value))
            print(repr(value))
            for number in range(256):
                value = bytes([number])
                print(value, str(value), repr(value))
            """
        );

    [Fact]
    public Task RepresentationChoosesQuotesAndUsesShortEscapesOnlyForTabLinefeedAndReturn() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([], [39], [34], [39, 34], [39, 39, 34], [92], [9, 10, 13], [0, 7, 8, 11, 12, 27, 31, 127, 128, 255], [39, 92, 9, 10, 13]):
                value = bytes(numbers)
                print(value, str(value), repr(value))
            """
        );

    [Fact]
    public Task ContainerRepresentationsUseTheSameBytesEscapesAndQuoteChoice() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [bytes([39, 10]), bytes([34, 9]), bytes([39, 34, 92]), bytes([0, 255])]
            print(values)
            print(tuple(values))
            print({value: index for index, value in enumerate(values)})
            """
        );

    [Fact]
    public Task DefaultUtf8DecodingHandlesMultibyteCharactersAndEmbeddedNulls() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([], [65, 0, 66], [194, 162], [226, 130, 172], [240, 159, 152, 128], [65, 194, 162, 226, 130, 172, 240, 159, 152, 128]):
                value = bytes(numbers)
                print([ord(character) for character in value.decode()])
            """
        );

    [Fact]
    public Task DecodeAcceptsPositionalKeywordAndMixedEncodingAndErrorArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = bytes([65, 255, 66])
            print(value.decode('latin1'))
            print(value.decode(encoding='latin1'))
            print(value.decode('ascii', errors='ignore'))
            print(value.decode(errors='replace', encoding='ascii'))
            print(bytes([65, 255]).decode(errors='ignore'))
            """
        );

    [Fact]
    public Task DecodeRejectsNonstringEncodingAndErrorsBeforeCodecLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (b'', b'a'):
                for bad in (None, 1, False, [], b'ascii'):
                    try: value.decode(bad)
                    except TypeError as error: print(str(error))
                    try: value.decode(errors=bad)
                    except TypeError as error: print(str(error))
                try: value.decode('unknown-codec', errors=1)
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task DecodeReportsNullNamesDuplicateArgumentsAndUnknownKeywords() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for call in (lambda: b'a'.decode('utf-8' + chr(0)), lambda: b'a'.decode(errors='strict' + chr(0)), lambda: b'a'.decode('ascii', encoding='utf8'), lambda: b'a'.decode('ascii', 'strict', errors='ignore'), lambda: b'a'.decode('ascii', 'strict', 1), lambda: b'a'.decode(unknown=1)):
                try: call()
                except (TypeError, ValueError) as error: print(str(error))
            """
        );

    [Fact]
    public Task CodecAliasesResolveToTheSameSupportedDecoders() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding in ('utf8', 'UTF_8', 'u8', 'utf 8', ' UTF-8 '):
                print(bytes([194, 162]).decode(encoding))
            for encoding in ('ascii', 'US-ASCII', '646', 'us_ascii'):
                print(b'ABC'.decode(encoding))
            for encoding in ('latin1', 'latin_1', 'ISO-8859-1', 'iso8859_1', 'L1'):
                print([ord(character) for character in bytes([0, 127, 128, 255]).decode(encoding)])
            """
        );

    [Fact]
    public Task Utf8StrictErrorsReportTheFirstMalformedSpanAndSpecificReason() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([255], [65, 128], [194], [194, 32], [226, 130], [226, 130, 32], [224, 128, 128], [237, 160, 128], [240, 159, 152], [244, 144, 128, 128], [245, 128, 128, 128]):
                value = bytes(numbers)
                try: value.decode('UTF_8')
                except UnicodeDecodeError as error: print(str(error))
            """
        );

    [Fact]
    public Task Utf8IgnoreAndReplaceConsumeTheSameMalformedSpans() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([255, 65, 128], [194], [194, 32], [226, 130], [226, 130, 32], [224, 128, 128], [237, 160, 128], [240, 159, 152], [244, 144, 128, 128], [65, 226, 130, 32, 66]):
                for errors in ('ignore', 'replace', 'backslashreplace'):
                    print([ord(character) for character in bytes(numbers).decode('utf8', errors)])
            """
        );

    [Fact]
    public Task AsciiStrictErrorsReportTheFirstNonAsciiByteAndCanonicalCodecName() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([128], [65, 255], [65, 128, 255], [127, 255, 0]):
                try: bytes(numbers).decode('US-ASCII')
                except UnicodeDecodeError as error: print(str(error))
            """
        );

    [Fact]
    public Task AsciiIgnoreAndReplaceHandleEachNonAsciiByteSeparately() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = bytes([65, 128, 255, 66, 0, 127])
            for errors in ('ignore', 'replace', 'backslashreplace'):
                print([ord(character) for character in value.decode('ascii', errors)])
            """
        );

    [Fact]
    public Task LatinOneMapsEveryByteAndDoesNotNeedAnErrorHandler() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = bytes(range(256))
            for errors in ('strict', 'ignore', 'replace', 'unregistered-handler'):
                print([ord(character) for character in value.decode('latin1', errors)])
            """
        );

    [Fact]
    public Task Utf16DetectsEitherBomAndDecodesSurrogatePairs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([255, 254, 65, 0, 172, 32], [254, 255, 0, 65, 32, 172], [255, 254, 61, 216, 0, 222], [254, 255, 216, 61, 222, 0], [255, 254], [254, 255]):
                value = bytes(numbers)
                print([ord(character) for character in value.decode('utf-16')])
            """
        );

    [Fact]
    public Task Utf16WithoutBomUsesTheNativeByteOrderAndPreservesEmbeddedBoms() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([65, 0, 66, 0], [65, 0, 255, 254, 66, 0], [255, 254, 65, 0, 255, 254]):
                print([ord(character) for character in bytes(numbers).decode('UTF16')])
            """
        );

    [Fact]
    public Task Utf16StrictErrorsDistinguishTruncationAndInvalidSurrogates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([65], [255, 254, 65], [255, 254, 0, 216], [255, 254, 0, 216, 65, 0], [255, 254, 0, 220], [254, 255, 216, 0, 0, 65], [254, 255, 220, 0]):
                try: bytes(numbers).decode('utf16')
                except UnicodeDecodeError as error: print(str(error))
            """
        );

    [Fact]
    public Task Utf16IgnoreAndReplaceAdvancePastTheReportedMalformedSpans() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for numbers in ([65], [255, 254, 65], [255, 254, 0, 216], [255, 254, 0, 216, 65, 0], [255, 254, 0, 220, 66, 0], [254, 255, 216, 0, 0, 65]):
                for errors in ('ignore', 'replace', 'backslashreplace'):
                    print([ord(character) for character in bytes(numbers).decode('utf16', errors)])
            """
        );

    [Fact]
    public Task UnknownErrorHandlersAreLookedUpOnlyWhenMalformedInputNeedsThem() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding in ('utf8', 'ascii', 'utf16'):
                clean = b'AB' if encoding != 'utf16' else bytes([65, 0])
                print(clean.decode(encoding, 'unregistered-handler'))
                malformed = bytes([255])
                try: malformed.decode(encoding, 'unregistered-handler')
                except LookupError as error: print(str(error))
            for handler in ('xmlcharrefreplace', 'namereplace'):
                for encoding in ('utf8', 'ascii', 'utf16'):
                    clean = b'AB' if encoding != 'utf16' else bytes([65, 0])
                    print(clean.decode(encoding, handler))
                    try: bytes([255]).decode(encoding, handler)
                    except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task EmptyBytesSkipCodecAndHandlerLookupAfterArgumentValidation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = b''
            for encoding in ('utf8', 'ascii', 'latin1', 'utf16', 'unknown-codec'):
                print(repr(value.decode(encoding, 'unregistered-handler')))
            try: b'a'.decode('unknown-codec')
            except LookupError as error: print(str(error))
            try: value.decode('unknown-codec', None)
            except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task StrDecodingAndTheDecodeMethodShareSupportedCodecResultsAndErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding, numbers in (('utf8', [65, 194, 162]), ('ascii', [65, 255, 66]), ('latin1', [0, 128, 255]), ('utf16', [254, 255, 0, 65, 216, 61, 222, 0])):
                value = bytes(numbers)
                for errors in ('strict', 'ignore', 'replace'):
                    for convert in (lambda: value.decode(encoding, errors), lambda: str(value, encoding, errors)):
                        try: print([ord(character) for character in convert()])
                        except UnicodeDecodeError as error: print(str(error))
            """
        );

    [Fact]
    public Task SavedDecodeMethodsRetainTheOriginalBytesReceiver() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = bytes([65, 128, 255])
            decode = value.decode
            value = b'changed'
            print([ord(character) for character in decode('latin1')])
            print(decode('ascii', 'ignore'))
            print(value)
            """
        );

    [Fact]
    public Task DecodeFailuresLeaveTheSourceAndTargetUnchanged() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = bytes([65, 226, 130])
            alias = value
            target = 'original'
            try: target = value.decode()
            except UnicodeDecodeError as error: print(str(error), target, value is alias, list(value))
            print([ord(character) for character in value.decode(errors='replace')])
            """
        );

    [Fact]
    public Task ExplicitUtf16EndianCodecsPreserveBomsAndApplyErrorHandlersToMalformedUnits() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for encoding, bom, letter, high, low in (('utf-16-le', [255, 254], [65, 0], [0, 216], [0, 220]), ('UTF_16_BE', [254, 255], [0, 65], [216, 0], [220, 0])):
                value = bytes(bom + letter)
                print([ord(character) for character in value.decode(encoding)])
                print([ord(character) for character in value.decode('utf16')])
                for numbers in ([65], high, high + letter, low + letter):
                    for errors in ('strict', 'ignore', 'replace', 'backslashreplace'):
                        try: print([ord(character) for character in bytes(numbers).decode(encoding, errors)])
                        except UnicodeDecodeError as error: print(str(error))
            """
        );

    [Fact]
    public Task EmbeddedNullCodecNamesAreRejectedEvenForEmptyBytesAcrossDecodeAndStr() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            try: 'a'.encode('utf' + chr(0) + ' 8')
            except ValueError as error: print(str(error))
            for value in (b'', b'a'):
                for encoding, errors in (('utf8' + chr(0), 'strict'), ('utf8', 'strict' + chr(0)), ('unknown-codec', 'strict' + chr(0))):
                    for convert in (lambda: value.decode(encoding, errors), lambda: str(value, encoding, errors)):
                        try: convert()
                        except ValueError as error: print(str(error))
            """
        );
}
