using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ObjectMemberCompatibilityTests
{
    [Fact]
    public Task ObjectFormattingAttributeAndState() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(list.__format__([1, 2], ''), dict.__format__({1: 2}, ''), tuple.__format__((1,), ''))
            print([1, 2].__format__(''), {1: 2}.__format__(''), b'ab'.__format__(''), bytearray(b'ab').__format__(''))
            print(str.__format__('a', '>3'), int.__format__(5, 'x'), float.__format__(1.5, '.2f'), True.__format__(''))
            probe(lambda: [1].__format__('>10'))
            probe(lambda: {1: 2}.__format__('d'))
            probe(lambda: [1].__format__(5))
            probe(lambda: [1].__format__())
            probe(lambda: list.__format__(1))
            print(repr(list.__format__), repr(str.__format__), type(list.__format__).__name__)
            probe(lambda: list.__getattribute__([1], 5))
            probe(lambda: list.__getattribute__([1], 'a', 2))
            probe(lambda: list.__getattribute__())
            probe(lambda: list.__getattribute__([1], 'nope'))
            probe(lambda: [1].__getattribute__('nope'))
            print(list.__getstate__([1]), (5).__getstate__(), str.__getstate__('a'), set.__getstate__({1}))
            probe(lambda: list.__getstate__([1], 2))
            probe(lambda: list.__getstate__())
            print(repr(list.__getstate__), type(list.__getstate__).__name__)
            print(tuple.__init__((1,)), tuple.__init__((1,), (2,)), int.__init__(5))
            print(repr(tuple.__init__), type(tuple.__init__).__name__)
            """
        );

    [Fact]
    public Task TypeConstructorEntryPoints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(list.__new__(list), dict.__new__(dict), int.__new__(int), str.__new__(str))
            print(list.__new__(list, [1, 2]), tuple.__new__(tuple, (1,)))
            print(frozenset.__new__(frozenset), bytearray.__new__(bytearray), set.__new__(set))
            probe(lambda: list.__new__())
            probe(lambda: list.__new__(int))
            probe(lambda: list.__new__(5))
            probe(lambda: dict.__new__(set))
            probe(lambda: list.__new__(cls=list))
            probe(lambda: list.__new__(list, [1], 2))
            print(list.__class_getitem__(int), tuple.__class_getitem__(int), dict.__class_getitem__((str, int)))
            print(set.__class_getitem__(int), frozenset.__class_getitem__(int))
            print(list.__class_getitem__((int, str)))
            probe(lambda: list.__class_getitem__())
            probe(lambda: list.__class_getitem__(int, str))
            probe(lambda: str.__class_getitem__)
            probe(lambda: int.__class_getitem__)
            print(callable(list.__new__), callable(list.__class_getitem__), type(list.__new__).__name__)
            """
        );
}
