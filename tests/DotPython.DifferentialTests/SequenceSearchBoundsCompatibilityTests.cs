using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SequenceSearchBoundsCompatibilityTests
{
    [Fact]
    public Task OptionalBoundsSelectTheFirstMatchWithinTheRequestedWindow() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for values in ([1, 2, 1, 2, 1], (1, 2, 1, 2, 1)):
                print(values.index(1), values.index(1, 1), values.index(1, 3, 5))
                print(values.index(2, -4, -1), values.index(1, False, True))
                for start, stop in ((1, 2), (3, 3), (4, 2), (9, 99), (-9, -8)):
                    try: values.index(1, start, stop)
                    except ValueError as error: print(str(error))
            """
        );

    [Fact]
    public Task ArbitraryIntegerBoundsAreClippedWithoutOverflow() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            huge = 2 ** 100
            for values in ([10, 20, 30], (10, 20, 30)):
                print(values.index(20, -huge, huge))
                for start, stop in ((huge, huge), (-huge, -huge), (-(2 ** 63), 2 ** 63), (2 ** 63, huge)):
                    try: print(values.index(20, start, stop))
                    except ValueError as error: print(str(error))
            """
        );

    [Fact]
    public Task UserBoundsConvertStartThenStopExactlyOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            for values in ([10, 20, 30], (10, 20, 30)):
                events.clear()
                print(values.index(20, Index('start', -(2 ** 100)), Index('stop', 2 ** 100)), events)
                events.clear()
                print(values.index(20, Index('start', 1)), events)
            """
        );

    [Fact]
    public Task ReusingOneBoundObjectConvertsEachArgumentSeparately() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self): self.calls = 0
                def __index__(self): self.calls += 1; return self.calls
            for values in ([10, 20, 30], (10, 20, 30)):
                index = Index()
                print(values.index(20, index, index), index.calls)
            """
        );

    [Fact]
    public Task NoneAndMissingIndexBoundsAreRejectedEvenForEmptyWindows() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Missing: pass
            for values in ([], (), [10], (10,)):
                for bound in (None, 1.5, '1', Missing()):
                    try: values.index(10, bound)
                    except TypeError as error: print(str(error))
                    try: values.index(10, 100, bound)
                    except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task InvalidStartResultsStopBeforeStopConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, result): self.name = name; self.result = result
                def __index__(self): events.append(self.name); return self.result
            for values in ([10], (10,)):
                for result in (None, 1.5, '1', NotImplemented):
                    for bad in ('start', 'stop'):
                        events.clear()
                        start = Index('start', result if bad == 'start' else 0)
                        stop = Index('stop', result if bad == 'stop' else 1)
                        try: values.index(10, start, stop)
                        except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task DisabledIndexMethodsRetainTheirCallError() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Disabled: __index__ = None
            for values in ([10], (10,)):
                try: values.index(10, Disabled())
                except TypeError as error: print(str(error))
                try: values.index(10, 0, Disabled())
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task BoundLookupUsesTheClassAndObservesMethodReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base:
                def __index__(self): events.append('base'); return 1
            class Index(Base): pass
            bound = Index()
            bound.__index__ = lambda: 0
            for values in ([10, 10, 10], (10, 10, 10)):
                print(values.index(10, bound), events)
            def replacement(self): events.append('replacement'); return 2
            Base.__index__ = replacement
            for values in ([10, 10, 10], (10, 10, 10)):
                print(values.index(10, bound), events)
            """
        );

    [Fact]
    public Task NegativeBoundsNormalizeAfterBothCallbacksMutateTheList() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [10, 20]
            events = []
            class Start:
                def __index__(self): events.append('start'); values.append(30); return -2
            class Stop:
                def __index__(self): events.append('stop'); values.clear(); values.extend([70, 80, 90, 100]); return -1
            print(values.index(90, Start(), Stop()), values, events)
            """
        );

    [Fact]
    public Task BoundFailuresPreserveExceptionIdentityAndEarlierMutations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('bound failed')
            for failing in ('start', 'stop'):
                values = [10, 20]
                events = []
                class Index:
                    def __init__(self, name): self.name = name
                    def __index__(self):
                        events.append(self.name)
                        values.append(30)
                        if self.name == failing: raise problem
                        return 0
                try: values.index(10, Index('start'), Index('stop'))
                except LookupError as error: print(error is problem, str(error), values, events)
            """
        );

    [Fact]
    public Task ListSearchCanReachItemsAppendedByEqualityWithinTheOriginalStop() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for bounded in (False, True):
                events = []
                class Item:
                    def __eq__(self, other): events.append('eq'); values.append(99); return False
                values = [Item()]
                if bounded: print(values.index(99, 0, 10), len(values), events)
                else: print(values.index(99), len(values), events)
            """
        );

    [Fact]
    public Task ListSearchStopsAtTheConvertedStopDespiteAppendedMatches() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); values.append(99); return False
            values = [Item()]
            try: values.index(99, 0, 1)
            except ValueError as error: print(str(error), len(values), events)
            """
        );

    [Fact]
    public Task ListSearchUsesLivePositionsAfterEqualityDeletesOrClearsItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for action in ('delete', 'clear'):
                events = []
                class Item:
                    def __eq__(self, other):
                        events.append('eq')
                        if action == 'delete': del values[0]
                        else: values.clear()
                        return False
                values = [Item(), 99, 99]
                try: print(values.index(99, 0, 10))
                except ValueError as error: print(str(error))
                print(values, events)
            """
        );

    [Fact]
    public Task TruthConversionMutationsAreVisibleAndSuccessfulComparisonsRetainTheirPosition() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for answer in (False, True):
                events = []
                class Truth:
                    def __bool__(self):
                        events.append('bool')
                        values.clear()
                        values.extend([70, 99])
                        return answer
                class Item:
                    def __eq__(self, other): events.append('eq'); return Truth()
                values = [Item()]
                print(values.index(99, 0, 10), values, events)
            """
        );

    [Fact]
    public Task EqualityAndTruthFailuresPreserveExceptionIdentityAndStopSearching() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('comparison failed')
            for sequence in (list, tuple):
                for failing in ('eq', 'bool'):
                    events = []
                    class Truth:
                        def __bool__(self): events.append('bool'); raise problem
                    class Item:
                        def __eq__(self, other):
                            events.append('eq')
                            if failing == 'eq': raise problem
                            return Truth()
                    values = sequence([Item(), 99])
                    try: values.index(99, 0, 2)
                    except LookupError as error: print(error is problem, str(error), events)
            """
        );

    [Fact]
    public Task IdenticalNaNsAndSelfRejectingObjectsMatchWithoutEqualityCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); raise RuntimeError('must not compare self')
            for sequence in (list, tuple):
                for item in (float('nan'), Item()):
                    values = sequence([item, item])
                    print(values.index(item), values.index(item, 1, 2), events)
            """
        );

    [Fact]
    public Task SearchFailureMessagesDoNotInvokeTheMissingValuesRepresentation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Missing:
                def __repr__(self): events.append('repr'); raise RuntimeError('do not format')
            for values in ([], (), [10], (10,)):
                for bounds in ((), (0,), (0, 10)):
                    try: values.index(Missing(), *bounds)
                    except ValueError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task AllArgumentsAreEvaluatedBeforeBoundsAndComparisons() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Bound:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            class Item:
                def __eq__(self, other): events.append('eq'); return True
            def target(): events.append('target'); return values
            def needle(): events.append('needle'); return 99
            def start(): events.append('start argument'); return Bound('start index', 0)
            def stop(): events.append('stop argument'); return Bound('stop index', 1)
            for values in ([Item()], (Item(),)):
                events.clear()
                print(target().index(needle(), start(), stop()), events)
            """
        );
}
