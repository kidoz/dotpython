using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SortingCompatibilityTests
{
    [Fact]
    public Task SortedAndListSortUseOnlyLessThanWithMatchingShortRunTraces() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, value): self.value = value
                def __lt__(self, other): events.append((self.value, other.value)); return self.value < other.value
                def __eq__(self, other): raise RuntimeError('no equality')
                def __gt__(self, other): raise RuntimeError('no greater than')
            for initial in ([3, 1, 4, 2], [4, 3, 2, 1], [1, 1, 2, 2], [2, 2, 1, 1]):
                for reverse in (False, True):
                    for method in ('sorted', 'sort'):
                        events.clear()
                        values = [Item(value) for value in initial]
                        if method == 'sorted': result = sorted(values, reverse=reverse)
                        else: print(values.sort(reverse=reverse)); result = values
                        print([item.value for item in result], events)
            """
        );

    [Fact]
    public Task LessThanResultsAreTruthConvertedExactlyOncePerComparison() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Truth:
                def __init__(self, value): self.value = value
                def __bool__(self): events.append('bool'); return self.value
            class Item:
                def __init__(self, value): self.value = value
                def __lt__(self, other): events.append((self.value, other.value)); return Truth(self.value < other.value)
            for reverse in (False, True):
                events.clear()
                values = [Item(3), Item(1), Item(2)]
                values.sort(reverse=reverse)
                print([value.value for value in values], events)
            """
        );

    [Fact]
    public Task NotImplementedLessThanFallsBackToReflectedGreaterThan() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class First:
                def __gt__(self, other): events.append('reflected gt'); return True
            class Second:
                def __lt__(self, other): events.append('lt'); return NotImplemented
            first = First()
            second = Second()
            print(sorted([first, second])[0] is second, events)
            """
        );

    [Fact]
    public Task StableOrderingPreservesOriginalIdentityForEqualKeysEvenInReverse() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Item:
                def __init__(self, group, name): self.group = group; self.name = name
            values = [Item(2, 'a'), Item(1, 'b'), Item(2, 'c'), Item(1, 'd')]
            for reverse in (False, True):
                result = sorted(values, key=lambda value: value.group, reverse=reverse)
                print([value.name for value in result], all(any(value is original for original in values) for value in result))
                copied = values.copy()
                print(copied.sort(key=lambda value: value.group, reverse=reverse), [value.name for value in copied])
            """
        );

    [Fact]
    public Task NaNsFollowTheObservedLessThanTraceWithoutInventingATotalOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            nan = float('nan')
            for initial in ([3.0, nan, 1.0, 2.0], [nan, 3.0, 2.0, 1.0], [3.0, 2.0, 1.0, nan], [2.0, nan, 1.0, nan, 3.0]):
                for reverse in (False, True):
                    result = sorted(initial, reverse=reverse)
                    print(['nan' if value is nan else value for value in result])
            """
        );

    [Fact]
    public Task EveryKeyIsEvaluatedOnceInInputOrderBeforeAnyComparison() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, value): self.value = value
                def __lt__(self, other): events.append(('lt', self.value, other.value)); return self.value < other.value
            def key(value): events.append(('key', value)); return Key(value)
            for reverse in (False, True):
                for method in ('sorted', 'sort'):
                    events.clear()
                    values = [3, 1, 2]
                    if method == 'sorted': print(sorted(values, key=key, reverse=reverse), events)
                    else: print(values.sort(key=key, reverse=reverse), values, events)
            """
        );

    [Fact]
    public Task SortedMaterializesAndRequestsHintsBeforeReverseAndKeyCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Source:
                def __init__(self): self.position = 0
                def __iter__(self): events.append('iter'); return self
                def __length_hint__(self): events.append('hint'); return 3
                def __next__(self):
                    events.append(('next', self.position))
                    if self.position == 3: raise StopIteration
                    self.position += 1
                    return self.position
            class Reverse:
                def __bool__(self): events.append('reverse'); return True
            def key(value): events.append(('key', value)); return value
            print(sorted(Source(), key=key, reverse=Reverse()), events)
            """
        );

    [Fact]
    public Task ReverseUsesTruthConversionRatherThanIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Truth:
                def __bool__(self): events.append('bool'); return False
                def __index__(self): raise RuntimeError('no index')
            class Length:
                def __len__(self): events.append('len'); return 1
            class IndexOnly:
                def __index__(self): events.append('index'); return 0
            for reverse in (Truth(), Length(), IndexOnly(), None, 2 ** 100):
                events.clear()
                print(sorted([1, 3, 2], reverse=reverse), events)
                values = [1, 3, 2]
                print(values.sort(reverse=reverse), values, events)
            """
        );

    [Fact]
    public Task ReverseConversionOccursBeforeTheListIsDetachedAndCanMutateItsInput() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            values = [3, 1]
            class Reverse:
                def __bool__(self): events.append(values.copy()); values.append(2); return True
            print(values.sort(reverse=Reverse()), values, events)
            """
        );

    [Fact]
    public Task ListReceiverIsEmptyThroughoutKeyGenerationAndComparison() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, value): self.value = value
                def __lt__(self, other): events.append(('lt', len(values), values.copy())); return self.value < other.value
            def key(value): events.append(('key', value, len(values), values.copy())); return Key(value)
            values = [3, 1, 2]
            alias = values
            print(values.sort(key=key), values, alias is values, events)
            """
        );

    [Fact]
    public Task ListSortDetectsAppendThenClearDuringKeysAndDiscardsTemporaryItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for reverse in (False, True):
                events = []
                values = [3, 1, 2]
                def key(value):
                    events.append((value, len(values)))
                    if value == 3: values.append(99); values.clear()
                    return value
                try: values.sort(key=key, reverse=reverse)
                except ValueError as error: print(str(error), values, events)
            """
        );

    [Fact]
    public Task ListSortDetectsComparisonMutationEvenWhenTheReceiverReturnsToEmpty() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, value): self.value = value
                def __lt__(self, other):
                    events.append((self.value, other.value, len(values)))
                    values.append(99)
                    values.clear()
                    return self.value < other.value
            values = [Item(3), Item(1), Item(2)]
            try: values.sort()
            except ValueError as error: print(str(error), [value.value for value in values], events)
            """
        );

    [Fact]
    public Task ClearingAnAlreadyEmptyDetachedReceiverIsNotAMutationFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            values = [3, 1, 2]
            def key(value): events.append(value); values.clear(); return value
            print(values.sort(key=key), values, events)
            """
        );

    [Fact]
    public Task KeyFailuresRestoreTheOriginalOrderIncludingReverseSorts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('key failed')
            for reverse in (False, True):
                values = [3, 1, 2]
                events = []
                def key(value):
                    events.append((value, len(values)))
                    if value == 1: raise problem
                    return value
                try: values.sort(key=key, reverse=reverse)
                except LookupError as error: print(error is problem, str(error), values, events)
            """
        );

    [Fact]
    public Task ComparisonFailuresRestoreTheObservedPartialPermutation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('comparison failed')
            for reverse in (False, True):
                for fail_at in (1, 3, 5, 8):
                    events = []
                    class Item:
                        def __init__(self, value): self.value = value
                        def __lt__(self, other):
                            events.append((self.value, other.value))
                            if len(events) == fail_at: raise problem
                            return self.value < other.value
                    values = [Item(value) for value in [5, 1, 4, 2, 3, 0]]
                    try: values.sort(reverse=reverse)
                    except LookupError as error: print(error is problem, str(error))
                    print([value.value for value in values], events)
            """
        );

    [Fact]
    public Task CallbackErrorsTakePrecedenceOverDetectedListMutation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('callback failed')
            for failing in ('key', 'lt', 'bool'):
                events = []
                class Truth:
                    def __bool__(self): events.append('bool'); values.append(99); raise problem
                class Item:
                    def __init__(self, value): self.value = value
                    def __lt__(self, other):
                        events.append('lt')
                        values.append(99)
                        if failing == 'lt': raise problem
                        return Truth()
                def key(value):
                    events.append('key')
                    if failing == 'key': values.append(99); raise problem
                    return value
                values = [Item(3), Item(1), Item(2)]
                try: values.sort(key=key)
                except LookupError as error: print(error is problem, str(error), [value.value for value in values], events)
            """
        );

    [Fact]
    public Task NestedSortOfTheDetachedReceiverDoesNotDisturbTheOuterSort() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            values = [3, 1, 2]
            entered = False
            def key(value):
                global entered
                events.append((value, len(values)))
                if not entered:
                    entered = True
                    events.append(values.sort())
                return value
            print(values.sort(key=key), values, events)
            """
        );

    [Fact]
    public Task SortedCompletesMaterializationBeforeKeyFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('key failed')
            events = []
            def source():
                for value in (3, 1, 2): events.append(('next', value)); yield value
            def key(value): events.append(('key', value)); raise problem
            iterator = source()
            try: sorted(iterator, key=key)
            except LookupError as error: print(error is problem, str(error), events)
            print(next(iterator, 'finished'))
            """
        );

    [Fact]
    public Task SortedRejectsUnknownKeywordsAfterMaterializingTheIterable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def source():
                for value in (3, 1, 2): events.append(value); yield value
            try: sorted(source(), unknown=1)
            except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task ReverseFailuresPreserveTheListAndOccurAfterSortedMaterialization() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('reverse failed')
            events = []
            class Reverse:
                def __bool__(self): events.append('reverse'); raise problem
            def source():
                for value in (3, 1, 2): events.append(value); yield value
            values = [3, 1, 2]
            try: values.sort(reverse=Reverse())
            except LookupError as error: print(error is problem, values, events)
            events.clear()
            try: sorted(source(), reverse=Reverse())
            except LookupError as error: print(error is problem, events)
            """
        );

    [Fact]
    public Task EmptyAndSingletonSortsRetainKeyAndReverseCallbackRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Reverse:
                def __bool__(self): events.append('reverse'); return True
            def key(value): events.append(('key', value)); return value
            for initial in ([], [1]):
                events.clear()
                values = initial.copy()
                print(values.sort(key=key, reverse=Reverse()), values, events)
            print(sorted([], key=42))
            try: sorted([1], key=42)
            except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task LongNaturalRunsUseTheReferenceComparisonTrace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, value, ordinal): self.value = value; self.ordinal = ordinal
                def __lt__(self, other): events.append((self.ordinal, other.ordinal)); return self.value < other.value
            initial = [value for start in (80, 0, 120, 40) for value in range(start, start + 40)]
            for reverse in (False, True):
                events.clear()
                values = [Item(value, ordinal) for ordinal, value in enumerate(initial)]
                values.sort(reverse=reverse)
                print([value.value for value in values], events)
            """
        );

    [Fact]
    public Task MixedRunsAndDuplicateKeysPreserveStableOrderAndComparisonTrace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, value, ordinal): self.value = value; self.ordinal = ordinal
                def __lt__(self, other): events.append((self.ordinal, other.ordinal)); return self.value < other.value
            initial = [(index * 37 + index // 7) % 23 for index in range(173)]
            for reverse in (False, True):
                events.clear()
                values = [Item(value, ordinal) for ordinal, value in enumerate(initial)]
                values.sort(reverse=reverse)
                print([(value.value, value.ordinal) for value in values], events)
            """
        );

    [Fact]
    public Task TupleKeysKeepSharedNaNIdentityAndTheirEqualPrefixSemantics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            nan = float('nan')
            values = [(nan, 3), (nan, 1), (nan, 2)]
            for reverse in (False, True):
                result = sorted(values, reverse=reverse)
                print([value[1] for value in result])
            """
        );

    [Fact]
    public Task HomogeneousNotImplementedKeysRepeatTheSlotBeforeReflectedFallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, rank): self.rank = rank
                def __eq__(self, other): events.append(('eq', self.rank, other.rank)); return False
                def __lt__(self, other): events.append(('lt', self.rank, other.rank)); return NotImplemented
                def __gt__(self, other): events.append(('gt', self.rank, other.rank)); return self.rank > other.rank
            for reverse in (False, True):
                for keyed in (False, True):
                    events.clear()
                    values = [Item(3), Item(1), Item(2)]
                    values = values.copy()
                    result = sorted(values, key=(lambda value: value) if keyed else None, reverse=reverse)
                    print([value.rank for value in result], events)
            """
        );

    [Fact]
    public Task HomogeneousTupleFirstElementsRepeatTheSlotAfterPrefixEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __init__(self, rank): self.rank = rank
                def __eq__(self, other): events.append(('eq', self.rank, other.rank)); return False
                def __lt__(self, other): events.append(('lt', self.rank, other.rank)); return NotImplemented
                def __gt__(self, other): events.append(('gt', self.rank, other.rank)); return self.rank > other.rank
            for reverse in (False, True):
                for keyed in (False, True):
                    events.clear()
                    values = [Item(3), Item(1), Item(2)]
                    values = [(value,) for value in values]
                    result = sorted(values, key=(lambda value: value) if keyed else None, reverse=reverse)
                    print([value[0].rank for value in result], events)
            """
        );

    [Fact]
    public Task EmptyMutationOperationsDistinguishNoOpsFromTemporaryAllocation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Empty:
                def __init__(self, hint): self.hint = hint
                def __iter__(self): return iter(())
                def __length_hint__(self): return self.hint
            for action in ('clear', 'reverse', 'list', 'tuple', 'iterator', 'generator', 'set', 'frozenset', 'dict', 'keys', 'values', 'items', 'slice', 'insert slice', 'repeat zero', 'repeat one', 'add', 'sort', 'hint zero', 'hint one'):
                values = [3, 1, 2]
                def key(value):
                    global values
                    if value == 3:
                        if action == 'clear': values.clear()
                        elif action == 'reverse': values.reverse()
                        elif action == 'list': values.extend([])
                        elif action == 'tuple': values.extend(())
                        elif action == 'iterator': values.extend(iter([]))
                        elif action == 'generator': values.extend(item for item in ())
                        elif action == 'set': values.extend(set())
                        elif action == 'frozenset': values.extend(frozenset())
                        elif action == 'dict': values.extend({})
                        elif action == 'keys': values.extend({}.keys())
                        elif action == 'values': values.extend({}.values())
                        elif action == 'items': values.extend({}.items())
                        elif action == 'slice': values[:] = []
                        elif action == 'insert slice': values[0:0] = []
                        elif action == 'repeat zero': values *= 0
                        elif action == 'repeat one': values *= 1
                        elif action == 'add': values += []
                        elif action == 'sort': values.sort()
                        elif action == 'hint zero': values.extend(Empty(0))
                        else: values.extend(Empty(1))
                    return value
                try: print(action, values.sort(key=key), values)
                except ValueError as error: print(action, str(error), values)
            """
        );

    [Fact]
    public Task DisabledComparisonSlotsRaiseTheirCallErrorsInSortAndSharedDispatch() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Disabled:
                __lt__ = None
                __gt__ = None
                __eq__ = None
                __ne__ = None
            class Other: pass
            for values in ([Disabled(), Disabled()], [Other(), Disabled()], [(Disabled(),), (Disabled(),)]):
                try: sorted(values)
                except TypeError as error: print(str(error))
            for compare in (lambda a, b: a < b, lambda a, b: a > b, lambda a, b: a == b, lambda a, b: a != b):
                for left, right in ((Disabled(), Other()), (Other(), Disabled()), (Disabled(), Disabled())):
                    try: compare(left, right)
                    except TypeError as error: print(str(error))
            class Base:
                def __lt__(self, other): return True
            class Child(Base):
                __gt__ = None
            try: Base() < Child()
            except TypeError as error: print(str(error))
            try: sorted([Child(), Base()])
            except TypeError as error: print(str(error))
            """
        );
}
