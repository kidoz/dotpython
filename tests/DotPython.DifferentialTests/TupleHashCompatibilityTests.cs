using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TupleHashCompatibilityTests
{
    [Fact]
    public Task EmptyAndIntegerTupleHashesMatchPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in ((), (0,), (1,), (-1,), (-2,), (1, 2), (2, 1), (0, 0, 0), tuple(range(20)), (2 ** 63 - 1, -(2 ** 63)), (2 ** 200, -(2 ** 200), 2 ** 61 - 1)):
                print(hash(value))
            """
        );

    [Fact]
    public Task FloatAndComplexTupleHashesUseFullNumericHashLanes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in ((0.5,), (-0.5, 0.25), (float('5e-324'), float('1.7976931348623157e308')), (float('inf'), float('-inf')), (complex(0.5, 0.25),), (complex(3, 4), complex(-3, -4)), (True, 0.0, complex(2, 0))):
                print(hash(value))
            print(hash((True, -0.0, complex(2, 0))) == hash((1, 0, 2)))
            """
        );

    [Fact]
    public Task SignedAndOversizedUserHashesAreNormalizedBeforeTupleMixing() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self, value): self.value = value
                def __hash__(self): return self.value
            for code in (True, False, -1, -2, 2 ** 61, 2 ** 63 - 1, -(2 ** 63), 2 ** 63, -(2 ** 63) - 1, 2 ** 200):
                print(hash((Key(code),)), hash((Key(code), Key(-2))))
            """
        );

    [Fact]
    public Task NestedTuplesAndFrozenSetsRetainFullHashValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (((),), ((1, 2), (3,)), (frozenset(),), (frozenset({1, 2}), (3, 0.5)), ((frozenset({0.25, complex(1, 2)}),), (2 ** 100,))):
                print(hash(value))
            print(hash(frozenset({(1, 2), (3, 4)})))
            print(hash(frozenset({((0.5,),), ((2 ** 100,),)})))
            """
        );

    [Fact]
    public Task SuccessfulTupleHashesSkipElementCallbacksOnLaterRequests() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 41
            key = Key()
            value = (key,)
            first = hash(value)
            print(first, events)
            print(hash(value) == first, hash(value) == first, events)
            print(hash(tuple(value)) == first, events)
            """
        );

    [Fact]
    public Task EqualDistinctTuplesOwnIndependentSuccessfulCaches() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 41
            key = Key()
            first = (key,)
            second = (key,)
            print(first is second, first == second)
            print(hash(first), events)
            print(hash(second), events)
            print(hash(first) == hash(second), events)
            """
        );

    [Fact]
    public Task FirstTupleHashVisitsRepeatedElementsInOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self): events.append(self.name); return self.code
            first = Key('first', 1)
            second = Key('second', 2)
            value = (first, second, first)
            print(hash(value), events)
            print(hash(value), events)
            """
        );

    [Fact]
    public Task FailedTupleHashesRetryFromTheFirstElement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code; self.fail = False
                def __hash__(self):
                    events.append(self.name)
                    if self.fail: raise ValueError(self.name)
                    return self.code
            first = Key('first', 1)
            second = Key('second', 2)
            third = Key('third', 3)
            second.fail = True
            value = (first, second, third)
            for attempt in range(2):
                try: hash(value)
                except ValueError as error: print(str(error), events)
            second.fail = False
            print(hash(value), events)
            print(hash(value), events)
            """
        );

    [Fact]
    public Task SuccessfulNestedHashesSurviveAnOuterFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code; self.fail = False
                def __hash__(self):
                    events.append(self.name)
                    if self.fail: raise ValueError(self.name)
                    return self.code
            inner_key = Key('inner', 11)
            outer_key = Key('outer', 13)
            outer_key.fail = True
            inner = (inner_key,)
            outer = (inner, outer_key)
            for attempt in range(2):
                try: hash(outer)
                except ValueError as error: print(str(error), events)
            outer_key.fail = False
            print(hash(outer), events)
            print(hash(inner), hash(outer), events)
            """
        );

    [Fact]
    public Task FailedNestedHashesRetryBothIncompleteTupleTraversals() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code; self.fail = False
                def __hash__(self):
                    events.append(self.name)
                    if self.fail: raise ValueError(self.name)
                    return self.code
            first = Key('first', 11)
            second = Key('second', 13)
            second.fail = True
            inner = (second,)
            outer = (first, inner)
            for attempt in range(2):
                try: hash(outer)
                except ValueError as error: print(str(error), events)
            second.fail = False
            print(hash(outer), events)
            print(hash(outer), hash(inner), events)
            """
        );

    [Fact]
    public Task InvalidElementHashResultsDoNotCacheTupleFailures() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self): self.result = 'invalid'
                def __hash__(self): events.append('hash'); return self.result
            key = Key()
            value = (key,)
            for result in ('invalid', None, 0.5):
                key.result = result
                try: hash(value)
                except TypeError as error: print(type(error).__name__, str(error), events)
            key.result = True
            print(hash(value), events)
            key.result = 'invalid again'
            print(hash(value), events)
            """
        );

    [Fact]
    public Task UnhashableTupleElementsStopTraversalAndFailuresAreRetried() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name): self.name = name
                def __hash__(self): events.append(self.name); return 19
            value = (Key('first'), [], Key('last'))
            for attempt in range(2):
                try: hash(value)
                except TypeError as error: print(type(error).__name__, str(error), events)
            """
        );

    [Fact]
    public Task CachedTupleHashSurvivesElementHashChanges() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('old'); return 11
            key = Key()
            value = (key,)
            first = hash(value)
            def replacement(self): events.append('new'); return 13
            Key.__hash__ = replacement
            print(hash(value) == first, events)
            print(hash((key,)) == hash((13,)), events)
            Key.__hash__ = None
            print(hash(value) == first, events)
            try: hash((key,))
            except TypeError as error: print(type(error).__name__, str(error), events)
            """
        );

    [Fact]
    public Task DictionaryOperationsUseTheTupleCacheAcrossElementHashChanges() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self): self.code = 11
                def __hash__(self): events.append(self.code); return self.code
            key = Key()
            value = (key,)
            values = {value: 'first'}
            key.code = 29
            print(value in values, values[value], events)
            values[value] = 'second'
            print(len(values), values.get(value), events)
            print(values.copy()[value], dict(values)[value], events)
            print(values.pop(value), len(values), events)
            """
        );

    [Fact]
    public Task SetAndFrozenSetOperationsReuseTheTupleCache() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self): self.code = 11
                def __hash__(self): events.append(self.code); return self.code
            key = Key()
            value = (key,)
            values = {value}
            key.code = 29
            print(value in values, events)
            values.add(value)
            print(len(values), value in values.copy(), events)
            frozen = frozenset(values)
            print(value in frozen, hash(frozen) == hash(frozen), events)
            values.remove(value)
            print(len(values), value in frozen, events)
            """
        );

    [Fact]
    public Task EqualTupleKeysDeduplicateWithIndependentCaches() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name): self.name = name
                def __hash__(self): events.append(self.name); return 11
                def __eq__(self, other): return True
            first = (Key('first'),)
            second = (Key('second'),)
            values = {first: 'first', second: 'second'}
            print(len(values), values[first], values[second], events)
            print(next(iter(values)) is first)
            print(len({first, second}), hash(first) == hash(second), events)
            """
        );

    [Fact]
    public Task ReentrantSuccessfulTupleHashIsOverwrittenByTheOuterSuccess() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self): self.calls = 0; self.inner = 0
                def __hash__(self):
                    self.calls += 1
                    events.append(self.calls)
                    if self.calls == 1:
                        self.inner = hash(self.owner)
                        return 13
                    return 17
            key = Key()
            value = (key,)
            key.owner = value
            outer = hash(value)
            print(outer == hash((13,)), key.inner == hash((17,)), events)
            print(hash(value) == outer, events)
            """
        );

    [Fact]
    public Task ReentrantSuccessfulTupleHashSurvivesAnOuterException() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self): self.calls = 0; self.inner = 0
                def __hash__(self):
                    self.calls += 1
                    events.append(self.calls)
                    if self.calls == 1:
                        self.inner = hash(self.owner)
                        raise ValueError('outer failed')
                    return 17
            key = Key()
            value = (key,)
            key.owner = value
            try: hash(value)
            except ValueError as error: print(str(error), events)
            print(hash(value) == key.inner, hash(value) == hash((17,)), events)
            """
        );

    [Fact]
    public Task ReservedMinusOneTupleHashUsesThePythonReplacementAndCachesIt() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return -8496733470247235670
            value = (Key(),)
            print(hash(value), events)
            print(hash(value), events)
            print(hash((value,)))
            """
        );

    [Fact]
    public Task CachedNanTupleHashesPreserveIdentityBasedContainerLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            first_nan = float('nan')
            second_nan = float('nan')
            first = (first_nan,)
            third = (second_nan,)
            print(first == first, first == third)
            print(hash(first) == hash(first), hash(third) == hash(third))
            values = {first: 'first', third: 'third'}
            print(len(values), values[first], values[third], len({first, first, third}))
            """
        );

    [Fact]
    public Task CachedTupleKeysRemainInsertableAfterTheirElementsBecomeUnhashable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 11
            key = Key()
            value = (key,)
            first = hash(value)
            Key.__hash__ = None
            dictionary = {value: 'value'}
            values = {value}
            frozen = frozenset([value])
            print(hash(value) == first, dictionary[value], value in values, value in frozen, events)
            dictionary[value] = 'updated'
            values.add(value)
            print(dictionary.pop(value), len(values), hash(frozen) == hash(frozen), events)
            """
        );

    [Fact]
    public Task TupleKeyInsertionHashesEarlierElementsBeforeReportingUnhashableChildren() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 11
            value = (Key(), [])
            dictionary = {}
            values = set()
            try: dictionary[value] = 1
            except TypeError as error: print(str(error), events, len(dictionary))
            try: values.add(value)
            except TypeError as error: print(str(error), events, len(values))
            try: frozenset([value])
            except TypeError as error: print(str(error), events)
            """
        );
}
