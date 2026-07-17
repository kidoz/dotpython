using DotPython.Runtime.Managed;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CompatibilityContractTests
{
    [Fact]
    public void ManagedProfile_DoesNotClaimExecutableNativeCompatibility()
    {
        Assert.Empty(ManagedRuntimeDescriptor.Compatibility.NativeExtensionCapabilities);
    }
}
