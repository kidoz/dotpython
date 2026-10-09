using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SlotDescriptorExecutionTests
{
    [Fact]
    public void ContainerSlotsAnswerWithAnExplicitReceiver()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "2 1 2 1",
                "1 2 2",
                "1 1 a",
                "97 2 [2, 3]",
                "[1, 2] (1,) ['a', 'b']",
                "[1] [1] [97]",
                "[2, 1] [1]",
                "True True True",
                "True True True",
                "[1, 'a'] 'a' b'a' 5",
                "1.5 [1] a b'a'",
                "[1, 2] (1, 2) ab b'ab'",
                "[1, 1] [1, 1] aa (1, 1)",
                "[1, 2, 1, 2] [1, 2, 1, 2] [1, 2, 1, 2] [1, 2, 1, 2]",
                "bytearray(b'abab') bytearray(b'abab') bytearray(b'abab') bytearray(b'abab')",
                "None [2] None [2]",
                "None {} None {}",
                "None None None",
                "((1,),) ('a',) (5,)",
                "a 5 1.50 1",
                "5 5 5.0 1 False",
                "5 1.5 -5 -1.5 -5",
                "-6 5 1152921504606846977 -6644214454873602895",
                "True True True True",
                "NotImplemented NotImplemented NotImplemented",
                "True True True False",
                "True False True",
                "True True True",
                "True True",
                "True True True True",
                "True True True True",
                "True True"
            ),
            output
        );
    }

    [Fact]
    public void SlotArgumentErrorsUseTheirOwnFamilies()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "TypeError descriptor '__len__' of 'list' object needs an argument",
                "TypeError descriptor '__len__' requires a 'list' object but received a 'int'",
                "TypeError expected 0 arguments, got 1",
                "TypeError wrapper __len__() takes no keyword arguments",
                "TypeError expected 1 argument, got 0",
                "TypeError expected 1 argument, got 2",
                "TypeError expected 1 argument, got 0",
                "TypeError descriptor '__eq__' requires a 'list' object but received a 'int'",
                "TypeError expected 1 argument, got 2",
                "TypeError expected 1 argument, got 0",
                "TypeError __setitem__ expected 2 arguments, got 1",
                "TypeError __setitem__ expected 2 arguments, got 3",
                "TypeError expected 1 argument, got 0",
                "TypeError list expected at most 1 argument, got 2",
                "TypeError list.__getitem__() takes exactly one argument (0 given)",
                "TypeError list.__getitem__() takes exactly one argument (2 given)",
                "TypeError dict.__contains__() takes exactly one argument (0 given)",
                "TypeError dict.__getitem__() takes exactly one argument (0 given)",
                "TypeError expected 0 arguments, got 1",
                "TypeError tuple.__getnewargs__() takes no arguments (1 given)",
                "TypeError list.__reversed__() takes no arguments (1 given)",
                "TypeError list.__getitem__() takes no keyword arguments",
                "TypeError wrapper __len__() takes no keyword arguments",
                "TypeError descriptor '__repr__' requires a 'int' object but received a 'str'",
                "TypeError descriptor '__hash__' requires a 'tuple' object but received a 'str'",
                "TypeError descriptor '__iter__' requires a 'list' object but received a 'str'",
                "None None None None",
                "False False",
                "True True True",
                "True False",
                "<slot wrapper '__len__' of 'list' objects> <method '__getitem__' of 'list' objects> <slot wrapper '__getitem__' of 'tuple' objects>",
                "<method '__contains__' of 'frozenset' objects> <slot wrapper '__repr__' of 'int' objects> <slot wrapper '__str__' of 'object' objects>",
                "<class 'wrapper_descriptor'> <class 'method_descriptor'> <class 'wrapper_descriptor'>"
            ),
            output
        );
    }

    [Fact]
    public void NumericMembersAreDescriptorsAtTheTypeLevel()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "<attribute 'real' of 'int' objects> <attribute 'imag' of 'int' objects> <attribute 'numerator' of 'int' objects> <attribute 'denominator' of 'int' objects>",
                "<attribute 'real' of 'float' objects> <attribute 'imag' of 'float' objects> <attribute 'real' of 'int' objects> <attribute 'numerator' of 'int' objects>",
                "<class 'getset_descriptor'> False real int.real <class 'int'>",
                "True True False",
                "5 0.0 1 7",
                "True",
                "TypeError __get__ expected at least 1 argument, got 0",
                "TypeError __get__ expected at most 2 arguments, got 3",
                "TypeError descriptor 'real' for 'int' objects doesn't apply to a 'str' object",
                "TypeError __get__ expected at most 2 arguments, got 3",
                "AttributeError type object 'float' has no attribute 'numerator'",
                "1",
                "5"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
