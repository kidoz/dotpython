using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CodecsErrorHandlerCompatibilityTests
{
    [Fact]
    public Task LookupHasStableIdentitiesAndExactPublicExports() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for name in ('strict','ignore','replace','xmlcharrefreplace','backslashreplace','namereplace','surrogatepass','surrogateescape'):
                handler=codecs.lookup_error(name)
                print(name,callable(handler),handler is codecs.lookup_error(name),handler.__name__)
                print(hasattr(codecs,name+'_errors'))
                if hasattr(codecs,name+'_errors'): print(handler is getattr(codecs,name+'_errors'))
            saved=codecs.replace_errors
            codecs.replace_errors=None
            print(codecs.lookup_error('replace') is saved)
            """
        );

    [Fact]
    public Task LookupValidatesNamesAndTruncatesUnknownNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for name in (None,1,b'ignore','IGNORE','','é','a\x00b',chr(0xd800),'a'*401,'€'*134,'é'*201):
                try: print(codecs.lookup_error(name))
                except Exception as e: print(type(e).__name__,ascii(str(e)))
            """
        );

    [Fact]
    public Task CallableArityAndKeywordsMatchBuiltinDiagnostics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for name in ('strict','ignore','replace','xmlcharrefreplace','backslashreplace','namereplace','surrogatepass','surrogateescape','lookup_error'):
                f=codecs.lookup_error(name) if name!='lookup_error' else codecs.lookup_error
                for args in ((),(None,None)):
                    try: f(*args)
                    except TypeError as e: print(str(e))
                try: f(error=None)
                except TypeError as e: print(str(e))
            """
        );

    [Fact]
    public Task StrictReraisesEveryExceptionByIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class E(Exception): pass
            for e in (ValueError('bad'),E('own'),KeyboardInterrupt(),UnicodeEncodeError('ascii','é',0,1,'bad'),UnicodeDecodeError('ascii',b'\xff',0,1,'bad'),UnicodeTranslateError('é',0,1,'bad')):
                try: codecs.strict_errors(e)
                except BaseException as caught: print(type(caught).__name__,caught is e,str(caught))
            for value in (None,1,'x',Exception):
                try: codecs.strict_errors(value)
                except TypeError as e: print(str(e))
            """
        );

    [Fact]
    public Task WrongExceptionTypesAreRejectedBeforeReadingMembers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class X:
                @property
                def object(self): raise AssertionError('must not read')
            class E(UnicodeTranslateError): pass
            for name in ('ignore','replace','xmlcharrefreplace','backslashreplace','namereplace','surrogatepass','surrogateescape'):
                for value in (None,1,'x',Exception('bad'),UnicodeError('bad'),X(),E('x',0,1,'bad')):
                    try: print(name,codecs.lookup_error(name)(value))
                    except Exception as e: print(name,type(e).__name__,str(e))
            """
        );

    [Fact]
    public Task TextRangesClampWithoutMutatingRawMembers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for T in (UnicodeEncodeError,UnicodeTranslateError):
                for source in ('','A','AéΩ😀Z',chr(0xd800)+'Z'+chr(0xdcff)):
                    for start,end in ((0,1),(-3,-2),(99,99),(2,1),(1,0),(0,0),(-(2**63),2**63-1)):
                        e=T('ascii',source,start,end,'bad') if T is UnicodeEncodeError else T(source,start,end,'bad')
                        for name in ('ignore','replace','backslashreplace'):
                            print(T.__name__,ascii(source),start,end,name,ascii(codecs.lookup_error(name)(e)),e.start,e.end)
            """
        );

    [Fact]
    public Task DecodeRangesIncludeEmptyAndReversedCases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for source in (b'',b'A',b'A\xff\x80Z'):
                for start,end in ((0,1),(-3,-2),(99,99),(2,1),(1,0),(0,0),(-(2**63),2**63-1)):
                    e=UnicodeDecodeError('ascii',source,start,end,'bad')
                    for name in ('ignore','replace','backslashreplace','surrogateescape'):
                        try: print(source,start,end,name,ascii(codecs.lookup_error(name)(e)),e.start,e.end)
                        except UnicodeDecodeError as caught: print(name,caught is e)
            """
        );

    [Fact]
    public Task XmlAndNameReplacementUseCodepointsAndUnicodeNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            source='AéΩ😀'+chr(0xd800)+chr(0x378)+chr(0xf0000)+chr(0x17000)
            for start,end in ((0,99),(3,1),(-1,-1),(99,0)):
                e=UnicodeEncodeError('ascii',source,start,end,'bad')
                for name in ('xmlcharrefreplace','namereplace'):
                    print(name,ascii(codecs.lookup_error(name)(e)))
            for name in ('xmlcharrefreplace','namereplace'):
                print(codecs.lookup_error(name)(UnicodeEncodeError('ascii','',99,-5,'bad')))
            """
        );

    [Fact]
    public Task MissingAndInvalidObjectMembersUseInternalGetters() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for T in (UnicodeEncodeError,UnicodeDecodeError,UnicodeTranslateError):
                e=T.__new__(T)
                for value in ('unset',None,1,'x',b'x'):
                    if value!='unset': e.object=value
                    for name in ('ignore','replace','backslashreplace','xmlcharrefreplace','namereplace','surrogateescape'):
                        try: print(T.__name__,repr(value),name,ascii(codecs.lookup_error(name)(e)))
                        except Exception as caught: print(T.__name__,repr(value),name,type(caught).__name__,str(caught))
            """
        );

    [Fact]
    public Task ErrorSubclassPropertiesDoNotOverrideStoredFields() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class E(UnicodeEncodeError):
                @property
                def object(self): raise AssertionError('object')
                @property
                def start(self): raise AssertionError('start')
                @property
                def end(self): raise AssertionError('end')
                @property
                def encoding(self): raise AssertionError('encoding')
                @property
                def reason(self): raise AssertionError('reason')
            e=E('utf-8',chr(0xdc80),0,1,'bad')
            for name in ('ignore','replace','backslashreplace','xmlcharrefreplace','namereplace','surrogatepass','surrogateescape'):
                print(name,ascii(codecs.lookup_error(name)(e)))
            """
        );

    [Fact]
    public Task EncodingAndReasonAreUnusedExceptBySurrogatePass() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class Bomb:
                def __str__(self): raise AssertionError('string conversion')
            e=UnicodeEncodeError('ascii',chr(0xdc80),0,1,'bad')
            e.encoding=Bomb(); e.reason=Bomb(); e.args=('unrelated',)
            for name in ('ignore','replace','backslashreplace','xmlcharrefreplace','namereplace','surrogateescape'):
                print(name,ascii(codecs.lookup_error(name)(e)))
            del e.encoding; del e.reason
            print(codecs.backslashreplace_errors(e))
            """
        );

    [Fact]
    public Task SurrogatePassRecognizesOnlyItsEncodingAliases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for encoding in ('utf8','UTF_8','utf-8','cp65001','CP65001','utf 8','utf--8','utf16','UTF_16_LE','utf16BE','utf-16','utf32','utf_32_le','UTF32BE','utf-32','ascii','utf-8\x00ignored','utf-8-sig'):
                e=UnicodeEncodeError(encoding,chr(0xd800)+'Z',0,1,'bad')
                try: print(repr(encoding),codecs.lookup_error('surrogatepass')(e))
                except UnicodeEncodeError as caught: print(repr(encoding),caught is e)
            """
        );

    [Fact]
    public Task SurrogatePassValidatesEncodingBeforeObject() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for T in (UnicodeEncodeError,UnicodeDecodeError):
                for encoding in ('unset',None,b'utf8',1,'ascii','utf8',chr(0xd800),'utf8\x00'+chr(0xd800)):
                    e=T.__new__(T)
                    if encoding!='unset': e.encoding=encoding
                    try: print(codecs.lookup_error('surrogatepass')(e))
                    except Exception as caught: print(T.__name__,ascii(encoding),type(caught).__name__,ascii(str(caught)),caught is e)
            """
        );

    [Fact]
    public Task SurrogatePassEncodeHandlesRangesAndOriginalFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for source in ('',chr(0xd800)+chr(0xd801),chr(0xdc80)+chr(0xdcff),'A',chr(0xd800)+'A','😀'):
                for start,end in ((0,99),(99,0),(-1,-1)):
                    for encoding in ('utf8','utf16le','utf16be','utf32le','utf32be'):
                        e=UnicodeEncodeError(encoding,source,start,end,'bad')
                        try: print(ascii(source),start,end,encoding,codecs.lookup_error('surrogatepass')(e))
                        except UnicodeEncodeError as caught: print(ascii(source),start,end,encoding,caught is e)
            """
        );

    [Fact]
    public Task SurrogatePassDecodeReadsOneSurrogateBeyondReportedEnd() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for encoding,source in (('utf8',b'\xed\xa0\x80\xed\xb0\x80'),('utf16le',b'\x00\xd8'),('utf16be',b'\xdc\xff'),('utf32le',b'\x00\xd8\x00\x00'),('utf32be',b'\x00\x00\xdc\x80')):
                for start,end in ((0,0),(0,1),(0,99),(-1,-1),(99,99)):
                    e=UnicodeDecodeError(encoding,source,start,end,'bad')
                    try: print(encoding,start,end,ascii(codecs.lookup_error('surrogatepass')(e)))
                    except UnicodeDecodeError as caught: print(encoding,start,end,caught is e)
            """
        );

    [Fact]
    public Task SurrogatePassDecodeRejectsTruncatedAndNonSurrogateSequences() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for encoding,sources in (('utf8',(b'',b'\xed\xa0',b'\xed\x20\x80',b'\xe0\x80\x80',b'\xef\xbf\xbd',b'\xfd\xa0\x80')),('utf16le',(b'',b'\x00',b'\x41\x00')),('utf32be',(b'\x00\x00\xd8',b'\xff\x00\xd8\x00',b'\x00\x00\x00\x41'))):
                for source in sources:
                    e=UnicodeDecodeError(encoding,source,0,99,'bad')
                    try: print(codecs.lookup_error('surrogatepass')(e))
                    except UnicodeDecodeError as caught: print(encoding,source,caught is e)
            """
        );

    [Fact]
    public Task SurrogateEscapeDecodeStopsAtAsciiAndFourBytes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for source in (b'\x80\x81\x82\x83\x84',b'\xffA\x80',b'A\xff',b''):
                for start,end in ((0,99),(0,1),(1,99),(0,0),(3,1)):
                    e=UnicodeDecodeError('unused',source,start,end,'bad')
                    try: print(source,start,end,ascii(codecs.lookup_error('surrogateescape')(e)))
                    except UnicodeDecodeError as caught: print(source,start,end,caught is e)
            """
        );

    [Fact]
    public Task SurrogateEscapeEncodeRequiresLowSurrogateByteRange() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for source in ('',chr(0xdc80)+chr(0xdcff),chr(0xdc7f),chr(0xdd00),chr(0xdc80)+'A'):
                for start,end in ((0,99),(99,0),(-1,-1)):
                    e=UnicodeEncodeError('unused',source,start,end,'bad')
                    try: print(ascii(source),start,end,codecs.lookup_error('surrogateescape')(e))
                    except UnicodeEncodeError as caught: print(ascii(source),start,end,caught is e)
            """
        );
}
