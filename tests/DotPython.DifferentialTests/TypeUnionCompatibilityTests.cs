using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TypeUnionCompatibilityTests
{
    [Fact]
    public Task UnionsSatisfyIsInstanceAndIsSubclass() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            print(isinstance(1, (int | str, float)))
            print(isinstance(1, (float, int | str)))
            """
        );

    [Fact]
    public Task UnionsRenderFlattenAndDeduplicate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(int | str)
            print(repr(int | str))
            print(str(int | str))
            print(int | str | bytes)
            print(int | int)
            print(repr(int | int | int))
            print((int | str) | bytes)
            print(int | (str | bytes))
            print(f'{int | str}')
            print([int | str])
            class A: pass
            class B(A): pass
            print(A | B)
            print(isinstance(B(), A | B))
            print(isinstance(A(), A | B))
            def nested():
                class Local: pass
                return Local
            print(nested() | int)
            """
        );

    [Fact]
    public Task NoneJoinsAUnionAsNoneType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(int | None)
            print(None | int)
            print((int | None) | None)
            print(type(None))
            print(isinstance(None, int | None))
            print(isinstance(1, int | None))
            print(issubclass(type(None), int | None))
            print(bool | None | int)
            print(repr(str | None))
            ui = int | None
            try: isinstance(1, ui)
            except TypeError as error: print(error)
            else: print(True)
            """
        );

    [Fact]
    public Task UnionEqualityAndHashingIgnoreMemberOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print((int | str) == (int | str))
            print((int | str) == (str | int))
            print((int | str) != (str | int))
            print((int | str) == int)
            print(int == (int | str))
            print(hash(int | str) == hash(str | int))
            print(isinstance(hash(int | str), int))
            mapping = {int | str: 'first'}
            print(mapping[str | int])
            print(len({int | str, str | int, bytes}))
            print(int | str in {str | int})
            """
        );

    [Fact]
    public Task UnionCarriesTypingUnionClassMetadata() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            union = int | str
            print(type(union).__name__)
            print(type(union).__qualname__)
            print(type(union).__module__)
            print(repr(type(union)))
            print(isinstance(union, type))
            print(type(union).__mro__)
            try: type(union)()
            except TypeError as error: print(error)
            """
        );

    [Fact]
    public Task BitwiseSetAndMappingOperatorsAreUnchanged() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print(1 | 2)
            print(True | False)
            print({1} | {2})
            print(frozenset([1]) | frozenset([2]))
            print({'a': 1} | {'b': 2})
            print({1: 'x'} | {2: 'y'})
            """
        );

    [Fact]
    public Task NonTypeOperandsReportCpythonOperandMessages() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def attempt(call):
                try: print(call())
                except TypeError as error: print(type(error).__name__, error)
            attempt(lambda: 1 | int)
            attempt(lambda: int | 1)
            attempt(lambda: None | None)
            attempt(lambda: 1.5 | int)
            attempt(lambda: [1] | None)
            attempt(lambda: None | 1)
            attempt(lambda: 'a' | int)
            """
        );

    [Fact]
    public Task UnionClassInformationValidatesItsArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def attempt(call):
                try: print(call())
                except Exception as error: print(type(error).__name__, error)
            def outcome(call):
                try: print(call())
                except Exception as error: print(type(error).__name__)
            attempt(lambda: isinstance(1, 1))
            attempt(lambda: issubclass(int, 1))
            attempt(lambda: issubclass(int | str, int))
            attempt(lambda: int(int | str))
            attempt(lambda: type(int | str)())
            outcome(lambda: (int | str).bit_length())
            outcome(lambda: len(int | str))
            outcome(lambda: 1 - (int | str))
            """
        );

    [Fact]
    public Task UnionsAcceptArbitraryClassesAndExceptionTypes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Custom: pass
            class DerivedCustom(Custom): pass
            class Failure(Exception): pass
            print(Custom | DerivedCustom)
            print(isinstance(Custom(), Custom | DerivedCustom))
            print(issubclass(Failure, int | Exception))
            print(isinstance(ValueError('x'), Exception | int))
            print(Failure | ValueError)
            class Meta(type):
                def __instancecheck__(cls, obj): return True
            class Always(metaclass=Meta): pass
            print(isinstance(123, int | Always))
            print(issubclass(int, Always))
            """
        );
}
