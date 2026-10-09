using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SlotDescriptorCompatibilityTests
{
    [Fact]
    public Task ContainerSlotsAnswerWithAnExplicitReceiver() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(list.__len__([1, 2]), tuple.__len__((1,)), str.__len__('ab'), dict.__len__({1: 2}))
            print(set.__len__({1}), bytes.__len__(b'ab'), bytearray.__len__(bytearray(b'ab')))
            print(list.__getitem__([1, 2], 0), tuple.__getitem__((1, 2), 0), str.__getitem__('ab', 0))
            print(bytes.__getitem__(b'ab', 0), dict.__getitem__({1: 2}, 1), list.__getitem__([1, 2, 3], slice(1, 3)))
            print(list(list.__iter__([1, 2])), tuple(tuple.__iter__((1,))), list(str.__iter__('ab')))
            print(list(dict.__iter__({1: 2})), list(set.__iter__({1})), list(bytearray.__iter__(bytearray(b'a'))))
            print(list(list.__reversed__([1, 2])), list(dict.__reversed__({1: 2})))
            print(list.__contains__([1, 2], 2), str.__contains__('ab', 'a'), bytes.__contains__(b'ab', 97))
            print(dict.__contains__({1: 2}, 1), set.__contains__({1}, 1), frozenset.__contains__(frozenset([1]), 1))
            print(list.__repr__([1, 'a']), str.__repr__('a'), bytes.__repr__(b'a'), int.__repr__(5))
            print(float.__repr__(1.5), list.__str__([1]), str.__str__('a'), bytes.__str__(b'a'))
            print(list.__add__([1], [2]), tuple.__add__((1,), (2,)), str.__add__('a', 'b'), bytes.__add__(b'a', b'b'))
            print(list.__mul__([1], 2), list.__rmul__([1], 2), str.__mul__('a', 2), tuple.__rmul__((1,), 2))
            items = [1]
            print(list.__iadd__(items, [2]), items, list.__imul__(items, 2), items)
            data = bytearray(b'a')
            print(bytearray.__iadd__(data, b'b'), data, bytearray.__imul__(data, 2), data)
            box = [1, 2]
            print(list.__setitem__(box, 0, 5), box, list.__delitem__(box, 0), box)
            mapping = {}
            print(dict.__setitem__(mapping, 1, 2), mapping, dict.__delitem__(mapping, 1), mapping)
            print(list.__init__([1], [2]), dict.__init__({}, {1: 2}), set.__init__({1}, {2}))
            print(tuple.__getnewargs__((1,)), str.__getnewargs__('a'), int.__getnewargs__(5))
            print(str.__format__('a', ''), int.__format__(5, 'x'), float.__format__(1.5, '.2f'), str.__mod__('%s', 1))
            print(int.__index__(5), int.__int__(5), int.__float__(5), float.__int__(1.5), int.__bool__(0))
            print(int.__abs__(-5), float.__abs__(-1.5), int.__neg__(5), float.__neg__(1.5), int.__pos__(-5))
            print(int.__invert__(5), int.__hash__(5), float.__hash__(1.5), tuple.__hash__((1,)))
            print(tuple.__eq__((1,), (1,)), str.__eq__('a', 'a'), int.__eq__(1, 1), float.__eq__(1.0, 1))
            print(list.__eq__([1], (1,)), list.__lt__([1], (2,)), dict.__lt__({1: 2}, {3: 4}))
            print(list.__lt__([1], [2]), str.__gt__('b', 'a'), int.__lt__(1, 2), float.__lt__(1.0, 1))
            print(set.__le__({1}, {1, 2}), set.__lt__({1}, {1}), frozenset.__lt__(frozenset([1]), {1, 2}))
            print(set.__or__({1}, {2}) == {1, 2}, set.__and__({1, 2}, {2}) == {2}, set.__sub__({1, 2}, {2}) == {1})
            print(frozenset.__or__(frozenset([1]), {2}) == frozenset([1, 2]), dict.__or__({1: 2}, {3: 4}) == {1: 2, 3: 4})
            first = {1}
            print(set.__ior__(first, {2}) == {1, 2}, first == {1, 2}, set.__isub__(first, {1}) == {2}, first == {2})
            second = {1, 2}
            print(set.__iand__(second, {2}) == {2}, second == {2}, set.__ixor__(second, {5}) == {2, 5}, second == {2, 5})
            third = {1: 2}
            print(dict.__ior__(third, {3: 4}) == {1: 2, 3: 4}, third == {1: 2, 3: 4})
            """
        );

    [Fact]
    public Task SlotArgumentErrorsUseTheirOwnFamilies() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            probe(lambda: list.__len__())
            probe(lambda: list.__len__(1))
            probe(lambda: list.__len__([1], 2))
            probe(lambda: list.__len__([1], x=1))
            probe(lambda: list.__contains__([1]))
            probe(lambda: list.__contains__([1], 1, 2))
            probe(lambda: list.__eq__([1]))
            probe(lambda: list.__eq__(1, 1))
            probe(lambda: list.__eq__([1], [1], 2))
            probe(lambda: list.__add__([1]))
            probe(lambda: list.__setitem__([1], 0))
            probe(lambda: list.__setitem__([1], 0, 2, 3))
            probe(lambda: list.__delitem__([1]))
            probe(lambda: list.__init__([1], [2], 3))
            probe(lambda: list.__getitem__([1, 2]))
            probe(lambda: list.__getitem__([1], 0, 2))
            probe(lambda: dict.__contains__({1: 2}))
            probe(lambda: dict.__getitem__({1: 2}))
            probe(lambda: int.__index__(5, 2))
            probe(lambda: tuple.__getnewargs__((1,), 2))
            probe(lambda: list.__reversed__([1], 2))
            probe(lambda: list.__getitem__([1], 0, x=1))
            probe(lambda: list.__len__([1], x=1))
            probe(lambda: int.__repr__('a'))
            probe(lambda: tuple.__hash__('a'))
            probe(lambda: list.__iter__('a'))
            print(list.__hash__, dict.__hash__, set.__hash__, bytearray.__hash__)
            print(tuple.__hash__ is None, str.__hash__ is None)
            print(callable(list.__len__), callable(tuple.__hash__), list.__len__ is list.__len__)
            print(list.__len__ == list.__len__, list.__len__ is list.__getitem__)
            print(repr(list.__len__), repr(list.__getitem__), repr(tuple.__getitem__))
            print(repr(frozenset.__contains__), repr(int.__repr__), repr(list.__str__))
            print(type(list.__len__), type(list.__getitem__), type(str.__eq__))
            """
        );

    [Fact]
    public Task NumericMembersAreDescriptorsAtTheTypeLevel() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(repr(int.real), repr(int.imag), repr(int.numerator), repr(int.denominator))
            print(repr(float.real), repr(float.imag), repr(bool.real), repr(bool.numerator))
            print(type(int.real), callable(int.real), int.real.__name__, int.real.__qualname__, int.real.__objclass__)
            print(int.real is int.real, float.imag is float.imag, int.real is float.real)
            print(int.real.__get__(5), float.imag.__get__(1.5), bool.real.__get__(True), int.numerator.__get__(7))
            print(int.real.__get__(None, int) is int.real)
            probe(lambda: int.real.__get__())
            probe(lambda: int.real.__get__(1, 2, 3))
            probe(lambda: int.real.__get__('a', int))
            probe(lambda: int.real.__get__(5, int, 1))
            probe(lambda: float.numerator)
            probe(lambda: bool.denominator.__get__(True))
            probe(lambda: (5).real, )
            """
        );
}
