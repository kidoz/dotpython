using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TypeAliasExecutionTests
{
    [Fact]
    public void PEP695AliasesRenderAndDescribeThemselves()
    {
        var output = Run(
            """
            type Pair = tuple[int, int]
            print(Pair)
            print(repr(Pair))
            print(Pair.__name__)
            print(Pair.__module__)
            print(Pair.__value__)
            print(Pair.__type_params__)
            print(type(Pair).__name__)
            print(type(Pair).__module__)
            print(callable(Pair))
            """
        );

        Assert.Equal(
            Lines(
                "Pair",
                "Pair",
                "Pair",
                "__main__",
                "tuple[int, int]",
                "()",
                "TypeAliasType",
                "typing",
                "False"
            ),
            output
        );
    }

    [Fact]
    public void AliasesBindLikeNamesAndHoldOrdinaryValues()
    {
        var output = Run(
            """
            type Number = int
            print(Number.__value__)
            type Sum = 1 + 2
            print(Sum, Sum.__value__)
            type Text = "hello"
            print(Text, Text.__value__)
            type Nothing = None
            print(Nothing, Nothing.__value__)
            type Number = str
            print(Number.__value__)
            type Copy = Number
            print(Copy.__value__)
            """
        );

        Assert.Equal(
            Lines(
                "<class 'int'>",
                "Sum 3",
                "Text hello",
                "Nothing None",
                "<class 'str'>",
                "Number"
            ),
            output
        );
    }

    [Fact]
    public void AliasesDeclaredInNestedScopesReportTheDefiningModule()
    {
        var output = Run(
            """
            def outer():
                type Local = dict
                return Local

            inner = outer()
            print(inner, inner.__name__, inner.__module__, inner.__value__)

            class Holder:
                type Slot = bytes

            print(Holder.Slot, Holder.Slot.__module__, Holder.Slot.__value__)
            """
        );

        Assert.Equal(
            Lines("Local Local __main__ <class 'dict'>", "Slot __main__ <class 'bytes'>"),
            output
        );
    }

    [Fact]
    public void AliasesJoinPEP604UnionsWithoutBeingResolved()
    {
        var output = Run(
            """
            type A = int
            type B = str
            union = A | str
            print(union)
            print(repr(union))
            print(union.__args__)
            print((str | A).__args__)
            print((B | A).__args__)
            print(A | A is A)
            print(A | None)
            print(A | (B | int))
            print(A | str == str | A)
            print(len({A | str, A | str}))
            """
        );

        Assert.Equal(
            Lines(
                "A | str",
                "A | str",
                "(A, <class 'str'>)",
                "(<class 'str'>, A)",
                "(B, A)",
                "True",
                "A | None",
                "A | B | int",
                "True",
                "1"
            ),
            output
        );
    }

    [Fact]
    public void AliasesAreRefusedWhereAClassIsRequired()
    {
        var output = Run(
            """
            type A = int

            try:
                isinstance(1, A)
            except TypeError as error:
                print(type(error).__name__, error)

            try:
                issubclass(int, A)
            except TypeError as error:
                print(type(error).__name__, error)

            try:
                isinstance('x', A | str)
            except TypeError as error:
                print(type(error).__name__, error)

            try:
                print(A | 5)
            except TypeError as error:
                print(type(error).__name__, error)

            try:
                print(5 | A)
            except TypeError as error:
                print(type(error).__name__, error)

            try:
                print(A | 'x')
            except TypeError as error:
                print(type(error).__name__, error)
            """
        );

        Assert.Equal(
            Lines(
                "TypeError isinstance() arg 2 must be a type, a tuple of types, or a union",
                "TypeError issubclass() arg 2 must be a class, a tuple of classes, or a union",
                "TypeError isinstance() arg 2 must be a type, a tuple of types, or a union",
                "TypeError unsupported operand type(s) for |: 'typing.TypeAliasType' and 'int'",
                "TypeError unsupported operand type(s) for |: 'int' and 'typing.TypeAliasType'",
                "TypeError unsupported operand type(s) for |: 'typing.TypeAliasType' and 'str'"
            ),
            output
        );
    }

    [Fact]
    public void TypeParametersAreReportedRatherThanBound()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "type Vec[T] = list[T]\n",
            "type_params.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY3123", Assert.Single(result.Diagnostics).Code);
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
