using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SequenceComparisonCompatibilityTests
{
    [Fact]
    public Task NaNDoesNotSuppressTypeErrorsForNonnumericSequenceElements() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for sequence in (list, tuple):
                for value in ('x', None, b'x'):
                    for reverse in (False, True):
                        left = sequence([float('nan')])
                        right = sequence([value])
                        if reverse: left, right = right, left
                        print(left == right, left != right)
                        for operation in ('lt', 'le', 'gt', 'ge'):
                            try:
                                if operation == 'lt': print(left < right)
                                elif operation == 'le': print(left <= right)
                                elif operation == 'gt': print(left > right)
                                else: print(left >= right)
                            except TypeError: print('TypeError')
            """
        );

    [Fact]
    public Task AllSixOperatorsComparePrimitivePrefixesAndLengths() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for sequence in (list, tuple):
                for a, b in (([], []), ([], [1]), ([1], []), ([1, 2], [1, 3]), ([1, 2], [1, 2]), ([1], [1, 0])):
                    print([compare(sequence(a), sequence(b)) for name, compare in operations])
            """
        );

    [Fact]
    public Task IdenticalNaNsAreEqualSequenceElementsAndAllowComparisonOfLaterItems() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            item = float('nan')
            for sequence in (list, tuple):
                for a, b in (([item], [item]), ([item, 1], [item, 2]), ([item], [item, 1])):
                    print([compare(sequence(a), sequence(b)) for name, compare in operations])
            """
        );

    [Fact]
    public Task IdenticalSelfRejectingElementsSkipEqualityAndOrderingCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); raise RuntimeError('self comparison')
                def __lt__(self, other): events.append('lt'); raise RuntimeError('self ordering')
            item = Item()
            for sequence in (list, tuple):
                left = sequence([item, 1])
                right = sequence([item, 2])
                print([compare(left, right) for name, compare in operations], events)
                print([compare(left, left) for name, compare in operations], events)
            """
        );

    [Fact]
    public Task DistinctNaNsRetainUnorderedElementResultsForEveryOperator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for sequence in (list, tuple):
                left = sequence([float('nan')])
                right = sequence([float('nan')])
                print([compare(left, right) for name, compare in operations])
            """
        );

    [Fact]
    public Task ListLengthMismatchSkipsEqualityWhileTupleStillComparesItsPrefix() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); return True
            for sequence in (list, tuple):
                for name, compare in operations:
                    events.clear()
                    print(name, compare(sequence([Item()]), sequence([Item(), 1])), events)
            """
        );

    [Fact]
    public Task OnlyElementEqualityIsUsedToLocateTheFirstDifference() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); return False
                def __ne__(self, other): events.append('ne'); raise RuntimeError('must not call ne')
            for sequence in (list, tuple):
                for name, compare in operations[:2]:
                    events.clear()
                    print(name, compare(sequence([Item()]), sequence([Item()])), events)
            """
        );

    [Fact]
    public Task OrderingInvokesOnlyTheRequestedRichOperatorAfterEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            events = []
            class Item:
                def __eq__(self, other): events.append('eq'); return False
                def __lt__(self, other): events.append('lt'); return 'less'
                def __le__(self, other): events.append('le'); return 'less equal'
                def __gt__(self, other): events.append('gt'); return 'greater'
                def __ge__(self, other): events.append('ge'); return 'greater equal'
            for sequence in (list, tuple):
                for name, compare in operations:
                    events.clear()
                    print(name, compare(sequence([Item()]), sequence([Item()])), events)
            """
        );

    [Fact]
    public Task OrderingPropagatesRawResultsWithoutTruthConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            events = []
            class Result:
                def __bool__(self): events.append('bool'); raise RuntimeError('do not coerce result')
            result = Result()
            class Item:
                def __eq__(self, other): events.append('eq'); return False
                def __lt__(self, other): events.append('lt'); return result
                def __le__(self, other): events.append('le'); return result
                def __gt__(self, other): events.append('gt'); return result
                def __ge__(self, other): events.append('ge'); return result
            for sequence in (list, tuple):
                for name, compare in operations[2:]:
                    events.clear()
                    print(name, compare(sequence([Item()]), sequence([Item()])) is result, events)
            """
        );

    [Fact]
    public Task ListComparisonUsesCurrentLengthsWhenEqualityClearsEitherOperand() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for side in ('left', 'right', 'both'):
                for answer in (False, True):
                    for name, compare in operations:
                        events = []
                        class Item:
                            def __eq__(self, other):
                                events.append('eq')
                                if side == 'left' or side == 'both': left.clear()
                                if side == 'right' or side == 'both': right.clear()
                                return answer
                        left = [Item(), 10]
                        right = [Item(), 20]
                        print(side, answer, name, compare(left, right), len(left), len(right), events)
            """
        );

    [Fact]
    public Task ListComparisonObservesGrowthAfterSuccessfulPrefixEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for side in ('left', 'right', 'both'):
                for name, compare in operations:
                    events = []
                    class Item:
                        def __eq__(self, other):
                            events.append('eq')
                            if side == 'left' or side == 'both': left.append(10)
                            if side == 'right' or side == 'both': right.append(20)
                            return True
                    left = [Item()]
                    right = [Item()]
                    print(side, name, compare(left, right), len(left), len(right), events)
            """
        );

    [Fact]
    public Task ListOrderingRereadsBothCurrentItemsAfterFalseEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for name, compare in operations:
                events = []
                class Item:
                    def __eq__(self, other): events.append('eq'); left[0] = 10; right[0] = 20; return False
                    def __lt__(self, other): raise RuntimeError('stale left')
                    def __le__(self, other): raise RuntimeError('stale left')
                    def __gt__(self, other): raise RuntimeError('stale left')
                    def __ge__(self, other): raise RuntimeError('stale left')
                left = [Item()]
                right = [Item()]
                print(name, compare(left, right), left, right, events)
            """
        );

    [Fact]
    public Task ListComparisonAdvancesPastDeletedPrefixPositions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for side in ('left', 'right', 'both'):
                for name, compare in operations:
                    events = []
                    class Item:
                        def __eq__(self, other):
                            events.append('eq')
                            if side == 'left' or side == 'both': del left[0]
                            if side == 'right' or side == 'both': del right[0]
                            return True
                    left = [Item(), 10, 30]
                    right = [Item(), 20, 40]
                    print(side, name, compare(left, right), len(left), len(right), events)
            """
        );

    [Fact]
    public Task TruthConversionMutationsAreObservedBeforeLengthAndOrderingDecisions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for action in ('replace', 'clear'):
                for answer in (False, True):
                    for name, compare in operations:
                        events = []
                        class Truth:
                            def __bool__(self):
                                events.append('bool')
                                left.clear()
                                right.clear()
                                if action == 'replace': left.extend([10, 30]); right.extend([20, 40])
                                return answer
                        class Item:
                            def __eq__(self, other): events.append('eq'); return Truth()
                        left = [Item(), 50]
                        right = [Item(), 60]
                        print(action, answer, name, compare(left, right), left, right, events)
            """
        );

    [Fact]
    public Task FalseEqualityStillDecidesEqualityEvenIfReplacementItemsNowMatch() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            for sequence in (list, tuple):
                for name, compare in operations[:2]:
                    events = []
                    class Item:
                        def __eq__(self, other):
                            events.append('eq')
                            if sequence is list: left[0] = 10; right[0] = 10
                            return False
                    left = sequence([Item()])
                    right = sequence([Item()])
                    print(name, compare(left, right), events)
            """
        );

    [Fact]
    public Task EqualityExceptionsPreserveIdentityAndDoNotInvokeOrdering() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            problem = LookupError('equality failed')
            for sequence in (list, tuple):
                for name, compare in operations:
                    events = []
                    class Item:
                        def __eq__(self, other): events.append('eq'); raise problem
                        def __lt__(self, other): events.append('lt'); return True
                    try: compare(sequence([Item()]), sequence([Item()]))
                    except LookupError as error: print(name, error is problem, str(error), events)
            """
        );

    [Fact]
    public Task TruthFailuresPreserveMutationsAndStopBeforeOrdering() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            problem = LookupError('truth failed')
            for name, compare in operations:
                events = []
                class Truth:
                    def __bool__(self): events.append('bool'); left.clear(); right.clear(); raise problem
                class Item:
                    def __eq__(self, other): events.append('eq'); return Truth()
                left = [Item()]
                right = [Item()]
                try: compare(left, right)
                except LookupError as error: print(name, error is problem, str(error), left, right, events)
            """
        );

    [Fact]
    public Task ReflectedSubtypeMethodsTakePriorityForTheRequestedOperator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            events = []
            class Base:
                def __eq__(self, other): events.append('base eq'); return False
                def __lt__(self, other): events.append('base lt'); return 'base lt'
                def __le__(self, other): events.append('base le'); return 'base le'
                def __gt__(self, other): events.append('base gt'); return 'base gt'
                def __ge__(self, other): events.append('base ge'); return 'base ge'
            class Child(Base):
                def __eq__(self, other): events.append('child eq'); return False
                def __lt__(self, other): events.append('child lt'); return 'child lt'
                def __le__(self, other): events.append('child le'); return 'child le'
                def __gt__(self, other): events.append('child gt'); return 'child gt'
                def __ge__(self, other): events.append('child ge'); return 'child ge'
            for sequence in (list, tuple):
                for name, compare in operations:
                    events.clear()
                    print(name, compare(sequence([Base()]), sequence([Child()])), events)
            """
        );

    [Fact]
    public Task NotImplementedOrderingFallsBackToTheCorrespondingReflectedOperator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            events = []
            class Left:
                def __eq__(self, other): events.append('left eq'); return NotImplemented
                def __lt__(self, other): events.append('left lt'); return NotImplemented
                def __le__(self, other): events.append('left le'); return NotImplemented
                def __gt__(self, other): events.append('left gt'); return NotImplemented
                def __ge__(self, other): events.append('left ge'); return NotImplemented
            class Right:
                def __eq__(self, other): events.append('right eq'); return False
                def __lt__(self, other): events.append('right lt'); return 'right lt'
                def __le__(self, other): events.append('right le'); return 'right le'
                def __gt__(self, other): events.append('right gt'); return 'right gt'
                def __ge__(self, other): events.append('right ge'); return 'right ge'
            for sequence in (list, tuple):
                for name, compare in operations:
                    events.clear()
                    print(name, compare(sequence([Left()]), sequence([Right()])), events)
            """
        );

    [Fact]
    public Task OrderingExceptionsPreserveIdentityAndCallbackMutations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            problem = LookupError('order failed')
            for name, compare in operations[2:]:
                events = []
                class Item:
                    def __eq__(self, other): events.append('eq'); return False
                    def __lt__(self, other): events.append('lt'); left.clear(); raise problem
                    def __le__(self, other): events.append('le'); left.clear(); raise problem
                    def __gt__(self, other): events.append('gt'); left.clear(); raise problem
                    def __ge__(self, other): events.append('ge'); left.clear(); raise problem
                left = [Item()]
                right = [Item()]
                try: compare(left, right)
                except LookupError as error: print(name, error is problem, str(error), len(left), len(right), events)
            """
        );

    [Fact]
    public Task NestedSequencesUseIdentityAwarePrefixAndRichOperatorSemantics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            operations = (('eq', lambda a, b: a == b), ('ne', lambda a, b: a != b), ('lt', lambda a, b: a < b), ('le', lambda a, b: a <= b), ('gt', lambda a, b: a > b), ('ge', lambda a, b: a >= b))

            item = float('nan')
            for sequence in (list, tuple):
                left = sequence([sequence([item, 10]), sequence([20])])
                right = sequence([sequence([item, 10]), sequence([30])])
                print([compare(left, right) for name, compare in operations])
            """
        );
}
