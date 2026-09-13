using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SurrogateDecodingCompatibilityTests
{
    [Fact]
    public Task Utf8PassPreservesEveryLoneSurrogate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in range(0xd800,0xe000):
                source=chr(point).encode('utf8','surrogatepass')
                value=source.decode('utf8','surrogatepass')
                print(point,ord(value),value.encode('utf8','surrogatepass')==source)
            """
        );

    [Fact]
    public Task Utf16PassPreservesLoneSurrogatesInBothByteOrders() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0xd800,0xd801,0xdbff,0xdc00,0xdc80,0xdcff,0xdfff):
                for codec in ('utf-16-le','utf-16-be','utf16'):
                    source=chr(point).encode(codec,'surrogatepass')
                    value=source.decode(codec,'surrogatepass')
                    print(point,codec,[ord(c) for c in value],value.encode(codec,'surrogatepass')==source)
            """
        );

    [Fact]
    public Task Utf8PassLeavesValidBoundaryScalarsAndPairsIntact() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for point in (0,0x7f,0x80,0x7ff,0x800,0xd7ff,0xe000,0xffff,0x10000,0x1f600,0x10ffff):
                source=chr(point).encode('utf8')
                print(point,source.decode('utf8','surrogatepass')==chr(point))
            source=('A'+chr(0xd800)+'😀'+chr(0xdcff)+'Z').encode('utf8','surrogatepass')
            print([ord(c) for c in source.decode('utf8','surrogatepass')])
            """
        );

    [Fact]
    public Task EscapeRoundtripsEveryAsciiAndUtf8Byte() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for codec in ('ascii','utf8'):
                for byte in range(256):
                    source=bytes([byte]); value=source.decode(codec,'surrogateescape')
                    print(codec,byte,[ord(c) for c in value],value.encode(codec,'surrogateescape')==source)
            """
        );

    [Fact]
    public Task Utf8EscapePreservesMalformedPrefixesAndAsciiInterruptions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for values in ([0xe2,0x82],[0xe2,0x82,0x41],[0xf0,0x90,0x80,0x41],[0xed,0xa0,0x80],[0xc0,0xaf],[0xf4,0x90,0x80,0x80],[0xff,0x41,0x80]):
                source=bytes(values); value=source.decode('utf8','surrogateescape')
                print(values,[ord(c) for c in value],value.encode('utf8','surrogateescape')==source)
            """
        );

    [Fact]
    public Task PassRejectsMalformedUtf8WithOriginalStructuredError() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for values in ([0xed],[0xed,0xa0],[0xed,0xa0,0x41],[0xed,0x9f],[0xff],[0xc0,0xaf],[0xe0,0x80,0x80],[0xf4,0x90,0x80,0x80],[0x41,0xed,0xa0,0x80,0xff]):
                source=bytes(values)
                try: print(source.decode('utf8','surrogatepass'))
                except UnicodeDecodeError as e: print(e.encoding,e.start,e.end,e.reason,list(e.object),e.object==source,str(e))
            """
        );

    [Fact]
    public Task PassRejectsAsciiErrorsWhileLatinOneNeedsNoRecovery() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for handler in ('surrogatepass','surrogateescape'):
                source=bytes([0x41,0xff,0x80])
                try: print([ord(c) for c in source.decode('ascii',handler)])
                except UnicodeDecodeError as e: print(e.encoding,e.start,e.end,e.reason,str(e))
                print([ord(c) for c in source.decode('latin1',handler)])
            """
        );

    [Fact]
    public Task Utf16PassConsumesOnlyOneSurrogateBeforeTruncatedTail() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for codec,values in (('utf-16-le',[0,0xd8,0xff]),('utf-16-be',[0xd8,0,0xff]),('utf-16-le',[0,0xdc,0x41]),('utf-16-be',[0xdc,0,0x41])):
                source=bytes(values)
                try: print(source.decode(codec,'surrogatepass'))
                except UnicodeDecodeError as e: print(codec,e.start,e.end,e.reason,list(e.object),str(e))
            """
        );

    [Fact]
    public Task Utf16EscapeRefusesAsciiBytesInAnErrorRange() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for codec,values in (('utf-16-le',[0,0xd8]),('utf-16-be',[0,0xdc]),('utf-16-be',[0xd8,0]),('utf-16-le',[0xff,0xd8,0]),('utf-16-be',[0xd8,0xff,0])):
                source=bytes(values)
                try: print(codec,[ord(c) for c in source.decode(codec,'surrogateescape')])
                except UnicodeDecodeError as e: print(codec,e.start,e.end,e.reason,list(e.object),str(e))
            """
        );

    [Fact]
    public Task Utf16EscapeResumesFromPartiallyConsumedErrorBytes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for codec,values in (('utf-16-be',[0xd8,0,0x41,0]),('utf-16-le',[0xff,0xdc,0x80,0]),('utf-16-be',[0xdc,0xff,0,0x41]),('utf-16-le',[0xff,0xd8,0x80]),('utf-16-be',[0xd8,0xff,0x80])):
                source=bytes(values)
                try: print(codec,[ord(c) for c in source.decode(codec,'surrogateescape')])
                except UnicodeDecodeError as e: print(codec,e.start,e.end,e.reason,str(e))
            """
        );

    [Fact]
    public Task NativeBomSelectsSurrogateByteOrderAndErrorOffsets() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for values in ([0xff,0xfe,0,0xd8],[0xfe,0xff,0xd8,0],[0xff,0xfe,0,0xd8,0xff],[0xfe,0xff,0xd8,0,0xff],[0xff,0xfe,0xff],[0xfe,0xff,0x41]):
                for handler in ('surrogatepass','surrogateescape'):
                    source=bytes(values)
                    try: print(values,handler,[ord(c) for c in source.decode('utf16',handler)])
                    except UnicodeDecodeError as e: print(values,handler,e.encoding,e.start,e.end,e.reason,str(e))
            """
        );

    [Fact]
    public Task ExplicitEndianCodecsRetainBomCharacters() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for codec,values in (('utf-16-le',[0xff,0xfe,0,0xd8,0x41,0]),('utf-16-be',[0xfe,0xff,0xd8,0,0,0x41])):
                source=bytes(values)
                print(codec,[ord(c) for c in source.decode(codec,'surrogatepass')])
            """
        );

    [Fact]
    public Task DecodeAndStrConstructorShareAliasesAndKeywordHandlers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source=bytes([0xed,0xa0,0x80,0x41,0xff])
            print([ord(c) for c in source.decode(errors='surrogateescape')])
            print(source.decode('UTF 8','surrogateescape')==str(source,encoding='u8',errors='surrogateescape'))
            source=bytes([0,0xd8])
            print(source.decode(errors='surrogatepass',encoding='unicodeLittleUnmarked')==str(source,'utf-16-le','surrogatepass'))
            """
        );

    [Fact]
    public Task HandlerLookupStaysLazyAndNamesRemainCaseSensitive() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for handler in ('surrogatepass','surrogateescape','SurrogatePass','SurrogateEscape','surrogate_pass'):
                print(handler,bytes().decode('missing-codec',handler),b'abc'.decode('utf8',handler))
                try: print([ord(c) for c in bytes([0xff]).decode('utf8',handler)])
                except Exception as e: print(type(e).__name__,str(e))
            """
        );

    [Fact]
    public Task ChunkBoundariesRetainValidAndInvalidByteSequences() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for padding in (250,251,252,253,254,255,256):
                source=b'a'*padding+bytes([0xed,0xa0,0x80])+b'Z'
                for handler in ('surrogatepass','surrogateescape'):
                    value=source.decode('utf8',handler)
                    print(padding,handler,len(value),[ord(c) for c in value[padding:]])
            """
        );

    [Fact]
    public Task SavedDecodersRetainInputAcrossRecoveryAndFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source=bytes([0xed,0xa0,0x80]); decode=source.decode
            for handler in ('surrogateescape','surrogatepass','strict','surrogatepass'):
                try: print(handler,[ord(c) for c in decode('utf8',handler)],list(source))
                except UnicodeDecodeError as e: print(handler,e.start,e.end,e.reason,list(source))
            """
        );
}
