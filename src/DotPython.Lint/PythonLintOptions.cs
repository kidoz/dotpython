namespace DotPython.Lint;

/// <summary>Exact rule identifiers to select and ignore. Ignore takes precedence.</summary>
public sealed class PythonLintOptions
{
    /// <summary>Null enables all rules; an empty collection enables none.</summary>
    public IReadOnlyCollection<string>? Select { get; init; }

    public IReadOnlyCollection<string> Ignore { get; init; } = [];
}
