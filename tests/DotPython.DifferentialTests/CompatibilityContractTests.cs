using DotPython.Runtime.Managed;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CompatibilityContractTests
{
    [Fact]
    public void ManagedProfile_DoesNotClaimCpythonBinaryCompatibility()
    {
        Assert.False(ManagedRuntimeDescriptor.Compatibility.SupportsCpythonAbi);
    }
}
