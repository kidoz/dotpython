using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Runtime.Managed.Native;

internal readonly record struct ManagedNativeHandle(long RuntimeId, long Generation, long HandleId)
{
    internal bool IsNull => HandleId == 0;
}

internal enum ManagedNativeReferenceOwnership
{
    Borrowed,
    New,
    Immortal,
}

internal enum ManagedNativeReferenceState
{
    Active,
    Released,
    Stolen,
    Invalidated,
}

internal sealed class ManagedNativeReference : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ManagedNativeHandleTable _owner;
    private ManagedRuntimeScheduler.SchedulerOwnedResource? _resource;
    private ManagedNativeReferenceState _state = ManagedNativeReferenceState.Active;

    internal ManagedNativeReference(
        ManagedNativeHandleTable owner,
        ManagedNativeHandle handle,
        ManagedNativeReferenceOwnership ownership,
        ManagedRuntimeScheduler.SchedulerOwnedResource? resource = null
    )
    {
        _owner = owner;
        Handle = handle;
        Ownership = ownership;
        _resource = resource;
    }

    internal ManagedNativeHandle Handle { get; }

    internal ManagedNativeReferenceOwnership Ownership { get; }

    internal ManagedNativeReferenceState State
    {
        get
        {
            lock (_gate)
            {
                return _state == ManagedNativeReferenceState.Active && !_owner.IsHandleLive(Handle)
                    ? ManagedNativeReferenceState.Invalidated
                    : _state;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        ManagedRuntimeScheduler.SchedulerOwnedResource? resource;
        lock (_gate)
        {
            if (_state != ManagedNativeReferenceState.Active)
            {
                return ValueTask.CompletedTask;
            }

            _state = ManagedNativeReferenceState.Released;
            resource = _resource;
            _resource = null;
        }

        return resource?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    internal PythonValue Resolve()
    {
        EnsureActive();
        return _owner.Resolve(Handle);
    }

    internal ManagedNativeReference TransferOwnership()
    {
        lock (_gate)
        {
            EnsureActiveUnderLock();
            if (Ownership != ManagedNativeReferenceOwnership.New || _resource is null)
            {
                throw Invariant("Only an active new reference can transfer ownership.");
            }

            var transferred = new ManagedNativeReference(
                _owner,
                Handle,
                ManagedNativeReferenceOwnership.New,
                _resource
            );
            _resource = null;
            _state = ManagedNativeReferenceState.Stolen;
            return transferred;
        }
    }

    private void EnsureActive()
    {
        lock (_gate)
        {
            EnsureActiveUnderLock();
        }
    }

    private void EnsureActiveUnderLock()
    {
        if (_state != ManagedNativeReferenceState.Active)
        {
            throw Invariant($"The native reference is in the '{_state}' state.");
        }

        if (!_owner.IsHandleLive(Handle))
        {
            _state = ManagedNativeReferenceState.Invalidated;
            throw Invariant("The native reference belongs to a stale runtime generation.");
        }
    }

    private static PythonRuntimeException Invariant(string message) =>
        new("DPY8006", message, default, "RuntimeError");
}

[SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "The lifetime lease is intentionally released by scheduler reverse-order shutdown."
)]
internal sealed class ManagedNativeHandleTable
{
    private static long _nextRuntimeId;
    private readonly ConcurrentDictionary<long, HandleEntry> _entries = [];
    private readonly ManagedRuntimeScheduler.SchedulerOwnedResource _lifetime;
    private readonly Action<ManagedNativeHandle>? _releaseObserver;
    private readonly ManagedRuntimeScheduler _scheduler;
    private long _generation = 1;
    private long _nextHandleId;

    internal ManagedNativeHandleTable(
        ManagedRuntimeScheduler scheduler,
        Action<ManagedNativeHandle>? releaseObserver = null
    )
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        if (!scheduler.IsOwnerThread)
        {
            throw Invariant(
                "The native handle table must be created on the owning runtime thread."
            );
        }

        _scheduler = scheduler;
        _releaseObserver = releaseObserver;
        RuntimeId = Interlocked.Increment(ref _nextRuntimeId);
        _lifetime = scheduler.RegisterResource(InvalidateCore);
    }

    internal long Generation => Volatile.Read(ref _generation);

    internal int LiveHandleCount => _entries.Count;

    internal int OwnedReferenceCount => _entries.Values.Sum(entry => entry.OwnedReferenceCount);

    internal long RuntimeId { get; }

    internal ManagedNativeReference CreateImmortal(PythonValue value)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(value);
        var handle = AddEntry(value, isImmortal: true);
        return new ManagedNativeReference(this, handle, ManagedNativeReferenceOwnership.Immortal);
    }

    internal ManagedNativeReference CreateNew(PythonValue value)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(value);
        return CreateOwnedReference(AddEntry(value, isImmortal: false));
    }

    internal ManagedNativeReference? CreateNullable(PythonValue? value) =>
        value is null ? null : CreateNew(value);

    internal ManagedNativeReference Borrow(ManagedNativeReference source)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(source);
        _ = source.Resolve();
        EnsureRuntime(source.Handle);
        return new ManagedNativeReference(
            this,
            source.Handle,
            ManagedNativeReferenceOwnership.Borrowed
        );
    }

    internal ManagedNativeReference NewReference(ManagedNativeReference source)
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(source);
        _ = source.Resolve();
        EnsureRuntime(source.Handle);
        return CreateOwnedReference(source.Handle);
    }

    internal PythonValue Resolve(ManagedNativeHandle handle)
    {
        EnsureOwnerThread();
        EnsureRuntime(handle);
        if (
            handle.Generation != Generation
            || !_entries.TryGetValue(handle.HandleId, out var entry)
        )
        {
            throw Invariant("The native handle is stale or has already been released.");
        }

        return entry.Value;
    }

    internal bool IsHandleLive(ManagedNativeHandle handle) =>
        handle.RuntimeId == RuntimeId
        && handle.Generation == Generation
        && _entries.ContainsKey(handle.HandleId);

    internal void InvalidateGeneration()
    {
        EnsureOwnerThread();
        InvalidateCore();
    }

    private ManagedNativeHandle AddEntry(PythonValue value, bool isImmortal)
    {
        if (_entries.Count >= ManagedRuntimeScheduler.MaximumOwnedResources)
        {
            throw Invariant(
                $"The managed native simulator cannot own more than {ManagedRuntimeScheduler.MaximumOwnedResources} handles."
            );
        }

        var handleId = checked(++_nextHandleId);
        if (!_entries.TryAdd(handleId, new HandleEntry(value, isImmortal)))
        {
            throw Invariant("The managed native simulator produced a duplicate handle.");
        }

        return new ManagedNativeHandle(RuntimeId, Generation, handleId);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned logical reference takes ownership of the scheduler resource lease."
    )]
    private ManagedNativeReference CreateOwnedReference(ManagedNativeHandle handle)
    {
        var entry = GetLiveEntry(handle);
        entry.OwnedReferenceCount++;
        try
        {
            var resource = _scheduler.RegisterResource(() => ReleaseOwnedReference(handle));
            return new ManagedNativeReference(
                this,
                handle,
                ManagedNativeReferenceOwnership.New,
                resource
            );
        }
        catch
        {
            entry.OwnedReferenceCount--;
            if (entry.OwnedReferenceCount == 0 && !entry.IsImmortal)
            {
                _entries.TryRemove(handle.HandleId, out _);
            }

            throw;
        }
    }

    private HandleEntry GetLiveEntry(ManagedNativeHandle handle)
    {
        EnsureRuntime(handle);
        if (
            handle.Generation != Generation
            || !_entries.TryGetValue(handle.HandleId, out var entry)
        )
        {
            throw Invariant("The native handle is stale or has already been released.");
        }

        return entry;
    }

    private void ReleaseOwnedReference(ManagedNativeHandle handle)
    {
        EnsureOwnerThread();
        if (
            handle.RuntimeId != RuntimeId
            || handle.Generation != Generation
            || !_entries.TryGetValue(handle.HandleId, out var entry)
        )
        {
            return;
        }

        if (entry.OwnedReferenceCount <= 0)
        {
            throw Invariant("The native handle reference count became unbalanced.");
        }

        entry.OwnedReferenceCount--;
        _releaseObserver?.Invoke(handle);
        if (entry.OwnedReferenceCount == 0 && !entry.IsImmortal)
        {
            _entries.TryRemove(handle.HandleId, out _);
        }
    }

    private void InvalidateCore()
    {
        EnsureOwnerThread();
        _entries.Clear();
        Interlocked.Increment(ref _generation);
        GC.KeepAlive(_lifetime);
    }

    private void EnsureRuntime(ManagedNativeHandle handle)
    {
        if (handle.IsNull)
        {
            throw Invariant("A null native handle cannot be dereferenced.");
        }

        if (handle.RuntimeId != RuntimeId)
        {
            throw Invariant("The native handle belongs to another runtime.");
        }
    }

    private void EnsureOwnerThread()
    {
        if (!_scheduler.IsOwnerThread)
        {
            throw Invariant("Native handles can only be accessed on the owning runtime thread.");
        }
    }

    private static PythonRuntimeException Invariant(string message) =>
        new("DPY8006", message, default, "RuntimeError");

    private sealed class HandleEntry(PythonValue value, bool isImmortal)
    {
        internal bool IsImmortal { get; } = isImmortal;

        internal int OwnedReferenceCount { get; set; }

        internal PythonValue Value { get; } = value;
    }
}

internal sealed class ManagedNativeErrorAdapter
{
    private readonly ManagedNativeHandleTable _handles;
    private ManagedNativeReference? _raisedException;

    internal ManagedNativeErrorAdapter(ManagedNativeHandleTable handles)
    {
        _handles = handles;
    }

    internal bool IsSet => _raisedException is not null;

    internal ManagedNativeReference? Occurred() =>
        _raisedException is null ? null : _handles.Borrow(_raisedException);

    internal void Clear()
    {
        var raised = _raisedException;
        _raisedException = null;
        ManagedNativeContext.DisposeInline(raised);
    }

    internal ManagedNativeReference? Fetch()
    {
        var raised = _raisedException;
        _raisedException = null;
        return raised;
    }

    internal ManagedNativeReference? Save() => Fetch();

    internal void Restore(ManagedNativeReference? exception)
    {
        Clear();
        _raisedException = exception?.TransferOwnership();
    }

    internal void SetException(string typeName, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(message);
        var exception = _handles.CreateNew(new PythonExceptionValue(typeName, message));
        Restore(exception);
    }
}

internal readonly record struct ManagedNativeCallbackResult<T>(
    T Value,
    ManagedNativeReference? RaisedException
);

internal sealed class ManagedNativeContext
{
    private readonly ManagedNativeHandleTable _handles;
    private readonly ManagedRuntimeScheduler _scheduler;

    internal ManagedNativeContext(
        ManagedRuntimeScheduler scheduler,
        ManagedNativeHandleTable handles,
        ManagedNativeErrorAdapter errors
    )
    {
        _scheduler = scheduler;
        _handles = handles;
        Errors = errors;
    }

    internal ManagedNativeErrorAdapter Errors { get; }

    internal int LiveHandleCount => _handles.LiveHandleCount;

    internal int OwnedReferenceCount => _handles.OwnedReferenceCount;

    internal ManagedNativeReference NewReference(PythonValue value) => _handles.CreateNew(value);

    internal ManagedNativeReference NewReference(ManagedNativeReference source) =>
        _handles.NewReference(source);

    internal ManagedNativeReference Borrow(ManagedNativeReference source) =>
        _handles.Borrow(source);

    internal ManagedNativeReference Immortal(PythonValue value) => _handles.CreateImmortal(value);

    internal ManagedNativeReference? Nullable(PythonValue? value) => _handles.CreateNullable(value);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Resolution is intentionally exposed through the owning native context boundary."
    )]
    internal PythonValue Resolve(ManagedNativeReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Resolve();
    }

    internal PythonValue Resolve(ManagedNativeHandle handle) => _handles.Resolve(handle);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Ownership transfer is intentionally exposed through the owning native context boundary."
    )]
    internal PythonValue ConsumeStolen(ManagedNativeReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var value = reference.Resolve();
        var transferred = reference.TransferOwnership();
        DisposeInline(transferred);
        return value;
    }

    internal void SetListItemSteal(
        ManagedNativeReference list,
        int index,
        ManagedNativeReference item
    )
    {
        var itemValue = item.Resolve();
        try
        {
            ManagedObjectProtocols.SetListItem(
                AssertType<PythonListValue>(list.Resolve(), "list"),
                index,
                itemValue
            );
        }
        finally
        {
            ConsumeStolen(item);
        }
    }

    internal void SetTupleItemSteal(
        ManagedNativeReference tuple,
        int index,
        ManagedNativeReference item
    )
    {
        var itemValue = item.Resolve();
        try
        {
            ManagedObjectProtocols.SetTupleItem(
                AssertType<PythonTupleValue>(tuple.Resolve(), "tuple"),
                index,
                itemValue
            );
        }
        finally
        {
            ConsumeStolen(item);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Borrowed item assignment is intentionally exposed through the owning native context boundary."
    )]
    internal void SetItemBorrowed(
        ManagedNativeReference target,
        ManagedNativeReference key,
        ManagedNativeReference value
    ) => ManagedObjectProtocols.SetItem(target.Resolve(), key.Resolve(), value.Resolve());

    internal ManagedNativeCallbackResult<T> InvokeCallback<T>(
        Func<ManagedNativeContext, CancellationToken, T> callback,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(callback);
        var savedError = Errors.Save();
        ManagedNativeReference? callbackError = null;
        try
        {
            var nested = _scheduler.InvokeAsync(token => callback(this, token), cancellationToken);
            if (!nested.IsCompleted)
            {
                throw Invariant("A native callback did not re-enter the runtime inline.");
            }

            var value = nested.GetAwaiter().GetResult();
            callbackError = Errors.Fetch();
            return new ManagedNativeCallbackResult<T>(value, callbackError);
        }
        finally
        {
            var unclaimedError = Errors.Fetch();
            DisposeInline(unclaimedError);
            Errors.Restore(savedError);
        }
    }

    internal void InvalidateGeneration() => _handles.InvalidateGeneration();

    internal static void DisposeInline(ManagedNativeReference? reference)
    {
        if (reference is null)
        {
            return;
        }

        var release = reference.DisposeAsync();
        if (!release.IsCompletedSuccessfully)
        {
            throw Invariant("A native reference release did not complete on the owning thread.");
        }

        release.GetAwaiter().GetResult();
    }

    private static T AssertType<T>(PythonValue value, string expectedType)
        where T : PythonValue =>
        value as T
        ?? throw new PythonRuntimeException(
            "DPY8006",
            $"The native operation requires a managed {expectedType} value.",
            default,
            "TypeError"
        );

    private static PythonRuntimeException Invariant(string message) =>
        new("DPY8006", message, default, "RuntimeError");
}

internal sealed class ManagedNativeRuntimeSimulator : IAsyncDisposable
{
    private readonly ManagedNativeContext _context;
    private readonly ManagedNativeHandleTable _handles;
    private readonly ManagedRuntimeScheduler _scheduler;

    internal ManagedNativeRuntimeSimulator(Action<ManagedNativeHandle>? releaseObserver = null)
    {
        _scheduler = new ManagedRuntimeScheduler();
        (_handles, _context) = _scheduler
            .InvokeAsync(_ =>
            {
                var handles = new ManagedNativeHandleTable(_scheduler, releaseObserver);
                var errors = new ManagedNativeErrorAdapter(handles);
                return (handles, new ManagedNativeContext(_scheduler, handles, errors));
            })
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    internal long Generation => _handles.Generation;

    internal long RuntimeId => _handles.RuntimeId;

    internal ValueTask<T> InvokeAsync<T>(
        Func<ManagedNativeContext, CancellationToken, T> callback,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _scheduler.InvokeAsync(token => callback(_context, token), cancellationToken);
    }

    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();
}
