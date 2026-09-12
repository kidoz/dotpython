using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ReverseIteratorCompatibilityTests
{
    [Fact]
    public Task BuiltinReverseIteratorsExposeTheirTypesAndLazyRemainingHints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([1, 2, 3], (1, 2, 3), 'a😀z', b'az', range(2, 8, 2)):
                iterator = reversed(source)
                print(type(iterator).__name__, iterator.__length_hint__(), iter(iterator) is iterator)
                print(iterator.__iter__() is iterator, iterator.__next__(), iterator.__length_hint__())
                print(list(iterator), iterator.__length_hint__(), next(iterator, 'done'))
            """
        );

    [Fact]
    public Task DirectReversedMethodsCreateFreshLazyCursorsAndValidateArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            mapping = {'a': 1, 'b': 2}
            values = [1, 2]
            for source in (values, range(2), mapping, mapping.keys(), mapping.values(), mapping.items(), MappingProxyType(mapping)):
                method = source.__reversed__
                try: method(1)
                except TypeError as error: print(str(error))
                try: method(unexpected=1)
                except TypeError as error: print(str(error))
                iterator = method()
                print(type(iterator).__name__, iterator is method(), iterator.__length_hint__(), list(iterator))
            iterator = values.__reversed__()
            values[1] = 20
            print(next(iterator), list(iterator))
            iterator = mapping.values().__reversed__()
            mapping['b'] = 20
            print(next(iterator), list(iterator))
            iterator = MappingProxyType(mapping).__reversed__()
            mapping['c'] = 3
            print(iterator.__length_hint__())
            try: next(iterator)
            except RuntimeError as error: print(str(error))
            """
        );

    [Fact]
    public Task ListReverseHintsObserveShrinkAndRegrowthWithoutExhaustingTheCursor() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [0, 1, 2, 3]
            iterator = reversed(values)
            print(next(iterator), iterator.__length_hint__())
            values.clear()
            print(iterator.__length_hint__())
            values.extend([10, 11, 12, 13, 14])
            print(iterator.__length_hint__(), next(iterator))
            values[0] = 99
            values.append(15)
            print(iterator.__length_hint__(), list(iterator))
            values.extend([20, 21])
            print(iterator.__length_hint__(), next(iterator, 'done'))
            """
        );

    [Fact]
    public Task EmptyAndFailedListReverseIteratorsCannotReviveAfterAppend() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = []
            iterator = reversed(values)
            values.append(7)
            print(iterator.__length_hint__(), next(iterator, 'empty'))
            values = [1, 2, 3]
            iterator = reversed(values)
            values.clear()
            print(next(iterator, 'done'))
            values.extend([4, 5, 6, 7])
            print(iterator.__length_hint__(), next(iterator, 'still done'))
            values = [1]
            iterator = reversed(values)
            print(next(iterator), iterator.__length_hint__())
            values.append(2)
            print(next(iterator, 'done'))
            """
        );

    [Fact]
    public Task SavedReverseMethodsShareTheirCursorAndRejectArgumentsWithoutAdvancing() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ([1, 2], (1, 2), 'ab', b'ab', range(2), {'a': 1, 'b': 2}):
                iterator = reversed(source)
                hint = iterator.__length_hint__
                advance = iterator.__next__
                identity = iterator.__iter__
                for method in (hint, advance, identity):
                    try: method(1)
                    except TypeError as error: print(str(error))
                    try: method(unexpected=1)
                    except TypeError as error: print(str(error))
                print(identity() is iterator, hint())
                del iterator
                print(advance(), hint(), advance(), hint())
                try: advance()
                except StopIteration as error: print(error.args, hint())
            """
        );

    [Fact]
    public Task HugeRangesReverseLazilyWithArbitraryPrecisionHintsAndSafeListOverflow() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            count = 2 ** 100
            iterator = reversed(range(count))
            print(type(iterator).__name__, iterator.__length_hint__() == count)
            try: list(iterator)
            except OverflowError as error: print(str(error), iterator.__length_hint__() == count)
            print(next(iterator) == count - 1, iterator.__length_hint__() == count - 1)
            target = ['original']
            try: target.extend(iterator)
            except OverflowError as error: print(str(error), target)
            print(next(iterator) == count - 2, iterator.__length_hint__() == count - 2)
            for source in (range(count, count + 6, 2), range(-count, -count - 6, -2), range(0, count * 3, count)):
                iterator = reversed(source)
                print(type(iterator).__name__, iterator.__length_hint__(), list(iterator))
            """
        );

    [Fact]
    public Task ReverseRangeRepresentationHandlesEmptyRangesAndMachineIntegerBoundaries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            maximum = 2 ** 63 - 1
            minimum = -(2 ** 63)
            cases = (
                (0, 0, 1), (2 ** 100, 2 ** 100, 1), (0, 0, 2 ** 100),
                (maximum - 2, maximum, 1), (minimum, minimum + 3, 1),
                (maximum + 1, maximum + 4, 1), (minimum - 1, minimum + 2, 1),
                (0, maximum, maximum - 1), (0, minimum, minimum + 1),
                (minimum, minimum + 3, 2), (maximum, maximum - 3, -2),
            )
            for arguments in cases:
                iterator = reversed(range(*arguments))
                print(type(iterator).__name__, iterator.__length_hint__(), list(iterator))
            """
        );

    [Fact]
    public Task DictionaryAndViewReverseIteratorsReadCurrentValuesInReverseInsertionOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for kind in ('dict', 'keys', 'values', 'items'):
                values = {'a': 1, 'b': 2, 'c': 3}
                source = values if kind == 'dict' else getattr(values, kind)()
                iterator = reversed(source)
                print(type(iterator).__name__, iterator.__length_hint__(), next(iterator))
                values['b'] = 20
                values['a'] = 10
                print(iterator.__length_hint__(), list(iterator), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task DictionaryReverseSizeHintsCanRecoverUntilNextPermanentlyInvalidates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for kind in ('dict', 'keys', 'values', 'items'):
                values = {'a': 1, 'b': 2}
                source = values if kind == 'dict' else getattr(values, kind)()
                iterator = reversed(source)
                values['c'] = 3
                print(iterator.__length_hint__())
                del values['c']
                print(iterator.__length_hint__(), next(iterator))
                values['c'] = 3
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
                del values['c']
                try: next(iterator)
                except RuntimeError as error: print(str(error), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task DictionaryReverseZeroHintStillChecksSizeUntilExhaustionIsObserved() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = {'a': 1}
            iterator = reversed(values)
            print(next(iterator), iterator.__length_hint__())
            values['b'] = 2
            try: next(iterator)
            except RuntimeError as error: print(str(error))
            iterator = reversed(values)
            print(list(iterator), iterator.__length_hint__())
            values['c'] = 3
            print(next(iterator, 'done'), iterator.__length_hint__())
            values = {}
            iterator = reversed(values)
            values['a'] = 1
            try: next(iterator)
            except RuntimeError as error: print(str(error))
            """
        );

    [Fact]
    public Task ReversedSpecialMethodsUseTypeLookupAndPermitArbitraryReturnValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Custom:
                def __reversed__(self):
                    print('reversed')
                    return 42
                def __len__(self): raise AssertionError('length should not run')
            value = Custom()
            value.__reversed__ = lambda: 'instance'
            print(reversed(value))
            class Disabled:
                __reversed__ = None
                def __len__(self): return 2
                def __getitem__(self, index): return index
            try: reversed(Disabled())
            except TypeError as error: print(str(error))
            class Invalid:
                __reversed__ = 7
            try: reversed(Invalid())
            except TypeError as error: print(str(error))
            class Descriptor:
                def __get__(self, instance, owner): raise AttributeError('reverse lookup failed')
            class DescriptorSequence:
                __reversed__ = Descriptor()
                def __len__(self): raise AssertionError('must not fall back')
                def __getitem__(self, index): return index
            try: reversed(DescriptorSequence())
            except AttributeError as error: print(str(error))
            """
        );

    [Fact]
    public Task GenericReverseFallbackIsLazyAndHintsUseCurrentLengthWithoutMovingTheCursor() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                def __init__(self): self.values = [10, 20, 30]
                def __len__(self):
                    print('len', len(self.values))
                    return len(self.values)
                def __getitem__(self, index):
                    print('get', index)
                    return self.values[index]
            value = Sequence()
            iterator = reversed(value)
            print(type(iterator).__name__, iterator.__length_hint__())
            value.values.clear()
            print(iterator.__length_hint__())
            value.values.extend([40, 50, 60, 70])
            print(iterator.__length_hint__(), next(iterator), iterator.__length_hint__())
            print(list(iterator), iterator.__length_hint__())
            """
        );

    [Fact]
    public Task ReentrantReverseHintsSnapshotTheCursorBeforeLengthCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Sequence:
                def __init__(self):
                    self.iterator = None
                    self.advance = False
                def __len__(self):
                    print('len')
                    if self.advance:
                        self.advance = False
                        print('advance', next(self.iterator, 'done'))
                    return 3
                def __getitem__(self, index):
                    print('get', index)
                    return index
            value = Sequence()
            iterator = reversed(value)
            value.iterator = iterator
            value.advance = True
            print('hint', iterator.__length_hint__())
            print('hint', iterator.__length_hint__())
            print(next(iterator), next(iterator))
            value.advance = True
            print('zero', iterator.__length_hint__(), next(iterator, 'done'), iterator.__length_hint__(), value.advance)
            """
        );

    [Fact]
    public Task GenericReverseItemFailuresPermanentlyExhaustEvenWhenTheErrorPropagates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for failure in (IndexError, StopIteration, ValueError, TypeError):
                class Sequence:
                    def __len__(self): return 2
                    def __getitem__(self, index):
                        print('get', index)
                        raise failure('failure')
                iterator = reversed(Sequence())
                try: print(next(iterator, 'done'))
                except Exception as error: print(type(error).__name__, str(error))
                print(iterator.__length_hint__(), next(iterator, 'still done'))
            """
        );

    [Fact]
    public Task GenericReverseHintErrorsPreserveCursorAndRetryTheCurrentLengthSlot() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for failure in (TypeError, ValueError, OverflowError, IndexError, StopIteration):
                class Sequence:
                    def __init__(self): self.fail = False
                    def __len__(self):
                        if self.fail: raise failure('hint failure')
                        return 2
                    def __getitem__(self, index): return index
                value = Sequence()
                iterator = reversed(value)
                value.fail = True
                try: iterator.__length_hint__()
                except Exception as error: print(type(error).__name__, str(error))
                value.fail = False
                print(iterator.__length_hint__(), next(iterator), list(iterator))
                value.fail = True
                print(iterator.__length_hint__())
            """
        );

    [Fact]
    public Task GenericReverseAdmissionRequiresSequenceSlotsAndValidMachineSizedLength() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class IterableOnly:
                def __iter__(self): return iter([1, 2])
            class LengthOnly:
                def __len__(self): return 2
            for value in (IterableOnly(), LengthOnly(), 1, {1, 2}):
                try: reversed(value)
                except TypeError as error: print(str(error))
            class Sequence:
                def __getitem__(self, index): return index
                def __len__(self): return self.size
            class DisabledItem:
                __getitem__ = None
                def __len__(self): return 2
            iterator = reversed(DisabledItem())
            print(type(iterator).__name__)
            try: next(iterator)
            except TypeError as error: print(str(error))
            print(next(iterator, 'done'))
            value = Sequence()
            for size in (-1, 1.5, 2 ** 100, True):
                value.size = size
                try: print(list(reversed(value)))
                except (TypeError, ValueError, OverflowError) as error: print(type(error).__name__, str(error))
            """
        );
}
