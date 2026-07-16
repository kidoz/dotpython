using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "xUnit tests intentionally resume in the test context."
)]
public sealed class ManagedRuntimeSchedulerTests
{
    [Fact]
    public async Task LifecycleCallbacks_RunOnceOnTheSameOwningThread()
    {
        var initializeCount = 0;
        var initializeThreadId = 0;
        var finalizeCount = 0;
        var finalizeThreadId = 0;
        var scheduler = new ManagedRuntimeScheduler(
            initialize: () =>
            {
                initializeThreadId = Environment.CurrentManagedThreadId;
                initializeCount++;
            },
            finalize: () =>
            {
                finalizeThreadId = Environment.CurrentManagedThreadId;
                finalizeCount++;
            }
        );

        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync();

        Assert.Equal(1, initializeCount);
        Assert.Equal(1, finalizeCount);
        Assert.Equal(scheduler.OwnerThreadId, initializeThreadId);
        Assert.Equal(initializeThreadId, finalizeThreadId);
    }

    [Fact]
    public async Task InvokeAsync_SerializesWorkOnOneOwningThread()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        var activeCalls = 0;
        var maximumActiveCalls = 0;
        var threadIds = new ConcurrentBag<int>();

        var calls = Enumerable
            .Range(0, 32)
            .Select(_ =>
                scheduler
                    .InvokeAsync(
                        _ =>
                        {
                            var active = Interlocked.Increment(ref activeCalls);
                            InterlockedExtensions.Max(ref maximumActiveCalls, active);
                            threadIds.Add(Environment.CurrentManagedThreadId);
                            Thread.SpinWait(10_000);
                            Interlocked.Decrement(ref activeCalls);
                            return true;
                        },
                        TestContext.Current.CancellationToken
                    )
                    .AsTask()
            );

        await Task.WhenAll(calls);

        Assert.Equal(1, maximumActiveCalls);
        Assert.Equal([scheduler.OwnerThreadId], threadIds.Distinct());
        Assert.Equal(ManagedRuntimeSchedulerState.Running, scheduler.State);
    }

    [Fact]
    public async Task ResourceLease_ReleasesExactlyOnceOnTheOwningThread()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        var releaseCount = 0;
        var releaseThreadId = 0;
        var resource = await scheduler.InvokeAsync(
            _ =>
                scheduler.RegisterResource(() =>
                {
                    releaseThreadId = Environment.CurrentManagedThreadId;
                    releaseCount++;
                }),
            TestContext.Current.CancellationToken
        );

        await resource.DisposeAsync();
        await resource.DisposeAsync();

        Assert.Equal(1, releaseCount);
        Assert.Equal(scheduler.OwnerThreadId, releaseThreadId);
        Assert.Equal(
            0,
            await scheduler.InvokeAsync(
                _ => scheduler.GetOwnedResourceCount(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task AbandonedResource_FinalizerOnlyEnqueuesItsRelease()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        var releaseThreadId = 0;
        var resource = AbandonResource(
            scheduler,
            () => releaseThreadId = Environment.CurrentManagedThreadId,
            TestContext.Current.CancellationToken
        );

        await scheduler.InvokeAsync(_ => true, TestContext.Current.CancellationToken);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await scheduler.InvokeAsync(_ => true, TestContext.Current.CancellationToken);

        Assert.False(resource.IsAlive);
        Assert.Equal(scheduler.OwnerThreadId, releaseThreadId);
        Assert.Equal(
            0,
            await scheduler.InvokeAsync(
                _ => scheduler.GetOwnedResourceCount(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task DisposeAsync_DrainsAdmittedWorkAndResourcesBeforeFinalization()
    {
        var events = new List<string>();
        using var workEntered = new ManualResetEventSlim();
        using var continueWork = new ManualResetEventSlim();
        var scheduler = new ManagedRuntimeScheduler(finalize: () => events.Add("finalize"));
        var firstResource = await scheduler.InvokeAsync(
            _ => scheduler.RegisterResource(() => events.Add("resource-1")),
            TestContext.Current.CancellationToken
        );
        var secondResource = await scheduler.InvokeAsync(
            _ => scheduler.RegisterResource(() => events.Add("resource-2")),
            TestContext.Current.CancellationToken
        );
        var first = scheduler
            .InvokeAsync(
                _ =>
                {
                    events.Add("first");
                    workEntered.Set();
                    continueWork.Wait(TestContext.Current.CancellationToken);
                    return true;
                },
                TestContext.Current.CancellationToken
            )
            .AsTask();
        workEntered.Wait(TestContext.Current.CancellationToken);
        var second = scheduler
            .InvokeAsync(
                _ =>
                {
                    events.Add("second");
                    return true;
                },
                TestContext.Current.CancellationToken
            )
            .AsTask();

        var shutdown = scheduler.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await scheduler.InvokeAsync(_ => true, TestContext.Current.CancellationToken)
        );
        continueWork.Set();
        await Task.WhenAll(first, second);
        await shutdown;
        await firstResource.DisposeAsync();
        await secondResource.DisposeAsync();

        Assert.Equal(["first", "second", "resource-2", "resource-1", "finalize"], events);
        Assert.Equal(ManagedRuntimeSchedulerState.Stopped, scheduler.State);
    }

    [Fact]
    public async Task InvokeAsync_CancelsQueuedWorkBeforeItRuns()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        using var workEntered = new ManualResetEventSlim();
        using var continueWork = new ManualResetEventSlim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var first = scheduler
            .InvokeAsync(
                _ =>
                {
                    workEntered.Set();
                    continueWork.Wait(TestContext.Current.CancellationToken);
                    return true;
                },
                TestContext.Current.CancellationToken
            )
            .AsTask();
        workEntered.Wait(TestContext.Current.CancellationToken);
        var ranCanceledWork = false;
        var canceled = scheduler
            .InvokeAsync(
                _ =>
                {
                    ranCanceledWork = true;
                    return true;
                },
                cancellation.Token
            )
            .AsTask();

        await cancellation.CancelAsync();
        continueWork.Set();
        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);

        Assert.False(ranCanceledWork);
    }

    [Fact]
    public async Task InvokeAsync_RejectsReentrantSchedulingWithoutStoppingTheRuntime()
    {
        await using var scheduler = new ManagedRuntimeScheduler();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scheduler.InvokeAsync(
                token => scheduler.InvokeAsync(_ => true, token).AsTask().GetAwaiter().GetResult(),
                TestContext.Current.CancellationToken
            )
        );

        Assert.True(await scheduler.InvokeAsync(_ => true, TestContext.Current.CancellationToken));
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The test intentionally observes a faulted asynchronous disposal path."
    )]
    public async Task DisposeAsync_AttemptsFinalizationAfterAResourceReleaseFailure()
    {
        var events = new List<string>();
        var scheduler = new ManagedRuntimeScheduler(finalize: () => events.Add("finalize"));
        var resource = await scheduler.InvokeAsync(
            _ =>
                scheduler.RegisterResource(() =>
                {
                    events.Add("resource");
                    throw new InvalidOperationException("release failed");
                }),
            TestContext.Current.CancellationToken
        );

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
            await scheduler.DisposeAsync()
        );
        await resource.DisposeAsync();

        Assert.Contains("release failed", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(["resource", "finalize"], events);
        Assert.Equal(ManagedRuntimeSchedulerState.Faulted, scheduler.State);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonResource(
        ManagedRuntimeScheduler scheduler,
        Action release,
        CancellationToken cancellationToken
    )
    {
        var resource = scheduler
            .InvokeAsync(_ => scheduler.RegisterResource(release), cancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new WeakReference(resource);
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
