namespace DotPython.Runtime.Managed.Execution;

public sealed record ManagedExecutionOptions
{
    public const long DefaultInstructionLimit = 1_000_000;

    public long InstructionLimit { get; init; } = DefaultInstructionLimit;

    /// <summary>Source for the `input()` builtin and `sys.stdin`; null reads as end-of-file.</summary>
    public TextReader? StandardInput { get; init; }

    /// <summary>Target of `sys.stderr`; null discards error-stream writes.</summary>
    public TextWriter? StandardError { get; init; }

    /// <summary>The `sys.argv` list: the program name followed by its arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
