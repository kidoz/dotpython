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
