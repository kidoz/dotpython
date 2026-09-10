using DotPython.Language;
using DotPython.Runtime.Managed;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ManagedRuntimeDescriptorTests
{
    [Fact]
    public void Compatibility_IdentifiesManagedRuntimeWithoutNativeExecutionCapabilities()
    {
        var compatibility = ManagedRuntimeDescriptor.Compatibility;

        Assert.Equal("dotpython-managed", compatibility.Implementation);
        Assert.Empty(compatibility.NativeExtensionCapabilities);
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

    [Fact]
    public void NativeExtensionCapability_SeparatesAbiBoundaryAndQualification()
    {
        var qualification = new PythonNativePackageQualification
        {
            Name = "anyver",
            Version = "1.1.0",
            ArtifactFileName = "anyver-1.1.0-cp311-abi3-macosx_11_0_arm64.whl",
            Sha256 = "0f2fa90663b0203d3086c313d6384a6d74177e1f52508abf613cb17439edc4f9",
        };
        var capability = new PythonNativeExtensionCapability
        {
            ProviderId = "dotpython-managed-abi3",
            ProviderVersion = "0.1.0",
            PythonRuntimeVersion = "3.14",
            CapabilityManifestVersion = "1",
            Abi = PythonNativeExtensionAbi.CpythonStableAbi,
            SupportLevel = PythonNativeExtensionSupportLevel.Qualified,
            ExecutionBoundary = PythonNativeExtensionExecutionBoundary.WorkerProcess,
            TrustPolicy = PythonNativeExtensionTrustPolicy.WorkerIsolationRequired,
            IsFreeThreaded = false,
            IsDebugBuild = false,
            Architecture = "arm64",
            Libc = null,
            SupportedAbiTags = ["abi3"],
            SupportedPlatformTags = ["macosx_11_0_arm64"],
            QualifiedPackages = [qualification],
        };

        Assert.Equal(PythonNativeExtensionAbi.CpythonStableAbi, capability.Abi);
        Assert.Equal(
            PythonNativeExtensionExecutionBoundary.WorkerProcess,
            capability.ExecutionBoundary
        );
        Assert.Equal(PythonNativeExtensionSupportLevel.Qualified, capability.SupportLevel);
        Assert.Same(qualification, Assert.Single(capability.QualifiedPackages));
    }
}
