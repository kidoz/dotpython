using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DeferredAnnotationExecutionTests
{
    [Fact]
    public void FunctionAnnotationsAreAvailableAsAMapping()
    {
        var output = Run(
            """
            def f(x: int) -> str:
                pass
            print(f.__annotations__)
            """
        );

        Assert.Equal(
            $@"{{'x': <class 'int'>, 'return': <class 'str'>}}{Environment.NewLine}",
            output
        );
    }

    [Fact]
    public void ADefinitionWithoutAnnotationsReportsAnEmptyMapping()
    {
        var output = Run(
            """
            def f(x):
                pass
            print(f.__annotations__, hasattr(f, "__annotations__"), f.__annotate__ is None)
            """
        );

        Assert.Equal($"{{}} True True{Environment.NewLine}", output);
    }

    [Fact]
    public void AnUndefinedNameFailsOnAccessRatherThanAtDefinition()
    {
        var output = Run(
            """
            def f(x: Undefined) -> AlsoUndefined:
                pass
            print('defined')
            try:
                f.__annotations__
            except NameError as error:
                print(type(error).__name__)
            """
        );

        Assert.Equal(string.Join(Environment.NewLine, "defined", "NameError", ""), output);
    }

    [Fact]
    public void AnnotationsAreEvaluatedAgainstGlobalsAtAccessTime()
    {
        var output = Run(
            """
            def f(x: T) -> R:
                pass
            T = int
            R = str
            print(f.__annotations__)
            """
        );

        Assert.Equal(
            $@"{{'x': <class 'int'>, 'return': <class 'str'>}}{Environment.NewLine}",
            output
        );
    }

    [Fact]
    public void AnnotationsCaptureEnclosingLocalsByClosure()
    {
        var output = Run(
            """
            def outer():
                T = int
                def inner(x: T) -> T:
                    pass
                return inner
            print(outer().__annotations__)

            def unbound():
                def inner(x: T):
                    pass
                return inner
            try:
                unbound().__annotations__
            except NameError as error:
                print(type(error).__name__)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'x': <class 'int'>, 'return': <class 'int'>}",
                "NameError",
                ""
            ),
            output
        );
    }

    [Fact]
    public void AnAnnotationsMappingIsCachedAndMutable()
    {
        var output = Run(
            """
            def f(x: int):
                pass
            first = f.__annotations__
            second = f.__annotations__
            print(first is second)
            first['extra'] = 1
            print(f.__annotations__)
            """
        );

        Assert.Equal(
            string.Join(Environment.NewLine, "True", "{'x': <class 'int'>, 'extra': 1}", ""),
            output
        );
    }

    [Fact]
    public void StringAnnotationsStayStrings()
    {
        var output = Run(
            """
            def f(x: "int") -> "str":
                pass
            print(f.__annotations__)
            """
        );

        Assert.Equal($@"{{'x': 'int', 'return': 'str'}}{Environment.NewLine}", output);
    }

    [Fact]
    public void TheAnnotateCallableAcceptsTheTwoValueFormats()
    {
        var output = Run(
            """
            def f(x: int) -> str:
                pass
            print(f.__annotate__(1))
            print(f.__annotate__(2))
            for unsupported in (3, 4):
                try:
                    f.__annotate__(unsupported)
                except NotImplementedError:
                    print(unsupported, 'NotImplementedError')
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'x': <class 'int'>, 'return': <class 'str'>}",
                "{'x': <class 'int'>, 'return': <class 'str'>}",
                "3 NotImplementedError",
                "4 NotImplementedError",
                ""
            ),
            output
        );
    }

    [Fact]
    public void ParameterKindsAnnotationsIncludingVariadics()
    {
        var output = Run(
            """
            def f(a: int, /, b: str = 'x', *args: float, c: bytes, **kwargs: object) -> None:
                pass
            print(f.__annotations__)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'b': <class 'str'>, 'a': <class 'int'>, 'args': <class 'float'>, "
                    + "'c': <class 'bytes'>, 'kwargs': <class 'object'>, 'return': None}",
                ""
            ),
            output
        );
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "deferred_annotation_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
