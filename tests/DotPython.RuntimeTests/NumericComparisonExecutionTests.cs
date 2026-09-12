using System.Numerics;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class NumericComparisonExecutionTests
{
    [Fact]
    public void MixedComparisonsPreserveExactIntegerBoundariesWithoutAnExecution()
    {
        foreach (var exponent in new[] { 53, 54, 61, 63, 100, 511, 1023 })
        {
            var integer = BigInteger.One << exponent;
            var floating = Math.ScaleB(1.0, exponent);
            foreach (var offset in new[] { -1, 0, 1 })
            {
                AssertComparison(integer + offset, floating, offset);
                AssertComparison(-integer - offset, -floating, -offset);
            }
        }

        var largest = ((BigInteger.One << 53) - 1) << 971;
        AssertComparison(largest - 1, double.MaxValue, -1);
        AssertComparison(largest, double.MaxValue, 0);
        AssertComparison(largest + 1, double.MaxValue, 1);
        AssertComparison(BigInteger.One << 10000, double.MaxValue, 1);
        AssertComparison(-(BigInteger.One << 10000), -double.MaxValue, -1);
    }

    [Fact]
    public void FractionalComparisonsRetainRemaindersSubnormalsAndSignedZero()
    {
        (int Integer, double Floating, int Expected)[] cases =
        [
            (0, 0.0, 0),
            (0, -0.0, 0),
            (0, double.Epsilon, -1),
            (0, -double.Epsilon, 1),
            (0, 0.5, -1),
            (0, -0.5, 1),
            (1, 1.5, -1),
            (2, 1.5, 1),
            (-1, -1.5, 1),
            (-2, -1.5, -1),
            (1, Math.BitDecrement(1.0), 1),
            (1, Math.BitIncrement(1.0), -1),
            (-1, Math.BitDecrement(-1.0), 1),
            (-1, Math.BitIncrement(-1.0), -1),
        ];
        foreach (var (integer, floating, expected) in cases)
            AssertComparison(integer, floating, expected);
    }

    [Fact]
    public void InfinitiesExceedArbitraryIntegersAndNaNsRemainUnordered()
    {
        foreach (
            var integer in new[]
            {
                BigInteger.Zero,
                BigInteger.One << 10000,
                -(BigInteger.One << 10000),
            }
        )
        {
            AssertComparison(integer, double.PositiveInfinity, -1);
            AssertComparison(integer, double.NegativeInfinity, 1);
            var whole = PythonWholeNumberValue.Create(integer);
            var nan = new PythonFloatingPointValue(double.NaN);
            foreach (var comparison in Enum.GetValues<PythonRichComparison>())
            {
                var expected = comparison == PythonRichComparison.NotEqual;
                Assert.Equal(
                    expected,
                    ManagedObjectProtocols.RichCompare(whole, nan, comparison).Value
                );
                Assert.Equal(
                    expected,
                    ManagedObjectProtocols.RichCompare(nan, whole, comparison).Value
                );
            }
        }
    }

    [Fact]
    public void ComplexEqualityUsesExactRealComparisonAndRejectsNonzeroImaginaryParts()
    {
        var exact = BigInteger.One << 100;
        var real = Math.ScaleB(1.0, 100);
        foreach (
            var imaginary in new[]
            {
                0.0,
                -0.0,
                double.Epsilon,
                double.PositiveInfinity,
                double.NaN,
            }
        )
        {
            var complex = new PythonComplexValue(new Complex(real, imaginary));
            foreach (var offset in new[] { -1, 0, 1 })
            {
                var whole = PythonWholeNumberValue.Create(exact + offset);
                var expected = offset == 0 && imaginary == 0;
                Assert.Equal(expected, ManagedObjectProtocols.AreEqual(whole, complex));
                Assert.Equal(expected, ManagedObjectProtocols.AreEqual(complex, whole));
                if (expected)
                    Assert.Equal(
                        ManagedObjectProtocols.ComputePythonHash(whole),
                        ManagedObjectProtocols.ComputePythonHash(complex)
                    );
            }
        }
        var huge = PythonWholeNumberValue.Create(BigInteger.One << 10000);
        Assert.False(
            ManagedObjectProtocols.AreEqual(
                huge,
                new PythonComplexValue(new Complex(double.PositiveInfinity, 0))
            )
        );
    }

    [Fact]
    public void ComplexOrderingStillRaisesTypeErrorEvenAgainstNaN()
    {
        var complex = new PythonComplexValue(Complex.Zero);
        foreach (
            PythonValue other in new PythonValue[]
            {
                PythonTruthValue.False,
                PythonWholeNumberValue.Create(0),
                new PythonFloatingPointValue(double.NaN),
            }
        )
        {
            foreach (
                var comparison in new[]
                {
                    PythonRichComparison.LessThan,
                    PythonRichComparison.LessThanOrEqual,
                    PythonRichComparison.GreaterThan,
                    PythonRichComparison.GreaterThanOrEqual,
                }
            )
            {
                var error = Assert.Throws<PythonRuntimeException>(() =>
                    ManagedObjectProtocols.RichCompare(complex, other, comparison)
                );
                Assert.Equal("TypeError", error.PythonExceptionTypeName);
                error = Assert.Throws<PythonRuntimeException>(() =>
                    ManagedObjectProtocols.RichCompare(other, complex, comparison)
                );
                Assert.Equal("TypeError", error.PythonExceptionTypeName);
            }
        }
    }

    [Fact]
    public void WarmedComparisonSitesDeoptimizeToExactMixedComparisons()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            def compare(a, b):
                return (a == b, a != b, a < b, a <= b, a > b, a >= b)
            for index in range(200):
                compare(index, index + 1)
            integer = 2 ** 53 + 1
            floating = float(2 ** 53)
            print(compare(integer, floating))
            for index in range(200):
                compare(float(index), float(index + 1))
            print(compare(floating, integer))
            print(compare(True, 1.0))
            """,
            "numeric_comparison_warmup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "(False, True, False, False, True, True)",
                "(False, True, True, True, False, False)",
                "(True, False, False, True, False, True)",
                ""
            ),
            output.ToString()
        );
    }

    private static void AssertComparison(BigInteger integer, double floating, int expected)
    {
        var whole = PythonWholeNumberValue.Create(integer);
        var value = new PythonFloatingPointValue(floating);
        Assert.Equal(
            expected,
            Math.Sign(ManagedObjectProtocols.CompareOrdered(whole, value, default))
        );
        Assert.Equal(
            -expected,
            Math.Sign(ManagedObjectProtocols.CompareOrdered(value, whole, default))
        );
        Assert.Equal(expected == 0, ManagedObjectProtocols.AreEqual(whole, value));
        Assert.Equal(expected == 0, ManagedObjectProtocols.AreEqual(value, whole));
        foreach (var comparison in Enum.GetValues<PythonRichComparison>())
        {
            Assert.Equal(
                Expected(comparison, expected),
                ManagedObjectProtocols.RichCompare(whole, value, comparison).Value
            );
            Assert.Equal(
                Expected(comparison, -expected),
                ManagedObjectProtocols.RichCompare(value, whole, comparison).Value
            );
        }
    }

    private static bool Expected(PythonRichComparison comparison, int order) =>
        comparison switch
        {
            PythonRichComparison.Equal => order == 0,
            PythonRichComparison.NotEqual => order != 0,
            PythonRichComparison.LessThan => order < 0,
            PythonRichComparison.LessThanOrEqual => order <= 0,
            PythonRichComparison.GreaterThan => order > 0,
            PythonRichComparison.GreaterThanOrEqual => order >= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
        };
}
