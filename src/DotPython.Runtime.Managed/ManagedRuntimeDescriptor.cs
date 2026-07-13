using DotPython.Language;

namespace DotPython.Runtime.Managed;

/// <summary>Exposes stable identity and compatibility metadata for the managed runtime.</summary>
public static class ManagedRuntimeDescriptor
{
    /// <summary>Gets the managed runtime compatibility profile.</summary>
    public static PythonCompatibilityProfile Compatibility { get; } =
        new()
        {
            LanguageVersion = PythonLanguageVersion.Current,
            Implementation = "dotpython-managed",
            SupportsCpythonAbi = false,
        };
}
