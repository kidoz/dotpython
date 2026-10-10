using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ContextLibCompatibilityTests
{
    [Fact]
    public Task ContextLibSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
