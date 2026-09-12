using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DictionaryCursorMutationCompatibilityTests
{
    [Fact]
    public Task ForwardDeletionOfTheConsumedKeyPreservesSlotsAndRaisesOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for kind in ('dict', 'keys', 'values', 'items'):
                values = {'a': 1, 'b': 2, 'c': 3}
                source = values if kind == 'dict' else getattr(values, kind)()
                iterator = iter(source)
                print(kind, next(iterator))
                del values['a']
                values['d'] = 4
                print(iterator.__length_hint__(), next(iterator), next(iterator))
                try: next(iterator)
                except RuntimeError as error: print(str(error))
                print(iterator.__length_hint__(), next(iterator, 'done'))
                values['e'] = 5
                print(next(iterator, 'still done'))
            """
        );

    [Fact]
    public Task ForwardDeletionOfAnUnconsumedKeyVisitsItsReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for kind in ('dict', 'keys', 'values', 'items'):
                values = {'a': 1, 'b': 2, 'c': 3}
                source = values if kind == 'dict' else getattr(values, kind)()
                iterator = iter(source)
                print(kind, next(iterator))
                del values['b']
                values['d'] = 4
                print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
                print(list(iterator), iterator.__length_hint__(), list(values.items()))
            """
        );

    [Fact]
    public Task ReinsertingAnExistingKeyMovesItToTheEndWithoutReusingItsHole() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for kind in ('dict', 'keys', 'values', 'items'):
                values = {'a': 1, 'b': 2, 'c': 3}
                source = values if kind == 'dict' else getattr(values, kind)()
                iterator = iter(source)
                print(kind, next(iterator))
                del values['b']
                values['b'] = 20
                print(list(iterator), list(values.items()))
                iterator = iter(source)
                print(next(iterator))
                del values['a']
                values['a'] = 10
                for attempt in range(4):
                    try: print(next(iterator), iterator.__length_hint__())
                    except Exception as error: print(type(error).__name__, str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task ForwardCursorsRetainTheirPhysicalPositionAcrossGrowthThresholds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (3, 5, 6, 10, 11):
                for kind in ('dict', 'keys', 'values', 'items'):
                    values = {}
                    for index in range(count): values[str(index)] = index
                    source = values if kind == 'dict' else getattr(values, kind)()
                    iterator = iter(source)
                    print(count, kind, next(iterator))
                    del values['0']
                    values['0'] = 100
                    for attempt in range(count + 1):
                        try: print(next(iterator), iterator.__length_hint__())
                        except Exception as error: print(type(error).__name__, str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task ReverseCursorsRetainTheirPhysicalPositionAcrossGrowthThresholds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (3, 5, 6, 10, 11):
                for kind in ('dict', 'keys', 'values', 'items'):
                    values = {}
                    for index in range(count): values[str(index)] = index
                    source = values if kind == 'dict' else getattr(values, kind)()
                    iterator = reversed(source)
                    print(count, kind, next(iterator))
                    del values['0']
                    values['new'] = 100
                    for attempt in range(count + 1):
                        try: print(next(iterator), iterator.__length_hint__())
                        except Exception as error: print(type(error).__name__, str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task ReverseDeletionAndReinsertionCanOmitNewEntriesOrRepeatOldOnes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for removed in ('a', 'b', 'c'):
                for kind in ('dict', 'keys', 'values', 'items'):
                    values = {'a': 1, 'b': 2, 'c': 3}
                    source = values if kind == 'dict' else getattr(values, kind)()
                    iterator = reversed(source)
                    print(removed, kind, next(iterator))
                    del values[removed]
                    values[removed] = 100
                    print(iterator.__length_hint__(), list(iterator), iterator.__length_hint__())
                    print(list(values.items()))
            """
        );

    [Fact]
    public Task MappingProxyCursorsShareTheUnderlyingMutationHistory() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for backward in (False, True):
                for kind in ('proxy', 'keys', 'values', 'items'):
                    values = {'a': 1, 'b': 2, 'c': 3}
                    proxy = MappingProxyType(values)
                    source = proxy if kind == 'proxy' else getattr(proxy, kind)()
                    iterator = reversed(source) if backward else iter(source)
                    print(backward, kind, next(iterator))
                    del values['b']
                    values['new'] = 99
                    print(iterator.__length_hint__(), list(iterator), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task PopLeavesDeletedSlotsUntilAnInsertionCompactsTheDictionary() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (3, 5, 6, 10, 11):
                for backward in (False, True):
                    values = {}
                    for index in range(count): values[str(index)] = index
                    iterator = reversed(values.items()) if backward else iter(values.items())
                    print(count, backward, next(iterator), values.pop('0'))
                    values['replacement'] = 100
                    for attempt in range(count + 1):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
                    print(iterator.__length_hint__())
            """
        );

    [Fact]
    public Task PopitemTruncatesTheEntryTailAndPreservesInsertionCapacity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (3, 5, 6, 10, 11):
                for backward in (False, True):
                    values = {}
                    for index in range(count): values[str(index)] = index
                    iterator = reversed(values) if backward else iter(values)
                    print(count, backward, next(iterator), values.popitem())
                    values['new'] = 100
                    print(iterator.__length_hint__(), list(iterator), iterator.__length_hint__())
                    print(list(values))
            """
        );

    [Fact]
    public Task ClearAndRestoreUseTheNewEntryTableAtTheExistingCursorPosition() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for backward in (False, True):
                for kind in ('dict', 'keys', 'values', 'items'):
                    values = {'a': 1, 'b': 2, 'c': 3}
                    source = values if kind == 'dict' else getattr(values, kind)()
                    iterator = reversed(source) if backward else iter(source)
                    print(backward, kind, next(iterator))
                    values.clear()
                    print(iterator.__length_hint__())
                    values['x'] = 10
                    values['y'] = 20
                    values['z'] = 30
                    print(iterator.__length_hint__(), list(iterator), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task NonStringKeyPromotionCompactsExistingHolesBeforeCursorResumption() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (3, 5, 6, 10, 11):
                for backward in (False, True):
                    values = {}
                    for index in range(count): values[str(index)] = index
                    iterator = reversed(values.items()) if backward else iter(values.items())
                    print(count, backward, next(iterator))
                    del values['0']
                    values[42] = 100
                    for attempt in range(count + 1):
                        try: print(next(iterator), iterator.__length_hint__())
                        except Exception as error: print(type(error).__name__, str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task AlreadyGenericKeyTablesRetainHolesUntilTheirCapacityIsExhausted() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (3, 5, 6, 10, 11):
                for backward in (False, True):
                    values = {}
                    for index in range(count): values[index] = index * 10
                    iterator = reversed(values) if backward else iter(values)
                    print(count, backward, next(iterator))
                    del values[0]
                    values['replacement'] = 100
                    for attempt in range(count + 1):
                        try: print(next(iterator), iterator.__length_hint__())
                        except Exception as error: print(type(error).__name__, str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task HintsTrackTheRemainingCounterEvenWhenTheCursorHasNoReachableEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = {'a': 1, 'b': 2, 'c': 3}
            iterator = reversed(values)
            print(next(iterator), iterator.__length_hint__())
            del values['a']
            values['new'] = 4
            print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
            print(next(iterator, 'done'), iterator.__length_hint__())
            values = {'a': 1, 'b': 2, 'c': 3}
            iterator = iter(values)
            print(next(iterator), next(iterator), iterator.__length_hint__())
            values.clear()
            values['x'] = 10
            values['y'] = 20
            values['z'] = 30
            print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
            print(next(iterator, 'done'), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task ValueReplacementUpdatesUnconsumedEntriesWithoutChangingTheirSlots() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for backward in (False, True):
                for kind in ('values', 'items'):
                    values = {'a': 1, 'b': 2, 'c': 3, 'd': 4, 'e': 5}
                    source = getattr(values, kind)()
                    iterator = reversed(source) if backward else iter(source)
                    print(backward, kind, next(iterator))
                    values['b'] = 20
                    values['c'] = 30
                    values['d'] = 40
                    print(iterator.__length_hint__(), list(iterator))
                    print(list(values.items()))
            """
        );

    [Fact]
    public Task MultipleDeletionsAreCompactedTogetherWhenTheEntryBudgetRunsOut() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for backward in (False, True):
                values = {'a': 1, 'b': 2, 'c': 3, 'd': 4, 'e': 5}
                iterator = reversed(values.items()) if backward else iter(values.items())
                print(backward, next(iterator))
                del values['a']
                del values['c']
                values['x'] = 10
                values['y'] = 20
                print(iterator.__length_hint__(), list(iterator), iterator.__length_hint__())
                print(list(values.items()))
            """
        );

    [Fact]
    public Task KeysChangedErrorsExhaustWhileObservedSizeChangesRemainSticky() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = {'a': 1, 'b': 2, 'c': 3}
            iterator = iter(values)
            advance = iterator.__next__
            hint = iterator.__length_hint__
            print(advance())
            del values['a']
            values['d'] = 4
            print(advance(), advance(), hint())
            try: advance()
            except RuntimeError as error: print(str(error), hint())
            try: advance()
            except StopIteration as error: print(error.args, hint())
            for backward in (False, True):
                values = {'a': 1, 'b': 2}
                iterator = reversed(values) if backward else iter(values)
                values['c'] = 3
                try: next(iterator)
                except RuntimeError as error: print(str(error))
                del values['c']
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task EqualNonStringKeysUpdateExistingStringEntriesWithoutPromotingTheTable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class EqualKey:
                def __hash__(self): return hash('b')
                def __eq__(self, other): return other == 'b'
            for backward in (False, True):
                values = {'a': 1, 'b': 2, 'c': 3}
                del values['a']
                iterator = reversed(values.items()) if backward else iter(values.items())
                print(backward, next(iterator))
                values[EqualKey()] = 20
                print(iterator.__length_hint__(), list(iterator), list(values.items()))
            """
        );
}
