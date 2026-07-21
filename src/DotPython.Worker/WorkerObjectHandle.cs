using DotPython.Protocol;

namespace DotPython.Worker;

public readonly record struct WorkerObjectHandle(
    string ProviderId,
    Guid WorkerId,
    long Generation,
    Guid SessionId,
    long ObjectId
);

internal sealed class WorkerGenerationScope(WorkerIdentity identity)
{
    private int _active = 1;

    internal WorkerIdentity Identity { get; } = identity;

    internal void Invalidate() => Interlocked.Exchange(ref _active, 0);

    internal void Validate(WorkerObjectHandle handle, Guid sessionId)
    {
        if (
            Volatile.Read(ref _active) == 0
            || !string.Equals(handle.ProviderId, Identity.ProviderId, StringComparison.Ordinal)
            || handle.WorkerId != Identity.WorkerId
            || handle.Generation != Identity.Generation
            || handle.SessionId != sessionId
            || handle.ObjectId <= 0
        )
        {
            throw new WorkerProtocolException(
                new WorkerFault(
                    WorkerProtocolFaultCodes.StaleHandle,
                    WorkerFaultPhase.Admission,
                    "The object handle does not belong to the active worker generation and session.",
                    true
                )
            );
        }
    }
}
