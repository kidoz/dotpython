namespace DotPython;

/// <summary>Reports one executable native-extension capability of a runtime provider.</summary>
public sealed record PythonNativeExtensionCapability
{
    /// <summary>Gets the stable execution-provider identifier.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Gets the provider implementation version.</summary>
    public required string ProviderVersion { get; init; }

    /// <summary>Gets the Python runtime or language-profile version used by the provider.</summary>
    public required string PythonRuntimeVersion { get; init; }

    /// <summary>Gets the version of the provider's supported symbol or context-function manifest.</summary>
    public required string CapabilityManifestVersion { get; init; }

    /// <summary>Gets the native binary contract implemented by the provider.</summary>
    public required PythonNativeExtensionAbi Abi { get; init; }

    /// <summary>Gets the evidence level behind the reported capability.</summary>
    public required PythonNativeExtensionSupportLevel SupportLevel { get; init; }

    /// <summary>Gets the process boundary at which extension code executes.</summary>
    public required PythonNativeExtensionExecutionBoundary ExecutionBoundary { get; init; }

    /// <summary>Gets the trust policy required to select the capability.</summary>
    public required PythonNativeExtensionTrustPolicy TrustPolicy { get; init; }

    /// <summary>Gets whether the capability was qualified for a free-threaded runtime build.</summary>
    public required bool IsFreeThreaded { get; init; }

    /// <summary>Gets whether the provider uses a debug runtime or native build.</summary>
    public required bool IsDebugBuild { get; init; }

    /// <summary>Gets the architecture qualified by the current provider instance.</summary>
    public required string Architecture { get; init; }

    /// <summary>Gets the qualified C library family, or <see langword="null"/> when not applicable.</summary>
    public required string? Libc { get; init; }

    /// <summary>Gets the exact wheel ABI tags accepted by this capability.</summary>
    public required IReadOnlyList<string> SupportedAbiTags { get; init; }

    /// <summary>Gets the exact wheel platform tags accepted by this capability.</summary>
    public required IReadOnlyList<string> SupportedPlatformTags { get; init; }

    /// <summary>Gets the package artifacts qualified for this capability.</summary>
    public required IReadOnlyList<PythonNativePackageQualification> QualifiedPackages { get; init; }
}
