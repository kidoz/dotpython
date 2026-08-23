namespace DotPython.Runtime.Managed.Execution;

public sealed record ManagedExecutionOptions
{
    public const long DefaultInstructionLimit = 1_000_000;

    public long InstructionLimit { get; init; } = DefaultInstructionLimit;

    /// <summary>Source for the `input()` builtin; null reads as end-of-file.</summary>
    public TextReader? StandardInput { get; init; }
}
