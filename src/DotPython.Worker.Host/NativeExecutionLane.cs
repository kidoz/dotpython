using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DotPython.Worker.Host;

[SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "The owning native lane must transfer every operation failure to its awaiting worker request."
)]
internal sealed class NativeExecutionLane : IAsyncDisposable
{
    private readonly BlockingCollection<IWorkItem> _queue = new(32);
    private readonly Thread _thread;
    private int _disposed;

    internal NativeExecutionLane()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DotPython Stable-ABI worker lane",
        };
        _thread.Start();
    }

    internal Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var work = new WorkItem<T>(operation, cancellationToken);
        _queue.Add(work, cancellationToken);
        return work.Completion;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work.Execute();
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<T>(Func<T> operation, CancellationToken cancellationToken)
        : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal Task<T> Completion => _completion.Task;

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
