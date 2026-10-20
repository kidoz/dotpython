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

    /// <summary>
    /// Assigning `__annotations__` replaces the mapping and drops the annotate body;
    /// assigning `__annotate__` invalidates the cache, while None keeps it. A body that
    /// answers a non-dict is refused when the mapping is read.
    /// </summary>
    [Fact]
    public void AnnotationsCanBeReplacedClearedAndRecomputed()
    {
        var output = Run(
            """"
            def f(x: int) -> str: pass
            f.__annotations__ = {"z": 1}
            print(f.__annotations__, f.__annotate__)
            try:
                f.__annotate__(1)
            except TypeError as e:
                print(e)
            f.__annotations__ = None
            print(f.__annotations__, f.__annotate__)
            try:
                f.__annotations__ = 5
            except TypeError as e:
                print(e)
            try:
                f.__annotate__ = 5
            except TypeError as e:
                print(e)
            try:
                del f.__annotate__
            except TypeError as e:
                print(e)
            def g(x: int): pass
            g.__annotate__ = lambda fmt: {"q": 1}
            print(g.__annotations__)
            g.__annotate__ = None
            print(g.__annotations__, g.__annotate__)
            def h(x: int): pass
            h.__annotate__ = lambda fmt: 7
            try:
                h.__annotations__
            except TypeError as e:
                print(e)
            def k(x: int): pass
            first = k.__annotations__
            k.__annotate__ = lambda fmt: {"r": 2}
            second = k.__annotations__
            print(first, second, second is k.__annotations__)
            del k.__annotations__
            print(k.__annotations__, k.__annotate__)
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'z': 1} None",
                "'NoneType' object is not callable",
                "{} None",
                "__annotations__ must be set to a dict object",
                "__annotate__ must be callable or None",
                "__annotate__ cannot be deleted",
                "{'q': 1}",
                "{'q': 1} None",
                "__annotate__ returned non-dict of type 'int'",
                "{'x': <class 'int'>} {'r': 2} True",
                "{} None",
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
