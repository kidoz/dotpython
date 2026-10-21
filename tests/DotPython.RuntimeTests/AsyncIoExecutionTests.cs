using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

/// <summary>
/// The minimal `asyncio` loop: run-to-completion tasks, gather, sleep, wait_for,
/// cancellation and task state, with the loop's ready queue ordering pinned.
/// </summary>
public sealed class AsyncIoExecutionTests
{
    [Fact]
    public void TasksGatherAndSleepRunToCompletion()
    {
        var output = Run(
            """
            import asyncio
            async def leaf(x):
                await asyncio.sleep(0)
                return x * 2
            async def main():
                a = asyncio.create_task(leaf(1))
                b = asyncio.create_task(leaf(2))
                print("before", a.done(), b.done())
                print("gather", await asyncio.gather(a, b))
                print("awaited", await asyncio.sleep(0, result="r"))
                print("leaf", await leaf(3))
            print("run", asyncio.run(main()))
            """
        );

        Assert.Equal(
            Lines("before False False", "gather [2, 4]", "awaited r", "leaf 6", "run None"),
            output
        );
    }

    [Fact]
    public void CancellationTimeoutAndTaskErrors()
    {
        var output = Run(
            """
            import asyncio

            async def slow():
                try:
                    await asyncio.sleep(10)
                except asyncio.CancelledError:
                    print("cancelled inner")
                    raise

            async def timeout_main():
                try:
                    await asyncio.wait_for(slow(), 0.01)
                except TimeoutError as e:
                    print("timed out", type(e).__name__, e)

            asyncio.run(timeout_main())

            async def cancelling():
                t = asyncio.create_task(slow())
                await asyncio.sleep(0)
                print("cancel", t.cancel())
                try:
                    await t
                except asyncio.CancelledError:
                    print("task cancelled", t.cancelled())

            asyncio.run(cancelling())

            def show(label, f):
                try: print(label, "ok", repr(f()))
                except Exception as e: print(label, type(e).__name__, e)

            show("run-int", lambda: asyncio.run(5))
            show("task-int", lambda: asyncio.create_task(5))
            show("gather-int", lambda: asyncio.gather(5))

            async def bad_result():
                t = asyncio.create_task(asyncio.sleep(0))
                show("result-early", lambda: t.result())
                show("exception-early", lambda: t.exception())
                await t

            asyncio.run(bad_result())
            print("timeout-alias", asyncio.TimeoutError is TimeoutError)
            print("cancelled-bases", [c.__name__ for c in asyncio.CancelledError.__mro__[:3]])
            """
        );

        Assert.Equal(
            Lines(
                "cancelled inner",
                "timed out TimeoutError ",
                "cancel True",
                "cancelled inner",
                "task cancelled True",
                "run-int TypeError An asyncio.Future, a coroutine or an awaitable is required",
                "task-int RuntimeError no running event loop",
                "gather-int TypeError An asyncio.Future, a coroutine or an awaitable is required",
                "result-early InvalidStateError Result is not set.",
                "exception-early InvalidStateError Exception is not set.",
                "timeout-alias True",
                "cancelled-bases ['CancelledError', 'BaseException', 'object']"
            ),
            output
        );
    }

    [Fact]
    public void OrderingCallbacksAndSuppressedCancellation()
    {
        var output = Run(
            """
            import asyncio

            async def worker(name, n):
                for i in range(n):
                    print("tick", name, i)
                    await asyncio.sleep(0)
                return name

            async def ordering():
                t1 = asyncio.create_task(worker("a", 2), name="alpha")
                t2 = asyncio.create_task(worker("b", 1))
                print("names", t1.get_name(), t2.get_name())
                print("results", await asyncio.gather(t1, t2))
                print("ef", asyncio.ensure_future(t1) is t1)
                seen = []
                t1.add_done_callback(lambda task: seen.append("late"))
                print("callbacks", seen)

            asyncio.run(ordering())

            async def stubborn():
                try:
                    await asyncio.sleep(10)
                except asyncio.CancelledError:
                    print("caught cancel")
                    return "survived"

            async def suppress():
                t = asyncio.create_task(stubborn())
                await asyncio.sleep(0)
                t.cancel()
                print("suppressed", await t, t.cancelled(), t.result())

            asyncio.run(suppress())

            async def failing():
                raise KeyError("k")

            async def waitfor_error():
                try:
                    await asyncio.wait_for(failing(), 1)
                except KeyError as e:
                    print("propagated", type(e).__name__, e)

            asyncio.run(waitfor_error())

            async def gather_empty():
                print("empty", await asyncio.gather())

            asyncio.run(gather_empty())

            async def returns():
                return 42

            print("value", asyncio.run(returns()))
            """
        );

        Assert.Equal(
            Lines(
                "names alpha Task-2",
                "tick a 0",
                "tick b 0",
                "tick a 1",
                "results ['a', 'b']",
                "ef True",
                "callbacks []",
                "caught cancel",
                "suppressed survived False survived",
                "propagated KeyError 'k'",
                "empty []",
                "value 42"
            ),
            output
        );
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
