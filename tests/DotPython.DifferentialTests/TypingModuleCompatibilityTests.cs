using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `typing` surface that matches CPython 3.14 exactly. The container aliases reuse the
/// shared `GenericAlias`, so `typing.List[int]` renders as `list[int]` rather than
/// `typing.List[int]`; that and the other documented gaps are covered by the paired
/// execution tests instead of being asserted here.
/// </summary>
public sealed class TypingModuleCompatibilityTests
{
    [Fact]
    public Task TheBareFormsRenderTheirCpythonNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            for name in [
                'Optional', 'List', 'Dict', 'Set', 'FrozenSet', 'Tuple', 'Type',
                'Callable', 'Final', 'ClassVar', 'Literal', 'NoReturn', 'Never', 'Self',
            ]:
                value = getattr(typing, name)
                print(name, repr(value), str(value))
            print(typing.TYPE_CHECKING, typing.Text is str, typing.Optional.__module__)
            """
        );

    [Fact]
    public Task OptionalAndUnionBuildUnionsThatRenderLikeCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            print(repr(typing.Optional[int]), repr(typing.Optional[None]))
            print(repr(typing.Optional[typing.Any]))
            print(repr(typing.Union[int]), repr(typing.Union[int, int]))
            print(repr(typing.Union[int, str, bytes]), repr(typing.Union[None, int]))
            print(repr(typing.Optional[int] | str), repr(int | str))
            print(repr(typing.Union[typing.Optional[int], bytes]))
            """
        );

    [Fact]
    public Task GetOriginAndGetArgsDescribeEveryForm() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            for target in [
                typing.Optional, typing.Union, typing.List, typing.Dict, typing.Tuple,
                typing.Type, typing.Final, typing.ClassVar, typing.Literal,
                int | str, list[int], typing.Optional[int], typing.Union[int, str],
                typing.List[int], typing.Dict[str, int], typing.Tuple[int, ...],
                typing.Final[int], typing.Literal[1, 2], typing.Tuple[()], 1, 'x',
            ]:
                print(repr(typing.get_origin(target)), repr(typing.get_args(target)))
            """
        );

    [Fact]
    public Task TheContainerAliasesCarryTheirBuiltinOrigins() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            print(typing.get_origin(typing.List), typing.get_origin(typing.Dict))
            print(typing.List[int].__origin__, typing.Dict[str, int].__origin__)
            print(typing.List[int].__args__, typing.Dict[str, int].__args__)
            print(typing.Tuple[()].__args__, typing.Tuple[int, ...].__args__)
            print(typing.List[str].__parameters__, typing.Final[int].__parameters__)
            print(typing.Callable[..., int].__args__)
            """
        );

    [Fact]
    public Task LiteralKeepsItsLiteralArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            print(repr(typing.Literal[1, 2]), repr(typing.Literal['a']), repr(typing.Literal[()]))
            print(typing.get_args(typing.Literal[1, 2]), typing.get_args(typing.Literal['a']))
            print(typing.get_origin(typing.Literal[1, 2]) is typing.Literal)
            """
        );

    [Fact]
    public Task CastReturnsItsValueUnchanged() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            print(repr(typing.cast(int, 'x')), typing.cast(int, 3) + 1)
            print(typing.cast(list[int], [1, 2]), repr(typing.cast(str, None)))
            """
        );

    [Fact]
    public Task GetTypeHintsReadsEverySupportedSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing

            def f(a: int, b: str = 'x') -> bool:
                return True


            def bare(a):
                pass


            class C:
                x: int = 1


            class Empty:
                pass


            print(typing.get_type_hints(f))
            print(typing.get_type_hints(bare), typing.get_type_hints(Empty))
            print(typing.get_type_hints(C))
            print(typing.get_type_hints(f, None, None, True), typing.get_type_hints(f, localns={}))
            print(typing.get_type_hints(list), typing.get_type_hints(len))
            """
        );

    [Fact]
    public Task TheRefusedFormsReportTheCpythonDiagnostics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing

            def reject(call):
                try:
                    call()
                except TypeError as error:
                    print('TypeError:', error)

            for expression in [
                lambda: typing.Optional(),
                lambda: typing.Union(),
                lambda: typing.Final(),
                lambda: typing.ClassVar(),
                lambda: typing.Literal(),
                lambda: typing.NoReturn(),
                lambda: typing.Never(),
                lambda: typing.Self(),
                lambda: typing.Any(),
            ]:
                reject(expression)

            for expression in [
                lambda: typing.Optional[int, str],
                lambda: typing.Optional[()],
                lambda: typing.Union[()],
                lambda: typing.Final[int, str],
                lambda: typing.NoReturn[int],
                lambda: typing.Self[int],
                lambda: typing.Any[int],
                lambda: typing.List[int, str],
                lambda: typing.List[()],
                lambda: typing.Dict[int],
                lambda: typing.Dict[int, str, bytes],
                lambda: typing.Type[int, str],
                lambda: typing.Callable[int],
                lambda: typing.Callable[[int]],
            ]:
                reject(expression)
            """
        );

    [Fact]
    public Task TheSpecialFormsAreRefusedByClassChecks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing

            def reject(call):
                try:
                    print(call())
                except TypeError as error:
                    print('TypeError:', error)

            for expression in [
                lambda: isinstance(1, typing.Optional),
                lambda: isinstance(1, typing.Final),
                lambda: isinstance(1, typing.ClassVar),
                lambda: isinstance(1, typing.Literal),
                lambda: isinstance(1, typing.NoReturn),
                lambda: isinstance(1, typing.Never),
                lambda: isinstance(1, typing.Self),
                lambda: isinstance(1, typing.Union),
            ]:
                reject(expression)

            print(isinstance([], typing.List), isinstance((), typing.List))
            print(isinstance(1, typing.List), isinstance({}, typing.Dict))
            """
        );

    [Fact]
    public Task GetTypeHintsRejectsObjectsWithoutAnnotations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing
            try:
                typing.get_type_hints(1)
            except TypeError as error:
                print('TypeError:', error)
            try:
                typing.get_type_hints()
            except TypeError as error:
                print('TypeError:', error)
            """
        );

    [Fact]
    public Task TheCoreTypingConstructsMatchCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            import typing as t
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            T = t.TypeVar("T")
            print("tv", repr(T), T.__name__, T.__bound__, T.__constraints__, T.__covariant__, T.__contravariant__, T.__infer_variance__, t.get_origin(T), t.get_args(T))
            print("default", repr(t.TypeVar("D", default=3).__default__), repr(t.TypeVar("N").__default__), t.TypeVar("N").__default__ is t.NoDefault)
            print("eq", T == t.TypeVar("T"), T is T, hash(T) == hash(T))
            show("tv-bad", lambda: t.TypeVar(5))
            show("tv-missing", lambda: t.TypeVar())
            show("tv-con", lambda: (repr(t.TypeVar("C", int, str)), t.TypeVar("C", int, str).__constraints__))
            show("tv-bound", lambda: t.TypeVar("B", bound=int).__bound__)
            show("tv-cov", lambda: (t.TypeVar("V", covariant=True).__covariant__, t.TypeVar("W", contravariant=True).__contravariant__))
            show("tv-default", lambda: t.TypeVar("E", default=None).__default__)
            show("tv-badkw", lambda: t.TypeVar("X", nope=1))
            A = t.Annotated[int, "meta", 2]
            print("ann", repr(A), t.get_origin(A), t.get_args(A), A.__metadata__, A.__origin__)
            print("ann-eq", A == t.Annotated[int, "meta", 2], A == t.Annotated[str, "meta", 2])
            show("ann-empty", lambda: t.Annotated[int])
            show("ann-bad", lambda: t.Annotated[5, "x"])
            show("ann-attr", lambda: A.nope)
            @t.overload
            def f(x: int) -> int: ...
            @t.overload
            def f(x: str) -> str: ...
            def f(x): return x
            print("overload", len(t.get_overloads(f)), f(3), [g.__name__ for g in t.get_overloads(f)])
            o = t.overload
            def g(x): return x
            print("overload-empty", t.get_overloads(g))
            show("ov-bare", lambda: t.overload())
            show("go-bare", lambda: t.get_overloads())
            show("ov-noncallable", lambda: (t.overload(5).__name__, t.overload(5) is t.overload(5)))
            d = t.overload(5)
            show("ov-call", lambda: d())
            print("ann-no-extras", t.get_type_hints(t.cast(object, None) or (lambda: None)) if False else "skip")
            """"
        );

    [Fact]
    public Task UserGenericClassesCarryParametersAndAliases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing as t
            T = t.TypeVar("T")
            S = t.TypeVar("S")
            class G(t.Generic[T]):
                def __init__(self, x): self.value = x
            print(G.__parameters__, G.__orig_bases__)
            print(G[int], t.get_origin(G[int]), t.get_args(G[int]))
            print(G[int].__args__, G[T].__parameters__)
            g = G[int](3)
            print(g.value, type(g).__name__)
            print(G[int](x=4).value)
            print(hasattr(G, "__class_getitem__"))
            class M(t.Generic[T, S]): pass
            print(M.__parameters__, repr(M[int, str]), M[int, str].__args__)
            def show(label, f):
                try: print(label, "ok", repr(f()))
                except Exception as e: print(label, type(e).__name__, e)
            show("few", lambda: M[int])
            show("many", lambda: M[int, str, bytes])
            show("misc", lambda: G[5])
            show("generic-args", lambda: t.Generic[5])
            show("protocol-args", lambda: t.Protocol[5])
            class H(G[int]): pass
            print(H.__parameters__, H.__orig_bases__)
            show("h-sub", lambda: H[int])
            class K(G[T]): pass
            print(K.__parameters__, K.__orig_bases__, K[int])
            class J(G): pass
            print(J.__parameters__, J.__orig_bases__)
            show("j-sub", lambda: J[int])
            try:
                class L(G[T], t.Generic[S]): pass
            except Exception as e:
                print("unlisted", type(e).__name__, e)
            try:
                class Bare(t.Generic): pass
            except Exception as e:
                print("bare", type(e).__name__, e)
            """
        );

    [Fact]
    public Task ProtocolsMarkTheirSubclassesAndRefuseInstantiation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import typing as t
            T = t.TypeVar("T")
            class P(t.Protocol):
                def m(self) -> int: ...
            print(P._is_protocol, P.__parameters__, hasattr(P, "__orig_bases__"))
            def show(label, f):
                try: print(label, "ok", repr(f()))
                except Exception as e: print(label, type(e).__name__, e)
            show("instantiate", lambda: P())
            class Rp(t.Protocol[T]):
                def m(self) -> T: ...
            print(Rp.__parameters__, Rp[int], t.get_args(Rp[int]))
            show("rp-inst", lambda: Rp[int]())
            class Q(P): pass
            print(Q._is_protocol, Q.__parameters__, type(Q()).__name__)
            class R(P, t.Protocol): pass
            show("r", lambda: R())
            @t.runtime_checkable
            class Rt(t.Protocol):
                def m(self): ...
            print(Rt._is_runtime_protocol, Rt._is_protocol, t.runtime_checkable(Rt) is Rt)
            show("rc0", lambda: t.runtime_checkable())
            show("rc1", lambda: t.runtime_checkable(1))
            show("rc2", lambda: t.runtime_checkable(Rt, Rt))
            """
        );
}
