using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotPython.Protocol;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = false,
    UseStringEnumConverter = true
)]
[JsonSerializable(typeof(WorkerEnvelope))]
[JsonSerializable(typeof(WorkerProtocolVersion))]
[JsonSerializable(typeof(WorkerProtocolLimits))]
[JsonSerializable(typeof(WorkerIdentity))]
[JsonSerializable(typeof(WorkerHandshakeRequest))]
[JsonSerializable(typeof(WorkerHandshakeResponse))]
[JsonSerializable(typeof(WorkerOpenSessionRequest))]
[JsonSerializable(typeof(WorkerOpenSessionResponse))]
[JsonSerializable(typeof(WorkerCloseSessionRequest))]
[JsonSerializable(typeof(WorkerCloseSessionResponse))]
[JsonSerializable(typeof(WorkerExecuteRequest))]
[JsonSerializable(typeof(WorkerExecuteResponse))]
[JsonSerializable(typeof(WorkerDiagnostic))]
[JsonSerializable(typeof(WorkerCancelRequest))]
[JsonSerializable(typeof(WorkerShutdownRequest))]
[JsonSerializable(typeof(WorkerShutdownResponse))]
[JsonSerializable(typeof(WorkerLoadStableAbiModuleRequest))]
[JsonSerializable(typeof(WorkerLoadStableAbiModuleResponse))]
[JsonSerializable(typeof(WorkerInvokeStableAbiModuleRequest))]
[JsonSerializable(typeof(WorkerInvokeStableAbiModuleResponse))]
[JsonSerializable(typeof(WorkerReleaseStableAbiModuleRequest))]
[JsonSerializable(typeof(WorkerReleaseStableAbiModuleResponse))]
[JsonSerializable(typeof(WorkerCompareAnyverRequest))]
[JsonSerializable(typeof(WorkerCompareAnyverResponse))]
[JsonSerializable(typeof(WorkerSortAnyverRequest))]
[JsonSerializable(typeof(WorkerSortAnyverResponse))]
[JsonSerializable(typeof(WorkerDescribeAnyverVersionRequest))]
[JsonSerializable(typeof(WorkerDescribeAnyverVersionResponse))]
[JsonSerializable(typeof(WorkerAnyverVersionInfo))]
[JsonSerializable(typeof(WorkerFault))]
[JsonSerializable(typeof(WorkerTestControlRequest))]
[JsonSerializable(typeof(WorkerTestControlResponse))]
internal sealed partial class WorkerProtocolJsonContext : JsonSerializerContext;
