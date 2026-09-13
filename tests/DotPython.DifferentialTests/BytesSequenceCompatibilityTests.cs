using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BytesSequenceCompatibilityTests
{
    [Fact]
    public Task ForwardAndReverseSlicesPreserveEveryByteValue() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = bytes([0, 9, 10, 13, 32, 39, 65, 127, 128, 255])
            for start, stop, step in ((None, None, None), (1, 8, 2), (8, 1, -2), (None, None, -1), (4, 4, 1), (-9, -1, 3), (-2, -9, -3)):
                print(list(values[start:stop:step]))
            print(b'abc'[1:], b'ABC'[::-1], bytes([128, 255])[::1])
            """
        );

    [Fact]
    public Task HugeSliceBoundsAndStepsAreClippedWithoutOverflow() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = bytes([0, 1, 2, 3, 4])
            huge = 2 ** 100
            for start, stop, step in ((-huge, huge, 1), (huge, -huge, -1), (None, None, huge), (None, None, -huge), (1, 5, 2 ** 63 - 1), (3, -6, -(2 ** 63)), (huge, huge, 1)):
                print(list(values[start:stop:step]))
            """
        );

    [Fact]
    public Task SliceBoundsConvertStepStartAndStopOnceInThatOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            values = b'abcdef'
            print(values[Index('start', 1):Index('stop', 6):Index('step', 2)], events)
            events.clear()
            print(values[Index('start', 5):Index('stop', -10):Index('step', -2)], events)
            """
        );

    [Fact]
    public Task ReusedIndexObjectsAreConvertedSeparatelyForEachSliceComponent() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self): self.calls = 0
                def __index__(self): self.calls += 1; return self.calls
            index = Index()
            print(b'abcdef'[index:index:index], index.calls)
            """
        );

    [Fact]
    public Task ZeroStepStopsBeforeAnyOtherBoundConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            for values in (b'', b'abc'):
                events.clear()
                try: values[Index('start', 0):Index('stop', 3):Index('step', 0)]
                except ValueError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task InvalidSliceBoundResultsStopAtTheFailingComponent() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): events.append(self.name); return self.value
            for bad in (None, 1.5, '1', NotImplemented):
                for failing in ('step', 'start', 'stop'):
                    events.clear()
                    try: b'abc'[Index('start', bad if failing == 'start' else 0):Index('stop', bad if failing == 'stop' else 3):Index('step', bad if failing == 'step' else 1)]
                    except TypeError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task BoundExceptionsKeepTheirIdentityAndEarlierSideEffects() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('bound failed')
            events = []
            class Index:
                def __init__(self, name): self.name = name
                def __index__(self):
                    events.append(self.name)
                    if self.name == 'start': raise problem
                    return 1
            try: b'abc'[Index('start'):Index('stop'):Index('step')]
            except LookupError as error: print(error is problem, str(error), events)
            """
        );

    [Fact]
    public Task FullSlicesPreserveIdentityAndEmptySlicesUseTheCanonicalEmptyBytes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = bytes([1, 2, 3])
            empty = bytes()
            print(values[:] is values, values[::1] is values, values[-100:100] is values)
            print(values[:0] is empty, values[2:1] is empty, values[2:2:-1] is empty)
            print(empty[:] is empty, empty[::-1] is empty)
            """
        );

    [Fact]
    public Task ContiguousSingletonSlicesReuseTheCacheButStridedSingletonSlicesAreFresh() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = bytes([65, 128, 255])
            for index in (0, 1, 2):
                cached = bytes([values[index]])
                contiguous = values[index:index + 1]
                strided = values[index:index + 1:2]
                other = values[index:index + 1:2]
                print(contiguous is cached, strided == cached, strided is cached, strided is other)
            single = bytes([65])
            print(single[:] is single, single[::2] is single, single[::-1] is single)
            """
        );

    [Fact]
    public Task RepetitionConvertsCountsOnceInBothDirectionsAndForInPlaceAssignment() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): events.append(self.value); return self.value
            for count in (-2, 0, 1, 2, 3):
                events.clear()
                original = b'ab'
                value = original
                print(value * Index(count), Index(count) * value)
                value *= Index(count)
                print(value, original, events)
            """
        );

    [Fact]
    public Task RepetitionPreservesNoOpIdentityAndAllocatesFreshEmptyResultsForNonemptySources() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = bytes([65, 66])
            empty = bytes()
            print(value * 1 is value, 1 * value is value)
            for count in (-(2 ** 63), -1, 0, 1, 2, 2 ** 63 - 1):
                print(empty * count is empty, count * empty is empty)
            for count in (-1, 0):
                result = value * count
                other = value * count
                print(result == empty, result is empty, result is other)
            alias = value
            alias *= 1
            print(alias is value)
            """
        );

    [Fact]
    public Task NativeWidthOverflowIsCheckedBeforeEmptyOrNegativeRepeatShortcuts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): events.append('index'); return self.value
            for values in (b'', b'ab'):
                for count in (-(2 ** 63) - 1, 2 ** 63, -(2 ** 100), 2 ** 100):
                    for index in (count, Index(count)):
                        events = []
                        try: values * index
                        except OverflowError as error: print(str(error), events)
                        events.clear()
                        try: index * values
                        except OverflowError as error: print(str(error), events)
            """
        );

    [Fact]
    public Task RepetitionLengthOverflowRaisesBeforeAttemptingAllocation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __index__(self): return 2 ** 62
            for count in (2 ** 62, Index()):
                try: b'ab' * count
                except OverflowError as error: print(str(error))
                try: count * b'ab'
                except OverflowError as error: print(str(error))
            """
        );

    [Fact]
    public Task RepetitionRejectsNonIntegerAndDisabledIndexMethods() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
            class Disabled: __index__ = None
            for count in (None, 1.5, '2', Index(None), Index(1.5), Index('2'), Index(NotImplemented), Disabled()):
                try: b'ab' * count
                except TypeError as error: print(str(error))
                try: count * b'ab'
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task MultiplicationMethodsTakePriorityOverIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self): events.append('index'); return 2
                def __mul__(self, other): events.append('mul'); return 'forward'
                def __rmul__(self, other): events.append('rmul'); return 'reflected'
            count = Index()
            print(b'ab' * count, count * b'ab', events)
            events.clear()
            values = b'ab'
            values *= count
            print(values, events)
            """
        );

    [Fact]
    public Task NotImplementedMultiplicationFallsBackToIndexConversion() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Index:
                def __index__(self): events.append('index'); return 2
                def __mul__(self, other): events.append('mul'); return NotImplemented
                def __rmul__(self, other): events.append('rmul'); return NotImplemented
            count = Index()
            print(b'ab' * count, count * b'ab', events)
            events.clear()
            values = b'ab'
            values *= count
            print(values, events)
            """
        );

    [Fact]
    public Task RepetitionCallbackExceptionsPreserveIdentityAndDoNotPublishAssignment() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            problem = LookupError('repeat failed')
            events = []
            class Index:
                def __index__(self): events.append('index'); raise problem
            values = b'ab'
            alias = values
            try: values *= Index()
            except LookupError as error: print(error is problem, str(error), values is alias, events)
            try: Index() * values
            except LookupError as error: print(error is problem, str(error), events)
            """
        );

    [Fact]
    public Task IndexLookupUsesTheClassAndObservesMethodReplacement() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base:
                def __index__(self): events.append('base'); return 1
            class Index(Base): pass
            index = Index()
            index.__index__ = lambda: 0
            values = b'abc'
            print(values[index:], values * index, events)
            def replacement(self): events.append('replacement'); return 2
            Base.__index__ = replacement
            print(values[index:], values * index, events)
            """
        );

    [Fact]
    public Task ScalarIndicesUseByteSpecificErrorsAndPreserveUnsignedValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Index:
                def __init__(self, value): self.value = value
                def __index__(self): return self.value
            values = bytes([0, 127, 128, 255])
            print(values[False], values[True], values[Index(-1)], values[Index(2)])
            for index in (None, 1.5, '1'):
                try: values[index]
                except TypeError as error: print(str(error))
            for count in (-5, 4, -(2 ** 63), 2 ** 63 - 1, 2 ** 63):
                for index in (count, Index(count)):
                    try: values[index]
                    except IndexError as error: print(str(error))
            """
        );

    [Fact]
    public Task BooleanAndMissingSliceBoundsKeepTheirDistinctMeaning() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = b'abcd'
            print(values[False:None:True], values[True:None], values[None:True])
            class Disabled: __index__ = None
            for bound in (1.5, '1', Disabled()):
                try: values[bound:]
                except TypeError as error: print(str(error))
                try: values[::bound]
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task ConstructorAndEncodingPathsRetainTheirDistinctCacheAndIdentityRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            empty = b''
            for value in (bytes(), bytes([]), bytes(()), bytes(0)):
                print(value is empty)
            cached = b'a'
            for value in (bytes([97]), 'a'.encode(), bytes('a', 'ascii')):
                print(value is cached)
            fresh = bytes((97,))
            other = bytes((97,))
            print(fresh == cached, fresh is cached, fresh is other, bytes(fresh) is fresh)
            zero = b'\x00'
            first_zero = bytes(1)
            second_zero = bytes(1)
            print(first_zero == zero, first_zero is zero, first_zero is second_zero)
            print(bytes(cached) is cached, bytes(empty) is empty)
            print(fresh[:] is fresh, fresh[::1] is fresh, fresh[-100:100] is fresh)
            strided = fresh[::2]
            reversed_value = fresh[::-1]
            print(strided == cached, strided is cached, strided is fresh)
            print(reversed_value == cached, reversed_value is cached, reversed_value is fresh)
            """
        );
}
