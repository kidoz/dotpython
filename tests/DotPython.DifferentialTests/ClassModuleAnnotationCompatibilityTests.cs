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

    /// <summary>
    /// Class and module annotation assignment follows the same cache and annotate
    /// rules; module error wording is pinned in the runtime test, where the module
    /// repr does not carry CPython's `(built-in)` suffix.
    /// </summary>
    [Fact]
    public Task ClassesAndModulesExposeTheSameWritableAnnotations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            import math, annotationlib, typing
            class C:
                y: int
            print(C.__annotations__, C.__annotate__ is None)
            C.__annotations__ = {"z": 2}
            print(C.__annotations__, C.__annotate__)
            C.__annotations__ = None
            print(C.__annotations__, type(C.__annotations__).__name__)
            del C.__annotations__
            print(C.__annotations__, C.__annotate__)
            class D: pass
            D.__annotate__ = lambda fmt: {"w": 1}
            print(D.__annotations__)
            D.__annotate__ = None
            print(D.__annotations__, D.__annotate__)
            try:
                D.__annotate__ = 5
            except TypeError as e:
                print(e)
            try:
                del D.__annotate__
            except TypeError as e:
                print(e)
            m = math
            print(hasattr(m, "__annotations__"), hasattr(m, "__annotate__"))
            print(m.__annotations__)
            m.__annotate__ = lambda fmt: {"a": 1}
            print(m.__annotations__)
            m.__annotations__ = {"b": 2}
            print(m.__annotations__, m.__annotate__)
            del m.__annotations__
            print(m.__annotations__, m.__annotate__)
            try:
                m.__annotate__ = 5
            except TypeError as e:
                print(e)
            try:
                del m.__annotate__
            except TypeError as e:
                print(e)
            m.__annotations__ = 5
            for label, fn in (("alib", annotationlib.get_annotations), ("hints", typing.get_type_hints)):
                try:
                    fn(m)
                except Exception as e:
                    print(label, type(e).__name__)
            class E: pass
            E.__annotations__ = 5
            for label, fn in (("alib", annotationlib.get_annotations), ("hints", typing.get_type_hints)):
                try:
                    fn(E)
                except Exception as e:
                    print(label, type(e).__name__, e)
            class F: pass
            F.__annotations__ = None
            try:
                annotationlib.get_annotations(F)
            except Exception as e:
                print("alib-none", type(e).__name__, e)
            """"
        );
}
