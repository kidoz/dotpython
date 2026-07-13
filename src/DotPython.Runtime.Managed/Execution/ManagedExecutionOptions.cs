namespace DotPython.Runtime.Managed.Execution;

public sealed record ManagedExecutionOptions
{
    public const long DefaultInstructionLimit = 1_000_000;

    public long InstructionLimit { get; init; } = DefaultInstructionLimit;
}
