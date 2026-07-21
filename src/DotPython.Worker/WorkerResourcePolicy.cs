using DotPython.Protocol;

namespace DotPython.Worker;

public sealed record WorkerResourcePolicy
{
    public WorkerProtocolLimits Limits { get; init; } = WorkerProtocolLimits.Default;

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan TerminationGracePeriod { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaxRequestsPerWorker { get; init; } = 1_000;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Limits.MaxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Limits.MaxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Limits.MaxConcurrentRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Limits.MaxSessions);
        ValidateTimeout(StartupTimeout, nameof(StartupTimeout));
        ValidateTimeout(ExecutionTimeout, nameof(ExecutionTimeout));
        ValidateTimeout(TerminationGracePeriod, nameof(TerminationGracePeriod));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRequestsPerWorker);
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
