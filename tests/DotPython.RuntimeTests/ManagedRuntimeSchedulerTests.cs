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
    public async Task InvokeAsync_ReentersInlineOnTheOwningThread()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        var events = new List<string>();
        var nestedThreadId = 0;

        var result = await scheduler.InvokeAsync(
            token =>
            {
                events.Add("outer-before");
                var nested = scheduler.InvokeAsync(
                    _ =>
                    {
                        nestedThreadId = Environment.CurrentManagedThreadId;
                        events.Add("nested");
                        return 41;
                    },
                    token
                );
                Assert.True(nested.IsCompletedSuccessfully);
                events.Add("outer-after");
                return nested.GetAwaiter().GetResult() + 1;
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(42, result);
        Assert.Equal(["outer-before", "nested", "outer-after"], events);
        Assert.Equal(scheduler.OwnerThreadId, nestedThreadId);
        Assert.True(await scheduler.InvokeAsync(_ => true, TestContext.Current.CancellationToken));
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2016:Forward the CancellationToken parameter to methods",
        Justification = "The test intentionally omits the nested token to verify active-call cancellation inheritance."
    )]
    public async Task InvokeAsync_ReentrantCallInheritsActiveCancellation()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var nestedRan = false;

        var call = scheduler
            .InvokeAsync(
                _ =>
                {
                    cancellation.Cancel();
                    var nested = scheduler.InvokeAsync(_ => nestedRan = true);
                    Assert.True(nested.IsCompleted);
                    return nested.GetAwaiter().GetResult();
                },
                cancellation.Token
            )
            .AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        Assert.False(nestedRan);
    }

    [Fact]
    public async Task InvokeAsync_ReentrantCallLinksExplicitAndActiveCancellation()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        using var outerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        using var nestedCancellation = new CancellationTokenSource();
        var nestedRan = false;

        var call = scheduler
            .InvokeAsync(
                _ =>
                {
                    outerCancellation.Cancel();
                    var nested = scheduler.InvokeAsync(
                        _ => nestedRan = true,
                        nestedCancellation.Token
                    );
                    Assert.True(nested.IsCompleted);
                    return nested.GetAwaiter().GetResult();
                },
                outerCancellation.Token
            )
            .AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        Assert.False(nestedRan);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesExplicitNestedCancellationAsCanceledWork()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        using var nestedCancellation = new CancellationTokenSource();
        await nestedCancellation.CancelAsync();

        var call = scheduler
            .InvokeAsync(
                _ =>
                {
                    var nested = scheduler.InvokeAsync(_ => true, nestedCancellation.Token);
                    Assert.True(nested.IsCompleted);
                    return nested.GetAwaiter().GetResult();
                },
                TestContext.Current.CancellationToken
            )
            .AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        Assert.True(call.IsCanceled);
    }

    [Fact]
    public async Task InvokeAsync_BoundsReentrantCallDepthAndRecovers()
    {
        await using var scheduler = new ManagedRuntimeScheduler();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scheduler.InvokeAsync(
                token =>
                    Reenter(scheduler, ManagedRuntimeScheduler.MaximumReentrantDepth + 1, token),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("64-call limit", exception.Message, StringComparison.Ordinal);
        Assert.True(await scheduler.InvokeAsync(_ => true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvokeAsync_RejectsCrossRuntimeEntryFromAnOwningThread()
    {
        await using var first = new ManagedRuntimeScheduler();
        await using var second = new ManagedRuntimeScheduler();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.InvokeAsync(
                _ =>
                {
                    var nested = second.InvokeAsync(
                        _ => true,
                        TestContext.Current.CancellationToken
                    );
                    Assert.True(nested.IsCompleted);
                    return nested.GetAwaiter().GetResult();
                },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("another runtime", exception.Message, StringComparison.Ordinal);
        Assert.True(await first.InvokeAsync(_ => true, TestContext.Current.CancellationToken));
        Assert.True(await second.InvokeAsync(_ => true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResourceRelease_CannotStartRuntimeExecutionDuringCleanup()
    {
        await using var scheduler = new ManagedRuntimeScheduler();
        Exception? callbackFailure = null;
        var resource = await scheduler.InvokeAsync(
            _ =>
                scheduler.RegisterResource(() =>
                {
                    var nested = scheduler.InvokeAsync(
                        _ => true,
                        TestContext.Current.CancellationToken
                    );
                    Assert.True(nested.IsCompleted);
                    callbackFailure = Record.Exception(() => nested.GetAwaiter().GetResult());
                }),
            TestContext.Current.CancellationToken
        );

        await resource.DisposeAsync();

        var exception = Assert.IsType<InvalidOperationException>(callbackFailure);
        Assert.Contains("active runtime call", exception.Message, StringComparison.Ordinal);
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

    private static int Reenter(
        ManagedRuntimeScheduler scheduler,
        int remaining,
        CancellationToken cancellationToken
    )
    {
        if (remaining == 0)
        {
            return 0;
        }

        var nested = scheduler.InvokeAsync(
            token => Reenter(scheduler, remaining - 1, token),
            cancellationToken
        );
        if (!nested.IsCompleted)
        {
            throw new InvalidOperationException(
                "A synchronous runtime callback did not complete inline."
            );
        }

        return nested.GetAwaiter().GetResult() + 1;
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
