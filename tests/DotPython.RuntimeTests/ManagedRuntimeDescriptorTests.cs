using DotPython.Language;
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

    [Fact]
    public void Compatibility_ReportsSupportedArtifactLanguageVersions()
    {
        var compatibility = ManagedRuntimeDescriptor.Compatibility;

        Assert.Contains(
            compatibility.LanguageVersion,
            compatibility.SupportedArtifactLanguageVersions
        );
        Assert.Equal(
            PythonLanguageVersion.SupportedArtifactVersions,
            compatibility.SupportedArtifactLanguageVersions
        );
    }
}
