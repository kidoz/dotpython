using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SequenceCountRemoveCompatibilityTests
{
    [Fact]
    public Task CountsMatchIdenticalNaNsAndSelfRejectingObjectsWithoutEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); raise RuntimeError('self comparison')
            for sequence in (list, tuple):
                for item in (float('nan'), Item()):
                    values = sequence([item, item, item])
                    print(values.count(item), events)
            """
        );

    [Fact]
    public Task RemoveMatchesIdenticalObjectsWithoutEqualityAndRemovesOnlyTheFirst() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); raise RuntimeError('self comparison')
            for item in (float('nan'), Item()):
                values = [item, item]
                print(values.remove(item), len(values), values[0] is item, events)
            """
        );

    [Fact]
    public Task ListCountIncludesMatchesAppendedByEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for answer in (False, True):
                events = []
                class Item:
                    def __eq__(self, other): events.append('eq'); values.extend([99, 99]); return answer
                values = [Item(), 99]
                print(values.count(99), len(values), events)
            """
        );

    [Fact]
    public Task ListCountAdvancesToTheNextLivePositionAfterDeletion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for answer in (False, True):
                events = []
                class Item:
                    def __eq__(self, other): events.append('eq'); del values[0]; return answer
                values = [Item(), 99, 99]
                print(values.count(99), values, events)
            """
        );

    [Fact]
    public Task ListCountKeepsASuccessfulCandidateEvenWhenEqualityClearsTheList() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for answer in (False, True):
                events = []
                class Item:
                    def __eq__(self, other): events.append('eq'); values.clear(); return answer
                values = [Item(), 99]
                print(values.count(99), values, events)
            """
        );

    [Fact]
    public Task ListCountReadsReplacementContentsAtSubsequentPositions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); values.clear(); values.extend([99, 10, 99]); return False
            values = [Item(), 20]
            print(values.count(99), values, events)
            """
        );

    [Fact]
    public Task ListCountObservesMutationsPerformedByComparisonTruthConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for answer in (False, True):
                events = []
                class Truth:
                    def __bool__(self): events.append('bool'); values.clear(); values.extend([10, 99]); return answer
                class Item:
                    def __eq__(self, other): events.append('eq'); return Truth()
                values = [Item()]
                print(values.count(99), values, events)
            """
        );

    [Fact]
    public Task RemoveCanReachAMatchAppendedDuringEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); values.append(99); return False
            item = Item()
            values = [item]
            print(values.remove(99), len(values), values[0] is item, events)
            """
        );

    [Fact]
    public Task RemoveContinuesAtTheNextLivePositionAfterAnUnsuccessfulMutation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for action in ('delete', 'replace'):
                events = []
                class Item:
                    def __eq__(self, other):
                        events.append('eq')
                        if action == 'delete': del values[0]
                        else: values.clear(); values.extend([10, 99, 30])
                        return False
                values = [Item(), 99, 99]
                print(values.remove(99), values, events)
            """
        );

    [Fact]
    public Task RemoveDeletesTheCurrentItemAtTheSuccessfulComparisonPosition() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for action in ('replace', 'delete'):
                events = []
                class Item:
                    def __eq__(self, other):
                        events.append('eq')
                        if action == 'replace': values[1] = 77
                        else: del values[1]
                        return True
                values = [10, Item(), 30]
                print(values.remove(99), values, events)
            """
        );

    [Fact]
    public Task RemoveSucceedsWithoutDeletionWhenTheMatchedPositionNoLongerExists() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for action in ('clear', 'shrink'):
                events = []
                class Item:
                    def __eq__(self, other):
                        events.append('eq')
                        if action == 'clear': values.clear()
                        else: del values[1:]
                        return True
                values = [10, Item(), 30]
                print(values.remove(99), values, events)
            """
        );

    [Fact]
    public Task RemoveAppliesSuccessfulTruthConversionToTheCurrentContents() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for action in ('replace', 'clear'):
                events = []
                class Truth:
                    def __bool__(self):
                        events.append('bool')
                        values.clear()
                        if action == 'replace': values.extend([70, 80, 90])
                        return True
                class Item:
                    def __eq__(self, other): events.append('eq'); return Truth()
                values = [10, Item(), 30]
                print(values.remove(99), values, events)
            """
        );

    [Fact]
    public Task ComparisonExceptionsPreserveIdentityAndEffectsWithoutFurtherWork() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('comparison failed')
            for method in ('list count', 'tuple count', 'remove'):
                for failing in ('eq', 'bool'):
                    events = []
                    effects = []
                    class Truth:
                        def __bool__(self): events.append('bool'); effects.append(2); raise problem
                    class Item:
                        def __eq__(self, other):
                            events.append('eq')
                            effects.append(1)
                            if failing == 'eq': raise problem
                            return Truth()
                    values = [Item(), 99]
                    if method == 'tuple count': values = tuple(values)
                    try:
                        if method == 'remove': values.remove(99)
                        else: values.count(99)
                    except LookupError as error: print(method, error is problem, str(error), len(values), events, effects)
            """
        );

    [Fact]
    public Task NotImplementedComparisonUsesReflectedEqualityAndItsTruthResult() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Truth:
                def __bool__(self): events.append('bool'); return True
            class Item:
                def __eq__(self, other): events.append('item'); return NotImplemented
            class Needle:
                def __eq__(self, other): events.append('needle'); return Truth()
            for sequence in (list, tuple):
                events.clear()
                print(sequence([Item(), Item()]).count(Needle()), events)
            events.clear()
            values = [Item(), Item()]
            print(values.remove(Needle()), len(values), events)
            """
        );

    [Fact]
    public Task SavedCountAndRemoveMethodsRetainTheirOriginalReceiver() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            item = float('nan')
            values = [item, item]
            original = values
            count = values.count
            remove = values.remove
            values = [10, 20]
            print(count(item), remove(item), count(item), len(original), values)
            value = (item, item)
            count = value.count
            value = (10, 20)
            print(count(item), value)
            """
        );

    [Fact]
    public Task NestedCountAndRemoveCallsFinishBeforeTheOuterScanContinues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for method in ('count', 'remove'):
                events = []
                class Item:
                    def __eq__(self, other):
                        events.append('eq')
                        del values[0]
                        events.append(values.count(99))
                        values.remove(99)
                        values.append(99)
                        return False
                values = [Item(), 99, 99]
                if method == 'count': print(values.count(99), values, events)
                else: print(values.remove(99), values, events)
            """
        );

    [Fact]
    public Task MissingRemoveUsesAFixedMessageWithoutFormattingTheNeedle() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Missing:
                def __repr__(self): events.append('repr'); raise RuntimeError('do not format')
            needle = Missing()
            for values in ([], [10, 20]):
                print(values.count(needle), tuple(values).count(needle), events)
                try: values.remove(needle)
                except ValueError as error: print(str(error), values, events)
            """
        );

    [Fact]
    public Task ArgumentEvaluationCompletesBeforeComparisonsAndTheirTruthTests() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Truth:
                def __bool__(self): events.append('bool'); return True
            class Item:
                def __eq__(self, other): events.append('eq'); return Truth()
            def target(): events.append('target'); return values
            def needle(): events.append('needle'); return 99
            for values in ([Item()], (Item(),)):
                events.clear()
                print(target().count(needle()), events)
            values = [Item()]
            events.clear()
            print(target().remove(needle()), values, events)
            """
        );
}
