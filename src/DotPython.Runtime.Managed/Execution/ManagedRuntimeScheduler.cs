using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace DotPython.Runtime.Managed.Execution;

internal enum ManagedRuntimeSchedulerState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted,
}

[SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "Admission waiters may still be unwinding after scheduler shutdown; disposing the semaphore would race those waiters."
)]
internal sealed class ManagedRuntimeScheduler : IAsyncDisposable
{
    internal const int MaximumPendingWork = 1024;
    internal const int MaximumOwnedResources = 4096;
    internal const int MaximumReentrantDepth = 64;

    [ThreadStatic]
    private static ManagedRuntimeScheduler? _activeScheduler;

    private readonly SemaphoreSlim _admission = new(MaximumPendingWork, MaximumPendingWork);
    private readonly List<Exception> _cleanupFailures = [];
    private readonly Action? _finalize;
    private readonly object _gate = new();
    private readonly Action? _initialize;
    private readonly SortedDictionary<long, Action> _resources = [];
    private readonly TaskCompletionSource<object?> _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource<object?> _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Thread _thread;
    private readonly Channel<ISchedulerWorkItem> _workItems =
        Channel.CreateUnbounded<ISchedulerWorkItem>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false,
            }
        );
    private long _nextResourceId;
    private int _ownerThreadId;
    private CancellationToken _activeCancellationToken;
    private int _executionDepth;
    private int _reentrantDepth;
    private ManagedRuntimeSchedulerState _state = ManagedRuntimeSchedulerState.Created;

    internal ManagedRuntimeScheduler(Action? initialize = null, Action? finalize = null)
    {
        _initialize = initialize;
        _finalize = finalize;
        _thread = new Thread(Run) { IsBackground = true, Name = "DotPython managed runtime" };

        lock (_gate)
        {
            _state = ManagedRuntimeSchedulerState.Starting;
            _thread.Start();
        }

        _started.Task.GetAwaiter().GetResult();
    }

    internal int OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    internal ManagedRuntimeSchedulerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal bool IsOwnerThread => ReferenceEquals(Thread.CurrentThread, _thread);

    internal ValueTask<T> InvokeAsync<T>(
        Func<CancellationToken, T> callback,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (IsOwnerThread)
        {
            return InvokeReentrant(callback, cancellationToken);
        }

        if (_activeScheduler is not null)
        {
            return ValueTask.FromException<T>(
                new InvalidOperationException(
                    "A managed-runtime owning thread cannot synchronously enter another runtime."
                )
            );
        }

        return EnqueueAsync(callback, cancellationToken);
    }

    internal SchedulerOwnedResource RegisterResource(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        EnsureOwnerThread();
        if (_resources.Count >= MaximumOwnedResources)
        {
            throw new InvalidOperationException(
                $"The managed runtime cannot own more than {MaximumOwnedResources} resources."
            );
        }

        var resourceId = checked(++_nextResourceId);
        _resources.Add(resourceId, release);
        return new SchedulerOwnedResource(this, resourceId);
    }

    internal int GetOwnedResourceCount()
    {
        EnsureOwnerThread();
        return _resources.Count;
    }

    public ValueTask DisposeAsync()
    {
        if (IsOwnerThread)
        {
            return ValueTask.FromException(
                new InvalidOperationException(
                    "The managed runtime cannot synchronously stop itself from its owning thread."
                )
            );
        }

        lock (_gate)
        {
            if (_state == ManagedRuntimeSchedulerState.Running)
            {
                _state = ManagedRuntimeSchedulerState.Stopping;
                if (!_workItems.Writer.TryWrite(StopWorkItem.Instance))
                {
                    throw new InvalidOperationException(
                        "The managed-runtime shutdown request could not be queued."
                    );
                }
            }

            return new ValueTask(_stopped.Task);
        }
    }

    private async ValueTask<T> EnqueueAsync<T>(
        Func<CancellationToken, T> callback,
        CancellationToken cancellationToken
    )
    {
        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkItem<T>? item = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            item = new WorkItem<T>(callback, _admission, cancellationToken);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(
                    _state != ManagedRuntimeSchedulerState.Running,
                    this
                );
                if (!_workItems.Writer.TryWrite(item))
                {
                    throw new InvalidOperationException(
                        "The managed-runtime work item could not be queued."
                    );
                }
            }
        }
        catch
        {
            if (item is null)
            {
                _admission.Release();
            }
            else
            {
                item.ReleaseAdmission();
            }

            throw;
        }

        return await item.Completion.ConfigureAwait(false);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Inline callback failures must retain the asynchronous invocation contract without escaping across a native callback boundary."
    )]
    private ValueTask<T> InvokeReentrant<T>(
        Func<CancellationToken, T> callback,
        CancellationToken cancellationToken
    )
    {
        if (_executionDepth == 0)
        {
            return ValueTask.FromException<T>(
                new InvalidOperationException(
                    "Managed-runtime execution can only re-enter from an active runtime call."
                )
            );
        }

        if (_reentrantDepth >= MaximumReentrantDepth)
        {
            return ValueTask.FromException<T>(
                new InvalidOperationException(
                    $"Managed-runtime callback reentrancy exceeded the {MaximumReentrantDepth}-call limit."
                )
            );
        }

        CancellationTokenSource? linkedCancellation = null;
        var effectiveCancellationToken = GetReentrantCancellationToken(
            cancellationToken,
            out linkedCancellation
        );
        var previousCancellationToken = _activeCancellationToken;
        _activeCancellationToken = effectiveCancellationToken;
        _reentrantDepth++;
        try
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<T>(callback(effectiveCancellationToken));
        }
        catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<T>(effectiveCancellationToken);
        }
        catch (Exception exception)
        {
            return ValueTask.FromException<T>(exception);
        }
        finally
        {
            _reentrantDepth--;
            _activeCancellationToken = previousCancellationToken;
            linkedCancellation?.Dispose();
        }
    }

    private CancellationToken GetReentrantCancellationToken(
        CancellationToken cancellationToken,
        out CancellationTokenSource? linkedCancellation
    )
    {
        linkedCancellation = null;
        if (!cancellationToken.CanBeCanceled)
        {
            return _activeCancellationToken;
        }

        if (
            !_activeCancellationToken.CanBeCanceled
            || cancellationToken == _activeCancellationToken
        )
        {
            return cancellationToken;
        }

        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _activeCancellationToken,
            cancellationToken
        );
        return linkedCancellation.Token;
    }

    private T ExecuteTopLevel<T>(
        Func<CancellationToken, T> callback,
        CancellationToken cancellationToken
    )
    {
        EnsureOwnerThread();
        if (_executionDepth != 0)
        {
            throw new InvalidOperationException(
                "The managed runtime attempted to start overlapping top-level work."
            );
        }

        _executionDepth = 1;
        _activeCancellationToken = cancellationToken;
        try
        {
            return callback(cancellationToken);
        }
        finally
        {
            _activeCancellationToken = default;
            _executionDepth = 0;
            _reentrantDepth = 0;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Owner-thread cleanup failures must be returned through the asynchronous resource-release contract."
    )]
    private ValueTask ReleaseResourceAsync(long resourceId)
    {
        if (IsOwnerThread)
        {
            try
            {
                ReleaseResource(resourceId);
                return ValueTask.CompletedTask;
            }
            catch (Exception exception)
            {
                return ValueTask.FromException(exception);
            }
        }

        lock (_gate)
        {
            if (_state == ManagedRuntimeSchedulerState.Running)
            {
                var item = new ReleaseResourceWorkItem(resourceId);
                if (!_workItems.Writer.TryWrite(item))
                {
                    return ValueTask.FromException(
                        new InvalidOperationException(
                            "The managed-runtime resource release could not be queued."
                        )
                    );
                }

                return new ValueTask(item.Completion);
            }

            return _state == ManagedRuntimeSchedulerState.Stopping
                ? new ValueTask(_stopped.Task)
                : ValueTask.CompletedTask;
        }
    }

    private void QueueFinalizerRelease(long resourceId)
    {
        lock (_gate)
        {
            if (_state == ManagedRuntimeSchedulerState.Running)
            {
                _workItems.Writer.TryWrite(
                    new ReleaseResourceWorkItem(resourceId, trackCompletion: false)
                );
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The scheduler must complete pending callers and preserve orderly shutdown after arbitrary runtime failures."
    )]
    private void Run()
    {
        Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
        _activeScheduler = this;
        try
        {
            _initialize?.Invoke();
            lock (_gate)
            {
                _state = ManagedRuntimeSchedulerState.Running;
            }

            _started.TrySetResult(null);
            while (ReadWorkItem() is { } item)
            {
                if (!item.Execute(this))
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                "The managed-runtime work queue ended unexpectedly."
            );
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _state = ManagedRuntimeSchedulerState.Faulted;
            }

            _started.TrySetException(exception);
            _stopped.TrySetException(exception);
            FailPendingWork(exception);
        }
        finally
        {
            _activeScheduler = null;
            _workItems.Writer.TryComplete();
        }
    }

    private ISchedulerWorkItem? ReadWorkItem()
    {
        while (_workItems.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            if (_workItems.Reader.TryRead(out var item))
            {
                return item;
            }
        }

        return null;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All resource releases and runtime finalization must be attempted during shutdown."
    )]
    private void Stop()
    {
        foreach (var resourceId in _resources.Keys.Reverse().ToArray())
        {
            try
            {
                ReleaseResource(resourceId);
            }
            catch (Exception)
            {
                // ReleaseResource records the failure while allowing the remaining cleanup to run.
            }
        }

        try
        {
            _finalize?.Invoke();
        }
        catch (Exception exception)
        {
            _cleanupFailures.Add(exception);
        }

        lock (_gate)
        {
            _state =
                _cleanupFailures.Count == 0
                    ? ManagedRuntimeSchedulerState.Stopped
                    : ManagedRuntimeSchedulerState.Faulted;
        }

        if (_cleanupFailures.Count == 0)
        {
            _stopped.TrySetResult(null);
        }
        else
        {
            _stopped.TrySetException(
                new AggregateException("Managed-runtime cleanup failed.", _cleanupFailures)
            );
        }
    }

    private void ReleaseResource(long resourceId)
    {
        EnsureOwnerThread();
        if (!_resources.Remove(resourceId, out var release))
        {
            return;
        }

        try
        {
            release();
        }
        catch (Exception exception)
        {
            _cleanupFailures.Add(exception);
            throw;
        }
    }

    private void EnsureOwnerThread()
    {
        if (!IsOwnerThread)
        {
            throw new InvalidOperationException(
                "Managed-runtime state can only be accessed from its owning thread."
            );
        }
    }

    private void FailPendingWork(Exception exception)
    {
        while (_workItems.Reader.TryRead(out var item))
        {
            item.Fail(exception);
        }
    }

    private interface ISchedulerWorkItem
    {
        bool Execute(ManagedRuntimeScheduler scheduler);

        void Fail(Exception exception);
    }

    private sealed class WorkItem<T>(
        Func<CancellationToken, T> callback,
        SemaphoreSlim admission,
        CancellationToken cancellationToken
    ) : ISchedulerWorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _admissionReleased;

        internal Task<T> Completion => _completion.Task;

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Arbitrary runtime failures must be returned to the originating caller without stopping the scheduler."
        )]
        public bool Execute(ManagedRuntimeScheduler scheduler)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _completion.TrySetResult(scheduler.ExecuteTopLevel(callback, cancellationToken));
            }
            catch (OperationCanceledException exception)
                when (exception.CancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                ReleaseAdmission();
            }

            return true;
        }

        public void Fail(Exception exception)
        {
            _completion.TrySetException(exception);
            ReleaseAdmission();
        }

        internal void ReleaseAdmission()
        {
            if (Interlocked.Exchange(ref _admissionReleased, 1) == 0)
            {
                admission.Release();
            }
        }
    }

    private sealed class ReleaseResourceWorkItem(long resourceId, bool trackCompletion = true)
        : ISchedulerWorkItem
    {
        private readonly TaskCompletionSource<object?>? _completion = trackCompletion
            ? new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        internal Task Completion =>
            _completion?.Task
            ?? throw new InvalidOperationException(
                "This resource release does not expose a completion task."
            );

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Resource-release failures must complete the owning lease without terminating the scheduler thread."
        )]
        public bool Execute(ManagedRuntimeScheduler scheduler)
        {
            try
            {
                scheduler.ReleaseResource(resourceId);
                _completion?.TrySetResult(null);
            }
            catch (Exception exception)
            {
                _completion?.TrySetException(exception);
            }

            return true;
        }

        public void Fail(Exception exception) => _completion?.TrySetException(exception);
    }

    private sealed class StopWorkItem : ISchedulerWorkItem
    {
        internal static StopWorkItem Instance { get; } = new();

        public bool Execute(ManagedRuntimeScheduler scheduler)
        {
            scheduler.Stop();
            return false;
        }

        public void Fail(Exception exception) { }
    }

    internal sealed class SchedulerOwnedResource : IAsyncDisposable
    {
        private readonly long _resourceId;
        private ManagedRuntimeScheduler? _owner;

        internal SchedulerOwnedResource(ManagedRuntimeScheduler owner, long resourceId)
        {
            _owner = owner;
            _resourceId = resourceId;
        }

        ~SchedulerOwnedResource()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.QueueFinalizerRelease(_resourceId);
        }

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return ValueTask.CompletedTask;
            }

            GC.SuppressFinalize(this);
            return owner.ReleaseResourceAsync(_resourceId);
        }
    }
}
