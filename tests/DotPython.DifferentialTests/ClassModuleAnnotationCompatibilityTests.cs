using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// Class and module annotations. A module's `__annotations__` is only reachable as a
/// module attribute, so the module cases here use the `__annotate__` global the body
/// leaves behind; the attribute path is covered by the paired execution tests.
/// </summary>
public sealed class ClassModuleAnnotationCompatibilityTests
{
    [Fact]
    public Task ClassAnnotationsArePerClassAndNeverMerged() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                y: str
            print(C.__annotations__)
            class Empty:
                pass
            print(Empty.__annotations__, Empty.__annotate__ is None)
            class A:
                x: int
            class B(A):
                y: str
            print(A.__annotations__, B.__annotations__)
            class U(A):
                pass
            print(U.__annotations__, U.__annotate__ is None)
            """
        );

    [Fact]
    public Task ClassAnnotationsResolveAgainstTheClassBodyAndEnclosingScopes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                T = int
                y: T
            print(C.__annotations__)

            def outer():
                Q = bytes
                class D:
                    z: Q
                return D
            print(outer().__annotations__)

            class E:
                def m(self, a: Later): pass
                Later = str
            print(E.__annotations__, E.m.__annotations__)
            """
        );

    [Fact]
    public Task ClassAnnotationsCacheAndDelete() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                y: str
            first = C.__annotations__
            print(first is C.__annotations__)
            first['extra'] = 1
            print(C.__annotations__)
            del C.__annotations__
            print(C.__annotations__)
            """
        );

    [Fact]
    public Task ModuleAnnotationsAreReachableThroughTheAnnotateGlobal() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            x: int = 1
            y: str
            print(x)
            print(__annotate__(1))
            print(type(__annotate__).__name__)
            try:
                print(__annotations__)
            except NameError:
                print('not a global')
            """
        );

    [Fact]
    public Task AModuleWithoutAnnotationsLeavesNoAnnotateGlobal() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            plain = 1
            try:
                print(__annotate__)
            except NameError:
                print('no annotate global')
            """
        );
}
