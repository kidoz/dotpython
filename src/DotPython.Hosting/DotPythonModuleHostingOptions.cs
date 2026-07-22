namespace DotPython.Hosting;

/// <summary>Controls activation behavior for one typed DotPython module registration.</summary>
public sealed class DotPythonModuleHostingOptions
{
    internal const int MaximumSupportedInitializationAttempts = 10;

    /// <summary>
    /// Gets or sets whether a Generic Host should load and validate the module during startup.
    /// </summary>
    public bool WarmUpOnHostStart { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of module initialization attempts, including the first
    /// attempt. The default is one and the maximum supported value is ten.
    /// </summary>
    public int MaximumInitializationAttempts { get; set; } = 1;

    internal void Validate()
    {
        if (MaximumInitializationAttempts is < 1 or > MaximumSupportedInitializationAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumInitializationAttempts),
                MaximumInitializationAttempts,
                $"The maximum initialization attempt count must be between 1 and {MaximumSupportedInitializationAttempts}."
            );
        }
    }
}
