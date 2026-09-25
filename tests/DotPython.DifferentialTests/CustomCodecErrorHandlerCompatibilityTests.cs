using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CustomCodecErrorHandlerCompatibilityTests
{
    [Fact]
    public Task RegistrationValidatesNamesArityKeywordsAndCallability() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for args in ((),('x',),('x',None),(1,lambda e:None),(None,None),(b'x',None),('x\0bad',None),(chr(0xd800),None),('x',lambda e:None,1)):
                try: print(codecs.register_error(*args))
                except Exception as e: print(type(e).__name__,ascii(str(e)))
            try: codecs.register_error(name='x',handler=lambda e:None)
            except TypeError as e: print(str(e))
            class Handler:
                def __call__(self,e): return ('?',e.end)
            h=Handler()
            for name in ('','mixed CASE','é'):
                print(codecs.register_error(name,h),codecs.lookup_error(name) is h,'é'.encode('ascii',name))
            """
        );

    [Fact]
    public Task ReregistrationPreservesSavedHandlersAndNamedExports() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            saved=codecs.replace_errors
            first=lambda e:('first',e.end)
            second=lambda e:('second',e.end)
            codecs.register_error('replace',first)
            print(codecs.lookup_error('replace') is first,codecs.replace_errors is saved)
            codecs.register_error('replace',second)
            print(codecs.lookup_error('replace') is second)
            try: codecs.register_error('replace',None)
            except TypeError: pass
            print(codecs.lookup_error('replace') is second,saved(UnicodeTranslateError('x',0,1,'bad')))
            codecs.register_error('replace',saved)
            """
        );

    [Fact]
    public Task AllTextEntryPointsUseCustomHandlersOncePerErrorRun() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            calls=[]
            def handler(e):
                calls.append((type(e).__name__,e.encoding,e.start,e.end,e.reason,ascii(e.object)))
                return ('[bad]',e.end)
            codecs.register_error('custom',handler)
            for codec,text,data in (('ascii','AéΩBé',b'A\xffB\x80'),('latin1','AΩ😀BΩ',b'A\xff'),('utf8','A'+chr(0xd800)+'B'+chr(0xdcff),b'A\xffB\xe2'),('utf-16','A'+chr(0xd800)+'B',b'\xff\xfeA\x00\x00\xd8B\x00'),('utf-16-le',chr(0xdcff),b'\x00\xdc'),('utf-16-be',chr(0xdcff),b'\xdc\x00')):
                calls=[];print(text.encode(codec,'custom'),calls)
                calls=[];print(bytes(text,codec,'custom'),calls)
                calls=[];print(ascii(data.decode(codec,'custom')),calls)
                calls=[];print(ascii(str(data,codec,'custom')),calls)
            """
        );

    [Fact]
    public Task HandlerLookupIsLazyForValidAndEmptyInput() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for c in ('ascii','latin1','utf8','utf-16','utf-16-le','utf-16-be'):
                print('abc'.encode(c,'missing'),''.encode(c,'missing'))
                print('abc'.encode(c).decode(c,'missing'),b''.decode(c,'missing'))
            for expr in (lambda:'é'.encode('ascii','missing'),lambda:b'\xff'.decode('ascii','missing')):
                try: expr()
                except LookupError as e: print(str(e))
            """
        );

    [Fact]
    public Task PerOperationHandlerCacheSurvivesReregistrationAndNestedCalls() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            calls=[]
            def newer(e): calls.append('new');return ('N',e.end)
            def original(e):
                calls.append('old')
                codecs.register_error('switch',newer)
                print('é'.encode('ascii','switch'))
                return ('O',e.end)
            for decode in (False,True):
                calls=[];codecs.register_error('switch',original)
                print(b'\xffA\xff'.decode('ascii','switch') if decode else 'éAé'.encode('ascii','switch'))
                print(calls)
            """
        );

    [Fact]
    public Task CachedExceptionKeepsArgsEncodingAndCustomState() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            seen=[]
            def handler(e):
                print(e is seen[0] if seen else False,e.encoding,e.start,e.end,e.reason,e.args)
                if not seen:
                    seen.append(e);e.encoding='changed';e.reason='changed';e.extra='note';e.args=('saved',)
                else: print(e.extra)
                return ('X',e.end)
            for decode in (False,True):
                seen=[];codecs.register_error('state',handler)
                print(b'\xffA\xff'.decode('ascii','state') if decode else 'éAé'.encode('ascii','state'))
            """
        );

    [Fact]
    public Task EncodeIgnoresChangedErrorObjectButRetainsItForLaterCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            seen=[]
            def handler(e):
                print(ascii(e.object),e.start,e.end)
                if not seen: seen.append(e);e.object='long replacement source'
                return ('X',e.end)
            codecs.register_error('object',handler)
            print('éAé'.encode('ascii','object'))
            print(seen[0].object,seen[0].args)
            """
        );

    [Fact]
    public Task DecodeResumesInChangedBytesAndKeepsEffectiveByteOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for codec,data,new in (('ascii',b'P\xffZ',b'ABC'),('utf8',b'P\xffZ',b'ABC'),('utf-16',b'\xfe\xff\xd8\x00',b'\x00A\x00B'),('utf-16-le',b'\x00\xdc',b'A\x00B\x00')):
                def handler(e): e.object=new;return ('X',0)
                codecs.register_error('change',handler)
                print(codec,ascii(data.decode(codec,'change')))
            """
        );

    [Fact]
    public Task DecodeReadsObjectAfterPositionIndexCallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            saved=[]
            class Index:
                def __index__(self): saved[0].object=b'new';return -2
            def handler(e): saved.append(e);e.object=b'old';return ('X',Index())
            codecs.register_error('index',handler)
            print(b'\xff'.decode('ascii','index'))
            """
        );

    [Fact]
    public Task InvalidDecodeObjectFollowsResultValidation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class Index:
                def __index__(self): print('index');return 0
            for value in (None,'x',1,'delete'):
                def handler(e):
                    if value=='delete': del e.object
                    else: e.object=value
                    return ('X',Index())
                codecs.register_error('object',handler)
                try: b'\xff'.decode('ascii','object')
                except Exception as e: print(type(e).__name__,str(e))
            """
        );

    [Fact]
    public Task CallbackResultsValidateTupleTypesAndIndexInOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class Index:
                def __index__(self): print('index');return 1
            for result in (None,['X',1],('X',),('X',1,2),(None,Index()),('X',1.0),('X',2**100),('X',Index()),('X',True)):
                codecs.register_error('result',lambda e:result)
                for decode in (False,True):
                    try: print(b'\xff'.decode('ascii','result') if decode else 'é'.encode('ascii','result'))
                    except Exception as e: print(type(e).__name__,str(e))
            """
        );

    [Fact]
    public Task PositionBoundsUseOriginalTextOrReplacementBytesLength() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for position in (-10,-3,-1,0,1,3,4,2**63-1,-(2**63)):
                calls=[]
                def handler(e):
                    calls.append(1)
                    if len(calls)>1: return ('Y',len(e.object))
                    return ('X',position)
                codecs.register_error('position',handler)
                for decode in (False,True):
                    calls=[]
                    try: print(position,b'A\xffZ'.decode('ascii','position') if decode else 'AéZ'.encode('ascii','position'))
                    except Exception as e: print(position,type(e).__name__,str(e))
            """
        );

    [Fact]
    public Task ByteAndTextReplacementRestrictionsPreserveOriginalException() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            seen=[]
            for codec,source in (('ascii','é'),('latin1','Ω'),('utf8',chr(0xd800)),('utf-16',chr(0xd800)),('utf-16-be',chr(0xd800))):
                for replacement in ('X','é','😀',chr(0xd800),b'X',b'XY',b''):
                    def handler(e): seen.append(e);return (replacement,e.end)
                    codecs.register_error('replace_custom',handler)
                    try: print(codec,ascii(replacement),source.encode(codec,'replace_custom'))
                    except UnicodeEncodeError as e: print(codec,ascii(replacement),e is seen[-1],e.start,e.end,e.reason)
            """
        );

    [Fact]
    public Task BuiltinOverridesFollowCodecSpecificFastPaths() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for codec,source,data in (('ascii','é',b'\xff'),('latin1',chr(0xd800),b'\xff'),('utf8',chr(0xd800),b'\xff'),('utf-16-le',chr(0xd800),b'\x00\xd8')):
                for name in ('strict','ignore','replace','backslashreplace','xmlcharrefreplace','namereplace','surrogatepass','surrogateescape'):
                    old=codecs.lookup_error(name)
                    def handler(e): print('callback');return ('X',e.end)
                    codecs.register_error(name,handler)
                    try:
                        try: print(codec,name,source.encode(codec,name))
                        except UnicodeEncodeError: print(codec,name,'error')
                        print(ascii(data.decode(codec,name)))
                    finally: codecs.register_error(name,old)
            """
        );

    [Fact]
    public Task SurrogateEscapeFallsBackAfterItsValidPrefix() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            old=codecs.lookup_error('surrogateescape')
            def handler(e): print(e.start,e.end,ascii(e.object));return ('X',e.end)
            codecs.register_error('surrogateescape',handler)
            try:
                for codec in ('ascii','latin1','utf8'):
                    print((chr(0xdc80)+chr(0xdcff)+chr(0xd800)+'Z').encode(codec,'surrogateescape'))
            finally: codecs.register_error('surrogateescape',old)
            """
        );

    [Fact]
    public Task CallbackFailuresAndIndexFailuresKeepIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            problem=LookupError('callback')
            class Index:
                def __index__(self): raise problem
            for phase in ('call','index'):
                def handler(e):
                    if phase=='call': raise problem
                    return ('X',Index())
                codecs.register_error('failure',handler)
                for decode in (False,True):
                    try:
                        if decode: b'\xff'.decode('ascii','failure')
                        else: 'é'.encode('ascii','failure')
                    except LookupError as e: print(phase,e is problem)
            """
        );

    [Fact]
    public Task BuiltinAliasesAreCallableInBothScanners() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for name in ('ignore','replace','backslashreplace','xmlcharrefreplace','namereplace','surrogatepass','surrogateescape'):
                codecs.register_error('alias',codecs.lookup_error(name))
                for decode in (False,True):
                    try: print(name,ascii(b'\xff'.decode('ascii','alias')) if decode else chr(0xdc80).encode('ascii','alias'))
                    except Exception as e: print(name,type(e).__name__,str(e))
            """
        );

    [Fact]
    public Task RegisteredCallableObjectsUseClassCallLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class Handler:
                def __call__(self,e): print('call');return ('X',e.end)
            h=Handler();h.__call__=None
            print(codecs.register_error('object',h))
            print('é'.encode('ascii','object'),b'\xff'.decode('ascii','object'))
            """
        );

    [Fact]
    public Task NameConversionUsesOverriddenStrictHandler() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            old=codecs.lookup_error('strict')
            def handler(e): print('converted');return ('x',e.end)
            codecs.register_error('strict',handler)
            try:
                for action in (lambda:codecs.register_error(chr(0xd800),handler),lambda:codecs.lookup_error(chr(0xd800))):
                    try: action()
                    except UnicodeEncodeError as e: print(e.encoding,e.start,e.end,e.reason)
            finally: codecs.register_error('strict',old)
            """
        );

    [Fact]
    public Task ConvertedNameCacheIsSharedWithAllCodecEntryPoints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            old=codecs.lookup_error('strict');calls=[]
            def conversion(e): calls.append(1);return ('alias',e.end)
            codecs.register_error('strict',conversion)
            n=chr(0xd800)
            def handler(e): return ('X',e.end)
            try:
                print(codecs.register_error(n,handler))
                print(codecs.lookup_error(n) is handler,codecs.lookup_error('alias') is handler,len(calls))
                print('é'.encode('ascii',n),bytes('é','ascii',n),b'\xff'.decode('ascii',n),str(b'\xff','ascii',n),len(calls))
            finally: codecs.register_error('strict',old)
            """
        );

    [Fact]
    public Task RegistrationUsesCallableDescriptorSemantics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for handler in (staticmethod(lambda e:('X',e.end)),classmethod(lambda cls,e:('X',e.end)),property(lambda self:1),str):
                print(type(handler).__name__,callable(handler))
                try: print(codecs.register_error('kind',handler))
                except TypeError as e: print(str(e))
            """
        );
}
