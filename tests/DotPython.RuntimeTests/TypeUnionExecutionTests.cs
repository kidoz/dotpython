using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TypeUnionExecutionTests
{
    [Fact]
    public void PEP604UnionsSatisfyIsInstanceAndIsSubclass()
    {
        var output = Run(
            """
            print(isinstance(1, int | str))
            print(isinstance(1.0, int | str))
            print(isinstance([], int | str))
            print(isinstance(True, int | str))
            print(isinstance('a', int | str))
            print(type(1) is int)
            print(issubclass(bool, int | str))
            print(issubclass(dict, int | str))
            print(isinstance(1, (int, str)))
            """
        );

        Assert.Equal(
            Lines("True", "False", "False", "True", "True", "True", "True", "False", "True"),
            output
        );
    }

    [Fact]
    public void UnionsFlattenDeduplicateAndRenderQualifiedTypeNames()
    {
        var output = Run(
            """
            print(int | str)
            print(repr(int | str))
            print(str(int | str))
            print(int | str | bytes)
            print(int | int)
            print(repr(int | int | int))
            print((int | str) | bytes)
            print(int | (str | bytes))


            class A:
                pass


            class B(A):
                pass


            print(A | B)
            print(isinstance(B(), A | B))
            print(isinstance(A(), A | B))

            def nested():
                class Local:
                    pass

                return Local


            print(nested() | int)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "int | str",
                "int | str",
                "int | str",
                "int | str | bytes",
                "<class 'int'>",
                "<class 'int'>",
                "int | str | bytes",
                "int | str | bytes",
                "__main__.A | __main__.B",
                "True",
                "True",
                "__main__.nested.<locals>.Local | int",
                ""
            ),
            output
        );
    }

    [Fact]
    public void NoneJoinsAUnionAsNoneType()
    {
        var output = Run(
            """
            print(int | None)
            print(None | int)
            print((int | None) | None)
            print(type(None))
            print(isinstance(None, int | None))
            print(isinstance(1, int | None))
            print(issubclass(type(None), int | None))
            print(bool | None | int)
            """
        );

        Assert.Equal(
            Lines(
                "int | None",
                "None | int",
                "int | None",
                "<class 'NoneType'>",
                "True",
                "True",
                "True",
                "bool | None | int"
            ),
            output
        );
    }

    [Fact]
    public void UnionOrderIsNotPartOfEqualityOrHash()
    {
        var output = Run(
            """
            print((int | str) == (int | str))
            print((int | str) == (str | int))
            print((int | str) != (str | int))
            print((int | str) == int)
            print(int == (int | str))
            print(hash(int | str) == hash(str | int))
            print(type(hash(int | str)).__name__)
            mapping = {int | str: 'first'}
            print(mapping[str | int])
            print(len({int | str, str | int, bytes}))
            print(int | str in {str | int})
            """
        );

        Assert.Equal(
            Lines("True", "True", "False", "False", "False", "True", "int", "first", "2", "True"),
            output
        );
    }

    [Fact]
    public void UnionTypeMetadataMatchesTypingUnion()
    {
        var output = Run(
            """
            union = int | str
            print(type(union).__name__)
            print(type(union).__qualname__)
            print(type(union).__module__)
            print(repr(type(union)))
            print(isinstance(union, type))
            print(type(union).__mro__)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Union",
                "Union",
                "typing",
                "<class 'typing.Union'>",
                "False",
                "(<class 'typing.Union'>, <class 'object'>)",
                ""
            ),
            output
        );
    }

    [Fact]
    public void UnionsLeaveBitwiseSetAndMappingOperatorsUntouched()
    {
        var output = Run(
            """
            print(1 | 2)
            print(True | False)
            print({1} | {2})
            print(frozenset([1]) | frozenset([2]))
            print({'a': 1} | {'b': 2})
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "3",
                "True",
                "{1, 2}",
                "frozenset({1, 2})",
                "{'a': 1, 'b': 2}",
                ""
            ),
            output
        );
    }

    [Theory]
    [InlineData("print(1 | int)", "DPY4005")]
    [InlineData("print(int | 1)", "DPY4005")]
    [InlineData("print(None | None)", "DPY4005")]
    [InlineData("print(1.5 | int)", "DPY4005")]
    [InlineData("print(type(int | str)())", "DPY4009")]
    public void Execute_ReturnsRuntimeDiagnostics(string source, string expectedCode)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
    }

    [Theory]
    [InlineData("print(1 | int)", "unsupported operand type(s) for |: 'int' and 'type'")]
    [InlineData("print(int | 1)", "unsupported operand type(s) for |: 'type' and 'int'")]
    [InlineData(
        "print(None | None)",
        "unsupported operand type(s) for |: 'NoneType' and 'NoneType'"
    )]
    [InlineData("print(1.5 | int)", "unsupported operand type(s) for |: 'float' and 'type'")]
    public void NonTypeUnionOperandsReportCpythonOperandMessages(string source, string message)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4005", diagnostic.Code);
        Assert.Equal(message, diagnostic.Message);
    }

    [Fact]
    public void UnionClassInfoRejectsNonTypes()
    {
        var output = Run(
            """
            try:
                print(isinstance(1, 1))
            except TypeError as error:
                print(type(error).__name__, error)

            try:
                print(issubclass(int, 1))
            except TypeError as error:
                print(type(error).__name__, error)

            try:
                print(issubclass(int | str, int))
            except TypeError as error:
                print(type(error).__name__, error)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "TypeError isinstance() arg 2 must be a type, a tuple of types, or a union",
                "TypeError issubclass() arg 2 must be a class, a tuple of classes, or a union",
                "TypeError issubclass() arg 1 must be a class",
                ""
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
