using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ContainerIteratorHintCompatibilityTests
{
    [Fact]
    public Task TextHintsCountCodePointsAndSavedMethodsShareTheCursor() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for text in ('ascii', 'A\U0001f600e\u0301\U0001f680', ''):
                iterator = iter(text)
                hint = iterator.__length_hint__
                advance = iterator.__next__
                del iterator
                print(hint(), hint())
                while hint():
                    print(ord(advance()), hint())
                try: advance()
                except StopIteration: print('done', hint())
                try: advance()
                except StopIteration: print('still done', hint())
            """
        );

    [Fact]
    public Task BytesHintsDecreaseWithoutConsumingAndMaterializationExhaustsTheCursor() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in (b'\x00\x7f\x80\xff', b''):
                iterator = iter(source)
                print(type(iterator).__name__, iterator.__length_hint__(), iterator.__length_hint__())
                print(next(iterator, 'empty'), iterator.__length_hint__())
                print(list(iterator), iterator.__length_hint__())
                print(next(iterator, 'done'), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task DictionaryAndViewHintsTrackTheRemainingCursor() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = {'a': 10, 'b': 20, 'c': 30}
            for source in (values, values.keys(), values.values(), values.items()):
                iterator = iter(source)
                print(type(iterator).__name__, iterator.__length_hint__(), iterator.__length_hint__())
                print(next(iterator), iterator.__length_hint__())
                print(list(iterator), iterator.__length_hint__())
                print(next(iterator, 'done'), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task DictionaryValueReplacementPreservesHintsAndUpdatesFutureValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for make in (iter, lambda d: iter(d.keys()), lambda d: iter(d.values()), lambda d: iter(d.items())):
                values = {'a': 10, 'b': 20}
                iterator = make(values)
                print(next(iterator), iterator.__length_hint__())
                values['a'] = 100
                values['b'] = 200
                print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
                print(next(iterator, 'done'))
            """
        );

    [Fact]
    public Task DictionaryHintsRecoverAfterTemporaryGrowthBeforeNext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for make in (iter, lambda d: iter(d.keys()), lambda d: iter(d.values()), lambda d: iter(d.items())):
                values = {'a': 10, 'b': 20}
                iterator = make(values)
                hint = iterator.__length_hint__
                print(next(iterator), hint())
                values['temporary'] = 30
                print(hint(), hint())
                del values['temporary']
                print(hint(), next(iterator), hint(), next(iterator, 'done'))
            """
        );

    [Fact]
    public Task DictionaryHintsRecoverAfterTemporaryShrinkBeforeNext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for make in (iter, lambda d: iter(d.keys()), lambda d: iter(d.values()), lambda d: iter(d.items())):
                values = {'a': 10, 'b': 20, 'c': 30}
                iterator = make(values)
                print(next(iterator), iterator.__length_hint__())
                del values['c']
                print(iterator.__length_hint__())
                values['c'] = 300
                print(iterator.__length_hint__(), list(iterator), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task DictionarySizeFailurePermanentlyInvalidatesTheIteratorAndHint() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for make in (iter, lambda d: iter(d.keys()), lambda d: iter(d.values()), lambda d: iter(d.items())):
                values = {'a': 10, 'b': 20}
                iterator = make(values)
                print(next(iterator), iterator.__length_hint__())
                values['c'] = 30
                print(iterator.__length_hint__())
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
                del values['c']
                for attempt in range(2):
                    try: next(iterator, 'done')
                    except RuntimeError as error: print(str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task ZeroDictionaryHintDoesNotSuppressSizeValidationUntilNextExhausts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for make in (iter, lambda d: iter(d.keys()), lambda d: iter(d.values()), lambda d: iter(d.items())):
                values = {'a': 1}
                iterator = make(values)
                print(next(iterator), iterator.__length_hint__())
                values['b'] = 2
                print(iterator.__length_hint__())
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
                values = {}
                iterator = make(values)
                print(iterator.__length_hint__())
                values['a'] = 1
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task ExhaustedDictionaryIteratorsStayExhaustedAfterMutation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for make in (iter, lambda d: iter(d.keys()), lambda d: iter(d.values()), lambda d: iter(d.items())):
                for values in ({'a': 1}, {}):
                    iterator = make(values)
                    hint = iterator.__length_hint__
                    print(list(iterator), hint())
                    values['new'] = 2
                    print(hint(), next(iterator, 'done'))
                    values.clear()
                    print(hint(), next(iterator, 'still done'))
            """
        );

    [Fact]
    public Task SetAndFrozenSetHintsDecreaseWithoutDependingOnIterationOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ({2, 4, 6}, frozenset({2, 4, 6}), set(), frozenset()):
                iterator = iter(source)
                hint = iterator.__length_hint__
                print(type(iterator).__name__, hint(), hint())
                seen = set()
                while hint():
                    value = next(iterator)
                    print(value in source, value not in seen, hint())
                    seen.add(value)
                print(seen == source, next(iterator, 'done'), hint())
            """
        );

    [Fact]
    public Task SetHintsRecoverAfterTemporarySizeChangesBeforeNext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = {1, 2, 3}
            iterator = iter(values)
            hint = iterator.__length_hint__
            values.add(99)
            print(hint(), hint())
            values.remove(99)
            print(hint())
            values.remove(3)
            print(hint())
            values.add(3)
            print(hint(), sorted(iterator), hint())
            """
        );

    [Fact]
    public Task SetSizeFailurePermanentlyInvalidatesTheIteratorAndHint() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for shrink in (False, True):
                values = {1, 2, 3}
                iterator = iter(values)
                if shrink: values.remove(3)
                else: values.add(99)
                print(iterator.__length_hint__())
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
                if shrink: values.add(3)
                else: values.remove(99)
                for attempt in range(2):
                    try: next(iterator, 'done')
                    except RuntimeError as error: print(str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task SetZeroHintsAndCompletedExhaustionHaveDifferentMutationBehavior() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for values in (set(), {1}):
                iterator = iter(values)
                if values: print(next(iterator) in values)
                print(iterator.__length_hint__())
                values.add(2)
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
            for values in (set(), {1}):
                iterator = iter(values)
                print(sorted(iterator), iterator.__length_hint__())
                values.add(2)
                print(iterator.__length_hint__(), next(iterator, 'done'))
                values.clear()
                print(iterator.__length_hint__(), next(iterator, 'still done'))
            """
        );

    [Fact]
    public Task ContainerHintMethodsRejectArgumentsWithoutConsumingValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            mapping = {'key': 7}
            for source in ('x', b'x', mapping, mapping.keys(), mapping.values(), mapping.items(), {7}, frozenset({7})):
                iterator = iter(source)
                hint = iterator.__length_hint__
                for invoke in (lambda: hint(1), lambda: hint(value=1)):
                    try: invoke()
                    except TypeError as error: print(type(iterator).__name__, str(error))
                print(hint(), next(iterator), hint(), next(iterator, 'done'))
            """
        );
}
