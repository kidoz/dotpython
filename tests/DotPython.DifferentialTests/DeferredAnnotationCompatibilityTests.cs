using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// PEP 649 keeps a definition's annotations unevaluated until something reads them.
/// Every case here catches its own failures so the reference interpreter exits 0.
/// </summary>
public sealed class DeferredAnnotationCompatibilityTests
{
    [Fact]
    public Task FunctionAnnotationsAreAvailableAsAMapping() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def f(x: int) -> str:
                pass
            print(f.__annotations__)
            def empty(x):
                pass
            print(empty.__annotations__, hasattr(empty, "__annotations__"))
            print(empty.__annotate__ is None, hasattr(f, "__annotate__"))
            """
        );

    [Fact]
    public Task AnUndefinedNameFailsOnAccessRatherThanAtDefinition() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def f(x: Undefined) -> AlsoUndefined:
                pass
            print('defined')
            try:
                f.__annotations__
            except NameError as error:
                print(type(error).__name__)
            try:
                f.__annotations__
            except NameError as error:
                print('again:', type(error).__name__)
            """
        );

    [Fact]
    public Task AnnotationsResolveAgainstGlobalsAndClosuresAtAccessTime() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def late(x: T) -> R:
                pass
            T = int
            R = str
            print(late.__annotations__)

            def outer():
                Q = bytes
                def inner(x: Q) -> Q:
                    pass
                return inner
            print(outer().__annotations__)

            def unbound():
                def inner(x: Missing):
                    pass
                return inner
            try:
                unbound().__annotations__
            except NameError as error:
                print(type(error).__name__)
            """
        );

    [Fact]
    public Task AnAnnotationsMappingIsCachedAndMutable() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def f(x: int):
                pass
            first = f.__annotations__
            second = f.__annotations__
            print(first is second)
            first['extra'] = 1
            print(f.__annotations__)
            """
        );

    [Fact]
    public Task TheAnnotateCallableAcceptsTheTwoValueFormats() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def f(x: int) -> str:
                pass
            print(f.__annotate__(1))
            print(f.__annotate__(2))
            for unsupported in (3, 4):
                try:
                    f.__annotate__(unsupported)
                except NotImplementedError:
                    print(unsupported, 'NotImplementedError')
            """
        );

    [Fact]
    public Task ParameterKindsAndStringAnnotationsAreRecordedVerbatim() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def f(a: int, /, b: str = 'x', *args: float, c: bytes, **kwargs: object) -> None:
                pass
            print(f.__annotations__)
            def q(x: "int") -> "str":
                pass
            print(q.__annotations__)
            """
        );

    /// <summary>
    /// The writable annotation surface: replacement, clearing, invalidation and the
    /// refusals for values that are not dicts or callables.
    /// </summary>
    [Fact]
    public Task AnnotationsCanBeReplacedClearedAndRecomputed() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            def f(x: int) -> str: pass
            f.__annotations__ = {"z": 1}
            print(f.__annotations__, f.__annotate__)
            try:
                f.__annotate__(1)
            except TypeError as e:
                print(e)
            f.__annotations__ = None
            print(f.__annotations__, f.__annotate__)
            try:
                f.__annotations__ = 5
            except TypeError as e:
                print(e)
            try:
                f.__annotate__ = 5
            except TypeError as e:
                print(e)
            try:
                del f.__annotate__
            except TypeError as e:
                print(e)
            def g(x: int): pass
            g.__annotate__ = lambda fmt: {"q": 1}
            print(g.__annotations__)
            g.__annotate__ = None
            print(g.__annotations__, g.__annotate__)
            def h(x: int): pass
            h.__annotate__ = lambda fmt: 7
            try:
                h.__annotations__
            except TypeError as e:
                print(e)
            def k(x: int): pass
            first = k.__annotations__
            k.__annotate__ = lambda fmt: {"r": 2}
            second = k.__annotations__
            print(first, second, second is k.__annotations__)
            del k.__annotations__
            print(k.__annotations__, k.__annotate__)
            """"
        );
}
