using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TupleIdentityCompatibilityTests
{
    [Fact]
    public Task FullSlicesReuseTheOriginalTupleAfterNormalizingBounds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (tuple(), tuple(range(1)), tuple(range(4))):
                for result in (value[:], value[::], value[::1], value[0:], value[:len(value)], value[-len(value):], value[-100:100], value[-(2 ** 100):2 ** 100:True], value[False:None:True]):
                    print(result is value, result)
            """
        );

    [Fact]
    public Task PartialAndNonUnitSlicesOwnDistinctTupleIdentities() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = tuple(range(4))
            for result in (value[1:], value[:-1], value[::2], value[::-1], value[1:3], value[3:0:-1]):
                print(result is value, result)
            singleton = tuple(range(1))
            for result in (singleton[::2], singleton[::-1], singleton[::2 ** 100], singleton[::-2 ** 100]):
                print(result is singleton, result == singleton, result)
            """
        );

    [Fact]
    public Task EmptyTupleConcatenationPreservesTheNonemptyOperand() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (tuple(range(1)), tuple(range(4))):
                print((value + ()) is value, (() + value) is value)
                print(((value + ()) + ()) is value, (() + (() + value)) is value)
                print((value + value) is value, value + value)
            """
        );

    [Fact]
    public Task RepetitionByOneAndTruePreservesTheOriginalTuple() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (tuple(range(1)), tuple(range(4))):
                print((value * 1) is value, (1 * value) is value)
                print((value * True) is value, (True * value) is value)
                for count in (-3, 0, False, 2, 3):
                    result = value * count
                    reflected = count * value
                    print(result is value, reflected is value, result, reflected)
            """
        );

    [Fact]
    public Task RepeatingAnEmptyTuplePreservesThatTupleForRepresentableCounts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = tuple()
            for count in (-(2 ** 63), -3, 0, False, True, 1, 2, 2 ** 63 - 1):
                print((value * count) is value, (count * value) is value)
            """
        );

    [Fact]
    public Task InPlaceIdentityOperationsPreserveAliasesAndTheHashCache() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 11
            original = (Key(),)
            first = hash(original)
            value = original
            value += ()
            print(value is original, hash(value) == first, events)
            value *= 1
            print(value is original, hash(value) == first, events)
            value *= True
            print(value is original, hash(value) == first, events)
            value += (23,)
            print(value is original, len(original), len(value), events)
            value *= 2
            print(value is original, len(original), len(value), events)
            """
        );

    [Fact]
    public Task IdentityOperationsDoNotHashElementsUntilTheirFirstHashRequest() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 11
            value = (Key(),)
            aliases = (value[:], value[-100:100], value + (), () + value, value * 1, 1 * value)
            print(events)
            for alias in aliases:
                print(alias is value, hash(alias) == hash((11,)), events)
            """
        );

    [Fact]
    public Task IdentityOperationsRetainSuccessfulHashesAfterElementHashChanges() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self): self.code = 11
                def __hash__(self): events.append(self.code); return self.code
            key = Key()
            value = (key,)
            first = hash(value)
            key.code = 29
            for alias in (value[:], value[::1], value[-100:100], value + (), () + value, value * 1, True * value):
                print(alias is value, hash(alias) == first, events)
            print(hash((key,)) == first, events)
            """
        );

    [Fact]
    public Task IdentityAliasesRemainValidContainerKeysAfterElementsBecomeUnhashable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 11
            value = (Key(),)
            first = hash(value)
            Key.__hash__ = None
            for alias in (value[:], value[-100:100:1], value + (), () + value, value * 1, 1 * value):
                dictionary = {alias: 'value'}
                values = {alias}
                frozen = frozenset([alias])
                print(alias is value, hash(alias) == first, dictionary[value], value in values, value in frozen, events)
            """
        );

    [Fact]
    public Task FailedTupleHashesRemainRetryableThroughIdentityAliases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self): self.fail = True
                def __hash__(self):
                    events.append('hash')
                    if self.fail: raise ValueError('retry')
                    return 11
            key = Key()
            value = (key,)
            for alias in (value[:], value + (), value * 1):
                try: hash(alias)
                except ValueError as error: print(alias is value, str(error), events)
            key.fail = False
            first = hash(value[:])
            print(hash(value) == first, hash(() + value) == first, hash(1 * value) == first, events)
            """
        );

    [Fact]
    public Task SlicesThatCreateNewTuplesDoNotInheritTheOriginalHashCache() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name): self.name = name
                def __hash__(self): events.append(self.name); return 11
            key = Key('single')
            singleton = (key,)
            first = hash(singleton)
            for copy in (singleton[::2], singleton[::-1]):
                print(copy is singleton, hash(copy) == first, events)
                print(hash(copy) == first, events)
            value = (key, Key('last'))
            hash(value)
            partial = value[:1]
            print(partial is singleton, hash(partial) == first, events)
            """
        );

    [Fact]
    public Task NonemptyConcatenationAndRepeatedTuplesOwnIndependentHashCaches() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 11
            value = (Key(),)
            hash(value)
            for result in (value + value, value * 2, 2 * value):
                print(result is value, result[0] is value[0], result[1] is value[0], events)
                first = hash(result)
                print(first == hash((11, 11)), events)
                print(hash(result) == first, events)
            """
        );

    [Fact]
    public Task TupleIdentityOperationsPreserveUnhashableElementReferences() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            child = []
            value = (child,)
            aliases = (value[:], value + (), () + value, value * 1, 1 * value)
            child.append(23)
            for alias in aliases:
                print(alias is value, alias[0] is child, alias)
                try: hash(alias)
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task IdentityOperationsSkipElementEqualityAndLengthCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __eq__(self, other): raise ValueError('equality')
                def __len__(self): raise ValueError('length')
                def __hash__(self): raise ValueError('hash')
            value = (Key(),)
            for alias in (value[:], value[False:100:True], value + (), () + value, value * True, True * value):
                print(alias is value, alias[0] is value[0])
            """
        );

    [Fact]
    public Task ListOperationsStillProduceIndependentContainers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = [1, 2]
            for result in (value[:], value[::1], value + [], [] + value, value * 1, 1 * value):
                print(result is value, result == value)
                result.append(3)
                print(value, result)
            """
        );

    [Fact]
    public Task ExtremeSliceStepsStopAfterTheLastValidElementAcrossSequences() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (tuple(range(4)), list(range(4)), 'abcd'):
                for step in (2 ** 31 - 1, 2 ** 31, 2 ** 100, -(2 ** 31), -(2 ** 100)):
                    print(value[1::step], value[1:3:step])
            """
        );
}
