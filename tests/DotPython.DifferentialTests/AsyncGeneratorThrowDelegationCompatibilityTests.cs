using Xunit;

namespace DotPython.DifferentialTests;

public sealed class AsyncGeneratorThrowDelegationCompatibilityTests
{
    [Fact]
    public async Task ThrowThroughAwaitedAsyncGeneratorRunsItsCleanupAndReleasesOwnership()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class Pause:
                def __await__(self):
                    yield 'pause'
            async def source():
                try:
                    await Pause()
                    yield 'value'
                finally:
                    print('source cleanup')
            target = source()
            async def drive(step): return await step
            driver = drive(target.__anext__())
            print(driver.send(None))
            try: driver.throw(ValueError('stop'))
            except ValueError as error: print('driver', error)
            check = drive(target.__anext__())
            try: check.send(None)
            except StopAsyncIteration: print('source completed')
            """
        );
    }

    [Fact]
    public async Task AsyncGeneratorCanHandleDelegatedThrowAndCompleteTheAwaitWithAYield()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class Pause:
                def __await__(self): yield 'pause'
            async def source():
                try:
                    await Pause()
                except ValueError as error:
                    print('handled', error)
                    yield 'recovered'
                yield 'after'
            target = source()
            async def drive(step): return await step
            driver = drive(target.__anext__())
            print(driver.send(None))
            try: driver.throw(ValueError('injected'))
            except StopIteration as result: print('first', result.value)
            check = drive(target.__anext__())
            try: check.send(None)
            except StopIteration as result: print('second', result.value)
            final = drive(target.__anext__())
            try: final.send(None)
            except StopAsyncIteration: print('done')
            """
        );
    }

    [Fact]
    public async Task ConstructorFailureCanSuspendAgainWhileTheOriginalAsyncStepKeepsOwnership()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class Pause:
                def __init__(self, label): self.label = label
                def __await__(self): yield self.label
            class Delivery(Exception):
                def __init__(self):
                    print('constructor')
                    raise LookupError('replacement')
            async def source():
                try:
                    await Pause('first pause')
                except LookupError as error:
                    print('handled', error)
                    await Pause('second pause')
                    yield 'recovered'
                yield 'after'
            target = source()
            async def drive(step): return await step
            driver = drive(target.__anext__())
            print(driver.send(None))
            print(driver.throw(Delivery))
            competing = drive(target.__anext__())
            try: competing.send(None)
            except RuntimeError as error: print('competing', error)
            try: driver.send(None)
            except StopIteration as result: print('first', result.value)
            check = drive(target.__anext__())
            try: check.send(None)
            except StopIteration as result: print('second', result.value)
            final = drive(target.__anext__())
            try: final.send(None)
            except StopAsyncIteration: print('done')
            """
        );
    }
}
