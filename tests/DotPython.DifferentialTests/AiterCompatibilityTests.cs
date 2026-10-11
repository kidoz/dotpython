using Xunit;

namespace DotPython.DifferentialTests;

public sealed class AiterCompatibilityTests
{
    [Fact]
    public Task AiterFollowsTheAsyncIteratorProtocol() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Iterator:
                def __aiter__(self):
                    return self
                def __anext__(self):
                    raise StopAsyncIteration
            class Iterable:
                def __aiter__(self):
                    return Iterator()
            class Bad:
                def __aiter__(self):
                    return 5
            async def generator():
                yield 1
            iterator = Iterator()
            print("class", type(aiter(iterator)).__name__, aiter(iterator) is iterator)
            print("indirect", type(aiter(Iterable())).__name__, aiter(Iterable()) is Iterable())
            asyncGenerator = generator()
            print("generator", type(aiter(asyncGenerator)).__name__, aiter(asyncGenerator) is asyncGenerator)
            def report(label, thunk):
                try:
                    thunk()
                except TypeError as error:
                    print("error", label, error)
            report("bad-aiter", lambda: aiter(Bad()))
            report("int", lambda: aiter(1))
            report("none", lambda: aiter())
            report("two", lambda: aiter(1, 2))
            """
        );
}
