namespace DotPython.Lint;

/// <summary>A stable identifier and description for a built-in lint warning.</summary>
public sealed record PythonLintRule(string Code, string Name, string Description);
