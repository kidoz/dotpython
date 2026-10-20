using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

/// <summary>
/// The implemented `typing` surface. The container aliases deliberately reuse the shared
/// `GenericAlias`, so `typing.List[int]` renders as `list[int]`; the paired differential
/// tests cover only the behaviours that match CPython exactly.
/// </summary>
public sealed class TypingModuleExecutionTests
{
    [Fact]
    public void TheModuleImportsAndCarriesTheReExportSurface()
    {
        var output = Run(
            """
            import typing
            print(typing.TYPE_CHECKING, typing.Text is str)
            print(repr(typing.Optional), repr(typing.Union), repr(typing.List), repr(typing.Dict))
            print(repr(typing.Tuple), repr(typing.Type), repr(typing.Callable))
            print(repr(typing.Final), repr(typing.ClassVar), repr(typing.Literal))
            print(repr(typing.NoReturn), repr(typing.Never), repr(typing.Self))
            print(typing.Optional.__module__, typing.Union.__module__)
            print(isinstance(1, typing.Union), isinstance('a', typing.Text))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "False True",
                "typing.Optional <class 'typing.Union'> typing.List typing.Dict",
                "typing.Tuple typing.Type typing.Callable",
                "typing.Final typing.ClassVar typing.Literal",
                "typing.NoReturn typing.Never typing.Self",
                "typing typing",
                "False True",
                ""
            ),
            output
        );
    }

    [Fact]
    public void OptionalAndUnionBuildPep604Unions()
    {
        var output = Run(
            """
            import typing
            print(repr(typing.Optional[int]), repr(typing.Optional[None]))
            print(repr(typing.Union[int, str]), repr(typing.Union[int]), repr(typing.Union[int, int]))
            print(repr(typing.Optional[int] | str), repr(typing.Union[int, str, bytes]))
            print(isinstance(None, typing.Optional[int]), isinstance(1, typing.Optional[str]))
            print(typing.Optional[int] == int | None)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "int | None <class 'NoneType'>",
                "int | str <class 'int'> <class 'int'>",
                "int | None | str int | str | bytes",
                "True False",
                "True",
                ""
            ),
            output
        );
    }

    [Fact]
    public void TheUnionMembersKeepTheWrittenOrder()
    {
        var output = Run(
            """
            import typing
            print(repr(typing.Union[None, int]), repr(typing.Union[str, int]))
            """
        );

        Assert.Equal(string.Join(Environment.NewLine, "None | int str | int", ""), output);
    }

    [Fact]
    public void AnyIsATypeThatJoinsUnions()
    {
        var output = Run(
            """
            import typing
            print(repr(int | typing.Any), repr(typing.Optional[typing.Any]))
            print(repr(typing.Any), typing.Any.__module__)
            try:
                typing.Any[int]
            except TypeError as error:
                print('TypeError:', error)
            try:
                typing.Any()
            except TypeError as error:
                print('TypeError:', error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "int | typing.Any typing.Any | None",
                "<class 'typing.Any'> typing",
                "TypeError: type 'Any' is not subscriptable",
                "TypeError: Any cannot be instantiated",
                ""
            ),
            output
        );
    }

    [Fact]
    public void ContainerAliasesAreGenericAliasesOverTheirBuiltins()
    {
        var output = Run(
            """
            import typing
            print(repr(typing.List[int]), repr(typing.Dict[str, int]), repr(typing.Tuple[int, str]))
            print(repr(typing.Tuple[int, ...]), repr(typing.Tuple[()]), repr(typing.Type[int]))
            print(repr(typing.Callable[[int], str]), repr(typing.Callable[..., int]))
            print(typing.get_origin(typing.List[int]), typing.get_args(typing.List[int]))
            print(typing.List[int].__origin__, typing.List[int].__parameters__)
            print(isinstance([], typing.List), isinstance((), typing.List))
            print(typing.List[int]([1, 2]))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "list[int] dict[str, int] tuple[int, str]",
                "tuple[int, ...] tuple[()] type[int]",
                "typing.Callable[[int], str] typing.Callable[..., int]",
                "<class 'list'> (<class 'int'>,)",
                "<class 'list'> ()",
                "True False",
                "[1, 2]",
                ""
            ),
            output
        );
    }

    [Fact]
    public void FinalClassVarAndLiteralKeepTheirOwnOrigin()
    {
        var output = Run(
            """
            import typing
            print(repr(typing.Final[int]), repr(typing.ClassVar[int]), repr(typing.Literal[1, 2]))
            print(repr(typing.Literal['a']), repr(typing.Literal[()]))
            print(typing.get_origin(typing.Final[int]), typing.get_args(typing.Final[int]))
            print(typing.get_args(typing.Literal[1, 2]))
            print(typing.get_origin(typing.Final), typing.get_origin(typing.Literal))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "typing.Final[int] typing.ClassVar[int] typing.Literal[1, 2]",
                "typing.Literal['a'] typing.Literal[()]",
                "typing.Final (<class 'int'>,)",
                "(1, 2)",
                "None None",
                ""
            ),
            output
        );
    }

    [Theory]
    [InlineData("typing.Optional()", "Cannot instantiate typing.Optional")]
    [InlineData("typing.Union()", "cannot create 'typing.Union' instances")]
    [InlineData("typing.List()", "Type List cannot be instantiated; use list() instead")]
    [InlineData("typing.NoReturn()", "Cannot instantiate typing.NoReturn")]
    [InlineData("typing.NoReturn[int]", "typing.NoReturn is not subscriptable")]
    [InlineData("typing.Self[int]", "typing.Self is not subscriptable")]
    [InlineData("typing.Optional[int, str]", "typing.Optional requires a single type")]
    [InlineData("typing.Union[()]", "Cannot take a Union of no types.")]
    [InlineData("typing.Final[int, str]", "typing.Final accepts only single type")]
    [InlineData(
        "typing.List[int, str]",
        "Too many arguments for typing.List; actual 2, expected 1"
    )]
    [InlineData("typing.Dict[int]", "Too few arguments for typing.Dict; actual 1, expected 2")]
    [InlineData(
        "typing.Type[int, str]",
        "Too many arguments for typing.Type; actual 2, expected 1"
    )]
    [InlineData("typing.Callable[int]", "Callable must be used as Callable[[arg, ...], result].")]
    [InlineData(
        "isinstance(1, typing.Optional)",
        "typing.Optional cannot be used with isinstance()"
    )]
    [InlineData("isinstance(1, typing.Final)", "typing.Final cannot be used with isinstance()")]
    public void RejectedFormsReportTheCpythonDiagnostic(string expression, string fragment)
    {
        var output = Run(
            $"""
            import typing
            try:
                {expression}
            except TypeError as error:
                print(error)
            """
        );

        Assert.Contains(fragment, output, StringComparison.Ordinal);
    }

    [Fact]
    public void CastReturnsItsValueAndChecksArity()
    {
        var output = Run(
            """
            import typing
            print(repr(typing.cast(int, 'x')), typing.cast(int, 3) + 1)
            try:
                typing.cast()
            except TypeError as error:
                print('TypeError:', error)
            try:
                typing.cast(int)
            except TypeError as error:
                print('TypeError:', error)
            try:
                typing.cast(int, 1, 2)
            except TypeError as error:
                print('TypeError:', error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "'x' 4",
                "TypeError: cast() missing 2 required positional arguments: 'typ' and 'val'",
                "TypeError: cast() missing 1 required positional argument: 'val'",
                "TypeError: cast() takes 2 positional arguments but 3 were given",
                ""
            ),
            output
        );
    }

    [Fact]
    public void GetOriginAndGetArgsDescribeEveryForm()
    {
        var output = Run(
            """
            import typing
            print(typing.get_origin(int | str), typing.get_args(int | str))
            print(typing.get_origin(typing.Optional[int]), typing.get_args(typing.Optional[int]))
            print(typing.get_origin(list[int]), typing.get_args(list[int]))
            print(typing.get_origin(typing.List), typing.get_args(typing.List))
            print(typing.get_origin(1), typing.get_args(1))
            print(typing.get_args(typing.Tuple[()]))
            try:
                typing.get_origin()
            except TypeError as error:
                print('TypeError:', error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "<class 'typing.Union'> (<class 'int'>, <class 'str'>)",
                "<class 'typing.Union'> (<class 'int'>, <class 'NoneType'>)",
                "<class 'list'> (<class 'int'>,)",
                "<class 'list'> ()",
                "None ()",
                "()",
                "TypeError: get_origin() missing 1 required positional argument: 'tp'",
                ""
            ),
            output
        );
    }

    [Fact]
    public void GetTypeHintsReadsTheEvaluatedAnnotations()
    {
        var output = Run(
            """
            import typing

            def f(a: int, b: str = 'x') -> bool:
                return True


            class C:
                x: int = 1


            print(typing.get_type_hints(f))
            print(typing.get_type_hints(C))
            print(typing.get_type_hints(f, None, None, True))
            print(typing.get_type_hints(list), typing.get_type_hints(len))
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

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'a': <class 'int'>, 'b': <class 'str'>, 'return': <class 'bool'>}",
                "{'x': <class 'int'>}",
                "{'a': <class 'int'>, 'b': <class 'str'>, 'return': <class 'bool'>}",
                "{} {}",
                "TypeError: 1 does not have annotations",
                "TypeError: get_type_hints() missing 1 required positional argument: 'obj'",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The typing constructs added here: `TypeVar` with its attributes, defaults and
    /// refusals, `NoDefault`, `Annotated` with its metadata/origin/equality surface, and
    /// the `overload`/`get_overloads` registry and dummy.
    /// </summary>
    [Fact]
    public void TheCoreTypingConstructsMatchPython()
    {
        var output = Run(
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

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "tv ~T T None () False False False None ()",
                "default 3 typing.NoDefault True",
                "eq False True True",
                "tv-bad TypeError typevar() argument 'name' must be str, not int",
                "tv-missing TypeError typevar() missing required argument 'name' (pos 1)",
                "tv-con ok ('~C', (<class 'int'>, <class 'str'>))",
                "tv-bound ok <class 'int'>",
                "tv-cov ok (True, True)",
                "tv-default ok None",
                "tv-badkw TypeError typevar() got an unexpected keyword argument 'nope'",
                "ann typing.Annotated[int, 'meta', 2] typing.Annotated (<class 'int'>, 'meta', 2) ('meta', 2) <class 'int'>",
                "ann-eq True False",
                "ann-empty TypeError Annotated[...] should be used with at least two arguments (a type and an annotation).",
                "ann-bad ok typing.Annotated[5, 'x']",
                "ann-attr AttributeError type object 'int' has no attribute 'nope'",
                "overload 2 3 ['f', 'f']",
                "overload-empty []",
                "ov-bare TypeError overload() missing 1 required positional argument: 'func'",
                "go-bare TypeError get_overloads() missing 1 required positional argument: 'func'",
                "ov-noncallable ok ('_overload_dummy', True)",
                "ov-call NotImplementedError You should not call an overloaded function. A series of @overload-decorated functions outside a stub module should always be followed by an implementation that is not @overload-ed.",
                "ann-no-extras skip",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The recorded gaps: `ParamSpec`, `TypeVarTuple`, `Unpack` and `Concatenate` are still
    /// absent, `TypeVar` is a builtin constructor rather than a class, a union operand does
    /// not accept a `TypeVar`, and `runtime_checkable` does not perform structural checks.
    /// </summary>
    [Fact]
    public void TheRuntimePinsItsRemainingTypingGaps()
    {
        var output = Run(
            """"
            import typing as t
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            print(sorted(n for n in ("TypeVar","Generic","Protocol","Annotated","overload","get_overloads","ParamSpec","TypeVarTuple","Unpack","Concatenate") if hasattr(t,n)))
            print(type(t.TypeVar).__name__, type(t.TypeVar("T")).__name__, type(t.TypeVar("T")).__module__)
            T = t.TypeVar("T")
            show("generic", lambda: t.Generic)
            show("protocol", lambda: t.Protocol)
            show("paramspec", lambda: t.ParamSpec)
            show("union", lambda: int | T)
            show("generic-class", lambda: type("G", (t.Generic[T],), {}))
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "['Annotated', 'Generic', 'Protocol', 'TypeVar', 'get_overloads', 'overload']",
                "builtin_function_or_method TypeVar typing",
                "generic ok <class 'typing.Generic'>",
                "protocol ok <class 'typing.Protocol'>",
                "paramspec AttributeError module 'typing' has no attribute 'ParamSpec'",
                "union TypeError unsupported operand type(s) for |: 'type' and 'TypeVar'",
                "generic-class TypeError type() doesn't support MRO entry resolution; use types.new_class()",
                ""
            ),
            output
        );
    }

    [Fact]
    public void GenericClassesCarryTheirParametersAndAliases()
    {
        var output = Run(
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

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "(~T,) (typing.Generic[~T],)",
                "__main__.G[int] <class '__main__.G'> (<class 'int'>,)",
                "(<class 'int'>,) (~T,)",
                "3 G",
                "4",
                "True",
                "(~T, ~S) __main__.M[int, str] (<class 'int'>, <class 'str'>)",
                "few TypeError Too few arguments for <class '__main__.M'>; actual 1, expected at least 2",
                "many TypeError Too many arguments for <class '__main__.M'>; actual 3, expected 2",
                "misc ok __main__.G[5]",
                "generic-args TypeError Parameters to Generic[...] must all be type variables or parameter specification variables.",
                "protocol-args TypeError Parameters to Protocol[...] must all be type variables or parameter specification variables.",
                "() (__main__.G[int],)",
                "h-sub TypeError <class '__main__.H'> is not a generic class",
                "(~T,) (__main__.G[~T],) __main__.K[int]",
                "() (typing.Generic[~T],)",
                "j-sub TypeError <class '__main__.J'> is not a generic class",
                "unlisted TypeError Some type variables (~T) are not listed in Generic[~S]",
                "bare TypeError Cannot inherit from plain Generic",
                ""
            ),
            output
        );
    }

    [Fact]
    public void ProtocolsMarkTheirSubclassesAndRefuseInstantiation()
    {
        var output = Run(
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

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "True () False",
                "instantiate TypeError Protocols cannot be instantiated",
                "(~T,) __main__.Rp[int] (<class 'int'>,)",
                "rp-inst TypeError Protocols cannot be instantiated",
                "False () Q",
                "r TypeError Protocols cannot be instantiated",
                "True True True",
                "rc0 TypeError runtime_checkable() missing 1 required positional argument: 'cls'",
                "rc1 TypeError issubclass() arg 1 must be a class",
                "rc2 TypeError runtime_checkable() takes 1 positional argument but 2 were given",
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
            "typing_module_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
