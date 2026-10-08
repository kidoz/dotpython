using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class FunctoolsExecutionTests
{
    // -------------------------------------------------------------------------
    // reduce
    // -------------------------------------------------------------------------

    [Fact]
    public void ReduceFoldsLeftWithAndWithoutAnInitialValue()
    {
        var output = Run(
            """
            import functools as ft
            print(ft.reduce(lambda a, b: a + b, [1, 2, 3, 4]))
            print(ft.reduce(lambda a, b: a + b, [1, 2, 3], 10))
            print(ft.reduce(lambda a, b: a + b, [1, 2, 3], initial=10))
            print(ft.reduce(lambda a, b: a + b, [7]))
            print(ft.reduce(lambda a, b: a + b, [], None))
            print(ft.reduce(lambda a, b: a + [b], [[1], [2]], []))
            print(ft.reduce(max, [3, 1, 2]))
            """
        );

        Assert.Equal(Lines("10", "16", "16", "7", "None", "[[1], [2]]", "3"), output);
    }

    [Fact]
    public void ReduceReportsItsArityAndTheEmptySequence()
    {
        var output = Run(
            """
            import functools as ft
            def attempt(thunk):
                try:
                    thunk()
                except TypeError as error:
                    print(type(error).__name__ + ":", error)

            attempt(lambda: ft.reduce(lambda a, b: a + b, []))
            attempt(lambda: ft.reduce(lambda a, b: a + b))
            attempt(lambda: ft.reduce(lambda a, b: a + b, [1], 2, 3))
            attempt(lambda: ft.reduce(lambda a, b: a + b, [1, 2], initial=1, bogus=2))
            attempt(lambda: ft.reduce(function=lambda a, b: a + b))
            attempt(lambda: ft.reduce(lambda a, b: a + b, [1, 2], bogus=5))
            attempt(lambda: ft.reduce(5, [1, 2]))
            """
        );

        Assert.Equal(
            Lines(
                "TypeError: reduce() of empty iterable with no initial value",
                "TypeError: reduce() takes at least 2 positional arguments (1 given)",
                "TypeError: reduce() takes at most 3 arguments (4 given)",
                "TypeError: reduce() takes at most 3 arguments (4 given)",
                "TypeError: reduce() takes at least 2 positional arguments (0 given)",
                "TypeError: reduce() got an unexpected keyword argument 'bogus'",
                "TypeError: 'int' object is not callable"
            ),
            output
        );
    }

    [Fact]
    public void ReduceCallsBoundMethodsAndShortCircuitsOnStopIteration()
    {
        var output = Run(
            """
            import functools as ft

            class Accumulator:
                def __init__(self, base):
                    self.base = base

                def combine(self, left, right):
                    return left * 10 + right

            print(ft.reduce(Accumulator(0).combine, [1, 2, 3]))
            print(ft.reduce(lambda a, b: a + b, iter([1, 2, 3])))
            print(ft.reduce(lambda a, b: a + b, (), 5))
            """
        );

        Assert.Equal(Lines("123", "6", "5"), output);
    }

    // -------------------------------------------------------------------------
    // partial
    // -------------------------------------------------------------------------

    [Fact]
    public void PartialExposesItsFunctionArgumentsAndKeywords()
    {
        var output = Run(
            """
            import functools as ft

            def f(a, b, c=3, *args, **kw):
                return (a, b, c, args, kw)

            p = ft.partial(f, 1)
            print(ft.partial.__name__)
            print(ft.partial.__qualname__)
            print(ft.partial.__module__)
            print(type(p))
            print(type(p).__name__)
            print(p.func is f)
            print(p.args)
            print(p.keywords)
            print(repr(p))
            print(repr(ft.partial(f, 1, 2, 3, 4, x=5)))
            print(repr(ft.partial(f, b=2, c=9)))
            """
        );

        Assert.Equal(
            Lines(
                "partial",
                "partial",
                "functools",
                "<class 'functools.partial'>",
                "partial",
                "True",
                "(1,)",
                "{}",
                "functools.partial(<function f>, 1)",
                "functools.partial(<function f>, 1, 2, 3, 4, x=5)",
                "functools.partial(<function f>, b=2, c=9)"
            ),
            output
        );
    }

    [Fact]
    public void PartialMergesStoredAndCallSiteArguments()
    {
        var output = Run(
            """
            import functools as ft

            def f(a, b, c=3, *args, **kw):
                return (a, b, c, args, kw)

            p = ft.partial(f, 1)
            print(p(2))
            print(ft.partial(f, 1, 2, 3, 4, x=5)())
            print(ft.partial(f, b=2, c=9)(1))
            print(ft.partial(f, b=2, c=9)(1, b=5))
            print(ft.partial(f, b=2, c=9)(1, c=8))
            print(p(a=1, b=2) if False else p(2, c=7))
            """
        );

        Assert.Equal(
            Lines(
                "(1, 2, 3, (), {})",
                "(1, 2, 3, (4,), {'x': 5})",
                "(1, 2, 9, (), {})",
                "(1, 5, 9, (), {})",
                "(1, 2, 8, (), {})",
                "(1, 2, 7, (), {})"
            ),
            output
        );
    }

    [Fact]
    public void NestedPartialsFlattenWhenCalledAndPrinted()
    {
        var output = Run(
            """
            import functools as ft

            def f(a, b, c=3):
                return (a, b, c)

            inner = ft.partial(f, 1)
            outer = ft.partial(inner, 2)
            print(repr(outer))
            print(outer())
            print(outer.func is f)
            print(outer.args)
            print(repr(ft.partial(ft.partial(ft.partial(f, 1), 2), 3)))
            """
        );

        Assert.Equal(
            Lines(
                "functools.partial(<function f>, 1, 2)",
                "(1, 2, 3)",
                "True",
                "(1, 2)",
                "functools.partial(<function f>, 1, 2, 3)"
            ),
            output
        );
    }

    [Fact]
    public void PartialMembersAreReadOnlyAndInstancesCarryAttributes()
    {
        var output = Run(
            """
            import functools as ft

            def f(a, b=2):
                return a + b

            p = ft.partial(f, 1)
            p.newattr = 42
            print(p.__dict__)
            print(p.__module__)
            print(hasattr(p, "__wrapped__"))
            print(p.newattr)

            def attempt(action):
                try:
                    action()
                except AttributeError as error:
                    print(type(error).__name__ + ":", error)

            attempt(lambda: setattr(p, "args", (9,)))
            attempt(lambda: setattr(p, "keywords", {}))
            attempt(lambda: delattr(p, "func"))
            attempt(lambda: p.__name__)
            """
        );

        Assert.Equal(
            Lines(
                "{'newattr': 42}",
                "functools",
                "False",
                "42",
                "AttributeError: readonly attribute",
                "AttributeError: readonly attribute",
                "AttributeError: readonly attribute",
                "AttributeError: 'partial' object has no attribute '__name__'"
            ),
            output
        );
    }

    [Fact]
    public void PartialRejectsAMissingOrUncallableTarget()
    {
        var output = Run(
            """
            import functools as ft

            def attempt(action):
                try:
                    action()
                except TypeError as error:
                    print(type(error).__name__ + ":", error)

            attempt(lambda: ft.partial())
            attempt(lambda: ft.partial(5))
            attempt(lambda: ft.partial([]))
            """
        );

        Assert.Equal(
            Lines(
                "TypeError: type 'partial' takes at least one argument",
                "TypeError: the first argument must be callable",
                "TypeError: the first argument must be callable"
            ),
            output
        );
    }

    [Fact]
    public void PartialBindsThroughTheDescriptorProtocol()
    {
        var output = Run(
            """
            import functools as ft

            def combine(self, left, right):
                return (type(self).__name__, left, right)

            class Widget:
                combine = ft.partial(combine, left="L")

            widget = Widget()
            print(widget.combine(right="R"))
            print(widget.combine(right=1))
            print(Widget.combine.func is combine)
            print(Widget.combine.args)
            print(Widget.combine.keywords)
            print(Widget.combine is Widget.__dict__["combine"])
            print(ft.partial(combine, None, "x")("y"))
            """
        );

        Assert.Equal(
            Lines(
                "('Widget', 'L', 'R')",
                "('Widget', 'L', 1)",
                "True",
                "()",
                "{'left': 'L'}",
                "True",
                "('NoneType', 'x', 'y')"
            ),
            output
        );
    }

    // -------------------------------------------------------------------------
    // update_wrapper / wraps
    // -------------------------------------------------------------------------

    [Fact]
    public void WrapsCopiesIdentityAndTheWrapperDictionary()
    {
        var output = Run(
            """
            import functools as ft

            def deco(fn):
                @ft.wraps(fn)
                def wrapper(*args, **keywords):
                    return fn(*args, **keywords)

                return wrapper

            def myfunc(a, b=2):
                return a + b

            myfunc.custom = "attr"
            wrapped = deco(myfunc)
            print(wrapped.__name__)
            print(wrapped.__qualname__)
            print(wrapped.__module__)
            print(wrapped.__wrapped__ is myfunc)
            print(wrapped.__dict__)
            print(wrapped.custom)
            print(wrapped(1, 2))
            """
        );

        Assert.Equal(
            Lines(
                "myfunc",
                "myfunc",
                "__main__",
                "True",
                "{'custom': 'attr', '__wrapped__': <function myfunc>}",
                "attr",
                "3"
            ),
            output
        );
    }

    [Fact]
    public void UpdateWrapperAcceptsCustomAssignmentAndUpdateLists()
    {
        var output = Run(
            """
            import functools as ft

            def wrapped():
                return "wrapped result"

            wrapped.marker = 1

            def wrapper():
                return "wrapper result"

            result = ft.update_wrapper(
                wrapper, wrapped, assigned=("__name__",), updated=("__dict__",)
            )
            print(result is wrapper)
            print(wrapper.__name__)
            print(wrapper.marker)
            print(wrapper.__wrapped__ is wrapped)
            print(wrapper())
            print(ft.update_wrapper(wrapped, wrapped) is wrapped)
            """
        );

        Assert.Equal(Lines("True", "wrapped", "1", "True", "wrapper result", "True"), output);
    }

    [Fact]
    public void WrapperAssignmentsExposeThePinnedSurface()
    {
        var output = Run(
            """
            import functools as ft
            print(ft.WRAPPER_ASSIGNMENTS)
            print(ft.WRAPPER_UPDATES)
            print(type(ft.WRAPPER_ASSIGNMENTS).__name__)

            def target():
                return 1

            partial = ft.wraps(target)
            print(type(partial).__name__)
            print(sorted(partial.keywords))
            print(partial.keywords["assigned"] == ft.WRAPPER_ASSIGNMENTS)
            print(partial.keywords["updated"] == ft.WRAPPER_UPDATES)

            def attempt(action):
                try:
                    action()
                except TypeError as error:
                    print(type(error).__name__ + ":", error)

            attempt(lambda: ft.wraps())
            attempt(lambda: ft.wraps(target, bogus=1))
            attempt(lambda: ft.update_wrapper())
            """
        );

        Assert.Equal(
            Lines(
                "('__module__', '__name__', '__qualname__', '__doc__',"
                    + " '__annotate__', '__type_params__')",
                "('__dict__',)",
                "tuple",
                "partial",
                "['assigned', 'updated', 'wrapped']",
                "True",
                "True",
                "TypeError: wraps() missing 1 required positional argument: 'wrapped'",
                "TypeError: wraps() got an unexpected keyword argument 'bogus'",
                "TypeError: update_wrapper() missing 2 required positional arguments:"
                    + " 'wrapper' and 'wrapped'"
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
