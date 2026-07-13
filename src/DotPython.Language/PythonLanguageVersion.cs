namespace DotPython.Language;

/// <summary>Defines the Python language version targeted by the managed front end.</summary>
public static class PythonLanguageVersion
{
    /// <summary>Gets the current Python language version.</summary>
    public static Version Current { get; } = new(3, 14);
}
