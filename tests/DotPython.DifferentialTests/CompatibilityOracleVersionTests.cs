using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CompatibilityOracleVersionTests
{
    [Theory]
    [InlineData("Python 3.14.7", "3.14", true)]
    [InlineData("Python 3.14.7", "3.14.7", true)]
    [InlineData("Python 3.14.7\n", "3.14.7", true)]
    [InlineData("Python 3.14.7+", "3.14.7", true)]
    [InlineData("Python 3.14.7 (main, Oct  1 2026)", "3.14.7", true)]
    [InlineData("Python 3.14", "3.14", true)]
    [InlineData("Python 3.14.0rc1", "3.14", true)]
    [InlineData("Python 3.14.70", "3.14.7", false)]
    [InlineData("Python 3.14.71", "3.14.7", false)]
    [InlineData("Python 3.14.7", "3.14.8", false)]
    [InlineData("Python 3.14.7", "3.14.7.1", false)]
    [InlineData("Python 3.14.7", "3.15", false)]
    [InlineData("Python 3.13.9", "3.14", false)]
    [InlineData("Python 3.140", "3.14", false)]
    [InlineData("PyPy 3.14.7", "3.14", false)]
    [InlineData("python 3.14.7", "3.14.7", false)]
    [InlineData("Python 2.7.18", "3.14", false)]
    [InlineData("", "3.14", false)]
    [InlineData("Python 3.14.7", "", false)]
    public void VersionBannerMatches_ComparesReleaseComponentsExactly(
        string banner,
        string version,
        bool expected
    )
    {
        ArgumentNullException.ThrowIfNull(banner);
        ArgumentNullException.ThrowIfNull(version);
        Assert.Equal(expected, CompatibilityOracle.VersionBannerMatches(banner, version));
    }
}
