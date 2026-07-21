namespace DotPython.Protocol;

public static class WorkerProtocolFaultCodes
{
    public const string HandshakeFailed = "DPY8103";
    public const string WorkerTerminated = "DPY8104";
    public const string HardTimeout = "DPY8105";
    public const string StaleHandle = "DPY8106";
    public const string LimitExceeded = "DPY8107";
    public const string RequestCanceled = "DPY8109";
}

public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException()
        : this(CreateFault("A worker protocol failure occurred.")) { }

    public WorkerProtocolException(string message)
        : this(CreateFault(message)) { }

    public WorkerProtocolException(string message, Exception innerException)
        : this(CreateFault(message), innerException) { }

    public WorkerProtocolException(WorkerFault fault, Exception? innerException = null)
        : base(fault?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(fault);
        Fault = fault;
    }

    public WorkerFault Fault { get; }

    private static WorkerFault CreateFault(string message) =>
        new(WorkerProtocolFaultCodes.HandshakeFailed, WorkerFaultPhase.Framing, message, false);
}
