using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ExtremumCompatibilityTests
{
    [Fact]
    public Task MinAndMaxInvokeOnlyTheirRequestedCandidateFirstOperator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, value): self.value = value
                def __eq__(self, other): events.append('eq'); raise RuntimeError('no equality')
                def __lt__(self, other): events.append(('lt', self.value, other.value)); return self.value < other.value
                def __gt__(self, other): events.append(('gt', self.value, other.value)); return self.value > other.value
            for choose in (min, max):
                events.clear()
                print(choose([Item(2), Item(3), Item(1)]).value, events)
            """
        );

    [Fact]
    public Task FalseComparisonsDoNotProbeTheReverseDirection() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, name): self.name = name
                def __lt__(self, other): events.append(('lt', self.name, other.name)); return False
                def __gt__(self, other): events.append(('gt', self.name, other.name)); return False
            for choose in (min, max):
                events.clear()
                first = Item('first')
                print(choose(first, Item('second'), Item('third')) is first, events)
            """
        );

    [Fact]
    public Task ComparisonResultsAreTruthConvertedExactlyOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Truth:
                def __init__(self, answer): self.answer = answer
                def __bool__(self): events.append('bool'); return self.answer
            class Item:
                def __init__(self, value): self.value = value
                def __lt__(self, other): events.append('lt'); return Truth(self.value < other.value)
                def __gt__(self, other): events.append('gt'); return Truth(self.value > other.value)
            for choose in (min, max):
                events.clear()
                print(choose([Item(2), Item(3), Item(1)]).value, events)
            """
        );

    [Fact]
    public Task NotImplementedUsesTheCorrespondingReflectedOperator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class First:
                def __lt__(self, other): events.append('first lt'); return True
                def __gt__(self, other): events.append('first gt'); return True
            class Next:
                def __lt__(self, other): events.append('next lt'); return NotImplemented
                def __gt__(self, other): events.append('next gt'); return NotImplemented
            for choose in (min, max):
                events.clear()
                last = Next()
                print(choose(First(), last) is last, events)
            """
        );

    [Fact]
    public Task IncumbentSubtypeReflectedMethodsTakePriority() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base:
                def __lt__(self, other): events.append('base lt'); return False
                def __gt__(self, other): events.append('base gt'); return False
            class Child(Base):
                def __lt__(self, other): events.append('child lt'); return True
                def __gt__(self, other): events.append('child gt'); return True
            for choose in (min, max):
                events.clear()
                candidate = Base()
                print(choose(Child(), candidate) is candidate, events)
            """
        );

    [Fact]
    public Task NaNsPreserveTheIncumbentWhenTheRequestedComparisonIsFalse() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for choose in (min, max):
                nan = float('nan')
                for values in ([nan, 1.0, 2.0], [1.0, nan, 2.0], [2.0, nan, 1.0], [nan, nan]):
                    result = choose(values)
                    print(result is nan, result if result is not nan else 'nan')
            """
        );

    [Fact]
    public Task EqualKeysKeepTheFirstOriginalObjectAndKeysAreEvaluatedOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, name): self.name = name
            first = Item('first')
            second = Item('second')
            third = Item('third')
            def key(value): events.append(value.name); return 1
            for choose in (min, max):
                events.clear()
                print(choose([first, second, third], key=key) is first, events)
                events.clear()
                print(choose(first, second, third, key=key) is first, events)
            """
        );

    [Fact]
    public Task NaNKeysRetainTheirFirstIncumbentWithoutComparingOriginalItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, name): self.name = name
                def __lt__(self, other): raise RuntimeError('do not compare original')
                def __gt__(self, other): raise RuntimeError('do not compare original')
            first = Item('first')
            second = Item('second')
            def key(value): events.append(value.name); return float('nan')
            for choose in (min, max):
                events.clear()
                print(choose([first, second], key=key) is first, events)
            """
        );

    [Fact]
    public Task IteratorAdvanceKeyAndComparisonAreInterleaved() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, value): self.value = value
                def __lt__(self, other): events.append(('lt', self.value, other.value)); return self.value < other.value
                def __gt__(self, other): events.append(('gt', self.value, other.value)); return self.value > other.value
            class Source:
                def __init__(self): self.position = 0
                def __iter__(self): events.append('iter'); return self
                def __next__(self):
                    events.append(('next', self.position))
                    if self.position == 3: raise StopIteration
                    self.position += 1
                    return self.position
                def __length_hint__(self): events.append('hint'); raise RuntimeError('no hint')
            def key(value): events.append(('key', value)); return Key(value)
            for choose in (min, max):
                events.clear()
                print(choose(Source(), key=key), events)
            """
        );

    [Fact]
    public Task ExtremaDoNotRequestIterableOrIteratorLengthHints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self): events.append('iter'); return iter([3, 1, 2])
                def __len__(self): events.append('len'); raise RuntimeError('no len')
                def __length_hint__(self): events.append('hint'); raise RuntimeError('no hint')
            for choose in (min, max):
                events.clear()
                print(choose(Source(), key=None), events)
            """
        );

    [Fact]
    public Task KeyFailuresStopBeforeRequestingFurtherItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('key failed')
            events = []
            def source():
                for value in (1, 2, 3): events.append(('next', value)); yield value
            def key(value):
                events.append(('key', value))
                if value == 2: raise problem
                return value
            for choose in (min, max):
                events.clear()
                iterator = source()
                try: choose(iterator, key=key)
                except LookupError as error: print(error is problem, str(error), events)
                print(next(iterator), events)
            """
        );

    [Fact]
    public Task ComparisonFailuresStopBeforeRequestingFurtherItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('comparison failed')
            events = []
            class Item:
                def __init__(self, value): self.value = value
                def __lt__(self, other): events.append('lt'); raise problem
                def __gt__(self, other): events.append('gt'); raise problem
            def source():
                for value in (1, 2, 3): events.append(('next', value)); yield Item(value)
            for choose in (min, max):
                events.clear()
                iterator = source()
                try: choose(iterator)
                except LookupError as error: print(error is problem, str(error), events)
                print(next(iterator).value, events)
            """
        );

    [Fact]
    public Task TruthFailuresPreserveIdentityAndStopIterationImmediately() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('truth failed')
            events = []
            class Truth:
                def __bool__(self): events.append('bool'); raise problem
            class Item:
                def __lt__(self, other): events.append('lt'); return Truth()
                def __gt__(self, other): events.append('gt'); return Truth()
            def source():
                for value in (1, 2, 3): events.append(('next', value)); yield Item()
            for choose in (min, max):
                events.clear()
                iterator = source()
                try: choose(iterator)
                except LookupError as error: print(error is problem, str(error), events)
                next(iterator)
                print(events)
            """
        );

    [Fact]
    public Task IteratorFailuresOccurAfterProcessingPreviouslyYieldedItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('iterator failed')
            events = []
            def source():
                events.append('next one')
                yield 1
                events.append('next two')
                yield 2
                events.append('failure')
                raise problem
            def key(value): events.append(('key', value)); return value
            for choose in (min, max):
                events.clear()
                try: choose(source(), key=key)
                except LookupError as error: print(error is problem, str(error), events)
            """
        );

    [Fact]
    public Task ListMutationsInKeysAffectSubsequentIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for choose in (min, max):
                for action in ('append', 'clear', 'delete'):
                    values = [3, 2]
                    events = []
                    def key(value):
                        events.append(value)
                        if value == 3:
                            if action == 'append': values.extend([1, 4])
                            elif action == 'clear': values.clear()
                            else: del values[0]
                        return value
                    print(action, choose(values, key=key), values, events)
            """
        );

    [Fact]
    public Task ComparisonMutationsCanAppendFutureCandidatesOrEndListIteration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for choose in (min, max):
                for action in ('append', 'clear'):
                    events = []
                    class Item:
                        def __init__(self, value): self.value = value
                        def compare(self, other, greater):
                            events.append((self.value, other.value))
                            if self.value == 2:
                                if action == 'append': values.extend([Item(0), Item(4)])
                                else: values.clear()
                            return self.value > other.value if greater else self.value < other.value
                        def __lt__(self, other): return self.compare(other, False)
                        def __gt__(self, other): return self.compare(other, True)
                    values = [Item(1), Item(2), Item(3)]
                    print(action, choose(values).value, len(values), events)
            """
        );

    [Fact]
    public Task EmptyIterablesReturnDefaultWithoutCallingOrValidatingTheKey() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Default:
                def __lt__(self, other): raise RuntimeError('do not compare default')
                def __gt__(self, other): raise RuntimeError('do not compare default')
            def key(value): events.append('key'); raise RuntimeError('do not key default')
            def source():
                events.append('iterated')
                return
                yield 1
            fallback = Default()
            for choose in (min, max):
                for selected_key in (None, key, 42):
                    events.clear()
                    print(choose(source(), default=fallback, key=selected_key) is fallback, events)
                try: choose([])
                except ValueError as error: print(str(error))
            """
        );

    [Fact]
    public Task SingleAndMultiplePositionalFormsPreserveKeysAndExplicitNone() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def key(value): events.append(value); return -value
            for choose in (min, max):
                events.clear()
                print(choose(3, 1, 2, key=key), events)
                print(choose([3, 1, 2], key=None), choose(3, 1, 2, key=None))
                events.clear()
                print(choose([3], key=key), events)
                print(choose([], default=None) is None)
            """
        );

    [Fact]
    public Task MultiplePositionalDefaultIsRejectedBeforeAnyKeyOrIteratorCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __iter__(self): events.append('iter'); raise RuntimeError('do not iterate')
                def __lt__(self, other): events.append('lt'); raise RuntimeError('do not compare')
                def __gt__(self, other): events.append('gt'); raise RuntimeError('do not compare')
            def key(value): events.append('key'); raise RuntimeError('do not key')
            for choose in (min, max):
                events.clear()
                try: choose(Item(), Item(), default=None, key=key)
                except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task ArgumentValidationPrecedesIterationAndUsesTheBuiltinSpecificMessages() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __iter__(self): events.append('iter'); raise RuntimeError('do not iterate')
            for choose in (min, max):
                for keywords in ({}, {'default': 1}, {'unknown': 1}):
                    try: choose(**keywords)
                    except TypeError as error: print(str(error), events)
                try: choose(Source(), unknown=1)
                except TypeError as error: print(str(error), events)
                try: choose(1, 2, default=0, unknown=1)
                except TypeError as error: print(str(error), events)
            """
        );
}
