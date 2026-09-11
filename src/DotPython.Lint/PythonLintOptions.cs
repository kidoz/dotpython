namespace DotPython.Lint;

/// <summary>Rule selection and host-provided global names. Ignore takes precedence over selection.</summary>
public sealed class PythonLintOptions
{
    /// <summary>Null enables all rules; an empty collection enables none.</summary>
    public IReadOnlyCollection<string>? Select { get; init; }

    public IReadOnlyCollection<string> Ignore { get; init; } = [];

    /// <summary>Additional global identifiers supplied by a host. This declares names, not values.</summary>
    public IReadOnlyCollection<string> KnownGlobals { get; init; } = [];
}
