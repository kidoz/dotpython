using DotPython.Runtime.Managed;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ManagedRuntimeDescriptorTests
{
    [Fact]
    public void Compatibility_IdentifiesManagedRuntimeWithoutCpythonAbi()
    {
        var compatibility = ManagedRuntimeDescriptor.Compatibility;

        Assert.Equal("dotpython-managed", compatibility.Implementation);
        Assert.False(compatibility.SupportsCpythonAbi);
    }
}
