using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class EnumExecutionTests
{
    [Fact]
    public void MembersIterateLookUpAndCompare()
    {
        var output = Run(
            """
            from enum import Enum

            class Color(Enum):
                RED = 1
                GREEN = 2
                BLUE = 3

            print(Color.RED, repr(Color.RED), Color.RED.name, Color.RED.value)
            print(list(Color), len(Color), Color(2), Color['BLUE'])
            print(Color.RED is Color.RED, Color.RED == 1, 1 in Color)
            print(type(Color.RED).__name__, type(Color).__name__)
            """
        );

        Assert.Equal(
            Lines(
                "Color.RED <Color.RED: 1> RED 1",
                "[<Color.RED: 1>, <Color.GREEN: 2>, <Color.BLUE: 3>] 3 Color.GREEN Color.BLUE",
                "True False True",
                "Color EnumType"
            ),
            output
        );
    }

    [Fact]
    public void DuplicateValuesAliasAndAutoNumbersMembers()
    {
        var output = Run(
            """
            from enum import Enum, auto

            class Alias(Enum):
                A = 1
                B = 1
                C = 2

            print(list(Alias), Alias.B.name, len(Alias.__members__))

            class Auto(Enum):
                X = auto()
                Y = auto()

            print([(m.name, m.value) for m in Auto])
            """
        );

        Assert.Equal(Lines("[<Alias.A: 1>, <Alias.C: 2>] A 3", "[('X', 1), ('Y', 2)]"), output);
    }

    [Fact]
    public void MixinsBehaveAsTheirUnderlyingType()
    {
        var output = Run(
            """
            from enum import IntEnum, StrEnum

            class Sizes(IntEnum):
                S = 1
                L = 2

            print(Sizes.S == 1, Sizes.S + 1, int(Sizes.L), Sizes.S < Sizes.L)
            print(Sizes.S | Sizes.L, ~Sizes.S)

            class Pets(StrEnum):
                DOG = 'dog'

            print(Pets.DOG == 'dog', Pets.DOG.upper(), Pets.DOG + '!')
            """
        );

        Assert.Equal(Lines("True 2 2 True", "3 -2", "True DOG dog!"), output);
    }

    [Fact]
    public void ErrorsMatchCPythonsForBadLookupsAndAssignment()
    {
        var output = Run(
            """
            from enum import Enum

            class Color(Enum):
                RED = 1

            for thunk, label in ((None, 'value'),):
                try:
                    Color(99)
                except ValueError as error:
                    print('value ->', type(error).__name__, error)

            try:
                Color['NOPE']
            except KeyError as error:
                print('name ->', type(error).__name__, error)

            try:
                Color.NOPE
            except AttributeError as error:
                print('attr ->', type(error).__name__, error)

            try:
                Color.RED.value = 5
            except AttributeError as error:
                print('assign ->', type(error).__name__, error)
            """
        );

        Assert.Equal(
            Lines(
                "value -> ValueError 99 is not a valid Color",
                "name -> KeyError 'NOPE'",
                "attr -> AttributeError type object 'Color' has no attribute 'NOPE'",
                "assign -> AttributeError <enum 'Enum'> cannot set attribute 'value'"
            ),
            output
        );
    }

    [Fact]
    public void UnhashableLookupsReportAValueErrorNotAHashingFailure()
    {
        var output = Run(
            """
            from enum import Enum

            class Plain(Enum):
                X = 1

            for bad in ([1, 2], {'a': 1}, {1}, (1, {'a': 1})):
                try:
                    Plain(bad)
                except ValueError as error:
                    print(type(error).__name__, error)
            """
        );

        Assert.Equal(
            Lines(
                "ValueError [1, 2] is not a valid Plain",
                "ValueError {'a': 1} is not a valid Plain",
                "ValueError {1} is not a valid Plain",
                "ValueError (1, {'a': 1}) is not a valid Plain"
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
