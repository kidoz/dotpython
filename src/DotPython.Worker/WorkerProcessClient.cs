using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using DotPython.Protocol;

namespace DotPython.Worker;

internal sealed class WorkerProcessClient : IAsyncDisposable
{
    private readonly WorkerProcessOptions _options;
    private readonly Process _process;
    private readonly WorkerFrameCodec _codec;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly SemaphoreSlim _admissionGate;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<WorkerEnvelope>> _pending =
        new();
    private readonly WorkerGenerationScope _generationScope;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _standardError;
    private readonly Task _standardErrorTask;
    private readonly object _stateGate = new();
    private Task? _readerTask;
    private WorkerProcessState _state = WorkerProcessState.Starting;
    private int _disposeStarted;
    private int _requestCount;

    private WorkerProcessClient(
        WorkerProcessOptions options,
        Process process,
        WorkerHandshakeResponse handshake,
        StringBuilder standardError,
        Task standardErrorTask
    )
    {
        _options = options;
        _process = process;
        Handshake = handshake;
        _codec = new WorkerFrameCodec(handshake.Limits.MaxMessageBytes);
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        _admissionGate = new SemaphoreSlim(handshake.Limits.MaxConcurrentRequests);
        _generationScope = new WorkerGenerationScope(handshake.Identity);
        _standardError = standardError;
        _standardErrorTask = standardErrorTask;
    }

    internal WorkerHandshakeResponse Handshake { get; }

    internal WorkerProcessState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    internal int RequestCount => Volatile.Read(ref _requestCount);

    internal string StandardError
    {
        get
        {
            lock (_standardError)
            {
                return _standardError.ToString();
            }
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned client assumes process ownership; failure paths dispose it."
    )]
    [SuppressMessage(
        "Reliability",
        "CA2025:Do not pass IDisposable instances into unawaited tasks",
        Justification = "The stderr drain task is owned and awaited by the returned client; failure paths terminate the stream before disposal."
    )]
    internal static async Task<WorkerProcessClient> StartAsync(
        WorkerProcessOptions options,
        long generation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        var workerId = Guid.NewGuid();
        var startInfo = CreateStartInfo(options, workerId, generation);
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The worker process could not be started.");
        }

        WorkerProcessClient? client = null;
        var standardError = new StringBuilder();
        var standardErrorTask = CaptureStandardErrorAsync(
            process,
            options.Policy.Limits.MaxOutputBytes,
            standardError,
            exception =>
            {
                if (client is null)
                {
                    KillProcess(process);
                }
                else
                {
                    client.Fault(exception);
                }
            }
        );

        var startupCodec = new WorkerFrameCodec(options.Policy.Limits.MaxMessageBytes);
        using var startupTimeout = new CancellationTokenSource(options.Policy.StartupTimeout);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            startupTimeout.Token
        );

        try
        {
            var correlationId = Guid.NewGuid();
            var request = new WorkerHandshakeRequest(
                "0.1.0",
                WorkerProtocolVersion.Current,
                WorkerProtocolVersion.Current,
                options.ProviderId,
                options.RuntimeId,
                options.Architecture,
                options.EnvironmentHash,
                options.RequiredFeatures,
                options.Policy.Limits
            );
            var envelope = WorkerProtocolSerializer.CreateEnvelope(
                WorkerMessageType.HandshakeRequest,
                correlationId,
                DateTimeOffset.UtcNow + options.Policy.StartupTimeout,
                request
            );
            await startupCodec
                .WriteAsync(process.StandardInput.BaseStream, envelope, startup.Token)
                .ConfigureAwait(false);
            var responseEnvelope = await startupCodec
                .ReadAsync(process.StandardOutput.BaseStream, startup.Token)
                .ConfigureAwait(false);
            if (responseEnvelope is null)
            {
                throw HandshakeFailure("The worker exited before sending a handshake response.");
            }

            if (responseEnvelope.CorrelationId != correlationId)
            {
                throw HandshakeFailure("The worker handshake correlation ID did not match.");
            }

            if (responseEnvelope.MessageType == WorkerMessageType.Fault)
            {
                throw new WorkerProtocolException(
                    WorkerProtocolSerializer.ReadPayload<WorkerFault>(responseEnvelope)
                );
            }

            if (responseEnvelope.MessageType != WorkerMessageType.HandshakeResponse)
            {
                throw HandshakeFailure("The worker sent an unexpected handshake message.");
            }

            var response = WorkerProtocolSerializer.ReadPayload<WorkerHandshakeResponse>(
                responseEnvelope
            );
            WorkerHandshakeNegotiator.Validate(request, response);
            if (
                response.Identity.WorkerId != workerId
                || response.Identity.Generation != generation
            )
            {
                throw HandshakeFailure("The launched worker did not echo its assigned identity.");
            }

            client = new WorkerProcessClient(
                options,
                process,
                response,
                standardError,
                standardErrorTask
            );
            client.SetState(WorkerProcessState.Running);
            client._readerTask = client.ReadLoopAsync();
            return client;
        }
        catch
        {
            KillProcess(process);
            process.Dispose();
            throw;
        }
    }

    internal async Task<WorkerSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();
        var response = await SendRequestAsync<WorkerOpenSessionRequest, WorkerOpenSessionResponse>(
                WorkerMessageType.OpenSessionRequest,
                WorkerMessageType.OpenSessionResponse,
                new WorkerOpenSessionRequest(sessionId),
                deadlineUtc: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (response.SessionId != sessionId)
        {
            throw ProtocolFailure("The worker opened a different session ID.");
        }

        return new WorkerSession(this, _generationScope, sessionId);
    }

    internal async Task<WorkerExecuteResponse> ExecuteAsync(
        Guid sessionId,
        string code,
        string fileName,
        long instructionLimit,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instructionLimit);
        EnsureRunning();

        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _requestCount);
            var correlationId = Guid.NewGuid();
            var completion = AddPending(correlationId);
            var deadline = DateTimeOffset.UtcNow + _options.Policy.ExecutionTimeout;
            try
            {
                await WriteAsync(
                        WorkerProtocolSerializer.CreateEnvelope(
                            WorkerMessageType.ExecuteRequest,
                            correlationId,
                            deadline,
                            new WorkerExecuteRequest(sessionId, code, fileName, instructionLimit)
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                _pending.TryRemove(correlationId, out _);
                throw;
            }

            using var deadlineCancellation = new CancellationTokenSource(
                _options.Policy.ExecutionTimeout
            );
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCancellation.Token
            );
            WorkerEnvelope envelope;
            try
            {
                envelope = await completion
                    .Task.WaitAsync(waitCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelAndAwaitGraceAsync(correlationId, completion.Task)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException)
            {
                await HardTimeoutAsync(correlationId).ConfigureAwait(false);
                throw new WorkerProtocolException(
                    new WorkerFault(
                        WorkerProtocolFaultCodes.HardTimeout,
                        WorkerFaultPhase.Cancellation,
                        "The worker exceeded its hard execution deadline and was terminated.",
                        false
                    )
                );
            }

            return ReadResponse<WorkerExecuteResponse>(envelope, WorkerMessageType.ExecuteResponse);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    internal async ValueTask CloseSessionAsync(Guid sessionId)
    {
        if (State != WorkerProcessState.Running)
        {
            return;
        }

        try
        {
            var response = await SendRequestAsync<
                WorkerCloseSessionRequest,
                WorkerCloseSessionResponse
            >(
                    WorkerMessageType.CloseSessionRequest,
                    WorkerMessageType.CloseSessionResponse,
                    new WorkerCloseSessionRequest(sessionId),
                    deadlineUtc: null,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (response.SessionId != sessionId)
            {
                throw ProtocolFailure("The worker closed a different session ID.");
            }
        }
        catch (WorkerProtocolException) when (State != WorkerProcessState.Running) { }
    }

    internal void ValidateHandle(WorkerObjectHandle handle, Guid sessionId) =>
        _generationScope.Validate(handle, sessionId);

    internal async Task InjectTestFaultAsync(
        WorkerTestFault fault,
        CancellationToken cancellationToken
    )
    {
        if (!_options.EnableTestFaultInjection)
        {
            throw new InvalidOperationException("Worker fault injection is disabled.");
        }

        if (fault == WorkerTestFault.Hang)
        {
            await InjectHangingTestFaultAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await SendRequestAsync<WorkerTestControlRequest, WorkerTestControlResponse>(
                WorkerMessageType.TestControlRequest,
                WorkerMessageType.TestControlResponse,
                new WorkerTestControlRequest(fault),
                DateTimeOffset.UtcNow + _options.Policy.ExecutionTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        if (State == WorkerProcessState.Running)
        {
            SetState(WorkerProcessState.Draining);
            using var shutdownGrace = new CancellationTokenSource(
                _options.Policy.TerminationGracePeriod
            );
            try
            {
                _ = await SendRequestAsync<WorkerShutdownRequest, WorkerShutdownResponse>(
                        WorkerMessageType.ShutdownRequest,
                        WorkerMessageType.ShutdownResponse,
                        new WorkerShutdownRequest("host-dispose"),
                        deadlineUtc: null,
                        shutdownGrace.Token,
                        allowDraining: true
                    )
                    .ConfigureAwait(false);
            }
            catch (WorkerProtocolException) { }
            catch (OperationCanceledException) { }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _input.Close();
        if (!_process.HasExited)
        {
            using var grace = new CancellationTokenSource(_options.Policy.TerminationGracePeriod);
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcess(_process);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        SetState(WorkerProcessState.Stopped);
        _generationScope.Invalidate();
        if (_readerTask is not null)
        {
            await IgnoreFailureAsync(_readerTask).ConfigureAwait(false);
        }

        await IgnoreFailureAsync(_standardErrorTask).ConfigureAwait(false);

        _writerGate.Dispose();
        _admissionGate.Dispose();
        _lifetime.Dispose();
        _process.Dispose();
    }

    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        WorkerMessageType requestType,
        WorkerMessageType responseType,
        TRequest request,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken,
        bool allowDraining = false
    )
        where TRequest : notnull
        where TResponse : notnull
    {
        if (!allowDraining)
        {
            EnsureRunning();
        }

        var correlationId = Guid.NewGuid();
        var completion = AddPending(correlationId);
        try
        {
            await WriteAsync(
                    WorkerProtocolSerializer.CreateEnvelope(
                        requestType,
                        correlationId,
                        deadlineUtc,
                        request,
                        Handshake.SelectedVersion
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(correlationId, out _);
            throw;
        }

        var envelope = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return ReadResponse<TResponse>(envelope, responseType);
    }

    private async Task InjectHangingTestFaultAsync(CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        var completion = AddPending(correlationId);
        await WriteAsync(
                WorkerProtocolSerializer.CreateEnvelope(
                    WorkerMessageType.TestControlRequest,
                    correlationId,
                    DateTimeOffset.UtcNow + _options.Policy.ExecutionTimeout,
                    new WorkerTestControlRequest(WorkerTestFault.Hang),
                    Handshake.SelectedVersion
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hardTimeout.CancelAfter(_options.Policy.ExecutionTimeout);
        try
        {
            _ = await completion.Task.WaitAsync(hardTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await HardTimeoutAsync(correlationId).ConfigureAwait(false);
            throw new WorkerProtocolException(
                new WorkerFault(
                    WorkerProtocolFaultCodes.HardTimeout,
                    WorkerFaultPhase.Cancellation,
                    "The injected worker hang exceeded its hard deadline and was terminated.",
                    false
                )
            );
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The process boundary converts every unexpected reader failure into a structured worker fault."
    )]
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var envelope = await _codec
                    .ReadAsync(_output, _lifetime.Token)
                    .ConfigureAwait(false);
                if (envelope is null)
                {
                    if (State is WorkerProcessState.Running or WorkerProcessState.Starting)
                    {
                        Fault(WorkerTerminated("The worker protocol stream ended unexpectedly."));
                    }

                    return;
                }

                if (envelope.Version != Handshake.SelectedVersion)
                {
                    Fault(
                        ProtocolFailure("A worker response used an unnegotiated protocol version.")
                    );
                    return;
                }

                if (!_pending.TryRemove(envelope.CorrelationId, out var completion))
                {
                    Fault(ProtocolFailure("The worker sent a duplicate or unknown response."));
                    return;
                }

                completion.TrySetResult(envelope);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Fault(
                exception as WorkerProtocolException
                    ?? WorkerTerminated($"The worker protocol reader failed: {exception.Message}")
            );
        }
    }

    private static async Task CaptureStandardErrorAsync(
        Process process,
        int maxOutputBytes,
        StringBuilder standardError,
        Action<WorkerProtocolException> onLimitExceeded
    )
    {
        var buffer = new char[1024];
        var byteCount = 0;
        try
        {
            while (true)
            {
                var read = await process
                    .StandardError.ReadAsync(buffer, CancellationToken.None)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                byteCount += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
                if (byteCount > maxOutputBytes)
                {
                    onLimitExceeded(
                        new WorkerProtocolException(
                            new WorkerFault(
                                WorkerProtocolFaultCodes.LimitExceeded,
                                WorkerFaultPhase.Framing,
                                "Worker standard error exceeded the configured output limit.",
                                false
                            )
                        )
                    );
                    return;
                }

                lock (standardError)
                {
                    standardError.Append(buffer, 0, read);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task CancelAndAwaitGraceAsync(Guid correlationId, Task<WorkerEnvelope> completion)
    {
        await SendCancellationAsync(correlationId).ConfigureAwait(false);
        using var grace = new CancellationTokenSource(_options.Policy.TerminationGracePeriod);
        try
        {
            _ = await completion.WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(WorkerProcessState.Draining);
            KillProcess(_process);
            _generationScope.Invalidate();
        }
    }

    private async Task HardTimeoutAsync(Guid correlationId)
    {
        SetState(WorkerProcessState.Draining);
        await SendCancellationAsync(correlationId).ConfigureAwait(false);
        await Task.Delay(_options.Policy.TerminationGracePeriod).ConfigureAwait(false);
        KillProcess(_process);
        _generationScope.Invalidate();
        Fault(
            new WorkerProtocolException(
                new WorkerFault(
                    WorkerProtocolFaultCodes.HardTimeout,
                    WorkerFaultPhase.Cancellation,
                    "The worker was terminated after a hard execution timeout.",
                    false
                )
            )
        );
    }

    private async Task SendCancellationAsync(Guid correlationId)
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            await WriteAsync(
                    WorkerProtocolSerializer.CreateEnvelope(
                        WorkerMessageType.CancelRequest,
                        Guid.NewGuid(),
                        deadlineUtc: null,
                        new WorkerCancelRequest(correlationId),
                        Handshake.SelectedVersion
                    ),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch (WorkerProtocolException) { }
        catch (IOException) { }
    }

    private async Task WriteAsync(WorkerEnvelope envelope, CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _codec.WriteAsync(_input, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private TaskCompletionSource<WorkerEnvelope> AddPending(Guid correlationId)
    {
        var completion = new TaskCompletionSource<WorkerEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (!_pending.TryAdd(correlationId, completion))
        {
            throw ProtocolFailure("A worker correlation ID was reused.");
        }

        return completion;
    }

    private static T ReadResponse<T>(WorkerEnvelope envelope, WorkerMessageType expectedMessageType)
        where T : notnull
    {
        if (envelope.MessageType == WorkerMessageType.Fault)
        {
            throw new WorkerProtocolException(
                WorkerProtocolSerializer.ReadPayload<WorkerFault>(envelope)
            );
        }

        if (envelope.MessageType != expectedMessageType)
        {
            throw ProtocolFailure(
                $"Expected {expectedMessageType}, but the worker sent {envelope.MessageType}."
            );
        }

        return WorkerProtocolSerializer.ReadPayload<T>(envelope);
    }

    private void EnsureRunning()
    {
        if (State != WorkerProcessState.Running)
        {
            throw WorkerTerminated($"The worker is not running; current state is {State}.");
        }
    }

    private void Fault(WorkerProtocolException exception)
    {
        lock (_stateGate)
        {
            if (_state is WorkerProcessState.Stopped or WorkerProcessState.Faulted)
            {
                return;
            }

            _state = WorkerProcessState.Faulted;
        }

        _generationScope.Invalidate();
        foreach (var pending in _pending.ToArray())
        {
            if (_pending.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }

        KillProcess(_process);
    }

    private void SetState(WorkerProcessState state)
    {
        lock (_stateGate)
        {
            _state = state;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        WorkerProcessOptions options,
        Guid workerId,
        long generation
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.FileName,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment.Clear();
        foreach (var variable in options.EnvironmentVariables)
        {
            startInfo.Environment.Add(variable.Key, variable.Value);
        }

        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        AddArgument(startInfo, "--provider-id", options.ProviderId);
        AddArgument(startInfo, "--provider-version", options.ProviderVersion);
        AddArgument(startInfo, "--runtime-id", options.RuntimeId);
        AddArgument(startInfo, "--runtime-version", options.RuntimeVersion);
        AddArgument(startInfo, "--architecture", options.Architecture);
        AddArgument(startInfo, "--environment-hash", options.EnvironmentHash);
        AddArgument(startInfo, "--worker-id", workerId.ToString("D"));
        AddArgument(
            startInfo,
            "--generation",
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        AddArgument(
            startInfo,
            "--max-message-bytes",
            options.Policy.Limits.MaxMessageBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        AddArgument(
            startInfo,
            "--max-output-bytes",
            options.Policy.Limits.MaxOutputBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        AddArgument(
            startInfo,
            "--max-concurrency",
            options.Policy.Limits.MaxConcurrentRequests.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        AddArgument(
            startInfo,
            "--max-sessions",
            options.Policy.Limits.MaxSessions.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        foreach (var packageRoot in options.PackageRoots)
        {
            AddArgument(startInfo, "--package-root", packageRoot);
        }

        if (options.EnableTestFaultInjection)
        {
            startInfo.ArgumentList.Add("--test-fault-injection");
        }

        return startInfo;
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static WorkerProtocolException HandshakeFailure(string message) =>
        new(
            new WorkerFault(
                WorkerProtocolFaultCodes.HandshakeFailed,
                WorkerFaultPhase.Handshake,
                message,
                false
            )
        );

    private static WorkerProtocolException ProtocolFailure(string message) =>
        new(
            new WorkerFault(
                WorkerProtocolFaultCodes.HandshakeFailed,
                WorkerFaultPhase.Framing,
                message,
                false
            )
        );

    private static WorkerProtocolException WorkerTerminated(string message) =>
        new(
            new WorkerFault(
                WorkerProtocolFaultCodes.WorkerTerminated,
                WorkerFaultPhase.Execution,
                message,
                false
            )
        );
}
