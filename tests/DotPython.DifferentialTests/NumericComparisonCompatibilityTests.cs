using Xunit;

namespace DotPython.DifferentialTests;

public sealed class NumericComparisonCompatibilityTests
{
    [Fact]
    public Task IntegerNeighborsBeyondFloatPrecisionRemainDistinct() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for sign in (1, -1):
                for offset in (-2, -1, 0, 1, 2, 3):
                    integer = sign * (2 ** 53 + offset)
                    floating = sign * float(2 ** 53)
                    for left, right in ((integer, floating), (floating, integer)):
                        print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            """
        );

    [Fact]
    public Task PowerOfTwoBoundariesUseEveryIntegerBit() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for exponent in (48, 52, 53, 54, 63, 64, 100, 511, 1023):
                floating = 2.0 ** exponent
                for offset in (-1, 0, 1):
                    integer = 2 ** exponent + offset
                    for left, right in ((integer, floating), (floating, integer), (-integer, -floating), (-floating, -integer)):
                        print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            """
        );

    [Fact]
    public Task FractionalFloatsCompareAgainstIntegersWithoutTruncating() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for integer in (-2, -1, 0, 1, 2):
                for floating in (-2.5, -1.5, -0.5, -0.1, 0.1, 0.5, 1.5, 2.5):
                    for left, right in ((integer, floating), (floating, integer)):
                        print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            """
        );

    [Fact]
    public Task SubnormalFloatsRemainStrictlyBetweenNeighboringIntegers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for spelling in ('5e-324', '-5e-324', '2.225073858507201e-308', '-2.225073858507201e-308', '2.2250738585072014e-308'):
                floating = float(spelling)
                for integer in (-1, 0, 1):
                    for left, right in ((integer, floating), (floating, integer)):
                        print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            """
        );

    [Fact]
    public Task LargestFiniteFloatComparesExactlyWithItsIntegerNeighbors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            floating = float('1.7976931348623157e308')
            exact = (2 ** 53 - 1) * 2 ** 971
            for integer in (exact - 1, exact, exact + 1, 2 ** 1024, 2 ** 20000):
                for left, right in ((integer, floating), (floating, integer), (-integer, -floating), (-floating, -integer)):
                    print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            """
        );

    [Fact]
    public Task InfinitiesExceedEveryFiniteIntegerWithoutOverflow() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for integer in (0, 1, -1, 2 ** 1024, -(2 ** 1024), 2 ** 20000, -(2 ** 20000)):
                for floating in (float('inf'), float('-inf')):
                    for left, right in ((integer, floating), (floating, integer)):
                        print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            """
        );

    [Fact]
    public Task NanIsUnorderedWithEveryIntegerInBothDirections() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            floating = float('nan')
            for integer in (False, True, 0, -1, 2 ** 53 + 1, 2 ** 20000, -(2 ** 20000)):
                for left, right in ((integer, floating), (floating, integer)):
                    print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            """
        );

    [Fact]
    public Task RealOnlyComplexEqualityUsesExactIntegerComparison() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for exponent in (53, 63, 100, 1023):
                for sign in (1, -1):
                    value = complex(sign * 2.0 ** exponent, 0)
                    for offset in (-1, 0, 1):
                        integer = sign * (2 ** exponent + offset)
                        print(value == integer, integer == value, value != integer, integer != value)
            for value in (complex(float('inf'), 0), complex(float('-inf'), 0), complex(float('nan'), 0)):
                for integer in (2 ** 20000, -(2 ** 20000)):
                    print(value == integer, integer == value, value != integer, integer != value)
            """
        );

    [Fact]
    public Task NonzeroOrNanImaginaryPartsNeverEqualRealNumbers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for imaginary in (1, -1, float('5e-324'), float('inf'), float('nan')):
                value = complex(2.0 ** 53, imaginary)
                for real in (2 ** 53, 2 ** 53 + 1, 2.0 ** 53, 2 ** 20000):
                    print(value == real, real == value, value != real, real != value)
            """
        );

    [Fact]
    public Task BooleansAndSignedZerosRetainNumericEqualityAndOrdering() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for boolean in (False, True):
                for real in (-1, 0, 1, 2, -0.0, 0.0, 0.5, 1.0, 1.5, 2 ** 53 + 1):
                    for left, right in ((boolean, real), (real, boolean)):
                        print(left == right, left != right, left < right, left <= right, left > right, left >= right)
                for value in (complex(0, -0.0), complex(1, -0.0), complex(0, 1)):
                    print(boolean == value, value == boolean, boolean != value, value != boolean)
            """
        );

    [Fact]
    public Task ListMembershipCountIndexAndRemovalUseExactEquality() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            integer = 2 ** 53 + 1
            floating = float(2 ** 53)
            values = [integer, floating, complex(floating, 0), integer]
            print(integer in [floating], floating in [integer], integer not in [floating])
            print(values.count(integer), values.count(floating), values.index(integer), values.index(floating))
            values.remove(floating)
            print([type(value).__name__ for value in values], values.count(integer), values.count(floating))
            try: [floating].index(integer)
            except ValueError: print('missing index')
            try: [floating].remove(integer)
            except ValueError: print('missing removal')
            """
        );

    [Fact]
    public Task ListsAndTuplesUseExactEqualityBeforeLexicographicOrdering() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            integer = 2 ** 53 + 1
            floating = float(2 ** 53)
            for first, second in (([integer], [floating]), ((integer,), (floating,)), ([integer, 0], [floating, 1]), ((integer, 0), (floating, 1))):
                for left, right in ((first, second), (second, first)):
                    print(left == right, left != right, left < right, left <= right, left > right, left >= right)
            print((integer,) in [(floating,)], [integer] in [[floating]])
            """
        );

    [Fact]
    public Task DictionaryAndSetEqualityAgreeWithSequenceMembership() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            integer = 2 ** 53 + 1
            floating = float(2 ** 53)
            values = {integer: 'integer', floating: 'floating'}
            print(len(values), values[integer], values[floating], integer in [floating], integer in {floating})
            print({'key': integer} == {'key': floating}, {'key': [integer]} == {'key': [floating]})
            print({integer} == {floating}, frozenset({integer}) == frozenset({floating}))
            print(len({integer, floating, complex(floating, 0)}), len({(integer,), (floating,)}))
            print((integer,) == (floating,), hash((integer,)) == hash((floating,)))
            """
        );

    [Fact]
    public Task MinAndMaxSelectTheExactMixedNumericExtrema() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            integer = 2 ** 53 + 1
            floating = float(2 ** 53)
            for values in ([integer, floating], [floating, integer]):
                print(min(values) is floating, max(values) is integer)
                print(min(*values) is floating, max(*values) is integer)
            huge = 2 ** 20000
            positive = float('inf')
            negative = float('-inf')
            print(min([positive, huge]) is huge, max([huge, positive]) is positive)
            print(min([negative, -huge]) is negative, max([-huge, negative]) == -huge)
            """
        );

    [Fact]
    public Task SortingDistinguishesRoundedNeighborsAndPreservesEqualValueOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            values = [2 ** 53 + 1, float(2 ** 53), 2 ** 53, 2 ** 53 - 1, -(2 ** 53), -float(2 ** 53), -(2 ** 53 + 1)]
            records = [(value, index) for index, value in enumerate(values)]
            print([record[1] for record in sorted(records, key=lambda record: record[0])])
            print([record[1] for record in sorted(records, key=lambda record: record[0], reverse=True)])
            records.sort(key=lambda record: record[0])
            print([record[1] for record in records])
            """
        );

    [Fact]
    public Task ChainedMixedComparisonsShortCircuitWithExactResults() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def mark(value):
                events.append('called')
                return value
            integer = 2 ** 53 + 1
            floating = float(2 ** 53)
            print(integer <= floating < mark(integer + 1), events)
            print(floating < integer < mark(integer + 1), events)
            print(integer != floating == mark(2 ** 53), events)
            """
        );

    [Fact]
    public Task NumericComparisonsDoNotInvokeImplicitConversionHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Convertible:
                def __float__(self): events.append('float'); return 1.0
                def __index__(self): events.append('index'); return 1
                def __complex__(self): events.append('complex'); return complex(1, 0)
            value = Convertible()
            for number in (1, 1.0, complex(1, 0), 2 ** 20000):
                print(number == value, value == number, number != value, value != number)
            for number in (1, 1.0):
                try: print(number < value)
                except TypeError: print('unsupported')
                try: print(value > number)
                except TypeError: print('unsupported')
            print(events)
            """
        );

    [Fact]
    public Task ComplexOrderingStillRaisesTypeErrorForRealOnlyValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = complex(2.0 ** 53, 0)
            for other in (2 ** 53, 2 ** 53 + 1, 2.0 ** 53, 2 ** 20000):
                for left, right in ((value, other), (other, value)):
                    try: print(left < right)
                    except TypeError: print('unsupported <')
                    try: print(left <= right)
                    except TypeError: print('unsupported <=')
                    try: print(left > right)
                    except TypeError: print('unsupported >')
                    try: print(left >= right)
                    except TypeError: print('unsupported >=')
            """
        );
}
