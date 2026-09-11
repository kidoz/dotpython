using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ExceptionAllocationCompatibilityTests
{
    [Fact]
    public Task ReconstructionCanReturnForeignValuesButStillAppliesMaterializedEmptyState() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            import pickle
            class Error(Exception): pass
            untouched = Error('untouched')
            touched = Error('touched')
            touched.__dict__
            replaced = Error('replaced')
            replaced.__dict__ = {}
            cleared = Error('cleared')
            cleared.attribute = 7
            del cleared.attribute
            Error.__new__ = staticmethod(lambda cls, *args: 17)
            for source in (untouched, touched, replaced, cleared):
                for rebuild in (copy.copy, copy.deepcopy, lambda value: pickle.loads(pickle.dumps(value))):
                    try: print(rebuild(source))
                    except AttributeError as error: print(type(error).__name__)
            graph = copy.deepcopy([untouched, untouched])
            print(graph, graph[0] is graph[1])
            """
        );

    [Fact]
    public Task ForeignReconstructionUpdatesItsDictionaryAndPreservesStateCycles() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            import pickle
            class Error(Exception): pass
            class Result: pass
            source = Error('original')
            source.self = source
            source.__dict__[7] = 'nonstring'
            Error.__new__ = staticmethod(lambda cls, *args: Result())
            for rebuild in (copy.copy, copy.deepcopy, lambda value: pickle.loads(pickle.dumps(value))):
                result = rebuild(source)
                print(type(result) is Result, result.self is result, result.self is source)
                print(result.__dict__[7])
            """
        );

    [Fact]
    public Task ForeignReconstructionCallsSetstateAndPropagatesItsFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            import pickle
            failure = ValueError('state failed')
            class Error(Exception): pass
            class Result:
                def __setstate__(self, state):
                    self.state = state
                    if state['fail']: raise failure
            source = Error('original')
            source.self = source
            source.fail = False
            Error.__new__ = staticmethod(lambda cls, *args: Result())
            for rebuild in (copy.copy, copy.deepcopy, lambda value: pickle.loads(pickle.dumps(value))):
                result = rebuild(source)
                print(type(result) is Result, result.state['self'] is result)
            source.fail = True
            for rebuild in (copy.copy, copy.deepcopy, lambda value: pickle.loads(pickle.dumps(value))):
                try: rebuild(source)
                except ValueError as error: print(error is failure)
            """
        );

    [Fact]
    public Task SpecializedBuiltinFieldsAreInitializedAfterRawAllocation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            stop = StopIteration.__new__(StopIteration, 'allocated')
            exit = SystemExit.__new__(SystemExit, 'allocated')
            print(stop.args, stop.value, exit.args, exit.code)
            StopIteration.__init__(stop, 'initialized')
            SystemExit.__init__(exit, 'initialized')
            print(stop.args, stop.value, exit.args, exit.code)
            stop.args = ('replacement',)
            exit.args = ('replacement',)
            print(stop.value, exit.code)
            """
        );

    [Fact]
    public Task CustomNewAllocatesBeforeInitAndReceivesTheOriginalCallArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __new__(cls, value, *, flag):
                    events.append(('new', cls.__name__, value, flag))
                    result = super().__new__(cls, 'allocated', flag)
                    result.marker = 'from new'
                    return result
                def __init__(self, value, *, flag):
                    events.append(('init', self.args, value, flag, self.marker))
            error = Error('original', flag=7)
            print(type(error) is Error, error.args, error.marker, events)
            """
        );

    [Fact]
    public Task InheritedNewCooperatesThroughSuperAndDefaultInitRebindsArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base(Exception):
                def __new__(cls, *args):
                    events.append(('base', cls.__name__, args))
                    return super().__new__(cls, 'allocated')
            class Middle(Base):
                def __new__(cls, *args):
                    events.append(('middle', cls.__name__, args))
                    return super().__new__(cls, *args)
            class Leaf(Middle): pass
            error = Leaf('called', 3)
            print(type(error) is Leaf, error.args, events)
            """
        );

    [Fact]
    public Task ExplicitBuiltinNewBypassesUserAllocationAndInitialization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(ValueError):
                def __new__(cls, *args):
                    events.append('user new')
                    return super().__new__(cls, *args)
                def __init__(self, *args): events.append('user init')
            for allocator in (BaseException.__new__, Exception.__new__, ValueError.__new__):
                error = allocator(Error, 'allocated', 9, ignored=True)
                print(type(error) is Error, error.args, events)
            """
        );

    [Fact]
    public Task ForeignNewResultsSkipInitialization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __new__(cls, *args, **kwargs):
                    events.append(('new', args, kwargs))
                    return result
                def __init__(self, *args, **kwargs): events.append('init')
            for result in ([], ValueError('foreign'), None, 42):
                events.clear()
                value = Error('argument', flag=7)
                print(value is result, events)
            """
        );

    [Fact]
    public Task ReturnedSubtypeUsesItsOwnInitializerWithOriginalArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base(Exception):
                def __new__(cls, *args, **kwargs):
                    events.append(('new', cls.__name__))
                    return Exception.__new__(Child, 'allocated')
                def __init__(self, *args, **kwargs): events.append('base init')
            class Child(Base):
                def __init__(self, *args, **kwargs):
                    events.append(('child init', self.args, args, kwargs))
                    self.initialized = True
            error = Base('original', flag=8)
            print(type(error) is Child, error.args, error.initialized, events)
            """
        );

    [Fact]
    public Task AllocationFailuresPreserveIdentityAndSkipInitialization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = RuntimeError('allocation failed')
            class Error(Exception):
                def __new__(cls):
                    events.append('new')
                    raise failure
                def __init__(self): events.append('init')
            for mode in ('call', 'raise'):
                try:
                    if mode == 'call': Error()
                    else: raise Error
                except RuntimeError as error: print(mode, error is failure, events)
            """
        );

    [Fact]
    public Task RaisingExceptionClassesRunsCustomAllocationAndRejectsForeignResults() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __new__(cls):
                    events.append('new')
                    return super().__new__(cls, 'allocated')
                def __init__(self): events.append(('init', self.args))
            try: raise Error
            except Error as error: print(type(error) is Error, error.args, events)
            class Foreign(Exception):
                def __new__(cls): return 42
            try: raise Foreign
            except TypeError as error: print(type(error).__name__)
            """
        );

    [Fact]
    public Task GeneratorThrowInjectsCustomAllocationFailures() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('new failed')
            events = []
            class Error(Exception):
                def __new__(cls):
                    events.append('new')
                    raise failure
                def __init__(self): events.append('init')
            def generator():
                try: yield 'ready'
                except ValueError as error: yield ('caught', error is failure)
                yield 'after'
            instance = generator()
            print(next(instance), instance.throw(Error), events)
            print(next(instance))
            instance.close()
            """
        );

    [Fact]
    public Task AsyncThrowLooksUpCustomNewWhenAwaitedAndInjectsItsFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('replacement failed')
            class Error(Exception):
                def __new__(cls):
                    events.append('original new')
                    return super().__new__(cls)
            def replacement(cls):
                events.append('replacement new')
                raise failure
            async def generator():
                try: yield 'ready'
                except ValueError as error: yield ('caught', error is failure)
            async def drive():
                instance = generator()
                print(await anext(instance))
                step = instance.athrow(Error)
                print(events)
                Error.__new__ = staticmethod(replacement)
                print(await step, events)
                await instance.aclose()
            driver = drive()
            try: driver.send(None)
            except StopIteration: pass
            """
        );

    [Fact]
    public Task ExceptionGroupCustomNewCanRemapArgumentsBeforeNoOpInit() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            leaf = ValueError('leaf')
            class Group(ExceptionGroup):
                def __new__(cls, leaves, *, label):
                    events.append(('new', cls.__name__, label))
                    result = super().__new__(cls, label, leaves)
                    result.marker = 7
                    return result
                def __init__(self, leaves, *, label):
                    events.append(('init', self.message, label, self.marker))
            leaves = (leaf,)
            group = Group(leaves, label='remapped')
            print(type(group) is Group, group.args[0], group.args[1] is leaves)
            print(group.message, group.exceptions is leaves, events)
            """
        );

    [Fact]
    public Task GroupDefaultInitRebindsArgsWithoutChangingAllocatedMessageOrLeaves() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            original = (ValueError('original'),)
            replacement = (TypeError('replacement'),)
            class Group(ExceptionGroup):
                def __new__(cls, message, leaves):
                    return super().__new__(cls, 'allocated', replacement)
            group = Group('called', original)
            print(group.args[0], group.args[1] is original)
            print(group.message, group.exceptions is replacement, str(group))
            """
        );

    [Fact]
    public Task ExplicitBuiltinAllocatorsValidateReceiverSubtypeAndLayout() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for allocator, target in (
                (BaseException.__new__, int),
                (Exception.__new__, BaseException),
                (ValueError.__new__, Exception),
                (Exception.__new__, 42),
                (Exception.__new__, ExceptionGroup),
                (BaseException.__new__, OSError),
                (BaseExceptionGroup.__new__, Exception),
            ):
                try: allocator(target, 'argument')
                except TypeError as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task ReplacingAndDeletingNewChangesSubsequentExceptionConstruction() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Error(Exception):
                def __init__(self, *args): events.append(self.args)
            for index in range(3): Error('warm')
            def replacement(cls, *args):
                return Exception.__new__(cls, 'replacement')
            Error.__new__ = staticmethod(replacement)
            print(Error('called').args)
            del Error.__new__
            print(Error('called').args, events)
            """
        );

    [Fact]
    public Task FailedInitLeavesTheEscapedAllocatedExceptionAndFollowingCallsUsable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            allocated = []
            class Error(Exception):
                def __new__(cls, *args):
                    result = super().__new__(cls, *args)
                    allocated.append(result)
                    return result
                def __init__(self, *args): return 7
            try: Error('failed')
            except TypeError as error: print(str(error), allocated[0].args)
            def initialize(self, *args): self.ready = True
            Error.__init__ = initialize
            result = Error('working')
            print(result is allocated[1], result.args, result.ready)
            """
        );
}
