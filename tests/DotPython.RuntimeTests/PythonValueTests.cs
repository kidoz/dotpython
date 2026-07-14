using System.Globalization;
using System.Numerics;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PythonValueTests
{
    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(256)]
    public void WholeNumberFactory_ReusesValuesInsideTheCache(int value)
    {
        var first = PythonWholeNumberValue.Create(value);
        var second = PythonWholeNumberValue.Create(value);

        Assert.Same(first, second);
        Assert.Equal(new BigInteger(value), first.Value);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(257)]
    public void WholeNumberFactory_DoesNotRetainValuesOutsideTheCache(int value)
    {
        var first = PythonWholeNumberValue.Create(value);
        var second = PythonWholeNumberValue.Create(value);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void WholeNumberFactory_PreservesArbitrarySizeValues()
    {
        var value = BigInteger.Parse(
            "1234567890123456789012345678901234567890",
            CultureInfo.InvariantCulture
        );

        var result = PythonWholeNumberValue.Create(value);

        Assert.Equal(value, result.Value);
    }
}
