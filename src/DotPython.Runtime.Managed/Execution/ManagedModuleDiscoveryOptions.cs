namespace DotPython.Runtime.Managed.Execution;

/// <summary>Configures deterministic managed-module discovery for an engine.</summary>
public sealed class ManagedModuleDiscoveryOptions
{
    /// <summary>
    /// Gets the ordered roots searched for UTF-8 Python sources, DotPython artifacts, native
    /// extension candidates, and installed-distribution metadata. Earlier roots take precedence.
    /// </summary>
    public IReadOnlyList<string> SearchPaths { get; init; } = Array.Empty<string>();
}
