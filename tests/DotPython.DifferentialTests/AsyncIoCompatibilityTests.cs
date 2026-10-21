using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The minimal `asyncio` loop against CPython 3.14: run, tasks, gather, sleep, wait_for,
/// cancellation and task state. The loop is single-threaded and run to completion, with
/// sleep(0) yielding the same FIFO order CPython's ready queue produces.
/// </summary>
public sealed class AsyncIoCompatibilityTests
{
    [Fact]
    public Task TasksGatherAndSleepMatchTheOracle() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task CancellationTimeoutAndTaskErrorsMatchTheOracle() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task OrderingCallbacksAndTaskStateMatchTheOracle() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import asyncio

            def show(label, f):
                try: print(label, "ok", repr(f()))
                except Exception as e: print(label, type(e).__name__, e)

            async def worker(name, n):
                for i in range(n):
                    print("tick", name, i)
                    await asyncio.sleep(0)
                return name

            async def ordering():
                t1 = asyncio.create_task(worker("a", 2), name="alpha")
                t2 = asyncio.create_task(worker("b", 1))
                print("names", t1.get_name(), t2.get_name())
                t1.set_name("renamed")
                print("renamed", t1.get_name())
                print("results", await asyncio.gather(t1, t2))
                print("ef", asyncio.ensure_future(t1) is t1)
                probe = asyncio.sleep(0)
                print("iscoro", asyncio.iscoroutine(probe), asyncio.iscoroutine(t1))
                await probe

            asyncio.run(ordering())

            async def boom():
                await asyncio.sleep(0)
                raise ValueError("boom")

            async def gather_errors():
                ok = asyncio.create_task(worker("x", 1))
                bad = asyncio.create_task(boom())
                try:
                    await asyncio.gather(ok, bad)
                except ValueError as e:
                    print("gather raised", e)
                results = await asyncio.gather(asyncio.create_task(worker("y", 1)), asyncio.create_task(boom()), return_exceptions=True)
                print("gather results", [type(r).__name__ if isinstance(r, Exception) else r for r in results])
                try:
                    await asyncio.create_task(boom())
                except ValueError as e:
                    print("await task", e)
                task = asyncio.create_task(boom())
                try:
                    await asyncio.gather(task)
                except ValueError:
                    pass
                print("task done", task.done(), type(task.exception()).__name__)
                try:
                    task.result()
                except ValueError as e:
                    print("result", e)

            asyncio.run(gather_errors())

            async def quick():
                return "q"

            async def waitfor():
                print("done", await asyncio.wait_for(quick(), None))
                print("done2", await asyncio.wait_for(quick(), 1.0))
                try:
                    await asyncio.wait_for(quick(), 0)
                except TimeoutError:
                    print("timeout-zero")

            asyncio.run(waitfor())

            async def nested():
                try:
                    asyncio.run(quick())
                except RuntimeError as e:
                    print("nested", e)
                try:
                    asyncio.create_task(5)
                except TypeError as e:
                    print("bad-task", e)

            asyncio.run(nested())

            async def callbacks():
                seen = []
                t = asyncio.create_task(quick())
                t.add_done_callback(lambda task: seen.append(task.result()))
                await t
                print("callbacks", seen)
                t.add_done_callback(lambda task: seen.append("late"))
                print("callbacks2", seen)

            asyncio.run(callbacks())
            print("alias", asyncio.TimeoutError is TimeoutError)
            """
        );
}
