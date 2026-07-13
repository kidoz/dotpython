using DotPython.StdLib;
using Xunit;

namespace DotPython.PackageCompatibilityTests;

public sealed class SupportStatusTests
{
    [Fact]
    public void StandardLibraryStatus_DefinesExplicitCompatibilityStates()
    {
        StandardLibrarySupportStatus[] expected =
        [
            StandardLibrarySupportStatus.Unsupported,
            StandardLibrarySupportStatus.Partial,
            StandardLibrarySupportStatus.Experimental,
            StandardLibrarySupportStatus.Supported,
        ];

        Assert.Equal(expected, Enum.GetValues<StandardLibrarySupportStatus>());
    }
}
