using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DotPython.Runtime.Managed.Execution;
using DotPython.Runtime.Managed.Native;
using Xunit;

namespace DotPython.RuntimeTests;

[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "xUnit tests intentionally resume in the test context."
)]
public sealed class ManagedNativeRuntimeSimulatorTests
{
    [Fact]
    public async Task References_ModelBorrowedNewImmortalNullableAndReleaseTransitions()
    {
        await using var runtime = new ManagedNativeRuntimeSimulator();
        ManagedNativeReference? owner = null;
        ManagedNativeReference? borrowed = null;
        ManagedNativeReference? duplicate = null;
        ManagedNativeReference? immortal = null;

        await runtime.InvokeAsync(
            (context, _) =>
            {
                owner = context.NewReference(new PythonTextValue("value"));
                borrowed = context.Borrow(owner);
                duplicate = context.NewReference(borrowed);
                immortal = context.Immortal(PythonNoneValue.Instance);

                Assert.Null(context.Nullable(null));
                Assert.Equal(2, context.OwnedReferenceCount);
                Assert.Equal(2, context.LiveHandleCount);
                Assert.Equal(ManagedNativeReferenceOwnership.Borrowed, borrowed.Ownership);
                Assert.Equal(ManagedNativeReferenceOwnership.New, duplicate.Ownership);
                Assert.Equal(ManagedNativeReferenceOwnership.Immortal, immortal.Ownership);
                return true;
            },
            TestContext.Current.CancellationToken
        );

        await owner!.DisposeAsync();
        await runtime.InvokeAsync(
            (context, _) =>
            {
                Assert.Equal(
                    "value",
                    Assert.IsType<PythonTextValue>(context.Resolve(borrowed!)).Value
                );
                Assert.Equal(1, context.OwnedReferenceCount);
                return true;
            },
            TestContext.Current.CancellationToken
        );

        await duplicate!.DisposeAsync();
        var exception = await Assert.ThrowsAsync<PythonRuntimeException>(async () =>
            await runtime.InvokeAsync(
                (context, _) => context.Resolve(borrowed!),
                TestContext.Current.CancellationToken
            )
        );
        await immortal!.DisposeAsync();

        Assert.Equal("DPY8006", exception.Code);
        Assert.Equal(ManagedNativeReferenceState.Released, owner.State);
        Assert.Equal(ManagedNativeReferenceState.Invalidated, borrowed!.State);
    }

    [Fact]
    public async Task StolenReference_TransfersOwnershipIntoAContainerOnSuccessAndFailure()
    {
        await using var runtime = new ManagedNativeRuntimeSimulator();
        ManagedNativeReference? list = null;
        ManagedNativeReference? item = null;
        ManagedNativeReference? failedItem = null;

        await runtime.InvokeAsync(
            (context, _) =>
            {
                list = context.NewReference(ManagedObjectProtocols.CreateList(1));
                item = context.NewReference(new PythonTextValue("stored"));
                context.SetListItemSteal(list, 0, item);

                Assert.Equal(ManagedNativeReferenceState.Stolen, item.State);
                Assert.Equal(
                    "stored",
                    Assert
                        .IsType<PythonTextValue>(
                            ManagedObjectProtocols.GetItem(
                                context.Resolve(list),
                                PythonWholeNumberValue.Create(0)
                            )
                        )
                        .Value
                );

                failedItem = context.NewReference(new PythonTextValue("discarded"));
                Assert.Throws<PythonRuntimeException>(() =>
                    context.SetListItemSteal(list, 4, failedItem)
                );
                Assert.Equal(ManagedNativeReferenceState.Stolen, failedItem.State);
                Assert.Equal(1, context.OwnedReferenceCount);
                return true;
            },
            TestContext.Current.CancellationToken
        );

        await list!.DisposeAsync();
    }

    [Fact]
    public async Task GenerationInvalidation_RejectsStaleAndCrossRuntimeHandles()
    {
        await using var first = new ManagedNativeRuntimeSimulator();
        await using var second = new ManagedNativeRuntimeSimulator();
        var reference = await first.InvokeAsync(
            (context, _) => context.NewReference(PythonWholeNumberValue.Create(42)),
            TestContext.Current.CancellationToken
        );
        var handle = reference.Handle;

        var crossRuntime = await Assert.ThrowsAsync<PythonRuntimeException>(async () =>
            await second.InvokeAsync(
                (context, _) => context.Resolve(handle),
                TestContext.Current.CancellationToken
            )
        );
        var generation = first.Generation;
        await first.InvokeAsync(
            (context, _) =>
            {
                context.InvalidateGeneration();
                return true;
            },
            TestContext.Current.CancellationToken
        );
        var stale = await Assert.ThrowsAsync<PythonRuntimeException>(async () =>
            await first.InvokeAsync(
                (context, _) => context.Resolve(reference),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("DPY8006", crossRuntime.Code);
        Assert.Contains("another runtime", crossRuntime.Message, StringComparison.Ordinal);
        Assert.Equal("DPY8006", stale.Code);
        Assert.Equal(generation + 1, first.Generation);
        Assert.Equal(ManagedNativeReferenceState.Invalidated, reference.State);
        await reference.DisposeAsync();
    }

    [Fact]
    public async Task ErrorAdapter_FetchesAndRestoresOwnedRaisedExceptions()
    {
        await using var runtime = new ManagedNativeRuntimeSimulator();

        await runtime.InvokeAsync(
            (context, _) =>
            {
                context.Errors.SetException("ValueError", "invalid version");
                var occurred = Assert.IsType<ManagedNativeReference>(context.Errors.Occurred());
                Assert.Equal(ManagedNativeReferenceOwnership.Borrowed, occurred.Ownership);

                var saved = Assert.IsType<ManagedNativeReference>(context.Errors.Save());
                Assert.False(context.Errors.IsSet);
                Assert.Equal(
                    "invalid version",
                    Assert.IsType<PythonExceptionValue>(context.Resolve(saved)).Message
                );

                context.Errors.SetException("TypeError", "temporary");
                context.Errors.Restore(saved);
                Assert.Equal(ManagedNativeReferenceState.Stolen, saved.State);
                var restored = Assert.IsType<ManagedNativeReference>(context.Errors.Fetch());
                Assert.Equal(
                    "ValueError",
                    Assert.IsType<PythonExceptionValue>(context.Resolve(restored)).TypeName
                );
                ManagedNativeContext.DisposeInline(restored);
                return true;
            },
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task Callback_ReentersInlineAndKeepsOuterAndCallbackErrorsIndependent()
    {
        await using var runtime = new ManagedNativeRuntimeSimulator();

        await runtime.InvokeAsync(
            (context, token) =>
            {
                context.Errors.SetException("ValueError", "outer");
                var callback = context.InvokeCallback(
                    (nested, nestedToken) =>
                    {
                        Assert.Equal(token, nestedToken);
                        nested.Errors.SetException("TypeError", "callback");
                        return Environment.CurrentManagedThreadId;
                    },
                    token
                );

                Assert.Equal(Environment.CurrentManagedThreadId, callback.Value);
                var callbackError = Assert.IsType<ManagedNativeReference>(callback.RaisedException);
                Assert.Equal(
                    "callback",
                    Assert.IsType<PythonExceptionValue>(context.Resolve(callbackError)).Message
                );
                ManagedNativeContext.DisposeInline(callbackError);

                var outer = Assert.IsType<ManagedNativeReference>(context.Errors.Fetch());
                Assert.Equal(
                    "outer",
                    Assert.IsType<PythonExceptionValue>(context.Resolve(outer)).Message
                );
                ManagedNativeContext.DisposeInline(outer);
                return true;
            },
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task Callback_PropagatesCancellationAndRestoresOuterError()
    {
        await using var runtime = new ManagedNativeRuntimeSimulator();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runtime.InvokeAsync(
                (context, _) =>
                {
                    context.Errors.SetException("ValueError", "outer");
                    cancellation.Cancel();
                    return context.InvokeCallback((_, _) => true, cancellation.Token);
                },
                TestContext.Current.CancellationToken
            )
        );

        Assert.True(exception.CancellationToken.IsCancellationRequested);
        await runtime.InvokeAsync(
            (context, _) =>
            {
                var outer = Assert.IsType<ManagedNativeReference>(context.Errors.Fetch());
                Assert.Equal(
                    "outer",
                    Assert.IsType<PythonExceptionValue>(context.Resolve(outer)).Message
                );
                ManagedNativeContext.DisposeInline(outer);
                return true;
            },
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task Callback_BoundsNestingAndRuntimeRecovers()
    {
        await using var runtime = new ManagedNativeRuntimeSimulator();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.InvokeAsync(
                (context, token) =>
                    Reenter(context, ManagedRuntimeScheduler.MaximumReentrantDepth + 1, token),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("64-call limit", exception.Message, StringComparison.Ordinal);
        Assert.True(
            await runtime.InvokeAsync((_, _) => true, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task NativeRelease_CannotStartExecutionDuringCleanup()
    {
        ManagedNativeRuntimeSimulator? runtime = null;
        Exception? callbackFailure = null;
        runtime = new ManagedNativeRuntimeSimulator(_ =>
        {
            var nested = runtime!.InvokeAsync(
                (_, _) => true,
                TestContext.Current.CancellationToken
            );
            Assert.True(nested.IsCompleted);
            callbackFailure = Record.Exception(() => nested.GetAwaiter().GetResult());
        });
        await using (runtime)
        {
            var reference = await runtime.InvokeAsync(
                (context, _) => context.NewReference(PythonNoneValue.Instance),
                TestContext.Current.CancellationToken
            );

            await reference.DisposeAsync();

            var exception = Assert.IsType<InvalidOperationException>(callbackFailure);
            Assert.Contains("active runtime call", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Shutdown_ReleasesNativeReferencesInReverseRegistrationOrder()
    {
        var released = new List<long>();
        var references = new List<ManagedNativeReference>();
        var runtime = new ManagedNativeRuntimeSimulator(handle => released.Add(handle.HandleId));
        await runtime.InvokeAsync(
            (context, _) =>
            {
                references.Add(context.NewReference(PythonWholeNumberValue.Create(1)));
                references.Add(context.NewReference(PythonWholeNumberValue.Create(2)));
                references.Add(context.NewReference(PythonWholeNumberValue.Create(3)));
                return true;
            },
            TestContext.Current.CancellationToken
        );

        await runtime.DisposeAsync();

        Assert.Equal([3, 2, 1], released);
        GC.KeepAlive(references);
    }

    [Fact]
    public async Task AbandonedReference_FinalizerEnqueuesReleaseOnTheRuntimeThread()
    {
        var releaseThreadId = 0;
        await using var runtime = new ManagedNativeRuntimeSimulator(_ =>
            releaseThreadId = Environment.CurrentManagedThreadId
        );
        var reference = AbandonReference(runtime, TestContext.Current.CancellationToken);

        await runtime.InvokeAsync((_, _) => true, TestContext.Current.CancellationToken);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await runtime.InvokeAsync(
            (context, _) =>
            {
                Assert.Equal(0, context.OwnedReferenceCount);
                return true;
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(reference.IsAlive);
        Assert.NotEqual(0, releaseThreadId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonReference(
        ManagedNativeRuntimeSimulator runtime,
        CancellationToken cancellationToken
    )
    {
        var reference = runtime
            .InvokeAsync(
                (context, _) => context.NewReference(new PythonTextValue("temporary")),
                cancellationToken
            )
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new WeakReference(reference);
    }

    private static int Reenter(
        ManagedNativeContext context,
        int remaining,
        CancellationToken cancellationToken
    )
    {
        if (remaining == 0)
        {
            return 0;
        }

        return context
                .InvokeCallback(
                    (nested, token) => Reenter(nested, remaining - 1, token),
                    cancellationToken
                )
                .Value + 1;
    }
}
