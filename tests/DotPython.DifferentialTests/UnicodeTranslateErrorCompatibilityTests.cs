using Xunit;

namespace DotPython.DifferentialTests;

public sealed class UnicodeTranslateErrorCompatibilityTests
{
    [Fact]
    public Task ConstructorAndHierarchyExposeIndependentState() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            source='A😀'; e=UnicodeTranslateError(source,1,2,'bad')
            print(type(e).__name__,isinstance(e,UnicodeError),isinstance(e,ValueError),e.args,e.object is source,e.encoding,e.start,e.end,e.reason,str(e),repr(e))
            print(UnicodeTranslateError.__bases__,UnicodeTranslateError.__mro__)
            print(UnicodeTranslateError in UnicodeError.__subclasses__())
            """
        );

    [Fact]
    public Task ArityKeywordsAndArgumentTypesUseBuiltinDiagnostics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for args in ((),('x',),('x',0,1),('x',0,1,'bad',9),(None,0,1,'bad'),(b'x',0,1,'bad'),('x',None,1,'bad'),('x',0,'1','bad'),('x',0,1,None)):
                try: UnicodeTranslateError(*args)
                except Exception as e: print(type(e).__name__,str(e))
            try: UnicodeTranslateError('x',0,1,reason='bad')
            except TypeError as e: print(str(e))
            """
        );

    [Fact]
    public Task ConstructorConvertsIndicesInOrderAndRetainsArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events=[]
            class Index:
                def __init__(self,name,value): self.name=name; self.value=value
                def __index__(self): events.append(self.name); return self.value
            a=Index('start',0); b=Index('end',1)
            e=UnicodeTranslateError('x',a,b,'bad')
            print(events,e.start,e.end,e.args[1] is a,e.args[2] is b)
            """
        );

    [Fact]
    public Task ValidationOrderAndNativeWidthKeepOriginalCallbackErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events=[]; problem=LookupError('callback')
            class Index:
                def __index__(self): events.append('index'); raise problem
            for args in ((None,Index(),1,None),('x',Index(),1,None),('x',0,Index(),None),('x',0,1,None)):
                try: UnicodeTranslateError(*args)
                except Exception as e: print(type(e).__name__,str(e),e is problem,events)
            for n in (-(2**63)-1,2**63):
                try: UnicodeTranslateError('x',n,1,'bad')
                except OverflowError as e: print(str(e))
            """
        );

    [Fact]
    public Task SingletonFormattingUsesCodepointsAndRawRanges() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ('','A','é','Ω','😀',chr(0xd800)):
                for start,end in ((0,1),(0,0),(-1,0),(0,2),(1,2),(5,2),(-(2**63),-(2**63)),(2**63-1,2**63-1)):
                    print(ascii(source),start,end,str(UnicodeTranslateError(source,start,end,'bad')))
            """
        );

    [Fact]
    public Task EncodingIsWritableButIgnoredAndPreservedByReinitialization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('x',0,1,'bad')
            class Encoding:
                def __str__(self): raise AssertionError('must not format encoding')
            encoding=Encoding();e.encoding=encoding
            e.__init__('y',0,1,'new')
            print(e.encoding is encoding,str(e))
            del e.encoding
            print(e.encoding,str(e))
            """
        );

    [Fact]
    public Task MemberMutationAndArgsAssignmentRemainIndependent() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('old',0,1,'old');args=e.args
            e.object='😀';e.start=0;e.end=1;e.reason='new';e.encoding=17
            print(e.args is args,e.args,str(e),e.__dict__)
            e.args=('unrelated',)
            print(e.args,e.object,e.start,e.end,e.reason,str(e))
            BaseException.__init__(e,'base')
            print(e.args,str(e))
            """
        );

    [Fact]
    public Task DeletionAndExplicitNoneHaveDifferentMeaning() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for name in ('encoding','object','reason'):
                e=UnicodeTranslateError('x',0,1,'bad');delattr(e,name)
                print(name,getattr(e,name),str(e))
                setattr(e,name,None)
                try: print(str(e))
                except TypeError as problem: print(str(problem))
            for name in ('start','end'):
                e=UnicodeTranslateError('x',0,1,'bad')
                try: delattr(e,name)
                except TypeError as problem: print(str(problem))
            """
        );

    [Fact]
    public Task NumericDescriptorsRequireIntegersWithoutIndexCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __index__(self): print('unexpected'); return 1
            for value in (True,False,2,Index(),1.5,2**100):
                e=UnicodeTranslateError('x',0,1,'bad')
                try: e.start=value;print(e.start,type(e.start).__name__)
                except Exception as problem: print(type(problem).__name__,str(problem),e.start)
            """
        );

    [Fact]
    public Task FailedReinitializationUpdatesArgsButKeepsMembers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('old',0,1,'old');e.encoding='retained'
            for args in (('new',0),('new',0,1,None),(None,0,1,'new')):
                try: e.__init__(*args)
                except TypeError as problem: print(str(problem))
                print(e.args,e.encoding,e.object,e.start,e.end,e.reason,str(e))
            """
        );

    [Fact]
    public Task ReentrantIndexInitializationPreservesCallbackArgsAndEncoding() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('old',0,1,'old')
            class Index:
                def __index__(self):
                    e.__init__('inner',1,2,'inner')
                    e.encoding='callback'
                    return 0
            index=Index(); e.__init__('outer',index,1,'outer')
            print(e.args,e.object,e.start,e.end,e.reason,e.encoding,str(e))
            """
        );

    [Fact]
    public Task ReasonConversionCanReinitializeObjectAndBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('old',0,1,'old')
            class Reason:
                def __str__(self):
                    e.__init__('😀Z',1,2,'inner')
                    return 'outer reason'
            e.reason=Reason()
            print(str(e),e.args)
            """
        );

    [Fact]
    public Task ReasonMutationDeletionAndReturnTypeErrorsAreObserved() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('x',0,1,'bad')
            class Delete:
                def __str__(self): del e.object;return 'deleted'
            e.reason=Delete()
            try: print(str(e))
            except TypeError as problem: print(str(problem))
            class Wrong:
                def __str__(self): return 12
            e.__init__('x',0,1,'bad');e.reason=Wrong()
            try: print(str(e))
            except TypeError as problem: print(str(problem))
            """
        );

    [Fact]
    public Task DescriptorMetadataAndDirectCallsRejectSiblingUnicodeErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('x',0,1,'bad')
            d=UnicodeTranslateError.object
            print(d.__name__,d.__qualname__,d.__objclass__ is UnicodeTranslateError,d.__doc__,repr(d))
            print(d.__get__(None,UnicodeTranslateError) is d,d.__get__(e,UnicodeTranslateError))
            d.__set__(e,'y');print(e.object);d.__delete__(e);print(e.object)
            for other in (UnicodeEncodeError('x','x',0,1,'bad'),UnicodeDecodeError('x',b'x',0,1,'bad'),1):
                try: d.__get__(other)
                except TypeError as problem: print(str(problem))
                try: UnicodeTranslateError.__str__(other)
                except TypeError as problem: print(str(problem))
            for owner in (UnicodeEncodeError,UnicodeDecodeError):
                try: owner.object.__get__(e)
                except TypeError as problem: print(str(problem))
            """
        );

    [Fact]
    public Task NewAllocatesUnsetMembersAndInheritedSubclassInitialization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Child(UnicodeTranslateError): pass
            for owner in (UnicodeTranslateError,Child):
                e=owner.__new__(owner,'raw')
                print(type(e).__name__,e.args,e.encoding,e.object,e.start,e.end,e.reason,str(e))
                e.__init__('x',0,1,'bad');print(type(e).__name__,str(e))
            class Custom(UnicodeTranslateError):
                def __str__(self): return 'custom'
            e=Custom('x',0,1,'bad');print(str(e),UnicodeTranslateError.__str__(e))
            """
        );

    [Fact]
    public Task DataDescriptorsOverrideInstanceDictionaryEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            e=UnicodeTranslateError('x',0,1,'bad');e.__dict__['object']='shadow';e.__dict__['start']=99
            print(e.object,e.start,e.__dict__)
            class Child(UnicodeTranslateError):
                @property
                def object(self): return 'override'
            e=Child('x',0,1,'bad');print(e.object,str(e),UnicodeTranslateError.object.__get__(e))
            """
        );

    [Fact]
    public Task CopyAndPickleReplayArgumentsInsteadOfMutatedMembers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            import pickle
            e=UnicodeTranslateError('original',0,1,'original');e.object='changed';e.reason='changed';e.encoding='changed';e.extra=['note']
            for clone in (copy.copy(e),copy.deepcopy(e),pickle.loads(pickle.dumps(e))):
                print(type(clone).__name__,clone.args,clone.object,clone.reason,clone.encoding,clone.extra,clone is e,clone.extra is e.extra,str(clone))
            """
        );

    [Fact]
    public Task CopyReplaysIndexCallbacksAndRejectsInvalidArgs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            import pickle
            class Index:
                def __index__(self): print('index');return 0
            index=Index();e=UnicodeTranslateError('x',index,1,'bad')
            clone=copy.copy(e);print(clone.args[1] is index,clone.start)
            e.args=('broken',)
            for operation in (copy.copy,copy.deepcopy):
                try: operation(e)
                except TypeError as problem: print(str(problem))
            try: pickle.loads(pickle.dumps(e))
            except TypeError as problem: print(str(problem))
            """
        );

    [Fact]
    public Task RaisingDefersReasonFormattingUntilStringConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events=[]
            class Reason:
                def __str__(self): events.append('str');return 'reason'
            e=UnicodeTranslateError('x',0,1,'bad');e.reason=Reason()
            try: raise e
            except UnicodeError as caught: print(caught is e,events)
            print(str(e),events)
            """
        );

    [Fact]
    public Task UnicodeSiblingMultipleInheritanceRetainsLayoutRestrictions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for other in (UnicodeEncodeError,UnicodeDecodeError):
                try: type('Mixed',(UnicodeTranslateError,other),{})
                except TypeError as problem: print(str(problem))
            """
        );
}
