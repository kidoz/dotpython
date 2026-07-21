using System.Text.Json;

namespace DotPython.Protocol;

public enum WorkerMessageType
{
    None = 0,
    HandshakeRequest = 1,
    HandshakeResponse = 2,
    OpenSessionRequest = 3,
    OpenSessionResponse = 4,
    CloseSessionRequest = 5,
    CloseSessionResponse = 6,
    ExecuteRequest = 7,
    ExecuteResponse = 8,
    CancelRequest = 9,
    ShutdownRequest = 10,
    ShutdownResponse = 11,
    Fault = 12,
    LoadStableAbiModuleRequest = 13,
    LoadStableAbiModuleResponse = 14,
    InvokeStableAbiModuleRequest = 15,
    InvokeStableAbiModuleResponse = 16,
    ReleaseStableAbiModuleRequest = 17,
    ReleaseStableAbiModuleResponse = 18,
    TestControlRequest = 100,
    TestControlResponse = 101,
}

public sealed record WorkerEnvelope(
    WorkerProtocolVersion Version,
    WorkerMessageType MessageType,
    Guid CorrelationId,
    DateTimeOffset? DeadlineUtc,
    JsonElement Payload
);

public sealed record WorkerProtocolLimits(
    int MaxMessageBytes,
    int MaxOutputBytes,
    int MaxConcurrentRequests,
    int MaxSessions
)
{
    public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;
    public const int DefaultMaxOutputBytes = 1024 * 1024;
    public const int DefaultMaxConcurrentRequests = 1;
    public const int DefaultMaxSessions = 32;

    public static WorkerProtocolLimits Default { get; } =
        new(
            DefaultMaxMessageBytes,
            DefaultMaxOutputBytes,
            DefaultMaxConcurrentRequests,
            DefaultMaxSessions
        );
}

public sealed record WorkerIdentity(
    string ProviderId,
    string ProviderVersion,
    string RuntimeId,
    string RuntimeVersion,
    string Architecture,
    string EnvironmentHash,
    Guid WorkerId,
    long Generation,
    IReadOnlyList<string> Features
);

public sealed record WorkerHandshakeRequest(
    string HostVersion,
    WorkerProtocolVersion MinimumVersion,
    WorkerProtocolVersion MaximumVersion,
    string ExpectedProviderId,
    string ExpectedRuntimeId,
    string ExpectedArchitecture,
    string ExpectedEnvironmentHash,
    IReadOnlyList<string> RequiredFeatures,
    WorkerProtocolLimits Limits
);

public sealed record WorkerHandshakeResponse(
    WorkerProtocolVersion SelectedVersion,
    string WorkerVersion,
    WorkerIdentity Identity,
    WorkerProtocolLimits Limits,
    IReadOnlyList<string> Features
);

public sealed record WorkerOpenSessionRequest(Guid SessionId);

public sealed record WorkerOpenSessionResponse(Guid SessionId);

public sealed record WorkerCloseSessionRequest(Guid SessionId);

public sealed record WorkerCloseSessionResponse(Guid SessionId);

public sealed record WorkerExecuteRequest(
    Guid SessionId,
    string Code,
    string FileName,
    long InstructionLimit
);

public sealed record WorkerDiagnostic(string Code, string Message);

public sealed record WorkerExecuteResponse(
    Guid SessionId,
    bool Success,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<WorkerDiagnostic> Diagnostics
);

public sealed record WorkerCancelRequest(Guid RequestId);

public sealed record WorkerShutdownRequest(string Reason);

public sealed record WorkerShutdownResponse(string Reason);

public sealed record WorkerLoadStableAbiModuleRequest(Guid SessionId);

public sealed record WorkerLoadStableAbiModuleResponse(
    Guid SessionId,
    long ObjectId,
    string ModuleName,
    string ManifestVersion,
    string ArtifactSha256,
    bool MultiPhase,
    long ReadyValue
);

public sealed record WorkerInvokeStableAbiModuleRequest(
    Guid SessionId,
    long ObjectId,
    string Method,
    long? Argument
);

public sealed record WorkerInvokeStableAbiModuleResponse(
    Guid SessionId,
    long ObjectId,
    long Result
);

public sealed record WorkerReleaseStableAbiModuleRequest(Guid SessionId, long ObjectId);

public sealed record WorkerReleaseStableAbiModuleResponse(Guid SessionId, long ObjectId);

public enum WorkerFaultPhase
{
    Handshake,
    Framing,
    Admission,
    Execution,
    Cancellation,
    Shutdown,
}

public sealed record WorkerFault(
    string Code,
    WorkerFaultPhase Phase,
    string Message,
    bool WorkerUsable,
    IReadOnlyDictionary<string, string>? Details = null
);

public enum WorkerTestFault
{
    Crash,
    Hang,
    TruncatedMessage,
    DuplicateResponse,
}

public sealed record WorkerTestControlRequest(WorkerTestFault Fault);

public sealed record WorkerTestControlResponse(WorkerTestFault Fault);
