using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CodecOperationCompatibilityTests
{
    [Fact]
    public Task DefaultsKeywordsAndAliasesUseRepresentedCodecs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            print(codecs.encode('Aé😀'),codecs.decode(b'A\xc3\xa9'))
            for name in ('ascii','US-ASCII','latin1','ISO-8859-1','utf8','UTF_8','utf-16','utf-16-le','utf-16-be'):
                print(name,codecs.encode(obj='Aé',encoding=name,errors='backslashreplace'))
                data='Aé'.encode(name,'backslashreplace')
                print(codecs.decode(encoding=name,obj=data))
            print(codecs.encode('é',errors='ignore'),codecs.decode(b'\xff',errors='replace'))
            """
        );

    [Fact]
    public Task ArgumentBindingUsesClinicDiagnosticsAndPrecedence() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            cases=(((),{}), (('x','ascii','strict',4),{}), ((),{'encoding':'ascii'}), ((None,),{'what':1}), (('x','ascii'),{'encoding':'utf8'}), ((),{'bad':1}), ((),{'obj':'x','bad':1,'extra':2,'last':3}), (('x',),{'bad':1,'encoding':1}), (('x',1),{'obj':'y'}), (('x',),{'bad':1,'obj':'y'}))
            for op in ('encode','decode'):
                for args,kw in cases:
                    try: getattr(codecs,op)(*args,**kw)
                    except Exception as e: print(op,type(e).__name__,str(e),getattr(e,'__notes__',None))
            """
        );

    [Fact]
    public Task NameValidationOccursBeforeLookupAndConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for op in ('encode','decode'):
                for encoding,errors in ((None,'strict'),(b'ascii','strict'),(1,'strict'),('missing',None),('ascii',1),('ascii','x\0y'),('a\0b','strict'),(chr(0xd800),'strict'),('ascii',chr(0xd800))):
                    try: getattr(codecs,op)(None,encoding,errors)
                    except Exception as e: print(op,type(e).__name__,ascii(str(e)),getattr(e,'__notes__',None))
            """
        );

    [Fact]
    public Task LookupStillRunsForEmptyObjectsAndInvalidInputTypes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for op in ('encode','decode'):
                for value in ('',b'',None,1):
                    try: getattr(codecs,op)(value,'missing')
                    except LookupError as e: print(op,str(e),getattr(e,'__notes__',None))
            for codec in ('ascii','latin1','utf8','utf-16','utf-16-le','utf-16-be'):
                print(codecs.encode('',codec,'unknown'),repr(codecs.decode(b'',codec,'unknown')))
            """
        );

    [Fact]
    public Task WrongInputTypesHaveCodecSpecificDiagnosticsAndNotes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class Text:
                def __str__(self): raise AssertionError('str')
                def __bytes__(self): raise AssertionError('bytes')
            for name in ('ASCII','latin1','utf8','utf-16','utf-16-le','utf-16-be'):
                for op in ('encode','decode'):
                    for value in (None,1,[],Text(),b'x' if op=='encode' else 'x'):
                        try: getattr(codecs,op)(value,name)
                        except TypeError as e: print(name,op,str(e),e.__notes__)
            """
        );

    [Fact]
    public Task UnicodeErrorsKeepStructuredStateAndOriginalEncodingNotes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for name in ('ASCII','latin1','utf8','UTF_16','utf-16-le','utf-16-be'):
                try: codecs.encode(chr(0xd800),name)
                except UnicodeEncodeError as e: print(e.encoding,ascii(e.object),e.start,e.end,e.reason,e.__notes__)
                data=b'\xff' if name not in ('UTF_16','utf-16-le','utf-16-be') else b'\x00'
                try: print(ascii(codecs.decode(data,name)))
                except UnicodeDecodeError as e: print(e.encoding,e.object,e.start,e.end,e.reason,e.__notes__)
            """
        );

    [Fact]
    public Task CustomHandlersAndBuiltinRecoveryShareScannerBehavior() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            calls=[]
            def handler(e): calls.append((type(e).__name__,e.start,e.end));return ('X',e.end)
            codecs.register_error('entry',handler)
            for name in ('ascii','latin1','utf8','utf-16','utf-16-le','utf-16-be'):
                calls=[]; print(codecs.encode('A'+chr(0xd800)+'Z',name,'entry'),calls)
                calls=[];print(codecs.decode(b'\xff',name,'entry'),calls)
            for name in ('ignore','replace','backslashreplace','xmlcharrefreplace','namereplace','surrogateescape','surrogatepass'):
                try: print(name,codecs.encode(chr(0xdc80),'utf8',name))
                except UnicodeError as e: print(name,type(e).__name__)
            """
        );

    [Fact]
    public Task CallbackExceptionsRetainIdentityAndAccumulateNotes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            problem=LookupError('callback');problem.add_note('prior')
            def handler(e): raise problem
            codecs.register_error('raise',handler)
            for op in ('encode','decode'):
                try: getattr(codecs,op)('é' if op=='encode' else b'\xff','ASCII','raise')
                except LookupError as e: print(e is problem,e.args,e.__notes__)
            """
        );

    [Fact]
    public Task NestedCodecCallsAppendNotesFromInnerToOuter() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            problem=ValueError('nested')
            def inner(e): raise problem
            def outer(e): return (codecs.decode(b'\xff','ascii','inner'),e.end)
            codecs.register_error('inner',inner);codecs.register_error('outer',outer)
            try: codecs.encode('é','ascii','outer')
            except ValueError as e: print(e is problem,e.__notes__)
            """
        );

    [Fact]
    public Task FailureNotesIgnoreAddNoteOverrides() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            class Problem(ValueError):
                def add_note(self,note): raise AssertionError('override')
            problem=Problem('original')
            def handler(e): raise problem
            codecs.register_error('notes',handler)
            try: codecs.encode('é','ascii','notes')
            except ValueError as e: print(e is problem,e.__notes__)
            """
        );

    [Fact]
    public Task InvalidNotesRaiseTypeErrorWithOriginalContext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for notes in (None,1,'x',('prior',)):
                problem=ValueError('original');problem.__notes__=notes
                def handler(e): raise problem
                codecs.register_error('notes',handler)
                try: codecs.encode('é','ascii','notes')
                except TypeError as e: print(str(e),e.__context__ is problem,problem.__notes__ is notes)
            """
        );

    [Fact]
    public Task HandlerLookupAndResultFailuresGetNotesButNameFailuresDoNot() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            codecs.register_error('badresult',lambda e:None)
            for op in ('encode','decode'):
                for errors in ('missing','badresult'):
                    try: getattr(codecs,op)('é' if op=='encode' else b'\xff','ascii',errors)
                    except Exception as e: print(type(e).__name__,str(e),e.__notes__)
                try: getattr(codecs,op)(None,'missing')
                except LookupError as e: print(str(e),getattr(e,'__notes__',None))
            """
        );

    [Fact]
    public Task BaseExceptionsFromCallbacksAreAnnotated() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for problem in (KeyboardInterrupt('stop'),SystemExit(3),GeneratorExit()):
                def handler(e): raise problem
                codecs.register_error('base',handler)
                try: codecs.decode(b'\xff','ascii','base')
                except BaseException as e: print(e is problem,type(e).__name__,e.__notes__)
            """
        );

    [Fact]
    public Task ConvertedNamesUseCurrentHandlersAndSharedCache() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            old=codecs.lookup_error('strict');calls=[]
            def handler(e): calls.append(1);return ('ascii',e.end)
            name=chr(0xd800)
            codecs.register_error('strict',handler)
            try:
                print(codecs.encode('A',name),codecs.decode(b'A',name),len(calls))
            finally: codecs.register_error('strict',old)
            """
        );

    [Fact]
    public Task DecodeUsesFinalInputAndBomSelection() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import codecs
            for name,data in (('utf8',b'\xe2\x82'),('utf-16',b'\xfe\xff\x00A'),('utf-16',b'A\x00'),('utf-16-le',b'\x00\xd8'),('utf-16-be',b'\xd8\x00')):
                for errors in ('strict','replace','surrogatepass'):
                    try: print(name,errors,ascii(codecs.decode(data,name,errors)))
                    except UnicodeDecodeError as e: print(name,errors,e.start,e.end,e.reason,e.__notes__)
            """
        );
}
