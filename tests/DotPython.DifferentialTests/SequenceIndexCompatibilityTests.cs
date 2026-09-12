using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SequenceIndexCompatibilityTests
{
    [Fact]
    public Task SliceBoundsConvertStepStartAndStopExactlyOnceInThatOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            for value in ([0, 1, 2, 3, 4], (0, 1, 2, 3, 4), 'abcde'):
                events.clear()
                print(value[Index('start', 1):Index('stop', 5):Index('step', 2)], events)
            """
        );

    [Fact]
    public Task NegativeSliceStepsUseConvertedBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            for value in ([0, 1, 2, 3, 4], (0, 1, 2, 3, 4), 'abcde'):
                events.clear()
                print(value[Index('start', 4):Index('stop', 0):Index('step', -2)], events)
                print(value[::Index('reverse', -1)], events)
            """
        );

    [Fact]
    public Task ReusingOneIndexObjectStillConvertsEverySliceComponent() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self): self.calls = 0
                def __index__(self):
                    self.calls += 1
                    return self.calls
            for value in ([0, 1, 2, 3, 4], (0, 1, 2, 3, 4), 'abcde'):
                bound = Index()
                print(value[bound:bound:bound], bound.calls)
            """
        );

    [Fact]
    public Task HugeConvertedBoundsAndStepsAreClipped() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
            huge = 2 ** 100
            for value in ([0, 1, 2, 3], (0, 1, 2, 3), 'abcd'):
                print(value[Index(-huge):Index(huge):Index(1)])
                print(value[Index(huge):Index(-huge):Index(-1)])
                print(value[Index(1)::Index(huge)], value[Index(2)::Index(-huge)])
            """
        );

    [Fact]
    public Task NoneAndBooleanSliceComponentsRetainBuiltinSemantics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __index__(self): return 1
            for value in ([0, 1, 2, 3], (0, 1, 2, 3), 'abcd'):
                print(value[False:None:Index()], value[Index():True:None])
                print(value[None:Index():True], value[None:None:Index()])
            """
        );

    [Fact]
    public Task ZeroConvertedStepStopsBeforeConvertingOtherBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            for value in ([0, 1], (0, 1), 'ab'):
                events.clear()
                try: value[Index('start', 0):Index('stop', 1):Index('step', 0)]
                except ValueError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task InvalidIndexResultsStopSliceConversionAtTheFailingComponent() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            for bad in (1.5, '1', None, NotImplemented):
                for name in ('step', 'start', 'stop'):
                    events.clear()
                    start = Index('start', bad if name == 'start' else 0)
                    stop = Index('stop', bad if name == 'stop' else 3)
                    step = Index('step', bad if name == 'step' else 1)
                    try: [0, 1, 2][start:stop:step]
                    except TypeError as error: print(name, str(error), events)
            """
        );

    [Fact]
    public Task SliceIndexExceptionsPreserveIdentityAndStopLaterCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            problem = LookupError('bound failed')
            class Index:
                def __init__(self, name): self.name = name
                def __index__(self):
                    events.append(self.name)
                    if self.name == 'start': raise problem
                    return 1
            try: (0, 1, 2)[Index('start'):Index('stop'):Index('step')]
            except LookupError as error: print(error is problem, str(error), events)
            """
        );

    [Fact]
    public Task IndexLookupUsesTheClassAndTracksMethodReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base:
                def __index__(self): events.append('base'); return 1
            class Index(Base): pass
            bound = Index()
            bound.__index__ = lambda: 0
            print([0, 1, 2][bound:], (7,) * bound, events)
            def replacement(self): events.append('replacement'); return 2
            Base.__index__ = replacement
            print([0, 1, 2][bound:], (7,) * bound, events)
            """
        );

    [Fact]
    public Task ListSlicingObservesMutationsMadeWhileConvertingBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            value = [0, 1, 2, 3]
            class Index:
                def __init__(self, name): self.name = name
                def __index__(self):
                    events.append(self.name)
                    if self.name == 'step': value.append(4); return 1
                    if self.name == 'start': value.clear(); value.extend([8, 9, 10]); return 1
                    value.append(11)
                    return 100
            print(value[Index('start'):Index('stop'):Index('step')], value, events)
            """
        );

    [Fact]
    public Task ListSliceAssignmentConvertsBoundsAfterEvaluatingTheReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            value = [0, 1, 2, 3]
            class Index:
                def __init__(self, name, result): self.name = name; self.result = result
                def __index__(self): events.append(self.name); return self.result
            def replacement(): events.append('replacement'); return [8, 9]
            value[Index('start', 1):Index('stop', 3):Index('step', 1)] = replacement()
            print(value, events)
            """
        );

    [Fact]
    public Task ExtendedSliceAssignmentNormalizesAgainstCallbackMutations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            value = [0, 1, 2, 3]
            class Index:
                def __init__(self, name, result): self.name = name; self.result = result
                def __index__(self):
                    events.append(self.name)
                    if self.name == 'step': value.extend([4, 5])
                    return self.result
            value[Index('start', 1):Index('stop', 100):Index('step', 2)] = [8, 9, 10]
            print(value, events)
            """
        );

    [Fact]
    public Task SliceDeletionNormalizesAfterAllIndexCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            value = [0, 1, 2, 3]
            class Index:
                def __init__(self, name, result): self.name = name; self.result = result
                def __index__(self):
                    events.append(self.name)
                    if self.name == 'stop': value.extend([4, 5])
                    return self.result
            del value[Index('start', 5):Index('stop', -100):Index('step', -2)]
            print(value, events)
            """
        );

    [Fact]
    public Task FailedSliceAssignmentPreservesCallbackMutationsWithoutAssigning() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = [0, 1, 2]
            class Index:
                def __index__(self): value.append(3); raise ValueError('index failed')
            try: value[Index():] = [8, 9]
            except ValueError as error: print(str(error), value)
            class Wrong:
                def __index__(self): return 2
            try: value[::Wrong()] = [7]
            except ValueError as error: print(str(error), value)
            """
        );

    [Fact]
    public Task RepetitionConvertsUserCountsOnceInBothDirections() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): events.append(self.value); return self.value
            for value in ([1, 2], (1, 2), 'ab'):
                for count in (-2, 0, 1, 2):
                    events.clear()
                    print(value * Index(count), Index(count) * value, events)
            """
        );

    [Fact]
    public Task RepetitionChecksSignedIndexWidthEvenForEmptySequences() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): events.append(self.value); return self.value
            for value in ([], (), '', [1], (1,), 'a'):
                for count in (-(2 ** 63) - 1, 2 ** 63):
                    events.clear()
                    try: value * Index(count)
                    except OverflowError as error: print(type(value).__name__, str(error), events)
                    events.clear()
                    try: Index(count) * value
                    except OverflowError as error: print(type(value).__name__, str(error), events)
            """
        );

    [Fact]
    public Task RepetitionRejectsNonIntegerIndexResults() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
            for value in ([1], (1,), 'a'):
                for result in (None, 1.5, '2', NotImplemented):
                    try: value * Index(result)
                    except TypeError as error: print(str(error))
                    try: Index(result) * value
                    except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task RepetitionPreservesIndexCallbackExceptionsAndListMutations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            problem = ValueError('repeat failed')
            value = [1, 2]
            class Index:
                def __index__(self): value.append(3); events.append('index'); raise problem
            try: value * Index()
            except ValueError as error: print(error is problem, str(error), value, events)
            class Mutating:
                def __index__(self): value.append(4); return 2
            print(value * Mutating(), value)
            """
        );

    [Fact]
    public Task MultiplicationMethodsTakePrecedenceOverIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self): events.append('index'); return 2
                def __mul__(self, other): events.append('mul'); return 'forward'
                def __rmul__(self, other): events.append('rmul'); return 'reflected'
            for value in ([1], (1,), 'a'):
                events.clear()
                print(value * Index(), Index() * value, events)
            """
        );

    [Fact]
    public Task NotImplementedMultiplicationFallsBackToIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self): events.append('index'); return 2
                def __mul__(self, other): events.append('mul'); return NotImplemented
                def __rmul__(self, other): events.append('rmul'); return NotImplemented
            for value in ([1], (1,), 'a'):
                events.clear()
                print(value * Index(), Index() * value, events)
            """
        );

    [Fact]
    public Task InPlaceListRepetitionPreservesAliasesAndConvertsBeforeReadingLength() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            value = [1, 2]
            alias = value
            class Index:
                def __index__(self): events.append('index'); value.append(3); return 2
            value *= Index()
            print(value is alias, value, alias, events)
            class Empty:
                def __index__(self): return 0
            value *= Empty()
            print(value is alias, value, alias)
            """
        );

    [Fact]
    public Task InPlaceListRepetitionHonorsReflectedMultiplicationBeforeIndex() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self): events.append('index'); return 2
                def __rmul__(self, other): events.append('rmul'); return 'replacement'
            value = [1, 2]
            alias = value
            value *= Index()
            print(value is alias, value, alias, events)
            class Fallback:
                def __index__(self): events.append('index'); return 2
                def __rmul__(self, other): events.append('rmul'); return NotImplemented
            events.clear()
            value = [1, 2]
            alias = value
            value *= Fallback()
            print(value is alias, value, alias, events)
            """
        );

    [Fact]
    public Task InPlaceUserMultiplicationCallsEachFallbackMethodOnlyOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __imul__(self, other): events.append('imul'); return NotImplemented
                def __mul__(self, other): events.append('mul'); return NotImplemented
                def __index__(self): events.append('index'); return 2
            for value in ([1, 2], (1, 2), 'ab'):
                events.clear()
                count = Index()
                try: count *= value
                except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task ExplicitlyDisabledIndexMethodsRaiseTheirCallError() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                __index__ = None
            for value in ([1, 2], (1, 2), 'ab'):
                try: value[Index():]
                except TypeError as error: print(str(error))
                try: value * Index()
                except TypeError as error: print(str(error))
                try: Index() * value
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task SliceAssignmentConvertsBoundsBeforeIteratingTheReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            class Iterator:
                def __init__(self): self.position = 0
                def __iter__(self): events.append('iterator iter'); return self
                def __length_hint__(self): events.append('iterator hint'); return 2
                def __next__(self):
                    events.append('next')
                    if self.position == 2: raise StopIteration
                    self.position += 1
                    return self.position + 7
            class Replacement:
                def __iter__(self): events.append('replacement iter'); return Iterator()
                def __length_hint__(self): events.append('replacement hint'); return 99
            value = [0, 1, 2, 3]
            value[Index('start', 1):Index('stop', 3):Index('step', 1)] = Replacement()
            print(value, events)
            """
        );

    [Fact]
    public Task ExtendedSliceAssignmentReadsLengthAfterReplacementIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            value = [0, 1, 2, 3]
            class Index:
                def __index__(self): events.append('index'); return 2
            class Replacement:
                def __iter__(self):
                    events.append('replacement')
                    value.extend([4, 5])
                    yield 8
                    yield 9
                    yield 10
            value[::Index()] = Replacement()
            print(value, events)
            """
        );

    [Fact]
    public Task ExplicitlyDisabledMultiplicationMethodsFailBeforeIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Reflected:
                __rmul__ = None
                def __index__(self): events.append('index'); return 2
            class Forward:
                __mul__ = None
                def __index__(self): events.append('index'); return 2
            class InPlace:
                __imul__ = None
                def __mul__(self, other): events.append('mul'); return NotImplemented
                def __index__(self): events.append('index'); return 2
            for value in ([1, 2], (1, 2), 'ab'):
                events.clear()
                try: value * Reflected()
                except TypeError as error: print(str(error), events)
                try: Forward() * value
                except TypeError as error: print(str(error), events)
                count = InPlace()
                try: count *= value
                except TypeError as error: print(str(error), events)
            value = [1, 2]
            alias = value
            try: value *= Reflected()
            except TypeError as error: print(str(error), value is alias, value, events)
            """
        );

    [Fact]
    public Task TupleIndexIdentityOperationsRetainTheSuccessfulHashCache() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 11
            class Index:
                def __index__(self): events.append('index'); return 1
            value = (Key(),)
            first = hash(value)
            for alias in (value[::Index()], value * Index(), Index() * value):
                print(alias is value, hash(alias) == first, events)
            alias = value
            alias *= Index()
            print(alias is value, hash(alias) == first, events)
            """
        );
}
