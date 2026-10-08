using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TypeAliasCompatibilityTests
{
    [Fact]
    public Task AliasesRenderAndDescribeThemselves() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            type Pair = tuple[int, int]
            print(Pair)
            print(repr(Pair))
            print(str(Pair))
            print(Pair.__name__)
            print(Pair.__module__)
            print(Pair.__value__)
            print(Pair.__type_params__)
            print(type(Pair).__name__)
            print(type(Pair).__module__)
            print(callable(Pair))
            print(f'{Pair}')
            print([Pair])
            """
        );

    [Fact]
    public Task AliasesBindLikeNamesAndHoldOrdinaryValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            type Number = int
            print(Number.__value__, Number.__name__)
            type Sum = 1 + 2
            print(Sum, Sum.__value__)
            type Text = "hello"
            print(Text, Text.__value__)
            type Nothing = None
            print(Nothing, Nothing.__value__)
            type Number = str
            print(Number.__value__)
            type Copy = Number
            print(Copy, Copy.__value__, Copy.__name__)
            type Generic = list[int]
            print(Generic.__value__)
            """
        );

    [Fact]
    public Task AliasesDeclaredInNestedScopesReportTheDefiningModule() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task AliasesJoinPEP604UnionsWithoutBeingResolved() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            type A = int
            type B = str
            union = A | str
            print(union)
            print(repr(union))
            print(union.__args__)
            print((str | A).__args__)
            print((B | A).__args__)
            print(A | A)
            print(A | None)
            print(None | A)
            print(A | (B | int))
            print(A | str == str | A)
            print(len({A | str, A | str}))
            print((A | str).__name__)
            print((A | str).__origin__)
            """
        );

    [Fact]
    public Task AliasesResolveInsideDeferredAnnotations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            type Pair = tuple[int, int]

            def f(x: Pair) -> Pair:
                pass

            print(f.__annotations__)
            print(f.__annotations__['x'] is Pair)

            class C:
                field: Pair

            print(C.__annotations__)
            print(C.__annotations__['field'] is Pair)

            import annotationlib
            print(annotationlib.get_annotations(f))
            print(annotationlib.get_annotations(C))
            """
        );

    [Fact]
    public Task AliasesAreRefusedWhereAClassIsRequired() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
