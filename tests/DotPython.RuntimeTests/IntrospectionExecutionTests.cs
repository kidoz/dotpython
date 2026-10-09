using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class IntrospectionExecutionTests
{
    [Fact]
    public void DirReportsTheScopeAndTheValue()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(sorted(dir()) == sorted(globals().keys()))
            print(dir() == sorted(dir()), type(dir()).__name__)
            def outer():
                alpha = 1
                beta = 'x'
                return dir(), sorted(dir()) == dir()
            print(outer())
            print((lambda: dir())())
            probe(lambda: dir(1, 2))
            probe(lambda: dir(x=1))
            probe(lambda: dir(1, 2, 3))
            print((lambda value: (lambda: dir())())(1))
            """
        );

        Assert.Equal(
            Lines(
                "True",
                "True list",
                "(['alpha', 'beta'], True)",
                "[]",
                "TypeError dir expected at most 1 argument, got 2",
                "TypeError dir() takes no keyword arguments",
                "TypeError dir expected at most 1 argument, got 3",
                "[]"
            ),
            output
        );
    }

    [Fact]
    public void SizeReportsTheModelledTypes()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(0 .__sizeof__(), 255 .__sizeof__(), (-7).__sizeof__(), True.__sizeof__(), False.__sizeof__())
            print((2 ** 30 - 1).__sizeof__(), (2 ** 30).__sizeof__(), (2 ** 900).__sizeof__())
            print(1.5.__sizeof__(), (-0.0).__sizeof__(), None.__sizeof__())
            print(repr(int.__sizeof__), repr(bool.__sizeof__), repr(float.__sizeof__))
            print(int.__sizeof__(5), bool.__sizeof__(True), float.__sizeof__(1.5), None.__sizeof__())
            print(sorted([1].__dir__()) == sorted([1].__dir__()), [1].__dir__() == sorted([1].__dir__()))
            print(type([1].__dir__()).__name__, '__sizeof__' in None.__dir__(), '__dir__' in [1].__dir__())
            probe(lambda: (1.5).__sizeof__(1))
            probe(lambda: dir(1, x=2))
            print('__dir__' in dir(), 'abs' in sorted(dir()))
            """
        );

        Assert.Equal(
            Lines(
                "44 44 44 44 44",
                "44 48 164",
                "40 40 32",
                "<method '__sizeof__' of 'int' objects> <method '__sizeof__' of 'int' objects> <method '__sizeof__' of 'object' objects>",
                "44 44 40 32",
                "True False",
                "list True True",
                "TypeError object.__sizeof__() takes no arguments (1 given)",
                "TypeError dir() takes no keyword arguments",
                "False False"
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
