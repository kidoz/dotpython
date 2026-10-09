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
            def shape(value):
                text = repr(value)
                result = ''
                while True:
                    head, sep, tail = text.partition(' at 0x')
                    if not sep:
                        return result + text
                    cut = 0
                    while cut < len(tail) and tail[cut] in '0123456789abcdef':
                        cut += 1
                    result = result + head + ' at 0xADDR'
                    text = tail[cut:]
            print(p.func is f)
            print(p.args)
            print(p.keywords)
            print(shape(p))
            print(shape(ft.partial(f, 1, 2, 3, 4, x=5)))
            print(shape(ft.partial(f, b=2, c=9)))
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
                "functools.partial(<function f at 0xADDR>, 1)",
                "functools.partial(<function f at 0xADDR>, 1, 2, 3, 4, x=5)",
                "functools.partial(<function f at 0xADDR>, b=2, c=9)"
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

            def shape(value):
                text = repr(value)
                result = ''
                while True:
                    head, sep, tail = text.partition(' at 0x')
                    if not sep:
                        return result + text
                    cut = 0
                    while cut < len(tail) and tail[cut] in '0123456789abcdef':
                        cut += 1
                    result = result + head + ' at 0xADDR'
                    text = tail[cut:]
            inner = ft.partial(f, 1)
            outer = ft.partial(inner, 2)
            print(shape(outer))
            print(outer())
            print(outer.func is f)
            print(outer.args)
            print(shape(ft.partial(ft.partial(ft.partial(f, 1), 2), 3)))
            """
        );

        Assert.Equal(
            Lines(
                "functools.partial(<function f at 0xADDR>, 1, 2)",
                "(1, 2, 3)",
                "True",
                "(1, 2)",
                "functools.partial(<function f at 0xADDR>, 1, 2, 3)"
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
            def shape(value):
                text = repr(value)
                result = ''
                while True:
                    head, sep, tail = text.partition(' at 0x')
                    if not sep:
                        return result + text
                    cut = 0
                    while cut < len(tail) and tail[cut] in '0123456789abcdef':
                        cut += 1
                    result = result + head + ' at 0xADDR'
                    text = tail[cut:]
            print(wrapped.__wrapped__ is myfunc)
            print(shape(wrapped.__dict__))
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
                "{'custom': 'attr', '__wrapped__': <function myfunc at 0xADDR>}",
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

    // -------------------------------------------------------------------------
    // lru_cache / cache
    // -------------------------------------------------------------------------

    [Fact]
    public void LruCacheCachesOnArgumentsAndCountsHits()
    {
        var output = Run(
            """
            import functools as ft

            calls = []

            @ft.lru_cache
            def f(x):
                calls.append(x)
                return x * 2

            print(f(1), f(2), f(1))
            print(f.cache_info())
            print(calls)
            print(f.cache_parameters())

            @ft.lru_cache(maxsize=2)
            def g(x):
                return x

            g(1)
            g(2)
            g(3)
            g(2)
            print(g.cache_info())

            @ft.lru_cache(maxsize=0)
            def z(x):
                return x

            z(1)
            z(1)
            print(z.cache_info())

            @ft.lru_cache(typed=True)
            def t(x):
                return x

            t(1)
            t(1.0)
            t(True)
            print(t.cache_info())

            @ft.lru_cache
            def m(x, y=0):
                return x

            m(1)
            m(1, 1)
            m(1, y=1)
            m(x=1)
            print(m.cache_info())

            @ft.cache
            def c(x):
                return x

            c(1)
            c(1)
            print(c.cache_info(), c.cache_parameters())
            """
        );

        Assert.Equal(
            Lines(
                "2 4 2",
                "CacheInfo(hits=1, misses=2, maxsize=128, currsize=2)",
                "[1, 2]",
                "{'maxsize': 128, 'typed': False}",
                "CacheInfo(hits=1, misses=3, maxsize=2, currsize=2)",
                "CacheInfo(hits=0, misses=2, maxsize=0, currsize=0)",
                "CacheInfo(hits=0, misses=3, maxsize=128, currsize=3)",
                "CacheInfo(hits=0, misses=4, maxsize=128, currsize=4)",
                "CacheInfo(hits=1, misses=1, maxsize=None, currsize=1)"
                    + " {'maxsize': None, 'typed': False}"
            ),
            output
        );
    }

    [Fact]
    public void LruCacheExposesTheWrapperAndCacheInfoSurface()
    {
        var output = Run(
            """
            import functools as ft

            def plain(x):
                return x * 3

            wrapped = ft.lru_cache(maxsize=4, typed=True)(plain)
            print(wrapped(2), wrapped.cache_info())
            wrapped.custom = "tag"
            print(wrapped.custom, wrapped.__wrapped__ is plain)
            print(wrapped.__name__, wrapped.__qualname__, wrapped.__module__)
            print(type(wrapped).__name__)
            print(sorted(wrapped.__dict__))
            print(ft.lru_cache.__name__)

            info = wrapped.cache_info()
            print(info, type(info) is ft._CacheInfo, ft._CacheInfo._fields)
            print(len(info), info[0], info[-1], list(info))
            print(info == wrapped.cache_info(), info == (0, 1, 4, 1))
            print(info._asdict())
            print(info._replace(hits=9))
            print(info.count(1), info.index(1, 0))
            print(wrapped.cache_clear(), wrapped.cache_info())
            """
        );

        Assert.Equal(
            Lines(
                "6 CacheInfo(hits=0, misses=1, maxsize=4, currsize=1)",
                "tag True",
                "plain plain __main__",
                "_lru_cache_wrapper",
                "['__annotate__', '__doc__', '__module__', '__name__', '__qualname__',"
                    + " '__type_params__', '__wrapped__', 'cache_parameters', 'custom']",
                "lru_cache",
                "CacheInfo(hits=0, misses=1, maxsize=4, currsize=1) True"
                    + " ('hits', 'misses', 'maxsize', 'currsize')",
                "4 0 1 [0, 1, 4, 1]",
                "True True",
                "{'hits': 0, 'misses': 1, 'maxsize': 4, 'currsize': 1}",
                "CacheInfo(hits=9, misses=1, maxsize=4, currsize=1)",
                "2 1",
                "None CacheInfo(hits=0, misses=0, maxsize=4, currsize=0)"
            ),
            output
        );
    }

    [Fact]
    public void LruCacheDecoratesMethodsAndReportsItsErrors()
    {
        var output = Run(
            """
            import functools as ft

            class Widget:
                @ft.lru_cache(maxsize=2)
                def compute(self, x):
                    return ("computed", x)

            first = Widget()
            second = Widget()
            print(type(first.compute).__name__)
            print(first.compute(1), first.compute(1), second.compute(1))
            print(Widget.compute.cache_info())
            print(Widget.compute.__wrapped__(first, 2))

            @ft.lru_cache()
            def plain(x):
                return x * 3

            print(plain(2))

            def attempt(action):
                try:
                    action()
                except TypeError as error:
                    print(type(error).__name__ + ":", error)

            attempt(lambda: ft.lru_cache("x"))
            attempt(lambda: ft.lru_cache()())
            attempt(lambda: plain([]))
            attempt(lambda: Widget.compute.cache_info(1))
            attempt(lambda: Widget.compute.cache_clear(1))
            attempt(lambda: Widget.compute.cache_info().count(1, 2))
            attempt(lambda: ft._CacheInfo(1))
            attempt(lambda: Widget.compute.cache_info()._replace(bogus=1))
            """
        );

        Assert.Equal(
            Lines(
                "method",
                "('computed', 1) ('computed', 1) ('computed', 1)",
                "CacheInfo(hits=1, misses=2, maxsize=2, currsize=2)",
                "('computed', 2)",
                "6",
                "TypeError: Expected first argument to be an integer, a callable, or None",
                "TypeError: lru_cache.<locals>.decorating_function() missing 1 required"
                    + " positional argument: 'user_function'",
                "TypeError: unhashable type: 'list'",
                "TypeError: _lru_cache_wrapper.cache_info() takes no arguments (1 given)",
                "TypeError: _lru_cache_wrapper.cache_clear() takes no arguments (1 given)",
                "TypeError: tuple.count() takes exactly one argument (2 given)",
                "TypeError: CacheInfo.__new__() missing 3 required positional arguments:"
                    + " 'misses', 'maxsize', and 'currsize'",
                "TypeError: Got unexpected field names: ['bogus']"
            ),
            output
        );
    }

    // -------------------------------------------------------------------------
    // cached_property
    // -------------------------------------------------------------------------

    [Fact]
    public void CachedPropertyComputesOnceAndStaysWritable()
    {
        var output = Run(
            """
            import functools as ft

            class Box:
                def __init__(self):
                    self.calls = 0

                @ft.cached_property
                def value(self):
                    self.calls += 1
                    return self.calls * 10

            box = Box()
            print(box.value, box.value, box.calls)
            print(Box.__dict__["value"].attrname)
            print(Box.__dict__["value"].func.__name__)
            print(type(Box.value).__name__)
            print(type(Box.__dict__["value"]).__name__)
            del box.value
            print(box.value, box.calls)
            box.value = 99
            print(box.value, box.calls)
            print(ft.cached_property(len).attrname)
            """
        );

        Assert.Equal(
            Lines(
                "10 10 1",
                "value",
                "value",
                "cached_property",
                "cached_property",
                "20 2",
                "99 2",
                "None"
            ),
            output
        );
    }

    // -------------------------------------------------------------------------
    // total_ordering
    // -------------------------------------------------------------------------

    [Fact]
    public void TotalOrderingDerivesTheMissingComparisons()
    {
        var output = Run(
            """
            import functools as ft


            class L:
                def __lt__(self, other):
                    return "lt"


            ft.total_ordering(L)
            print(sorted(n for n in ("__ge__", "__gt__", "__le__", "__lt__") if n in L.__dict__))
            a, b = L(), L()
            print(a < b, a > b, a <= b, a >= b)


            class Both:
                def __lt__(self, other):
                    return False

                def __gt__(self, other):
                    return False


            ft.total_ordering(Both)
            print(sorted(n for n in ("__ge__", "__gt__", "__le__", "__lt__") if n in Both.__dict__))


            class Nothing:
                pass


            try:
                ft.total_ordering(Nothing)
            except ValueError as error:
                print(type(error).__name__ + ":", error)
            except TypeError as error:
                print(type(error).__name__ + ":", error)

            print(ft.total_ordering(5))
            print(ft.total_ordering(str) is str)
            print(ft.total_ordering(complex) is complex)
            """
        );

        Assert.Equal(
            Lines(
                "['__ge__', '__gt__', '__le__', '__lt__']",
                "lt False lt False",
                "['__ge__', '__gt__', '__le__', '__lt__']",
                "ValueError: must define at least one ordering operation: < > <= >=",
                "5",
                "True",
                "True"
            ),
            output
        );
    }

    // -------------------------------------------------------------------------
    // cmp_to_key
    // -------------------------------------------------------------------------

    [Fact]
    public void CmpToKeyBuildsSortableKeys()
    {
        var output = Run(
            """
            import functools as ft


            def cmp(x, y):
                return (x > y) - (x < y)


            k = ft.cmp_to_key(cmp)
            w1, w2, w3 = k(1), k(2), k(1)
            print(type(w1).__name__, type(w1).__module__)
            print(w1.obj, w2.obj, k(obj=7).obj)
            print(w1 < w2, w2 > w1, w1 == w3, w1 != w2, w1 <= w3, w3 >= w2)
            print(sorted([3, 1, 2], key=k))
            print(sorted(["b", "a", "c"], key=ft.cmp_to_key(cmp)))


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            attempt(lambda: ft.cmp_to_key())
            attempt(lambda: ft.cmp_to_key(cmp, other=1))
            attempt(lambda: ft.cmp_to_key(mycmp=cmp, other=1))
            attempt(lambda: k())
            attempt(lambda: k(1, 2))
            attempt(lambda: k(bogus=1))
            attempt(lambda: k(1) < 5)

            kn = ft.cmp_to_key(cmp)
            attempt(lambda: kn < kn)
            attempt(lambda: kn == kn)


            def truthy(x, y):
                return 5


            k5 = ft.cmp_to_key(truthy)
            print(k5(1) < k5(2), k5(1) <= k5(2), k5(1) == k5(2), k5(1) > k5(2))


            def nothing(x, y):
                return NotImplemented


            def attempt_type(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__)


            attempt_type(lambda: ft.cmp_to_key(nothing)(1) < ft.cmp_to_key(nothing)(2))
            print(ft.cmp_to_key(nothing)(1) == ft.cmp_to_key(nothing)(2))
            """
        );

        Assert.Equal(
            Lines(
                "KeyWrapper functools",
                "1 2 7",
                "True True True True True False",
                "[1, 2, 3]",
                "['a', 'b', 'c']",
                "TypeError: cmp_to_key() missing required argument 'mycmp' (pos 1)",
                "TypeError: cmp_to_key() takes at most 1 argument (2 given)",
                "TypeError: cmp_to_key() takes at most 1 keyword argument (2 given)",
                "TypeError: K() missing required argument 'obj' (pos 1)",
                "TypeError: K() takes at most 1 argument (2 given)",
                "TypeError: K() missing required argument 'obj' (pos 1)",
                "TypeError: other argument must be K instance",
                "AttributeError: object",
                "AttributeError: object",
                "False False False True",
                "TypeError",
                "False"
            ),
            output
        );
    }

    // -------------------------------------------------------------------------
    // singledispatch
    // -------------------------------------------------------------------------

    [Fact]
    public void SingleDispatchRegistersByClassAndAnnotation()
    {
        var output = Run(
            """
            import functools as ft


            @ft.singledispatch
            def fun(arg, verbose=False):
                return "base"


            @fun.register
            def _(arg: int, verbose=False):
                return "int"


            @fun.register(complex)
            def _(arg, verbose=False):
                return "complex"


            @fun.register(float)
            @fun.register(complex)
            def _(arg, verbose=False):
                return "floatcomplex"


            print(fun(1), fun(1.0), fun(1 + 2j), fun("x"), fun(1, verbose=True))
            print(fun.__name__, fun.__qualname__)
            print(fun.__wrapped__(1))
            print(sorted(c.__name__ for c in fun.registry))
            print(fun.registry[object](0), fun.dispatch(str)(99))


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            attempt(lambda: fun())
            attempt(lambda: fun.register(3))
            attempt(lambda: fun.dispatch(3))


            class Base:
                pass


            class Sub(Base):
                pass


            @ft.singledispatch
            def fun3(arg):
                return "base"


            fun3.register(Base, lambda arg: "base-impl")
            print(fun3(Sub()), fun3(Base()), fun3(1))


            @ft.singledispatch
            def fun4(arg):
                return "base"


            fun4.register(int, lambda arg: "int")
            print(fun4(1), fun4("x"))
            """
        );

        Assert.Equal(
            Lines(
                "int floatcomplex floatcomplex base int",
                "fun fun",
                "base",
                "['complex', 'float', 'int', 'object']",
                "base base",
                "TypeError: fun requires at least 1 positional argument",
                "TypeError: Invalid first argument to `register()`: 3. Use either `@register(some_class)` or plain `@register` on an annotated function.",
                "TypeError: cannot create weak reference to 'int' object",
                "base-impl base-impl base",
                "int base"
            ),
            output
        );
    }

    // -------------------------------------------------------------------------
    // singledispatchmethod
    // -------------------------------------------------------------------------

    [Fact]
    public void SingleDispatchMethodBindsToInstances()
    {
        var output = Run(
            """
            import functools as ft


            class C:
                def __repr__(self):
                    return "<C>"

                @ft.singledispatchmethod
                def meth(self, arg):
                    return "base"

                @meth.register
                def _(self, arg: int):
                    return "int"

                @meth.register(complex)
                def _(self, arg):
                    return "complex"


            c = C()
            print(c.meth(1), c.meth(1.0), c.meth(1 + 0j))
            print(repr(C.meth), repr(c.meth))
            print(c.meth.__name__, c.meth.__qualname__, c.meth.__wrapped__(c, 1))
            print(C.meth(c, 1))


            class D(C):
                pass


            print(D().meth(1))
            print(isinstance(ft.singledispatchmethod(len), ft.singledispatchmethod))


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            attempt(lambda: c.meth())
            attempt(lambda: ft.singledispatchmethod(5))
            attempt(lambda: c.meth.__isabstractmethod__)
            """
        );

        Assert.Equal(
            Lines(
                "int base complex",
                "<single dispatch method C.meth> <bound single dispatch method C.meth of <C>>",
                "meth C.meth base",
                "base",
                "int",
                "True",
                "TypeError: meth requires at least 1 positional argument",
                "TypeError: 5 is not callable or a descriptor",
                "AttributeError: 'function' object has no attribute '__isabstractmethod__'"
            ),
            output
        );
    }

    // -------------------------------------------------------------------------
    // partialmethod
    // -------------------------------------------------------------------------

    [Fact]
    public void PartialMethodAppliesStoredArgumentsAndKeywords()
    {
        var output = Run(
            """
            import functools as ft


            class C:
                def base(self, *args, **kwargs):
                    return (args, kwargs)

                m = ft.partialmethod(base, 1, x=2)

                @ft.partialmethod
                def n(self, *args):
                    return args


            c = C()
            print(c.m(3), c.m(3, y=4), C.m(c, 5), c.n(7), C.n(c, 8), c.m(4, x=9))
            print(
                type(c.m).__name__,
                type(c.m.func).__name__,
                c.m.__self__ is c,
                c.m.func.__self__ is c,
            )
            print(
                ft.partialmethod(len).func.__name__,
                ft.partialmethod(len).args,
                ft.partialmethod(len).keywords,
            )
            print(repr(ft.partialmethod(len, 1, x=2)))
            print(
                sorted(ft.partialmethod(len).__dict__),
                ft.partialmethod(len)._phcount,
                ft.partialmethod(len)._merger,
            )
            """
        );

        Assert.Equal(
            Lines(
                "((1, 3), {'x': 2}) ((1, 3), {'x': 2, 'y': 4}) ((1, 5), {'x': 2}) (7,) (8,) ((1, 4), {'x': 9})",
                "partial method True True",
                "len () {}",
                "functools.partialmethod(<built-in function len>, 1, x=2)",
                "['_merger', '_phcount', 'args', 'func', 'keywords'] 0 None"
            ),
            output
        );
    }

    [Fact]
    public void PartialMethodFlattensNestingWrapsDescriptorsAndMutates()
    {
        var output = Run(
            """
            import functools as ft


            def base(*args, **kwargs):
                return (args, kwargs)


            nested = ft.partialmethod(ft.partialmethod(base, 2, y=3), 4, y=5)
            print(nested.func.__name__, nested.args, nested.keywords)


            class M:
                m = nested

                def __repr__(self):
                    return "<M>"


            print(M().m(6))


            class S:
                sm = ft.partialmethod(staticmethod(lambda *args: ("s", args)), 1)

                @ft.partialmethod
                @staticmethod
                def sm2(*args):
                    return ("s2", args)


            s = S()
            print(s.sm(2), S.sm(3), s.sm2(4), S.sm2(5))


            class CM:
                cm = ft.partialmethod(classmethod(lambda cls, *args: (cls.__name__, args)), 1)


            print(CM().cm(2), CM.cm(3), CM.cm.__self__ is CM)


            class D:
                pm = ft.partialmethod(base, 7)

                def __repr__(self):
                    return "<D>"


            print(D().pm(8))
            raw = D.__dict__["pm"]
            raw.func = base
            raw.args = (9,)
            raw.keywords = {"z": 1}
            print(D().pm(8), raw.args, raw.keywords)


            class Marker:
                __isabstractmethod__ = True

                def __call__(self, *args):
                    return args


            print(
                ft.partialmethod(Marker()).__isabstractmethod__,
                ft.partialmethod(len).__isabstractmethod__,
            )


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            attempt(lambda: ft.partialmethod(5))
            attempt(lambda: ft.partialmethod())
            attempt(lambda: ft.partialmethod(func=base))
            """
        );

        Assert.Equal(
            Lines(
                "base (2, 4) {'y': 5}",
                "((<M>, 2, 4, 6), {'y': 5})",
                "('s', (1, 2)) ('s', (1, 3)) ('s2', (4,)) ('s2', (5,))",
                "('CM', (1, 2)) ('CM', (1, 3)) True",
                "((<D>, 7, 8), {})",
                "((<D>, 9, 8), {'z': 1}) (9,) {'z': 1}",
                "True False",
                "TypeError: the first argument 5 must be a callable or a descriptor",
                "TypeError: _partial_new() missing 1 required positional argument: 'func'",
                "TypeError: _partial_new() missing 1 required positional argument: 'func'"
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
