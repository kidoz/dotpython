using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class GenericAliasExecutionTests
{
    [Fact]
    public void SubscriptingABuiltinContainerBuildsAnAlias()
    {
        var output = Run(
            """
            alias = list[int]
            print(repr(alias), str(alias))
            print(alias.__origin__, alias.__args__, alias.__parameters__)
            print(type(alias).__name__, type(alias).__module__)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "list[int] list[int]",
                "<class 'list'> (<class 'int'>,) ()",
                "GenericAlias types",
                ""
            ),
            output
        );
    }

    [Fact]
    public void TupleFormsAndNestingRenderLikeCpython()
    {
        var output = Run(
            """
            print(repr(tuple[int, ...]), repr(tuple[()]))
            print(repr(list[list[int]]), repr(dict[str, list[int]]))
            print(repr(list[int, str]), repr(list["x"]))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "tuple[int, ...] tuple[()]",
                "list[list[int]] dict[str, list[int]]",
                "list[int, str] list['x']",
                ""
            ),
            output
        );
    }

    [Fact]
    public void AnAliasComparesAndConstructsThroughItsOrigin()
    {
        var output = Run(
            """
            print(list[int] == list[int], list[int] == list[str], list[int] == list)
            print(list[int](), list[int]([1, 2]), dict[str, int]({'a': 1}))
            """
        );

        Assert.Equal(
            string.Join(Environment.NewLine, "True False False", "[] [1, 2] {'a': 1}", ""),
            output
        );
    }

    [Theory]
    [InlineData("isinstance([], list[int])", "a parameterized generic")]
    [InlineData("issubclass(list, list[int])", "a parameterized generic")]
    public void AParameterizedGenericIsRefusedByTheClassChecks(string call, string fragment)
    {
        var output = Run(
            $"""
            try:
                {call}
            except TypeError as error:
                print(error)
            """
        );

        Assert.Contains(fragment, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("int[int]")]
    [InlineData("str[int]")]
    [InlineData("U[int]")]
    public void OnlyBuiltinContainerTypesAreSubscriptable(string call)
    {
        var output = Run(
            $"""
            class U: pass
            try:
                {call}
            except TypeError as error:
                print(error)
            """
        );

        Assert.Contains("is not subscriptable", output, StringComparison.Ordinal);
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "generic_alias_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
