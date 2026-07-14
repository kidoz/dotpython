using System.Collections.ObjectModel;

namespace DotPython;

/// <summary>Describes one positional call to a contract-defined Python function.</summary>
public sealed class PythonFunctionInvocation
{
    /// <summary>Initializes a function invocation.</summary>
    public PythonFunctionInvocation(string functionName, IEnumerable<object?>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        FunctionName = functionName;
        Arguments = new ReadOnlyCollection<object?>((arguments ?? []).ToList());
    }

    /// <summary>Gets the Python export name.</summary>
    public string FunctionName { get; }

    /// <summary>Gets positional arguments in declaration order.</summary>
    public IReadOnlyList<object?> Arguments { get; }
}
