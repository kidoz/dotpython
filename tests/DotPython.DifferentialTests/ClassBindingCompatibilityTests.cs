using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ClassBindingCompatibilityTests
{
    [Fact]
    public Task PrivateNamesFollowLexicalClassAcrossNestedScopes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class _Base:
                __value = 10
                def __method(self, __amount, *, __extra=2):
                    __local = __amount + __extra
                    def nested():
                        return __local + self.__value
                    return nested()
                def call(self):
                    return self.__method(3), self.__method(3, _Base__extra=4)
                def comprehension(self):
                    return [__item + self.__value for __item in [1, 2]]
                def closure(self):
                    __value = 9
                    return lambda __n: __n + __value
                class __Nested:
                    __value = 20
                    def get(self):
                        return self.__value
            class Child(_Base):
                __value = 99
            value = Child()
            print(value.call(), value.comprehension(), value.closure()(1))
            print(value._Base__value, value._Child__value)
            print(_Base._Base__Nested().get())
            print(_Base._Base__Nested.__name__, _Base._Base__method.__name__)
            class ___:
                __value = 42
            print(___.__value)
            """
        );

    [Fact]
    public Task PrivateNamesApplyToDeclarationsButNotKeywordLabelsOrStrings() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            _C__global = 1
            class C:
                def change(self):
                    global __global
                    __global = 2
                    __local = 3
                    def inner():
                        nonlocal __local
                        __local = 4
                    inner()
                    return __local
                def keyword(self, __arg):
                    return __arg
            value = C()
            print(value.change(), _C__global)
            print(value.keyword(_C__arg=5))
            try:
                value.keyword(__arg=5)
            except TypeError:
                print('literal keyword label')
            value.__literal = 6
            print(getattr(value, '__literal'))
            """
        );

    [Fact]
    public Task ClassCellsPreserveLexicalClassWhenNamesAreRebound() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base:
                def value(self):
                    return 10
            class Child(Base):
                __class__ = 'namespace value'
                def value(self):
                    return super().value() + 1
                def lexical(self):
                    def nested():
                        return __class__
                    return nested()
                def collected(self):
                    return [__class__ for item in [1, 2]]
                def closure(self):
                    return lambda: __class__
            Original = Child
            Child = None
            value = Original()
            print(value.value(), value.lexical() is Original)
            print([item is Original for item in value.collected()])
            print(value.closure()() is Original, value.__class__)
            def decorate(cls):
                return cls()
            @decorate
            class Wrapped:
                def lexical(self):
                    return __class__
            print(Wrapped.lexical() is type(Wrapped))
            """
        );

    [Fact]
    public Task ClassBodyCanReadOuterClassCellWhileMethodsCaptureNewClass() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def outer():
                __class__ = 'outer value'
                class C:
                    print(__class__)
                    def lexical(self):
                        return __class__
                return C
            C = outer()
            print(C().lexical() is C)
            """
        );

    [Fact]
    public Task ClassCellIsUnboundWhileClassBodyRuns() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example:
                def lexical():
                    return __class__
                try:
                    lexical()
                except NameError:
                    print('cell not populated yet')
            print(Example.lexical() is Example)
            """
        );
}
