using Xunit;

namespace DotPython.DifferentialTests;

public sealed class RuntimeCompatibilityGapTests
{
    [Fact]
    public async Task GeneratorTerminationErrors_PreserveCauseAndBecomeRuntimeErrors()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class End(StopIteration):
                pass
            def broken(error):
                yield 1
                raise error
            for error in [StopIteration('bad'), End('subclass')]:
                g = broken(error)
                print(next(g))
                try:
                    next(g)
                except RuntimeError as e:
                    print(str(e), e.__cause__ is error, e.__context__ is error, e.__suppress_context__)
                print(next(g, 'exhausted'))
            def handled():
                try:
                    raise StopIteration('handled')
                except StopIteration:
                    yield 'still running'
                return 42
            g = handled()
            print(next(g))
            try:
                next(g)
            except StopIteration as e:
                print(e.value)
            """
        );
    }

    [Fact]
    public async Task InjectedGeneratorTermination_ConvertsOnlyAfterTheGeneratorHasStarted()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            def g():
                yield 1
            fresh = g()
            try:
                fresh.throw(StopIteration('fresh'))
            except Exception as e:
                print(type(e).__name__, str(e))
            started = g()
            print(next(started))
            try:
                started.throw(StopIteration('started'))
            except Exception as e:
                print(type(e).__name__, str(e), str(e.__cause__))
            try:
                started.throw(StopIteration('finished'))
            except Exception as e:
                print(type(e).__name__, str(e))
            """
        );
    }

    [Fact]
    public async Task AsyncTerminationErrors_DoNotSilentlyFinishIteration()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class AsyncEnd(StopAsyncIteration):
                pass
            async def coroutine():
                raise StopIteration('coroutine bug')
            c = coroutine()
            try:
                c.send(None)
            except RuntimeError as e:
                print(str(e), str(e.__cause__))
            async def broken(error):
                yield 1
                raise error
            async def consume(error):
                try:
                    async for value in broken(error):
                        print(value)
                except RuntimeError as e:
                    print(str(e), e.__cause__ is error)
            for error in [StopAsyncIteration('bad'), AsyncEnd('subclass'), StopIteration('wrong end')]:
                c = consume(error)
                try:
                    c.send(None)
                except StopIteration:
                    print('finished')
            """
        );
    }

    [Fact]
    public async Task GeneratorClose_ReturnsTheCompletionValueOnce()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            marker = []
            def g():
                try:
                    yield 1
                except GeneratorExit:
                    return marker
            fresh = g()
            print(fresh.close())
            active = g()
            print(next(active), active.close() is marker, active.close())
            def bad():
                try:
                    yield 1
                except GeneratorExit:
                    raise ValueError('cleanup failed')
            active = bad()
            next(active)
            try:
                active.close()
            except ValueError as e:
                print(str(e))
            print(active.close())
            """
        );
    }

    [Fact]
    public async Task MapStrict_ReportsShorterAndLongerInputsLazily()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            def add(*values):
                print('called', values)
                return sum(values)
            for inputs in [([1, 2], [10]), ([1], [10, 20]), ([1], [2], []), ([], [], [3]), ([], []), ([1], [2])]:
                values = map(add, *inputs, strict=True)
                print('created')
                try:
                    print(list(values))
                except ValueError as e:
                    print(str(e))
            print(list(map(add, [1, 2], [3], strict=False)))
            print(list(map(None, [], strict=True)))
            try:
                map(add, [], unknown=True)
            except TypeError as e:
                print(str(e))
            """
        );
    }

    [Fact]
    public async Task MapStrict_PreservesIteratorConsumptionAndTruthConversion()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class Strict:
                def __bool__(self):
                    print('strict evaluated')
                    return True
            def source(name, values):
                for value in values:
                    print(name, value)
                    yield value
            a = source('a', [1, 2])
            b = source('b', [3])
            values = map(lambda x, y: x + y, a, b, strict=Strict())
            print(next(values))
            try:
                next(values)
            except ValueError as e:
                print(str(e))
            print(next(a, 'done'), next(b, 'done'))
            def fail(value):
                raise ValueError('call failed')
            try:
                next(map(fail, [1], strict=True))
            except ValueError as e:
                print(str(e))
            """
        );
    }

    [Fact]
    public async Task MapStrict_HandlesCallbackExhaustionAndValidatesKeywordsBeforeConversion()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class End(StopIteration):
                pass
            def stop(value):
                raise End('end')
            print(list(map(stop, [1, 2])))
            values = map(stop, [1, 2])
            try:
                next(values)
            except StopIteration as e:
                print(type(e).__name__, str(e))
            print(next(values, 'default'))
            end = End('original')
            def stop_same(value):
                raise end
            for wrapper in [lambda x: map(lambda v: v, x), lambda x: filter(None, x), enumerate, zip]:
                values = wrapper(map(stop_same, [1]))
                try:
                    next(values)
                except StopIteration as e:
                    print(e is end, e.value)
            def delegate():
                return (yield from map(stop_same, [1]))
            try:
                next(delegate())
            except StopIteration as e:
                print(e.value)
            try:
                print(list(map(lambda a,b: a+b, [1], map(stop, [2]), strict=True)))
            except ValueError as e:
                print(str(e))
            class Strict:
                def __bool__(self):
                    print('must not be called')
                    return True
            for keywords in [{'strict': Strict(), 'bad': True}, {'bad': True, 'strict': Strict()}]:
                try:
                    map(stop, [], **keywords)
                except TypeError as e:
                    print(str(e))
            """
        );
    }

    [Fact]
    public async Task NotImplemented_RejectsBooleanContextsButPreservesOperatorFallback()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            for operation in [lambda: bool(NotImplemented), lambda: not NotImplemented, lambda: any([NotImplemented]), lambda: all([NotImplemented])]:
                try:
                    operation()
                except TypeError as e:
                    print(str(e))
            try:
                if NotImplemented:
                    print('incorrect')
            except TypeError as e:
                print(str(e))
            class C:
                def __eq__(self, other):
                    return NotImplemented
            a = C()
            print(a == a, a == C())
            try:
                map(lambda x: x, [], strict=NotImplemented)
            except TypeError as e:
                print(str(e))
            """
        );
    }
}
