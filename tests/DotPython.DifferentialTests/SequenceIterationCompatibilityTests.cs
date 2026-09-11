using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SequenceIterationCompatibilityTests
{
    [Fact]
    public Task SequenceIteratorsAreLazyIndependentAndStartWithIntegerZero() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Sequence:
                def __len__(self): raise RuntimeError('length is not required')
                def __getitem__(self, index):
                    events.append((type(index).__name__, index))
                    if index == 2: raise IndexError('finished')
                    return index + 10
            sequence = Sequence()
            first = iter(sequence)
            second = iter(sequence)
            print(events, first is second, iter(first) is first)
            print(next(first), next(first), next(second), events)
            print(next(first, 'done'), next(first, 'still done'), events)
            """
        );

    [Fact]
    public Task ExplicitIterTakesPrecedenceAndNoneOrInvalidResultsDisableSequenceFallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Sequence:
                def __getitem__(self, index):
                    events.append('getitem')
                    raise IndexError
                def __iter__(self):
                    events.append('iter')
                    return iter([7, 8])
            sequence = Sequence()
            print(list(sequence), events)
            Sequence.__iter__ = None
            try: iter(sequence)
            except TypeError as error: print(type(error).__name__, events)
            Sequence.__iter__ = lambda self: []
            try: iter(sequence)
            except TypeError as error: print(type(error).__name__, events)
            del Sequence.__iter__
            print(list(sequence), events)
            """
        );

    [Fact]
    public Task IndexErrorAndStopIterationSubclassesPermanentlyExhaustTheIterator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class EndIndex(IndexError): pass
            class EndIteration(StopIteration): pass
            events = []
            class Sequence:
                def __getitem__(self, index):
                    events.append(index)
                    if index == 0: return 'first'
                    raise end('payload')
            for end in (IndexError, EndIndex, StopIteration, EndIteration):
                events.clear()
                sequence = Sequence()
                iterator = iter(sequence)
                print(next(iterator))
                try: next(iterator)
                except StopIteration as error: print(type(error).__name__, error.args)
                sequence.__getitem__ = lambda index: 'revived'
                print(next(iterator, 'done'), events)
            """
        );

    [Fact]
    public Task OtherGetitemFailuresPreserveIdentityAndRetryTheSameIndex() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('temporary failure')
            class Sequence:
                fail = True
                def __getitem__(self, index):
                    events.append(index)
                    if index == 1 and self.fail: raise failure
                    if index == 2: raise IndexError
                    return index
            sequence = Sequence()
            iterator = iter(sequence)
            print(next(iterator))
            for attempt in range(2):
                try: next(iterator)
                except ValueError as error: print(error is failure)
            sequence.fail = False
            print(next(iterator), next(iterator, 'done'), events)
            """
        );

    [Fact]
    public Task ExistingIteratorsResolveReplacedAndDeletedClassGetitemOnEveryStep() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                def __getitem__(self, index): return ('original', index)
            iterator = iter(Sequence())
            print(next(iterator))
            Sequence.__getitem__ = lambda self, index: ('replacement', index)
            print(next(iterator))
            del Sequence.__getitem__
            try: next(iterator)
            except TypeError as error: print(type(error).__name__)
            def restored(self, index):
                if index == 3: raise IndexError
                return ('restored', index)
            Sequence.__getitem__ = restored
            print(next(iterator), next(iterator, 'done'))
            Sequence.__getitem__ = lambda self, index: 'revived'
            print(next(iterator, 'still done'))
            """
        );

    [Fact]
    public Task InstanceSpecialMethodsAndGetattributeOverridesDoNotReplaceSequenceSlots() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                def __getitem__(self, index):
                    if index == 2: raise IndexError
                    return index
                def __getattribute__(self, name):
                    if name in ('__iter__', '__getitem__'): raise RuntimeError('ordinary lookup')
                    return super().__getattribute__(name)
            sequence = Sequence()
            sequence.__getitem__ = lambda index: 99
            sequence.__iter__ = lambda: iter([99])
            print(list(sequence))
            class OnlyInstance: pass
            value = OnlyInstance()
            value.__getitem__ = lambda index: 99
            try: iter(value)
            except TypeError as error: print(type(error).__name__)
            """
        );

    [Fact]
    public Task GetitemDescriptorsAreBoundAtEachStepWithTheActualInstanceAndOwner() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Getter:
                def __get__(self, instance, owner):
                    events.append(('bind', instance is sequence, owner.__name__))
                    def get(index):
                        events.append(('get', index))
                        if index == 2: raise IndexError
                        return index + 4
                    return get
            class Base: __getitem__ = Getter()
            class Sequence(Base): pass
            sequence = Sequence()
            iterator = iter(sequence)
            print(events)
            print(next(iterator), next(iterator), next(iterator, 'done'))
            print(events)
            """
        );

    [Fact]
    public Task SequenceFallbackFeedsLoopsMaterializationStarArgumentsAndUnpacking() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                def __getitem__(self, index):
                    if index == 4: raise IndexError
                    return index + 1
            sequence = Sequence()
            values = []
            for value in sequence: values.append(value)
            print(values, list(sequence), tuple(sequence), [*sequence], (*sequence,))
            def collect(*args): return args
            print(collect(*sequence))
            first, *middle, last = sequence
            print(first, middle, last)
            a, b, c, d = sequence
            print(a, b, c, d)
            print([value * 2 for value in sequence], sum(sequence))
            """
        );

    [Fact]
    public Task MembershipFallsBackToSequenceIterationAndStopsAtTheFirstMatch() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Sequence:
                def __getitem__(self, index):
                    events.append(index)
                    if index == 3: raise IndexError
                    return index
            sequence = Sequence()
            print(1 in sequence, events)
            events.clear()
            print(9 not in sequence, events)
            Sequence.__contains__ = lambda self, value: True
            events.clear()
            print(99 in sequence, events)
            Sequence.__contains__ = None
            try: print(99 in sequence)
            except TypeError as error: print(type(error).__name__, events)
            """
        );

    [Fact]
    public Task YieldFromUsesTheSequenceIteratorAndInjectsThrowIntoTheDelegatingGenerator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Sequence:
                def __getitem__(self, index):
                    events.append(index)
                    if index == 2: raise IndexError
                    return index
            def generator():
                try:
                    result = yield from Sequence()
                    yield ('returned', result)
                except ValueError as error: yield ('caught', error.args)
            instance = generator()
            print(next(instance), instance.send(None), next(instance), events)
            instance.close()
            events.clear()
            instance = generator()
            print(next(instance), instance.throw(ValueError('injected')), events)
            instance.close()
            """
        );

    [Fact]
    public Task OnlyAttributeErrorFromIterDescriptorLookupFallsBackToGetitem() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = AttributeError('iteration failed')
            class Sequence:
                def __getitem__(self, index):
                    events.append(index)
                    raise IndexError
                def __iter__(self): raise failure
            sequence = Sequence()
            try: iter(sequence)
            except AttributeError as error: print(error is failure, events)
            class IterDescriptor:
                def __get__(self, instance, owner): raise failure
            Sequence.__iter__ = IterDescriptor()
            iterator = iter(sequence)
            print(next(iterator, 'done'), events)
            other_failure = RuntimeError('descriptor failed')
            class FailingDescriptor:
                def __get__(self, instance, owner): raise other_failure
            Sequence.__iter__ = FailingDescriptor()
            try: iter(sequence)
            except RuntimeError as error: print(error is other_failure, events)
            """
        );

    [Fact]
    public Task ExceptionGroupsAndDeriveAcceptGetitemOnlySequencesAndRetainOriginalArgs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            leaf = ValueError('leaf')
            class Sequence:
                def __getitem__(self, index):
                    events.append(index)
                    if index == 1: raise IndexError
                    return leaf
            sequence = Sequence()
            group = ExceptionGroup('root', sequence)
            print(group.args[1] is sequence, group.exceptions[0] is leaf, events)
            events.clear()
            derived = group.derive(sequence)
            print(derived.args[1] is sequence, derived.exceptions[0] is leaf, events)
            """
        );

    [Fact]
    public Task ExceptionGroupSequenceAdmissionRejectsIterableOnlyValuesBeforeIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Iterable:
                def __iter__(self):
                    events.append('iter')
                    return iter([ValueError('leaf')])
            root = ExceptionGroup('root', [ValueError('leaf')])
            for value in (Iterable(), (item for item in [ValueError('leaf')]), iter([ValueError('leaf')]), {}, set()):
                for construct in (lambda values: ExceptionGroup('new', values), root.derive):
                    try: construct(value)
                    except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task AdmittedGroupSequencesUseIterPrecedenceAndPropagateIterationFailures() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            leaf = ValueError('leaf')
            class Sequence:
                def __getitem__(self, index):
                    events.append('getitem')
                    raise IndexError
                def __iter__(self):
                    events.append('iter')
                    return iter([leaf])
            sequence = Sequence()
            root = ExceptionGroup('root', sequence)
            print(root.exceptions[0] is leaf, events)
            failure = RuntimeError('failed iteration')
            def fail(self): raise failure
            Sequence.__iter__ = fail
            for construct in (lambda values: ExceptionGroup('new', values), root.derive):
                try: construct(sequence)
                except RuntimeError as error: print(error is failure, events)
            """
        );

    [Fact]
    public Task GroupFilteringCopiesGetitemSequenceNotesButIgnoresIterableOnlyNotes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Sequence:
                def __getitem__(self, index):
                    events.append(index)
                    if index == 2: raise IndexError
                    return 'note ' + str(index)
            class Iterable:
                def __iter__(self):
                    events.append('iter')
                    return iter(['ignored'])
            root = ExceptionGroup('root', [ValueError('matched'), TypeError('remaining')])
            for notes in (Sequence(), Iterable()):
                root.__notes__ = notes
                result = root.subgroup(ValueError)
                print(getattr(result, '__notes__', None), events)
                events.clear()
            """
        );
}
