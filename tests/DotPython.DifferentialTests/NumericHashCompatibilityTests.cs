using Xunit;

namespace DotPython.DifferentialTests;

public sealed class NumericHashCompatibilityTests
{
    [Fact]
    public Task FractionalFloatHashesUseThePythonNumericModulus() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (0.5, -0.5, 0.25, -0.25, 1.5, -1.5, 0.1, -0.1, 1.1, -1.1, 3.141592653589793):
                print(hash(value))
            """
        );

    [Fact]
    public Task SmallestAndLargestFiniteFloatsHashTheirExactBinaryValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for spelling in ('5e-324', '-5e-324', '1e-323', '2.225073858507201e-308', '2.2250738585072014e-308', '1e-300', '-1e-300', '1.7976931348623157e308', '-1.7976931348623157e308'):
                print(hash(float(spelling)))
            """
        );

    [Fact]
    public Task FloatExponentRotationsCoverBothSidesOfTheModulusWidth() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for exponent in (-1074, -1022, -123, -122, -121, -62, -61, -60, -1, 0, 1, 60, 61, 62, 121, 122, 123, 1023):
                value = 2.0 ** exponent
                print(hash(value), hash(-value))
            """
        );

    [Fact]
    public Task IntegralFloatsAgreeWithIntegersAndSignedZero() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for integer in (0, 1, -1, 2, -2, 2 ** 53, -(2 ** 53), 2 ** 61, 2 ** 100, -(2 ** 100)):
                value = float(integer)
                print(hash(integer), hash(value), hash(complex(value, 0)))
            print(hash(0.0), hash(-0.0), hash(complex(-0.0, -0.0)), hash(False), hash(True))
            """
        );

    [Fact]
    public Task FloatInfinitiesHaveSignedConstantHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (float('inf'), float('-inf'), float('Infinity'), float('-Infinity')):
                print(hash(value), hash(complex(value, 0)))
            """
        );

    [Fact]
    public Task ComplexHashesCombineFractionalRealAndImaginaryParts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for real, imaginary in ((0, 0.5), (0.5, 0), (0.5, 0.25), (-0.5, 0.25), (0.1, -0.1), (3.5, 7.25), (-1, 0), (0, -1), (1, -1), (-1000004, 1)):
                print(hash(complex(real, imaginary)))
            """
        );

    [Fact]
    public Task ComplexHashesWrapToSignedMachineWidth() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for real, imaginary in ((1e100, 1e100), (-1e100, 1e100), (1e100, -1e100), (-1e100, -1e100), (1.7976931348623157e308, 0.1), (0.1, 1.7976931348623157e308), (2 ** 60, 2 ** 60), (5e-324, -5e-324)):
                print(hash(complex(real, imaginary)))
            """
        );

    [Fact]
    public Task ComplexInfinityComponentsUseTheSameCombinationRule() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            positive = float('inf')
            negative = float('-inf')
            for real, imaginary in ((0, positive), (0, negative), (positive, positive), (positive, negative), (negative, positive), (negative, negative), (0.5, positive), (negative, -0.25)):
                print(hash(complex(real, imaginary)))
            """
        );

    [Fact]
    public Task UserHashesRetainEveryValidSignedMachineInteger() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self, value): self.value = value
                def __hash__(self): return self.value
            for value in (0, 1, -1, -2, 2 ** 61 - 1, 2 ** 61, -(2 ** 61), 2 ** 63 - 1, -(2 ** 63)):
                print(hash(Key(value)))
            """
        );

    [Fact]
    public Task OversizedUserHashesFallBackToTheIntegerHashAlgorithm() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self, value): self.value = value
                def __hash__(self): return self.value
            for value in (2 ** 63, -(2 ** 63) - 1, 2 ** 64 - 1, -(2 ** 64 - 1), 2 ** 100 + 123, -(2 ** 100 + 123), 2 ** 200):
                print(hash(Key(value)), hash(value))
            """
        );

    [Fact]
    public Task BooleanUserHashesAreAcceptedAndInvalidResultsRaiseTypeError() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self, value): self.value = value
                def __hash__(self): return self.value
            for value in (True, False, 0.5, None, '12', [], complex(1, 0)):
                try: print(hash(Key(value)))
                except TypeError as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task HashCallbacksAreInvokedOncePerRequestAndExceptionsPropagate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self):
                    events.append('hash')
                    if len(events) == 3: raise ValueError('third hash')
                    return 2 ** 63 - 1
            key = Key()
            print(hash(key), hash(key), events)
            try: hash(key)
            except ValueError as error: print(str(error), events)
            print(hash(key), events)
            """
        );

    [Fact]
    public Task FractionalNumbersAndEqualUserKeysShareDictionaryEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Half:
                def __hash__(self): return 1152921504606846976
                def __eq__(self, other): return other == 0.5
            key = Half()
            values = {key: 'first'}
            print(values[0.5], values[complex(0.5, 0)])
            values[0.5] = 'updated'
            print(len(values), values[key], next(iter(values)) is key)
            print(values.pop(complex(0.5, 0)), len(values))
            values = {0.5: 'numeric'}
            print(values[key], values.pop(key), len(values))
            """
        );

    [Fact]
    public Task FractionalNumbersAndEqualUserKeysShareSetAndFrozenSetEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Half:
                def __hash__(self): return 1152921504606846976
                def __eq__(self, other): return other == 0.5
            key = Half()
            values = {key}
            print(0.5 in values, complex(0.5, 0) in values)
            values.add(0.5)
            print(len(values), next(iter(values)) is key)
            print(len(values | {0.5}), len(values & {0.5}), len(values - {0.5}))
            values.remove(complex(0.5, 0))
            print(len(values))
            print(hash(frozenset({key})) == hash(frozenset({0.5})), frozenset({key}) == frozenset({0.5}))
            print({frozenset({key}): 'frozen'}[frozenset({0.5})])
            """
        );

    [Fact]
    public Task EqualAggregateKeysKeepConsistentNumericHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Half:
                def __hash__(self): return 1152921504606846976
                def __eq__(self, other): return other == 0.5
            key = Half()
            first = (key, complex(3, 0), frozenset({0.25}))
            second = (0.5, 3, frozenset({complex(0.25, 0)}))
            print(first == second, hash(first) == hash(second))
            print({first: 'tuple'}[second], len({first, second}))
            """
        );

    [Fact]
    public Task SignedRangeUserHashCollisionsReachEqualityInContainers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self): return self.code
                def __eq__(self, other):
                    events.append(self.name + ':' + other.name)
                    return self.name == other.name
            for code in (2 ** 61, 2 ** 63 - 1, -(2 ** 63), 2 ** 100):
                first = Key('same', code)
                second = Key('same', code)
                values = {first: 1}
                events.clear()
                print(values[second], events)
                values = {first}
                events.clear()
                print(second in values, events)
            """
        );

    [Fact]
    public Task FloatNanHashesRemainStableForIdentityBasedContainerLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            first = float('nan')
            second = float('nan')
            print(hash(first) == hash(first), hash(second) == hash(second))
            values = {first: 'first', second: 'second'}
            print(len(values), values[first], values[second])
            values = {first, second}
            print(len(values), first in values, second in values)
            values.remove(first)
            print(len(values), second in values)
            frozen = frozenset({first, second})
            print(hash(frozen) == hash(frozen), {frozen: 'frozen'}[frozenset({second, first})])
            """
        );

    [Fact]
    public Task ComplexNanHashesRemainStableForIdentityBasedContainerLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for real, imaginary in ((float('nan'), 0), (0, float('nan')), (float('nan'), float('nan'))):
                first = complex(real, imaginary)
                second = complex(real, imaginary)
                print(hash(first) == hash(first), hash(second) == hash(second))
                values = {first: 'first', second: 'second'}
                print(len(values), values[first], values[second])
                values = {first, second}
                print(len(values), first in values, second in values)
                values.remove(first)
                print(len(values), second in values)
            """
        );

    [Fact]
    public Task ComplexConstructionPreservesInfinityComponentsAndExistingObjectIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for real, imaginary in ((1, 2), (0, float('inf')), (0, float('-inf')), (float('inf'), 0), (float('-inf'), 0), (float('inf'), float('-inf'))):
                value = complex(real, imaginary)
                print(str(value), repr(value), complex(value) is value)
            for scalar in (0.0, -0.0, float('inf'), float('-inf')):
                value = complex(scalar)
                print(hash(value) == hash(scalar), complex(value) is value)
            value = complex(float('nan'), 0)
            print(complex(value) is value, hash(complex(value)) == hash(value))
            """
        );
}
