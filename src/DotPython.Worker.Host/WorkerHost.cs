using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using DotPython.Protocol;
using DotPython.Runtime.Managed.Execution;
using DotPython.Runtime.Native;

namespace DotPython.Worker.Host;

internal sealed class WorkerHost(WorkerHostOptions options) : IAsyncDisposable
{
    private readonly WorkerFrameCodec _codec = new(options.Limits.MaxMessageBytes);
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly SemaphoreSlim _executionGate = new(options.Limits.MaxConcurrentRequests);
    private readonly ConcurrentDictionary<Guid, WorkerSessionState> _sessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRequests = new();
    private readonly ConcurrentBag<Task> _executionTasks = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Stream? _output;

    internal async Task RunAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token
        );

        var handshakeEnvelope = await _codec.ReadAsync(input, lifetime.Token).ConfigureAwait(false);
        if (
            handshakeEnvelope is null
            || handshakeEnvelope.MessageType != WorkerMessageType.HandshakeRequest
        )
        {
            throw new InvalidDataException("A handshake request must be the first worker message.");
        }

        var request = WorkerProtocolSerializer.ReadPayload<WorkerHandshakeRequest>(
            handshakeEnvelope
        );
        var selectedLimits = WorkerHandshakeNegotiator.SelectLimits(request.Limits, options.Limits);
        var response = new WorkerHandshakeResponse(
            options.ProtocolVersion,
            "0.1.0",
            options.Identity,
            selectedLimits,
            options.Identity.Features
        );
        if (options.ProtocolVersion == WorkerProtocolVersion.Current)
        {
            WorkerHandshakeNegotiator.Validate(request, response);
        }

        await SendAsync(
                WorkerProtocolSerializer.CreateEnvelope(
                    WorkerMessageType.HandshakeResponse,
                    handshakeEnvelope.CorrelationId,
                    deadlineUtc: null,
                    response,
                    options.ProtocolVersion
                ),
                lifetime.Token
            )
            .ConfigureAwait(false);

        while (!lifetime.IsCancellationRequested)
        {
            var envelope = await _codec.ReadAsync(input, lifetime.Token).ConfigureAwait(false);
            if (envelope is null)
            {
                break;
            }

            if (envelope.Version != options.ProtocolVersion)
            {
                await SendFaultAsync(
                        envelope.CorrelationId,
                        WorkerProtocolFaultCodes.HandshakeFailed,
                        WorkerFaultPhase.Framing,
                        "The request used an unnegotiated protocol version.",
                        workerUsable: false
                    )
                    .ConfigureAwait(false);
                break;
            }

            switch (envelope.MessageType)
            {
                case WorkerMessageType.OpenSessionRequest:
                    await OpenSessionAsync(envelope).ConfigureAwait(false);
                    break;
                case WorkerMessageType.CloseSessionRequest:
                    await CloseSessionAsync(envelope).ConfigureAwait(false);
                    break;
                case WorkerMessageType.ExecuteRequest:
                    var task = ExecuteAsync(envelope);
                    _executionTasks.Add(task);
                    break;
                case WorkerMessageType.CancelRequest:
                    await CancelAsync(envelope).ConfigureAwait(false);
                    break;
                case WorkerMessageType.TestControlRequest:
                    await InjectTestFaultAsync(envelope).ConfigureAwait(false);
                    break;
                case WorkerMessageType.ShutdownRequest:
                    await ShutdownAsync(envelope).ConfigureAwait(false);
                    return;
                default:
                    await SendFaultAsync(
                            envelope.CorrelationId,
                            WorkerProtocolFaultCodes.HandshakeFailed,
                            WorkerFaultPhase.Framing,
                            $"Message type {envelope.MessageType} is not valid for a request.",
                            workerUsable: false
                        )
                        .ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task OpenSessionAsync(WorkerEnvelope envelope)
    {
        var request = WorkerProtocolSerializer.ReadPayload<WorkerOpenSessionRequest>(envelope);
        if (_sessions.Count >= options.Limits.MaxSessions)
        {
            await SendLimitFaultAsync(envelope.CorrelationId, "Worker session limit exceeded.")
                .ConfigureAwait(false);
            return;
        }

        if (
            !_sessions.TryAdd(
                request.SessionId,
                new WorkerSessionState(options.PackageRoots, options.StableAbiModule)
            )
        )
        {
            await SendFaultAsync(
                    envelope.CorrelationId,
                    WorkerProtocolFaultCodes.HandshakeFailed,
                    WorkerFaultPhase.Admission,
                    "The worker session ID already exists.",
                    workerUsable: true
                )
                .ConfigureAwait(false);
            return;
        }

        await SendAsync(
                WorkerProtocolSerializer.CreateEnvelope(
                    WorkerMessageType.OpenSessionResponse,
                    envelope.CorrelationId,
                    deadlineUtc: null,
                    new WorkerOpenSessionResponse(request.SessionId),
                    options.ProtocolVersion
                ),
                CancellationToken.None
            )
            .ConfigureAwait(false);
    }

    private async Task CloseSessionAsync(WorkerEnvelope envelope)
    {
        var request = WorkerProtocolSerializer.ReadPayload<WorkerCloseSessionRequest>(envelope);
        if (!_sessions.TryRemove(request.SessionId, out var session))
        {
            await SendFaultAsync(
                    envelope.CorrelationId,
                    WorkerProtocolFaultCodes.StaleHandle,
                    WorkerFaultPhase.Admission,
                    "The worker session is not active.",
                    workerUsable: true
                )
                .ConfigureAwait(false);
            return;
        }

        await session.DisposeAsync().ConfigureAwait(false);

        await SendAsync(
                WorkerProtocolSerializer.CreateEnvelope(
                    WorkerMessageType.CloseSessionResponse,
                    envelope.CorrelationId,
                    deadlineUtc: null,
                    new WorkerCloseSessionResponse(request.SessionId),
                    options.ProtocolVersion
                ),
                CancellationToken.None
            )
            .ConfigureAwait(false);
    }

    private async Task ExecuteAsync(WorkerEnvelope envelope)
    {
        if (!await _executionGate.WaitAsync(0).ConfigureAwait(false))
        {
            await SendLimitFaultAsync(
                    envelope.CorrelationId,
                    "Worker concurrent request limit exceeded."
                )
                .ConfigureAwait(false);
            return;
        }

        CancellationTokenSource? execution = null;
        var executionCompleted = false;
        void CompleteExecution()
        {
            if (executionCompleted)
            {
                return;
            }

            executionCompleted = true;
            _activeRequests.TryRemove(envelope.CorrelationId, out _);
            execution?.Dispose();
            execution = null;
            _executionGate.Release();
        }

        try
        {
            var request = WorkerProtocolSerializer.ReadPayload<WorkerExecuteRequest>(envelope);
            if (!_sessions.TryGetValue(request.SessionId, out var session))
            {
                await SendFaultAsync(
                        envelope.CorrelationId,
                        WorkerProtocolFaultCodes.StaleHandle,
                        WorkerFaultPhase.Admission,
                        "The execution session is not active.",
                        workerUsable: true
                    )
                    .ConfigureAwait(false);
                return;
            }

            execution = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            if (envelope.DeadlineUtc is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                }
                else
                {
                    execution.CancelAfter(remaining);
                }
            }

            if (!_activeRequests.TryAdd(envelope.CorrelationId, execution))
            {
                throw new InvalidDataException("A worker request correlation ID was reused.");
            }

            using var output = new BoundedTextWriter(options.Limits.MaxOutputBytes);
            var result = await Task.Run(
                    () =>
                        session.Engine.Execute(
                            request.Code,
                            request.FileName,
                            output,
                            new ManagedExecutionOptions
                            {
                                InstructionLimit = request.InstructionLimit,
                            },
                            execution.Token
                        ),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            var response = new WorkerExecuteResponse(
                request.SessionId,
                result.Success,
                output.ToString(),
                string.Empty,
                result
                    .Diagnostics.Select(diagnostic => new WorkerDiagnostic(
                        diagnostic.Code,
                        diagnostic.Message
                    ))
                    .ToArray()
            );
            CompleteExecution();
            await SendAsync(
                    WorkerProtocolSerializer.CreateEnvelope(
                        WorkerMessageType.ExecuteResponse,
                        envelope.CorrelationId,
                        deadlineUtc: null,
                        response,
                        options.ProtocolVersion
                    ),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (WorkerOutputLimitException)
        {
            CompleteExecution();
            await SendLimitFaultAsync(
                    envelope.CorrelationId,
                    "Worker standard output exceeded the configured output limit."
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompleteExecution();
            await SendFaultAsync(
                    envelope.CorrelationId,
                    WorkerProtocolFaultCodes.RequestCanceled,
                    WorkerFaultPhase.Cancellation,
                    "Worker execution was cancelled.",
                    workerUsable: true
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not IOException)
        {
            CompleteExecution();
            await SendFaultAsync(
                    envelope.CorrelationId,
                    WorkerProtocolFaultCodes.WorkerTerminated,
                    WorkerFaultPhase.Execution,
                    $"Worker execution failed: {exception.Message}",
                    workerUsable: true
                )
                .ConfigureAwait(false);
        }
        finally
        {
            CompleteExecution();
        }
    }

    private async Task CancelAsync(WorkerEnvelope envelope)
    {
        var request = WorkerProtocolSerializer.ReadPayload<WorkerCancelRequest>(envelope);
        if (_activeRequests.TryGetValue(request.RequestId, out var cancellation))
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ShutdownAsync(WorkerEnvelope envelope)
    {
        var request = WorkerProtocolSerializer.ReadPayload<WorkerShutdownRequest>(envelope);
        foreach (var execution in _activeRequests.Values)
        {
            await execution.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(_executionTasks.ToArray()).ConfigureAwait(false);
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();
        await SendAsync(
                WorkerProtocolSerializer.CreateEnvelope(
                    WorkerMessageType.ShutdownResponse,
                    envelope.CorrelationId,
                    deadlineUtc: null,
                    new WorkerShutdownResponse(request.Reason),
                    options.ProtocolVersion
                ),
                CancellationToken.None
            )
            .ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
    }

    private async Task InjectTestFaultAsync(WorkerEnvelope envelope)
    {
        if (!options.TestFaultInjection)
        {
            await SendFaultAsync(
                    envelope.CorrelationId,
                    WorkerProtocolFaultCodes.HandshakeFailed,
                    WorkerFaultPhase.Admission,
                    "Worker fault injection is disabled.",
                    workerUsable: true
                )
                .ConfigureAwait(false);
            return;
        }

        var request = WorkerProtocolSerializer.ReadPayload<WorkerTestControlRequest>(envelope);
        var response = WorkerProtocolSerializer.CreateEnvelope(
            WorkerMessageType.TestControlResponse,
            envelope.CorrelationId,
            deadlineUtc: null,
            new WorkerTestControlResponse(request.Fault),
            options.ProtocolVersion
        );
        switch (request.Fault)
        {
            case WorkerTestFault.Crash:
                Process.GetCurrentProcess().Kill(entireProcessTree: true);
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                break;
            case WorkerTestFault.Hang:
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                break;
            case WorkerTestFault.TruncatedMessage:
                await WriteTruncatedFrameAsync().ConfigureAwait(false);
                Environment.Exit(86);
                break;
            case WorkerTestFault.DuplicateResponse:
                await SendAsync(response, CancellationToken.None).ConfigureAwait(false);
                await SendAsync(response, CancellationToken.None).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(envelope));
        }
    }

    private async Task WriteTruncatedFrameAsync()
    {
        var output =
            _output ?? throw new InvalidOperationException("Worker output is unavailable.");
        await _writerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, 128);
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(new byte[] { (byte)'{' }).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private Task SendLimitFaultAsync(Guid correlationId, string message) =>
        SendFaultAsync(
            correlationId,
            WorkerProtocolFaultCodes.LimitExceeded,
            WorkerFaultPhase.Admission,
            message,
            workerUsable: true
        );

    private Task SendFaultAsync(
        Guid correlationId,
        string code,
        WorkerFaultPhase phase,
        string message,
        bool workerUsable
    ) =>
        SendAsync(
            WorkerProtocolSerializer.CreateEnvelope(
                WorkerMessageType.Fault,
                correlationId,
                deadlineUtc: null,
                new WorkerFault(code, phase, message, workerUsable),
                options.ProtocolVersion
            ),
            CancellationToken.None
        );

    private async Task SendAsync(WorkerEnvelope envelope, CancellationToken cancellationToken)
    {
        var output =
            _output ?? throw new InvalidOperationException("Worker output is unavailable.");
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _codec.WriteAsync(output, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var execution in _activeRequests.Values)
        {
            await execution.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(_executionTasks.ToArray()).ConfigureAwait(false);
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();

        _executionGate.Dispose();
        _writerGate.Dispose();
        _shutdown.Dispose();
    }
}
