using Xunit;

namespace DotPython.DifferentialTests;

public sealed class EnumCompatibilityTests
{
    [Fact]
    public Task MembersCarryNameValueIdentityAndLookups() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from enum import Enum

            class Color(Enum):
                RED = 1
                GREEN = 2
                BLUE = 3

            print(Color.RED, repr(Color.RED), str(Color.RED), Color.RED.name, Color.RED.value)
            print(list(Color), len(Color), Color(2), Color['BLUE'])
            print(list(Color.__members__))
            print(Color.RED in Color, 1 in Color)
            print(Color.RED is Color.RED, Color.RED == Color.RED, Color.RED != Color.GREEN)
            print(Color.RED == 1, Color.RED == Color.GREEN)
            print(type(Color.RED).__name__, type(Color).__name__, Color.__name__, Color.__module__)
            """
        );

    [Fact]
    public Task DuplicateValuesAliasAndUnhashableLookupsFallThrough() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from enum import Enum

            class Alias(Enum):
                A = 1
                B = 1
                C = 2

            print(list(Alias), Alias.B.name, Alias(1), len(Alias.__members__))

            class Plain(Enum):
                X = 1

            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: Plain([1, 2]))
            probe(lambda: Plain({'a': 1}))
            probe(lambda: Plain({1}))
            probe(lambda: Plain((1, {'a': 1})))
            probe(lambda: Plain('New', {'Z': 9}))
            probe(lambda: Plain(99))
            probe(lambda: Plain['NOPE'])
            probe(lambda: Plain.NOPE)
            """
        );

    [Fact]
    public Task AutoUniqueAndTheFunctionalFormFollowCPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from enum import Enum, auto, unique

            class Auto(Enum):
                X = auto()
                Y = auto()

            print([(m.name, m.value) for m in Auto])

            Made = Enum('Made', {'A': 1, 'B': 2})
            print(Made.A, Made.B.value, list(Made))

            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: unique(Enum('Dup', {'A': 1, 'B': 1})))
            probe(lambda: setattr(Auto.X, 'value', 5))
            """
        );

    [Fact]
    public Task IntEnumActsAsAnIntAndFlagCombinesBits() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from enum import IntEnum, StrEnum, Flag, IntFlag, auto

            class Sizes(IntEnum):
                S = 1
                L = 2

            print(Sizes.S == 1, Sizes.S + 1, int(Sizes.L), Sizes.L * 2, Sizes.S < Sizes.L)
            print(Sizes.S | Sizes.L, Sizes.S & Sizes.L, Sizes.S ^ Sizes.L, -Sizes.S, ~Sizes.S, Sizes.S >> 1)

            class Pets(StrEnum):
                DOG = 'dog'

            print(Pets.DOG, Pets.DOG == 'dog', f'{Pets.DOG}', Pets.DOG.upper(), Pets.DOG + '!')

            class Perm(Flag):
                R = auto()
                W = auto()
                X = auto()

            print(Perm.R, Perm.R | Perm.W, (Perm.R | Perm.W).value, Perm(3))
            print((Perm.R | Perm.W) & Perm.R, (Perm.R | Perm.W) ^ Perm.R, Perm.R in (Perm.R | Perm.W))
            print((~Perm.R).value)
            """
        );

    [Fact]
    public Task MissingClassAttributesNameTheTypeObject() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Plain:
                pass

            class Derived(Plain):
                pass

            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: Plain.NOPE)
            probe(lambda: Derived.NOPE)
            probe(lambda: int.NOPE)
            probe(lambda: Plain().NOPE)
            """
        );
}
