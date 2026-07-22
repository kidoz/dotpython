namespace DotPython.Hosting;

/// <summary>Controls activation behavior for one typed DotPython module registration.</summary>
public sealed class DotPythonModuleHostingOptions
{
    /// <summary>
    /// Gets or sets whether a Generic Host should load and validate the module during startup.
    /// </summary>
    public bool WarmUpOnHostStart { get; set; }
}
