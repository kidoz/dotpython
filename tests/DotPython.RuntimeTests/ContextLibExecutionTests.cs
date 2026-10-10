using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ContextLibExecutionTests
{
    [Fact]
    public void ContextLibSurface()
    {
        var output = Run(
            """
            import contextlib

            print("doc", contextlib.__doc__)
            print("class-docs", contextlib.AbstractContextManager.__doc__, contextlib.ContextDecorator.__doc__)
            print("manager-doc", contextlib.contextmanager(lambda: iter([]))().__doc__)
            print("names", sorted(n for n in ("contextmanager", "closing", "nullcontext", "suppress", "ExitStack", "ContextDecorator", "AbstractContextManager") if hasattr(contextlib, n)))
            print("abstract", contextlib.AbstractContextManager, contextlib.AbstractContextManager.__abstractmethods__)
            print("meta", type(contextlib.AbstractContextManager).__name__, type(contextlib.ContextDecorator).__name__, type(contextlib.ExitStack).__name__)


            @contextlib.contextmanager
            def managed(value):
                print("enter", value)
                try:
                    yield value * 2
                finally:
                    print("exit", value)


            with managed(3) as doubled:
                print("body", doubled)

            print("helper", managed.__name__, managed.__module__, type(managed).__name__, managed.__wrapped__.__name__)


            @contextlib.contextmanager
            def failing():
                print("failing-enter")
                try:
                    yield
                except ValueError as error:
                    print("caught", error)
                finally:
                    print("finally")


            try:
                with failing():
                    raise ValueError("boom")
            except Exception as error:
                print("escaped", error)


            @contextlib.contextmanager
            def rethrow_same():
                try:
                    yield
                except ValueError:
                    raise


            try:
                with rethrow_same():
                    raise ValueError("same")
            except ValueError as error:
                print("same-escaped", error)


            @contextlib.contextmanager
            def no_yield():
                if False:
                    yield


            try:
                with no_yield():
                    pass
            except RuntimeError as error:
                print("no-yield", error)


            @contextlib.contextmanager
            def extra_yield():
                yield 1
                yield 2


            try:
                with extra_yield() as first:
                    print("first", first)
            except RuntimeError as error:
                print("extra-yield", error)


            @contextlib.contextmanager
            def keeps_yielding():
                try:
                    yield 1
                    yield 2
                except ValueError:
                    yield 3


            try:
                with keeps_yielding() as first:
                    print("keeps-first", first)
                    raise ValueError("trigger")
            except RuntimeError as error:
                print("keeps", error)


            @contextlib.contextmanager
            def stop_iteration_inside():
                yield
                raise StopIteration("boom")


            try:
                with stop_iteration_inside():
                    pass
            except RuntimeError as error:
                print("pep479", error)

            print("manager-mro", [cls.__name__ for cls in type(managed(1)).__mro__])
            print("manager-class", type(managed(1)).__module__, type(managed(1)).__qualname__)

            manager = managed(4)
            print("recreate", type(manager._recreate_cm()).__name__)
            with manager as value:
                print("reused", value)
            try:
                manager.__enter__()
            except AttributeError as error:
                print("spent", error)


            @contextlib.contextmanager
            def counting(registry, name):
                registry.append("enter-" + name)
                try:
                    yield name
                finally:
                    registry.append("exit-" + name)


            registry = []


            @counting(registry, "x")
            def add(a, b=1, *, c=2):
                registry.append("body")
                return a + b + c


            print("sum", add(1), add(1, b=2, c=3))
            print("registry", registry)


            class Own:
                def __enter__(self):
                    return "own"

                def __exit__(self, *info):
                    return False


            class Bare:
                pass


            print("duck", isinstance(Own(), contextlib.AbstractContextManager), issubclass(Own, contextlib.AbstractContextManager), isinstance(Bare(), contextlib.AbstractContextManager))


            class Inherited(contextlib.AbstractContextManager):
                def __exit__(self, *info):
                    print("inherited-exit", info[0] is not None)
                    return False


            with Inherited() as entered:
                print("entered-self", type(entered).__name__)

            try:
                contextlib.AbstractContextManager()
            except TypeError as error:
                print("abstract-refused", error)


            class Closer:
                def __init__(self, name, fail=False):
                    self.name = name
                    self.fail = fail

                def close(self):
                    print("closing", self.name)
                    if self.fail:
                        raise RuntimeError("close failed")


            with contextlib.closing(Closer("ok")) as closed:
                print("inside", closed.name, type(closed).__name__)

            try:
                with contextlib.closing(Closer("bad", fail=True)):
                    print("inside bad")
            except RuntimeError as error:
                print("close-error", error)

            with contextlib.nullcontext(5) as value:
                print("null", value)

            with contextlib.nullcontext() as value:
                print("null-none", value)

            print("null-class", type(contextlib.nullcontext(5)).__name__, type(contextlib.nullcontext()).__name__, contextlib.nullcontext(9).enter_result)

            with contextlib.suppress(ValueError):
                raise ValueError("silent")
            print("suppressed")

            try:
                with contextlib.suppress(KeyError):
                    raise ValueError("not suppressed")
            except ValueError as error:
                print("escaped2", error)

            with contextlib.suppress(KeyError, IndexError):
                raise IndexError("quiet")
            print("suppress-multi")

            with contextlib.suppress():
                pass
            print("suppress-nothing")


            class Sub(ValueError):
                pass


            with contextlib.suppress(ValueError):
                raise Sub("child")
            print("suppress-subclass")

            try:
                with contextlib.suppress(42):
                    raise ValueError("x")
            except TypeError as error:
                print("suppress-bad", error)

            print("suppress-class", type(contextlib.suppress(ValueError)).__name__)


            def cm(name, swallow=False):
                class M:
                    def __enter__(self):
                        print("enter", name)
                        return name

                    def __exit__(self, *info):
                        print("exit", name, info[0] is not None)
                        return swallow

                return M()


            stack = contextlib.ExitStack()
            with stack as entered:
                print("same", entered is stack)
                stack.enter_context(cm("a"))
                stack.enter_context(cm("b"))
            print("stack-class", type(stack).__name__, [cls.__name__ for cls in type(stack).__mro__])

            stack = contextlib.ExitStack()
            with stack:
                stack.callback(print, "cb", 1)
                stack.callback(lambda a, b=2: print("kw", a, b), 5, b=9)

            stack = contextlib.ExitStack()
            try:
                with stack:
                    stack.callback(lambda: "ignored")
                    raise ValueError("boom")
            except ValueError as error:
                print("stack-raised", error)

            stack = contextlib.ExitStack()
            with stack:
                stack.push(cm("p", swallow=True))
                raise ValueError("swallowed-by-push")
            print("stack-suppressed")


            def raising_callback():
                print("raising-callback")
                raise KeyError("callback failed")


            stack = contextlib.ExitStack()
            stack.callback(raising_callback)
            stack.callback(print, "before")
            try:
                with stack:
                    pass
            except KeyError as error:
                print("callback-error", error)

            stack = contextlib.ExitStack()
            try:
                stack.enter_context(object())
            except TypeError as error:
                print("not-cm", error)


            @contextlib.contextmanager
            def failing_enter():
                print("failing-enter")
                raise ValueError("enter failed")
                yield


            stack = contextlib.ExitStack()
            try:
                with stack:
                    stack.enter_context(failing_enter())
            except ValueError as error:
                print("enter-error", error)

            order = []
            stack = contextlib.ExitStack()
            with stack:
                stack.callback(order.append, "a")
                with stack.pop_all() as moved:
                    moved.callback(order.append, "b")
            print("order", order)

            stack = contextlib.ExitStack()
            inner = stack.pop_all()
            print("pop-all", len(inner._exit_callbacks), len(stack._exit_callbacks))

            values = []
            stack = contextlib.ExitStack()
            stack.callback(values.append, 1)
            stack.close()
            print("closed", values)


            @contextlib.contextmanager
            def labelled(name):
                print("open", name)
                try:
                    yield name.upper()
                finally:
                    print("close", name)


            stack = contextlib.ExitStack()
            with stack:
                print("value", stack.enter_context(labelled("inner")))
            print("exitstack-docs", contextlib.ExitStack.__module__, contextlib.ExitStack.__qualname__, contextlib.closing.__module__)
            """
        );
        Assert.Equal(
            Lines(
                "doc Utilities for with-statement contexts.  See PEP 343.",
                "class-docs An abstract base class for context managers. A base class or mixin that enables context managers to work as decorators.",
                "manager-doc Helper for @contextmanager decorator.",
                "names ['AbstractContextManager', 'ContextDecorator', 'ExitStack', 'closing', 'contextmanager', 'nullcontext', 'suppress']",
                "abstract <class 'contextlib.AbstractContextManager'> frozenset({'__exit__'})",
                "meta ABCMeta type ABCMeta",
                "enter 3",
                "body 6",
                "exit 3",
                "helper managed __main__ function managed",
                "failing-enter",
                "caught boom",
                "finally",
                "same-escaped same",
                "no-yield generator didn't yield",
                "first 1",
                "extra-yield generator didn't stop",
                "keeps-first 1",
                "keeps generator didn't stop after throw()",
                "pep479 generator raised StopIteration",
                "manager-mro ['_GeneratorContextManager', '_GeneratorContextManagerBase', 'AbstractContextManager', 'ABC', 'ContextDecorator', 'object']",
                "manager-class contextlib _GeneratorContextManager",
                "recreate _GeneratorContextManager",
                "enter 4",
                "reused 8",
                "exit 4",
                "spent '_GeneratorContextManager' object has no attribute 'args'",
                "sum 4 6",
                "registry ['enter-x', 'body', 'exit-x', 'enter-x', 'body', 'exit-x']",
                "duck True True False",
                "entered-self Inherited",
                "inherited-exit False",
                "abstract-refused Can't instantiate abstract class AbstractContextManager without an implementation for abstract method '__exit__'",
                "inside ok Closer",
                "closing ok",
                "inside bad",
                "closing bad",
                "close-error close failed",
                "null 5",
                "null-none None",
                "null-class nullcontext nullcontext 9",
                "suppressed",
                "escaped2 not suppressed",
                "suppress-multi",
                "suppress-nothing",
                "suppress-subclass",
                "suppress-bad issubclass() arg 2 must be a class, a tuple of classes, or a union",
                "suppress-class suppress",
                "same True",
                "enter a",
                "enter b",
                "exit b False",
                "exit a False",
                "stack-class ExitStack ['ExitStack', '_BaseExitStack', 'AbstractContextManager', 'ABC', 'object']",
                "kw 5 9",
                "cb 1",
                "stack-raised boom",
                "exit p True",
                "stack-suppressed",
                "before",
                "raising-callback",
                "callback-error 'callback failed'",
                "not-cm 'builtins.object' object does not support the context manager protocol",
                "failing-enter",
                "enter-error enter failed",
                "order ['b', 'a']",
                "pop-all 0 0",
                "closed [1]",
                "open inner",
                "value INNER",
                "close inner",
                "exitstack-docs contextlib ExitStack contextlib"
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
