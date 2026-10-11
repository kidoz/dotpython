using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class AiterExecutionTests
{
    [Fact]
    public void AiterFollowsTheAsyncIteratorProtocol()
    {
        var output = Run(
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
        Assert.Equal(
            Lines(
                "class Iterator True",
                "indirect Iterator False",
                "generator async_generator True",
                "error bad-aiter aiter() returned not an async iterator of type 'int'",
                "error int 'int' object is not an async iterable",
                "error none aiter() takes exactly one argument (0 given)",
                "error two aiter() takes exactly one argument (2 given)"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
