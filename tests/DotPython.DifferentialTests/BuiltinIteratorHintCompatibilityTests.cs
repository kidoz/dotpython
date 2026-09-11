using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BuiltinIteratorHintCompatibilityTests
{
    [Fact]
    public Task BuiltinIteratorHintsDecreaseWithoutConsumingElements() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([2, 4, 6], (2, 4, 6), range(2, 8, 2)):
                iterator = iter(source)
                print(type(iterator).__name__, iterator.__length_hint__(), iterator.__length_hint__())
                print(next(iterator), iterator.__length_hint__())
                print(next(iterator), iterator.__length_hint__())
                print(next(iterator), iterator.__length_hint__())
                print(next(iterator, 'done'), iterator.__length_hint__(), iter(iterator) is iterator)
            """
        );

    [Fact]
    public Task ListAppendAfterTheLastValueRevivesZeroHintUntilNextReportsExhaustion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [1]
            iterator = iter(values)
            print(next(iterator), iterator.__length_hint__())
            values.append(2)
            print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
            print(next(iterator, 'done'))
            values.append(3)
            print(iterator.__length_hint__(), next(iterator, 'still done'))
            """
        );

    [Fact]
    public Task ListShrinkHintsClampAtZeroWithoutMarkingTheIteratorExhausted() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [0, 1, 2, 3]
            iterator = iter(values)
            print(next(iterator), next(iterator), iterator.__length_hint__())
            values.clear()
            print(iterator.__length_hint__())
            values.extend([7, 8, 9, 10])
            print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
            values.clear()
            print(next(iterator, 'done'))
            values.extend([1, 2, 3, 4, 5])
            print(iterator.__length_hint__(), next(iterator, 'still done'))
            """
        );

    [Fact]
    public Task ListRemovalBeforeTheCursorChangesRemainingLengthAndNextPosition() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = ['a', 'b', 'c', 'd']
            iterator = iter(values)
            print(next(iterator), iterator.__length_hint__())
            del values[0]
            print(iterator.__length_hint__(), next(iterator))
            values.pop()
            print(iterator.__length_hint__())
            values.append('new')
            print(iterator.__length_hint__(), next(iterator))
            print(next(iterator, 'done'), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task InitiallyEmptyListIteratorCanObserveAppendsUntilItsFirstFailedNext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = []
            iterator = iter(values)
            print(iterator.__length_hint__())
            values.append(7)
            print(iterator.__length_hint__(), next(iterator))
            print(next(iterator, 'done'))
            values.append(8)
            print(iterator.__length_hint__(), next(iterator, 'still done'))
            for source in ((), range(0), range(2 ** 100, 2 ** 100)):
                empty = iter(source)
                print(type(empty).__name__, empty.__length_hint__(), next(empty, 'empty'), empty.__length_hint__())
            """
        );

    [Fact]
    public Task SavedBoundHintsObserveTheSameIteratorStateAfterItsVariableIsDeleted() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([1, 2], (1, 2), range(1, 3)):
                iterator = iter(source)
                hint = iterator.__length_hint__
                advance = lambda saved=iterator: next(saved)
                del iterator
                print(hint(), advance(), hint(), advance(), hint())
                try: advance()
                except StopIteration: print('done', hint())
            """
        );

    [Fact]
    public Task MaterializingPartlyConsumedBuiltinIteratorsUsesOnlyRemainingElements() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([1, 2, 3], (1, 2, 3), range(1, 4)):
                iterator = iter(source)
                print(next(iterator), iterator.__length_hint__())
                hint = iterator.__length_hint__
                print(list(iterator), hint(), next(iterator, 'done'))
                iterator = iter(source)
                next(iterator)
                print(tuple(iterator), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task LongRangeHintsAreArbitraryPrecisionAndListOverflowDoesNotConsumeTheIterator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            count = 2 ** 100
            iterator = iter(range(count))
            print(type(iterator).__name__, iterator.__length_hint__() == count)
            for attempt in range(2):
                try: list(iterator)
                except OverflowError as error: print(str(error), iterator.__length_hint__() == count)
            print(next(iterator), iterator.__length_hint__() == count - 1)
            target = ['original']
            try: target.extend(iterator)
            except OverflowError as error: print(str(error), target)
            print(next(iterator), iterator.__length_hint__() == count - 2)
            """
        );

    [Fact]
    public Task RangeIteratorSelectionAccountsForBoundsStepsLengthsAndEndpointOverflow() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            maximum = 2 ** 63 - 1
            minimum = -(2 ** 63)
            cases = (
                (0, 3, 1), (0, maximum, 1), (0, maximum + 1, 1),
                (maximum + 1, maximum + 4, 1), (minimum, minimum + 3, 1),
                (minimum - 1, minimum + 3, 1), (0, 1, maximum + 1),
                (0, 0, maximum + 1), (2 ** 100, 2 ** 100, 1),
                (minimum, maximum, 2), (0, minimum, -1),
                (maximum - 2, maximum, 2), (minimum + 2, minimum, -2),
                (maximum, maximum, 2), (minimum, minimum, -2),
            )
            for arguments in cases:
                iterator = iter(range(*arguments))
                print(type(iterator).__name__, iterator.__length_hint__())
            """
        );

    [Fact]
    public Task SmallLongRangesDecreaseHintsWhilePreservingHugePositiveAndNegativeValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            huge = 2 ** 100
            for source in (range(huge, huge + 3), range(-huge, -huge - 6, -2), range(0, huge * 3, huge)):
                iterator = iter(source)
                print(type(iterator).__name__, iterator.__length_hint__())
                print(next(iterator), iterator.__length_hint__())
                print(next(iterator), iterator.__length_hint__())
                print(next(iterator), iterator.__length_hint__())
                print(next(iterator, 'done'), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task HintMethodsRejectArgumentsWithoutChangingIteratorPosition() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([7], (7,), range(7, 8), range(2 ** 100, 2 ** 100 + 1)):
                iterator = iter(source)
                for invoke in (lambda: iterator.__length_hint__(1), lambda: iterator.__length_hint__(value=1)):
                    try: invoke()
                    except TypeError as error: print(type(iterator).__name__, type(error).__name__)
                print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
            """
        );
}
