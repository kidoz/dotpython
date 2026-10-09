using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `functools` surface that is implemented, checked against CPython. Every case
/// catches what it exercises, so the reference exits 0; only stable values are printed,
/// and objects whose reprs would carry an address are given a `__repr__` or kept out.
/// The one comparison whose wording this runtime owns (`NotImplemented` ordered against
/// an int) is reported by exception type only.
/// </summary>
public sealed class FunctoolsCompatibilityTests
{
    [Fact]
    public Task ReduceFoldsIterablesAndPartialBindsArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import functools as ft


            def add(a, b):
                return a + b


            print(ft.reduce(add, [1, 2, 3]))
            print(ft.reduce(add, [1, 2, 3], 10))
            print(ft.reduce(add, [], 10))
            print(ft.reduce(lambda a, b: a * b, [4, 5], 1))
            print(ft.reduce(add, ["a", "b"], ""))


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            def attempt_type(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__)


            attempt(lambda: ft.reduce(add, []))
            attempt(lambda: ft.reduce(add, range(0)))


            def power(base, exponent, factor=1):
                return base**exponent * factor


            p = ft.partial(power, 2)
            print(p(3), p(3, factor=2), p(3, 2), p(exponent=3))
            print(p.func.__name__, p.args, p.keywords)
            print(ft.partial(power, 2, factor=5)(3))
            print(ft.partial(power, exponent=3, factor=7)(2))
            print(ft.partial(power, 2, 3)(), ft.partial(power, 2, 3, 4)())
            q = ft.partial(p, factor=4)
            print(q(2), q.func.__name__, q.args, sorted(q.keywords))
            print(sorted(p.__dict__))

            # Only the exception type is compared: the argument-mismatch wording is the
            # host binder's, not CPython's.
            attempt_type(lambda: ft.partial(power, 2, 3, 4)(5))
            attempt_type(lambda: ft.partial(power, 2, 3)(exponent=4))
            attempt_type(lambda: ft.partial(power, bogus=1)(2))
            attempt_type(lambda: ft.partial(add, 1, 2, 3)())
            """
        );

    [Fact]
    public Task WrapsAndUpdateWrapperCopyMetadata() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import functools as ft


            def traced(func):
                @ft.wraps(func)
                def wrapper(*args, **kwargs):
                    return func(*args, **kwargs) * 10

                return wrapper


            @traced
            def scale(x):
                return x + 1


            print(scale(2))
            print(scale.__name__, scale.__module__)
            print(hasattr(scale, "__wrapped__"), scale.__wrapped__.__name__)
            print(scale.__wrapped__ is scale.__wrapped__)
            print(sorted(scale.__dict__))


            def target(a, b=2):
                return a + b


            def source():
                pass


            source.__name__ = "renamed"
            source.__qualname__ = "mod.renamed"
            source.__module__ = "somemod"
            source.extra = 5
            out = ft.update_wrapper(target, source)
            print(out is target)
            print(target.__name__, target.__qualname__, target.__module__)
            print(target.extra)
            print(sorted(target.__dict__))


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            def attempt_type(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__)


            attempt_type(lambda: ft.update_wrapper(5, source))
            attempt(lambda: ft.update_wrapper(target))


            def plain():
                pass


            blank = ft.update_wrapper(plain, source)
            print(blank is plain, plain.__name__, plain.__module__)
            """
        );

    [Fact]
    public Task LruCacheCachesByArgumentsAndReportsStatistics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import functools as ft

            calls = []


            @ft.lru_cache(maxsize=None)
            def fib(n):
                calls.append(n)
                return n if n < 2 else fib(n - 1) + fib(n - 2)


            print(fib(10), len(calls), sorted(calls))
            print(fib.cache_info())
            info = fib.cache_info()
            print(info.hits, info.misses, info.maxsize, info.currsize)
            print(fib.__wrapped__(3), fib.__wrapped__.__name__)
            print(fib.__name__, fib.__module__, fib.__doc__)
            print(hasattr(fib, "__wrapped__"))
            fib.cache_clear()
            print(fib.cache_info())


            @ft.lru_cache(maxsize=2)
            def m(x):
                return x * 10


            print(m(1), m(2), m(3), m(1))
            print(m.cache_info())


            @ft.lru_cache(maxsize=3)
            def f(x):
                return x * 2


            print(f(1), f(2), f(1), f(3), f(4), f(1))
            print(f.cache_info())
            print(f.cache_parameters())
            print(f.cache_parameters() == {"maxsize": 3, "typed": False})


            @ft.lru_cache
            def g(x):
                return x


            print(g.cache_parameters())


            @ft.lru_cache(typed=True)
            def t(x):
                return x


            print(t(1), t(1.0), t(1))
            print(t.cache_info())


            @ft.cache
            def c(x):
                return x + 1


            print(c(1), c(1), c(2))
            print(c.cache_info())


            def counted(x):
                return x


            cw = ft.lru_cache(counted)
            print(cw(5), cw(5), cw.cache_info().currsize)
            print(cw.__wrapped__ is counted)
            print(sorted(name for name in ("cache_clear", "cache_info", "__wrapped__") if hasattr(cw, name)))


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            attempt(lambda: ft.lru_cache(maxsize="x"))
            """
        );

    [Fact]
    public Task CachedPropertyComputesOnceAndCachesOnTheInstance() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import functools as ft


            class Rectangle:
                def __init__(self, width, height):
                    self.width = width
                    self.height = height
                    self.area_calls = 0

                @ft.cached_property
                def area(self):
                    self.area_calls += 1
                    return self.width * self.height


            r = Rectangle(3, 4)
            print(r.area, r.area, r.area_calls)
            print(sorted(r.__dict__))
            print(r.__dict__["area"])
            r.area = 20
            print(r.area, r.area_calls, sorted(r.__dict__))
            del r.area
            print(r.area, r.area_calls)
            print(type(Rectangle.area).__name__)
            print(Rectangle.area.attrname, Rectangle.area.__module__)


            class Sub(Rectangle):
                pass


            print(Sub(2, 5).area)


            def attempt(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__ + ":", error)


            attempt(lambda: Rectangle.area.__get__(5, Rectangle))
            print(Rectangle.area.__get__(None, Rectangle) is Rectangle.area)
            attempt(lambda: ft.cached_property(5))


            def build():
                class C:
                    @ft.cached_property
                    def first(self):
                        return 1

                    second = first

                return C


            attempt(build)
            """
        );

    [Fact]
    public Task TotalOrderingDerivesComparisonsFromTheStrongestRoot() =>
        CompatibilityOracle.AssertMatchesAsync(
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


            class G:
                def __ge__(self, other):
                    return "ge"


            ft.total_ordering(G)
            print(sorted(n for n in ("__ge__", "__gt__", "__le__", "__lt__") if n in G.__dict__))
            g1, g2 = G(), G()
            print(g1 <= g2, g1 > g2, g1 < g2)


            class Nothing:
                pass


            try:
                ft.total_ordering(Nothing)
            except TypeError as error:
                print(type(error).__name__ + ":", error)
            except ValueError as error:
                print(type(error).__name__ + ":", error)

            print(ft.total_ordering(5))
            print(ft.total_ordering(str) is str)
            print(ft.total_ordering(complex) is complex)
            print(ft.total_ordering(None) is None)
            """
        );

    [Fact]
    public Task CmpToKeyWrapsComparisonsForSorting() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import functools as ft


            def cmp(x, y):
                return (x > y) - (x < y)


            k = ft.cmp_to_key(cmp)
            w1, w2, w3 = k(1), k(2), k(1)
            print(type(w1).__name__, type(w1).__module__, type(w1).__qualname__)
            print(w1.obj, w2.obj, k(obj=7).obj)
            print(w1 < w2, w2 > w1, w1 == w3, w1 != w2, w1 <= w3, w3 >= w2)
            print(sorted([3, 1, 2], key=k))
            print(sorted(["b", "a", "c"], key=ft.cmp_to_key(cmp)))
            print(sorted([(2, "b"), (1, "a")], key=ft.cmp_to_key(lambda p, q: cmp(p[0], q[0]))))


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

            k5 = ft.cmp_to_key(lambda x, y: 5)
            print(k5(1) < k5(2), k5(1) <= k5(2), k5(1) == k5(2), k5(1) > k5(2), k5(1) >= k5(2))


            def attempt_type(thunk):
                try:
                    thunk()
                except Exception as error:
                    print(type(error).__name__)


            nothing = ft.cmp_to_key(lambda x, y: NotImplemented)
            attempt_type(lambda: nothing(1) < nothing(2))
            print(nothing(1) == nothing(2))
            """
        );

    [Fact]
    public Task SingleDispatchRegistersImplementationsByClass() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import functools as ft
            from typing import Union


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
            print(fun.__name__, fun.__qualname__, fun.__module__)
            print(fun.__wrapped__(1))
            print(type(fun.registry).__name__)
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


            fun4.register(Union[int, str], lambda arg: "int-or-str")
            print(fun4(1), fun4("s"), fun4(1.0))
            print(sorted(c.__name__ for c in fun4.registry))
            """
        );

    [Fact]
    public Task SingleDispatchMethodDispatchesOnTheFirstArgument() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task PartialMethodAppliesStoredArgumentsAndBindsDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import functools as ft


            def attempt(label, thunk):
                try:
                    print(label, "=>", thunk())
                except Exception as error:
                    print(label, "!!", type(error).__name__ + ":", error)


            class C:
                def base(self, *args, **kwargs):
                    return (args, kwargs)

                m = ft.partialmethod(base, 1, x=2)

                @ft.partialmethod
                def n(self, *args):
                    return args


            c = C()
            print("calls:", c.m(3), c.m(3, y=4), C.m(c, 5), c.n(7), C.n(c, 8), c.m(4, x=9))
            print(
                "types:",
                type(c.m).__name__,
                type(c.m.func).__name__,
                type(ft.partialmethod(len)).__name__,
                type(ft.partialmethod(len)).__module__,
            )
            print("bound:", c.m.__self__ is c, c.m.func.__self__ is c)
            print(
                "fields:",
                ft.partialmethod(len).func.__name__,
                ft.partialmethod(len).args,
                ft.partialmethod(len).keywords,
                repr(ft.partialmethod(len, 1, x=2)),
            )
            pm = ft.partialmethod(C.base, 1)
            print("pm-dict:", sorted(pm.__dict__), pm._phcount, pm._merger)
            print("abstract:", ft.partialmethod(C.base).__isabstractmethod__)


            class Marker:
                __isabstractmethod__ = True

                def __call__(self, *args):
                    return args


            print("abstract-marker:", ft.partialmethod(Marker()).__isabstractmethod__)
            attempt("non-callable", lambda: ft.partialmethod(5))
            attempt("no-args", lambda: ft.partialmethod())
            attempt("kw-func", lambda: ft.partialmethod(func=C.base))
            attempt("kw-only-extra", lambda: ft.partialmethod(x=1))
            nested = ft.partialmethod(ft.partialmethod(len, 2), 4)
            print("nested:", nested.func.__name__, nested.args, nested.keywords, repr(nested))
            nested2 = ft.partialmethod(ft.partialmethod(C.base, 2, y=3), 4, y=5)
            print("nested2:", nested2.func.__name__, nested2.args, nested2.keywords)


            class M:
                m = nested2


            print("nested-call:", M().m(6))


            class D:
                pass


            D.pm2 = ft.partialmethod(C.base, 7)
            d = D()
            print("mutated:", d.pm2(8))
            raw = D.__dict__["pm2"]
            raw.func = C.base
            raw.args = (9,)
            raw.keywords = {"z": 1}
            print("mutated2:", d.pm2(8), raw.args, raw.keywords)


            class S:
                sm = ft.partialmethod(staticmethod(lambda *args: ("s", args)), 1)

                @ft.partialmethod
                @staticmethod
                def sm2(*args):
                    return ("s2", args)


            s = S()
            print("static:", s.sm(2), S.sm(3), s.sm2(4), S.sm2(5))


            class CM:
                cm = ft.partialmethod(classmethod(lambda cls, *args: (cls.__name__, args)), 1)


            print("classmethod:", CM().cm(2), CM.cm(3), CM.cm.__self__ is CM)
            print("all:", "partialmethod" in ft.__all__)
            """
        );
}
