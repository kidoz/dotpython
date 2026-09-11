using Xunit;

namespace DotPython.DifferentialTests;

public sealed class LengthHintCompatibilityTests
{
    [Fact]
    public Task DictionaryAndSetConsumersSkipHintsWhileSortedAndExtendRequestThem() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self):
                    events.append('iter')
                    return iter([('b', 2), ('a', 1)])
                def __length_hint__(self):
                    events.append('hint')
                    return 0
            print(dict(Source()), events)
            events.clear()
            result = set(Source())
            print(('a', 1) in result, ('b', 2) in result, events)
            events.clear()
            print(sorted(Source()), events)
            events.clear()
            target = []
            target.extend(Source())
            print(target, events)
            """
        );

    [Fact]
    public Task ListCreatesTheIteratorBeforeRequestingTheOriginalSourcesLength() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Iterator:
                index = 0
                def __iter__(self): return self
                def __length_hint__(self):
                    events.append('iterator hint')
                    return 0
                def __next__(self):
                    events.append(('next', self.index))
                    if self.index == 2: raise StopIteration
                    self.index += 1
                    return self.index
            class Source:
                def __iter__(self):
                    events.append('source iter')
                    return Iterator()
                def __len__(self):
                    events.append('source len')
                    return 1
                def __length_hint__(self):
                    events.append('source hint')
                    return 1
            print(list(Source()), events)
            """
        );

    [Fact]
    public Task LengthTypeErrorsFallBackToHintsButOtherLengthFailuresPropagate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class LengthFailure(TypeError): pass
            class Source:
                def __iter__(self):
                    events.append('iter')
                    return iter([7])
                def __len__(self):
                    events.append('len')
                    raise failure
                def __length_hint__(self):
                    events.append('hint')
                    return 0
            for failure in (TypeError('unsupported'), LengthFailure('subclass'), ValueError('failed'), AttributeError('failed')):
                events.clear()
                try: print(list(Source()), events)
                except BaseException as error: print(error is failure, events)
            """
        );

    [Fact]
    public Task LengthAcceptsIndexValuesButLengthHintRequiresAnActualInteger() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self):
                    events.append('index')
                    return 1
            class Source:
                def __iter__(self): return iter([7])
                def __len__(self): return Index()
            print(list(Source()), events)
            del Source.__len__
            Source.__length_hint__ = lambda self: Index()
            events.clear()
            try: list(Source())
            except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task UnsupportedHintsAndHintCallTypeErrorsUseTheDefaultEstimate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class HintFailure(TypeError): pass
            class Source:
                def __iter__(self):
                    events.append('iter')
                    return iter([1, 2])
                def __length_hint__(self):
                    events.append('hint')
                    if isinstance(result, BaseException): raise result
                    return result
            for result in (NotImplemented, TypeError('unsupported'), HintFailure('subclass'), True, False):
                events.clear()
                print(list(Source()), events)
            Source.__length_hint__ = None
            print(list(Source()))
            """
        );

    [Fact]
    public Task InvalidHintResultsRaiseBeforeConsumingItemsWithoutLargeAllocations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self):
                    events.append('iter')
                    return self
                def __next__(self):
                    events.append('next')
                    raise StopIteration
                def __length_hint__(self): return result
            for result in (-1, 1.5, None, '1', 2 ** 100):
                events.clear()
                try: list(Source())
                except (TypeError, ValueError, OverflowError) as error:
                    print(type(error).__name__, str(error), events)
            """
        );

    [Fact]
    public Task HintDescriptorLookupErrorsPropagateWhileLengthDescriptorTypeErrorsFallBack() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Descriptor:
                def __get__(self, instance, owner):
                    events.append('bind')
                    raise failure
            class Source:
                def __iter__(self):
                    events.append('iter')
                    return iter([7])
            for name in ('__len__', '__length_hint__'):
                setattr(Source, name, Descriptor())
                for failure in (TypeError('type'), AttributeError('attribute'), ValueError('value')):
                    events.clear()
                    try: print(name, list(Source()), events)
                    except BaseException as error: print(name, error is failure, events)
                delattr(Source, name)
            """
        );

    [Fact]
    public Task LengthAndHintUseTypeSlotsInsteadOfInstanceAttributesOrGetattribute() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self): return iter([7])
                def __length_hint__(self):
                    events.append('class hint')
                    return 0
                def __getattribute__(self, name):
                    if name in ('__len__', '__length_hint__'): raise RuntimeError('ordinary lookup')
                    return super().__getattribute__(name)
            source = Source()
            source.__len__ = lambda: 99
            source.__length_hint__ = lambda: 99
            print(list(source), events)
            Source.__len__ = lambda self: 0
            events.clear()
            print(list(source), events)
            """
        );

    [Fact]
    public Task IteratorCreationCanReplaceLengthAndItsFailurePreventsAnyHintLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def changed(self):
                events.append('changed len')
                return 0
            class Source:
                def __iter__(self):
                    events.append('iter')
                    Source.__len__ = changed
                    return iter([7])
                def __len__(self):
                    events.append('old len')
                    return 0
            print(list(Source()), events)
            failure = ValueError('iterator failed')
            def fail(self): raise failure
            Source.__iter__ = fail
            events.clear()
            try: list(Source())
            except ValueError as error: print(error is failure, events)
            """
        );

    [Fact]
    public Task SequenceIteratorHintsTrackCurrentLengthAndBecomeZeroAfterExhaustion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Sequence:
                size = 3
                def __len__(self):
                    events.append('len')
                    return self.size
                def __getitem__(self, index):
                    if index >= self.size: raise IndexError
                    return index
            sequence = Sequence()
            iterator = iter(sequence)
            print(iterator.__length_hint__(), events)
            print(next(iterator), iterator.__length_hint__())
            sequence.size = 0
            print(iterator.__length_hint__())
            sequence.size = 4
            print(iterator.__length_hint__())
            sequence.size = 0
            print(next(iterator, 'done'))
            events.clear()
            sequence.size = 9
            print(iterator.__length_hint__(), next(iterator, 'still done'), events)
            """
        );

    [Fact]
    public Task SequenceIteratorHintsReturnNotImplementedWithoutLengthAndIgnoreSourceHints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Sequence:
                def __getitem__(self, index): raise IndexError
                def __length_hint__(self):
                    events.append('source hint')
                    return 7
            iterator = iter(Sequence())
            print(iterator.__length_hint__() is NotImplemented, events)
            Sequence.__len__ = lambda self: 3
            print(iterator.__length_hint__(), events)
            del Sequence.__len__
            print(iterator.__length_hint__() is NotImplemented, events)
            print(next(iterator, 'done'), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task DirectSequenceHintsPropagateLengthValidationAndListOnlyIgnoresTypeErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                def __len__(self): return result
                def __getitem__(self, index): raise IndexError
            for result in (None, 1.5, -1, 2 ** 100):
                iterator = iter(Sequence())
                try: print('hint', iterator.__length_hint__())
                except (TypeError, ValueError, OverflowError) as error:
                    print('hint', type(error).__name__, str(error))
                try: print('list', list(iterator))
                except (TypeError, ValueError, OverflowError) as error:
                    print('list', type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task TupleAndGroupConstructionSkipHintsWhileListsConsultThem() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            leaf = ValueError('leaf')
            class Sequence:
                def __len__(self):
                    events.append('len')
                    return 1
                def __length_hint__(self):
                    events.append('hint')
                    return 1
                def __getitem__(self, index):
                    events.append(index)
                    if index == 1: raise IndexError
                    return leaf
            sequence = Sequence()
            root = ExceptionGroup('root', [leaf])
            for consume in (list, tuple, lambda value: ExceptionGroup('new', value), root.derive):
                events.clear()
                consume(sequence)
                print(events)
            """
        );

    [Fact]
    public Task GroupNoteCopyingConsultsLengthAndPropagatesItsFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = ValueError('note length failed')
            class Notes:
                failing = False
                def __len__(self):
                    events.append('len')
                    if self.failing: raise failure
                    return 1
                def __getitem__(self, index):
                    events.append(index)
                    if index == 1: raise IndexError
                    return 'note'
            root = ExceptionGroup('root', [ValueError('matched'), TypeError('remaining')])
            notes = Notes()
            root.__notes__ = notes
            print(root.subgroup(ValueError).__notes__, events)
            notes.failing = True
            events.clear()
            try: root.subgroup(ValueError)
            except ValueError as error: print(error is failure, events)
            """
        );

    [Fact]
    public Task ListExtendRequestsHintsBeforeAppendingAndPreservesPartialIterationResults() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            failure = RuntimeError('iteration failed')
            class Source:
                def __iter__(self):
                    events.append(('iter', tuple(target)))
                    return self
                def __length_hint__(self):
                    events.append(('hint', tuple(target)))
                    return 1
                index = 0
                def __next__(self):
                    events.append(('next', tuple(target)))
                    if self.index == 2: raise failure
                    self.index += 1
                    return self.index
            target = [0]
            try: target.extend(Source())
            except RuntimeError as error: print(error is failure, target, events)
            """
        );

    [Fact]
    public Task SmallInaccurateHintsNeverTruncateOrPadTheActualResults() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Source:
                def __iter__(self): return iter([1, 2, 3])
                def __length_hint__(self): return hint
            for hint in (0, 1, 7):
                print(list(Source()))
                target = [0]
                target.extend(Source())
                print(target)
            """
        );

    [Fact]
    public Task NonTypeErrorHintFailuresPreserveIdentityAndDoNotConsumeTheIterator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self): return self
                def __next__(self):
                    events.append('next')
                    raise StopIteration
                def __length_hint__(self): raise failure
            for failure in (ValueError('value'), AttributeError('attribute'), StopIteration('stop')):
                source = Source()
                try: list(source)
                except BaseException as error: print(error is failure, events)
            """
        );
}
