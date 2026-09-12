using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BuiltinIteratorProtocolCompatibilityTests
{
    [Fact]
    public Task ExplicitIteratorMethodsShareIdentityCursorAndExhaustionWithBuiltins() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([1, 2, 3], (1, 2, 3), range(1, 4)):
                iterator = iter(source)
                print(type(iterator).__name__, iterator.__iter__() is iterator, iter(iterator) is iterator)
                print(iterator.__next__(), next(iterator), iterator.__length_hint__())
                print(list(iterator.__iter__()), iterator.__length_hint__())
                for attempt in range(2):
                    try: iterator.__next__()
                    except StopIteration as error: print(error.args, error.value)
                print(iterator.__iter__() is iterator, next(iterator, 'done'))
            """
        );

    [Fact]
    public Task SavedMethodsRetainTheirReceiverAndObserveTheSameCursor() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([4, 5, 6], (4, 5, 6), range(4, 7)):
                iterator = iter(source)
                again = iterator.__iter__
                advance = iterator.__next__
                hint = iterator.__length_hint__
                del iterator
                print(again() is again(), advance(), hint())
                print(next(again()), advance(), hint())
                try: advance()
                except StopIteration as error: print(error.args, hint())
                print(next(again(), 'done'))
            """
        );

    [Fact]
    public Task ExplicitListNextObservesMutationsUntilItsFirstFailedAdvance() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [0, 1, 2]
            iterator = iter(values)
            advance = iterator.__next__
            print(advance(), next(iterator))
            values.clear()
            print(iterator.__length_hint__())
            values.extend([7, 8, 9])
            print(advance(), iterator.__length_hint__())
            values.append(10)
            print(next(iterator), iterator.__length_hint__())
            try: advance()
            except StopIteration: print('done')
            values.extend([11, 12])
            print(iterator.__iter__() is iterator, iterator.__length_hint__(), next(iterator, 'still done'))
            try: advance()
            except StopIteration as error: print(error.args)
            """
        );

    [Fact]
    public Task ExplicitRangeNextPreservesArbitraryPrecisionAndDirection() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            huge = 2 ** 100
            for source in (range(huge, huge + 3), range(-huge, -huge - 6, -2), range(0, huge * 3, huge)):
                iterator = iter(source)
                print(type(iterator).__name__, iterator.__iter__() is iterator)
                print(iterator.__next__(), next(iterator), iterator.__length_hint__())
                print(iterator.__next__(), iterator.__length_hint__())
                try: iterator.__next__()
                except StopIteration as error: print(error.args, error.value)
            iterator = iter(range(huge))
            print(iterator.__next__(), iterator.__length_hint__() == huge - 1)
            """
        );

    [Fact]
    public Task ExplicitMethodsRejectArgumentsBeforeConsumingValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                def __getitem__(self, index):
                    print('item', index)
                    if index: raise IndexError
                    return 7
            for source in ([7], (7,), range(7, 8), range(2 ** 100, 2 ** 100 + 1), Sequence()):
                iterator = iter(source)
                for name in ('__iter__', '__next__'):
                    method = getattr(iterator, name)
                    for arguments, keywords in (((1,), {}), ((1, 2), {}), ((), {'value': 1}), ((1,), {'value': 2})):
                        try: method(*arguments, **keywords)
                        except TypeError as error: print(name, str(error))
                print(iterator.__iter__() is iterator, next(iterator))
            """
        );

    [Fact]
    public Task ExplicitMethodsWorkAcrossRepresentedContainerAndAdapterIterators() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            mapping = {'a': 1, 'b': 2}
            sources = ('ab', 'a\U0001f600', b'ab', mapping, mapping.keys(), mapping.values(), mapping.items(),
                       {7}, enumerate([4, 5], 8), zip([1, 2], [3, 4]), map(abs, [-1, -2]), filter(None, [0, 1, 2]))
            for source in sources:
                iterator = iter(source)
                print(iterator.__iter__() is iterator, iterator.__next__(), list(iterator))
                try: iterator.__next__()
                except StopIteration as error: print(error.args, error.value)
                print(next(iterator, 'done'))
            """
        );

    [Fact]
    public Task IndexSequenceFailuresRetryTheSameIndexThroughSavedNext() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                fail = True
                def __getitem__(self, index):
                    print('item', index)
                    if index == 1 and self.fail:
                        self.fail = False
                        raise ValueError('retry')
                    if index == 3: raise IndexError('end')
                    return index + 10
            iterator = iter(Sequence())
            advance = iterator.__next__
            print(iterator.__iter__() is iterator, advance())
            try: advance()
            except ValueError as error: print(str(error))
            print(next(iterator), advance())
            for attempt in range(2):
                try: advance()
                except StopIteration as error: print(error.args, error.value)
            """
        );

    [Fact]
    public Task IndexSequenceStopIterationBecomesEmptyStickyExhaustion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Finished(StopIteration): pass
            class Sequence:
                def __getitem__(self, index):
                    print('item', index)
                    if index == 1: raise Finished('payload')
                    return index
            iterator = iter(Sequence())
            advance = iterator.__next__
            print(advance())
            for attempt in range(2):
                try: advance()
                except StopIteration as error: print(type(error).__name__, error.args, error.value)
            print(iterator.__iter__() is iterator, next(iterator, 'done'))
            """
        );

    [Fact]
    public Task AdapterCallbackFailuresKeepTheUnderlyingConsumptionOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def apply(value):
                print('apply', value)
                if value == 2: raise ValueError('skip')
                return value * 10
            def keep(value):
                print('keep', value)
                if value == 2: raise ValueError('skip')
                return value % 2
            for iterator in (map(apply, [1, 2, 3]), filter(keep, [1, 2, 3])):
                advance = iterator.__next__
                print(advance())
                try: advance()
                except ValueError as error: print(str(error))
                print(next(iterator))
                try: advance()
                except StopIteration as error: print(error.args)
            """
        );

    [Fact]
    public Task ExplicitMapNextPreservesCallbackStopIterationIdentityAndPayload() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Finished(StopIteration): pass
            failure = Finished('payload', 7)
            def stop(value): raise failure
            iterator = map(stop, [1, 2])
            advance = iterator.__next__
            for method in (advance, lambda: next(iterator), advance):
                try: method()
                except StopIteration as error: print(error is failure, type(error).__name__, error.args, error.value)
            """
        );

    [Fact]
    public Task ExplicitAdapterNextPreservesStrictLengthErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for iterator in (zip([1], [2, 3], strict=True), map(lambda a, b: a + b, [1], [2, 3], strict=True)):
                print(iterator.__iter__() is iterator, iterator.__next__())
                try: iterator.__next__()
                except ValueError as error: print(str(error))
                print(next(iterator, 'done'))
            """
        );
}
