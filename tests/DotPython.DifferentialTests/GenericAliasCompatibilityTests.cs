using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// `list[int]` and friends: subscripting a builtin container type parameterizes it.
/// User classes are not subscriptable unless they define `__class_getitem__`, and the
/// refused cases are asserted in the paired execution tests because CPython raises.
/// A nested class's `__qualname__` is a pre-existing gap, so its rendering is not
/// asserted here.
/// </summary>
public sealed class GenericAliasCompatibilityTests
{
    [Fact]
    public Task SubscriptingABuiltinContainerRendersItsArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(repr(list[int]), str(list[int]), f"{list[int]}")
            print(repr(list[list[int]]), repr(dict[str, list[int]]))
            print(repr(tuple[int, ...]), repr(tuple[()]))
            print(repr(list["x"]), repr(list[1]), repr(list[None]))
            print(repr(list[int, str]), repr(set[int]), repr(frozenset[str]), repr(type[int]))
            print(repr(list[list[int]]), [list[int]], {list[int]: 1})
            """
        );

    [Fact]
    public Task ATypesArgumentsRenderAsModuleQualifiedNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class U: pass
            print(repr(list[U]))
            """
        );

    [Fact]
    public Task AnAliasExposesOriginArgsAndParameters() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            alias = list[int]
            print(alias.__origin__, alias.__args__, alias.__parameters__)
            print(type(alias).__name__, type(alias).__module__)
            print(alias.__origin__ is list, alias.__args__[0] is int)
            print(dict[str, int].__args__)
            """
        );

    [Fact]
    public Task AnAliasComparesAndHashesByItsOriginAndArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(list[int] == list[int], list[int] == list[str], list[int] == list)
            print(hash(list[int]) == hash(list[int]))
            alias = list[int]
            print(alias in [list[int]], {alias: 1}[list[int]])
            print(len({list[int], list[int], list[str]}))
            """
        );

    [Fact]
    public Task AnAliasConstructsThroughItsOrigin() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(list[int](), list[int]([1, 2]))
            print(dict[str, int]({'a': 1}), set[int]([1, 1, 2]))
            print(bool(list[int]), type(list[int]()).__name__)
            """
        );
}
