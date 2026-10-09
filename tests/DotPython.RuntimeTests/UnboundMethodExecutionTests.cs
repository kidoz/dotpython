using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class UnboundMethodExecutionTests
{
    [Fact]
    public void TypeMethodsBindAnExplicitReceiver()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(list.append([1], 2), str.upper('a'), int.bit_length(5), float.hex(1.5))
            print(dict.get({1: 2}, 1), dict.get({1: 2}, 3, 9), bytes.hex(b'ab'), tuple.count((1, 2), 1))
            print(set.add({1}, 2), frozenset.union(frozenset([1]), [2]), bool.bit_length(True), str.join(',', ['a', 'b']))
            print(str.split('a,b', sep=','), list.sort([2, 1], reverse=True), dict.pop({1: 2}, 1))
            print(list.append is list.append, callable(list.append), [].append is list.append)
            f = list.append
            f([1], 2)
            print(list.append.__name__, list.append.__qualname__, list.append.__objclass__)
            print(list.append.__get__(None, list) is list.append)
            print(list.append.__get__([1])(2))
            probe(lambda: list.append())
            probe(lambda: str.upper())
            probe(lambda: int.bit_length())
            probe(lambda: list.append((1,), 2))
            probe(lambda: str.upper(1))
            probe(lambda: int.bit_length('a'))
            probe(lambda: float.hex('x'))
            probe(lambda: tuple.count('a', 'a'))
            probe(lambda: list.append(None, 1))
            probe(lambda: set.union(frozenset([1]), [2]))
            probe(lambda: frozenset.union({1}, [2]))
            probe(lambda: set.add(frozenset([1]), 2))
            probe(lambda: list.append([1], object=2))
            probe(lambda: [1].append(object=2))
            probe(lambda: list.append.__self__)
            probe(lambda: list.append.__get__())
            probe(lambda: list.append.__get__(1, 2, 3))
            probe(lambda: list.append.__get__(1, list))
            probe(lambda: list.append.__get__(None, None))
            probe(lambda: frozenset.add)
            probe(lambda: [].append is list.append)
            """
        );

        Assert.Equal(
            Lines(
                "None A 3 0x1.8000000000000p+0",
                "2 9 6162 1",
                "None frozenset({1, 2}) 1 a,b",
                "['a', 'b'] None 2",
                "True True False",
                "append list.append <class 'list'>",
                "True",
                "None",
                "TypeError unbound method list.append() needs an argument",
                "TypeError unbound method str.upper() needs an argument",
                "TypeError unbound method int.bit_length() needs an argument",
                "TypeError descriptor 'append' for 'list' objects doesn't apply to a 'tuple' object",
                "TypeError descriptor 'upper' for 'str' objects doesn't apply to a 'int' object",
                "TypeError descriptor 'bit_length' for 'int' objects doesn't apply to a 'str' object",
                "TypeError descriptor 'hex' for 'float' objects doesn't apply to a 'str' object",
                "TypeError descriptor 'count' for 'tuple' objects doesn't apply to a 'str' object",
                "TypeError descriptor 'append' for 'list' objects doesn't apply to a 'NoneType' object",
                "TypeError descriptor 'union' for 'set' objects doesn't apply to a 'frozenset' object",
                "TypeError descriptor 'union' for 'frozenset' objects doesn't apply to a 'set' object",
                "TypeError descriptor 'add' for 'set' objects doesn't apply to a 'frozenset' object",
                "TypeError list.append() takes no keyword arguments",
                "TypeError list.append() takes no keyword arguments",
                "AttributeError 'method_descriptor' object has no attribute '__self__'",
                "TypeError __get__ expected at least 1 argument, got 0",
                "TypeError __get__ expected at most 2 arguments, got 3",
                "TypeError descriptor 'append' for 'list' objects doesn't apply to a 'int' object",
                "TypeError __get__(None, None) is invalid",
                "AttributeError type object 'frozenset' has no attribute 'add'",
                "False"
            ),
            output
        );
    }

    [Fact]
    public void EveryBuiltinTypeAnswersItsMethods()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            surface = [
                (list, 'append', 'extend', 'sort', 'copy'),
                (tuple, 'count', 'index'),
                (str, 'upper', 'split', 'join', 'format'),
                (bytes, 'hex', 'decode', 'split'),
                (bytearray, 'append', 'upper', 'extend'),
                (dict, 'get', 'pop', 'update', 'copy'),
                (set, 'add', 'discard', 'union', 'copy'),
                (frozenset, 'copy', 'union', 'issubset'),
                (int, 'bit_length', 'to_bytes', 'as_integer_ratio'),
                (float, 'hex', 'is_integer', 'as_integer_ratio'),
            ]
            for row in surface:
                for name in row[1:]:
                    probe(lambda row=row, name=name: getattr(row[0], name))
            print(type(list.append), type(str.upper), type(int.bit_length))
            print(bytearray.append(bytearray(b'a'), 98))
            print(bytearray.upper(bytearray(b'ab')), frozenset.copy(frozenset([1])), list.copy([1, 2]))
            print(bytes.upper(b'ab'), set.discard({1, 2}, 1), str.format('{}', 5))
            print(int.to_bytes(5, 1, 'big'), float.is_integer(1.0), dict.update({}, {'a': 1}))
            probe(lambda: (lambda f: f(1))(list.append))
            probe(lambda: bytearray.upper(b'ab'))
            """
        );

        Assert.Equal(
            Lines(
                "<method 'append' of 'list' objects>",
                "<method 'extend' of 'list' objects>",
                "<method 'sort' of 'list' objects>",
                "<method 'copy' of 'list' objects>",
                "<method 'count' of 'tuple' objects>",
                "<method 'index' of 'tuple' objects>",
                "<method 'upper' of 'str' objects>",
                "<method 'split' of 'str' objects>",
                "<method 'join' of 'str' objects>",
                "<method 'format' of 'str' objects>",
                "<method 'hex' of 'bytes' objects>",
                "<method 'decode' of 'bytes' objects>",
                "<method 'split' of 'bytes' objects>",
                "<method 'append' of 'bytearray' objects>",
                "<method 'upper' of 'bytearray' objects>",
                "<method 'extend' of 'bytearray' objects>",
                "<method 'get' of 'dict' objects>",
                "<method 'pop' of 'dict' objects>",
                "<method 'update' of 'dict' objects>",
                "<method 'copy' of 'dict' objects>",
                "<method 'add' of 'set' objects>",
                "<method 'discard' of 'set' objects>",
                "<method 'union' of 'set' objects>",
                "<method 'copy' of 'set' objects>",
                "<method 'copy' of 'frozenset' objects>",
                "<method 'union' of 'frozenset' objects>",
                "<method 'issubset' of 'frozenset' objects>",
                "<method 'bit_length' of 'int' objects>",
                "<method 'to_bytes' of 'int' objects>",
                "<method 'as_integer_ratio' of 'int' objects>",
                "<method 'hex' of 'float' objects>",
                "<method 'is_integer' of 'float' objects>",
                "<method 'as_integer_ratio' of 'float' objects>",
                "<class 'method_descriptor'> <class 'method_descriptor'> <class 'method_descriptor'>",
                "None",
                "bytearray(b'AB') frozenset({1}) [1, 2]",
                "b'AB' None 5",
                """b'\x05' True None""",
                "TypeError descriptor 'append' for 'list' objects doesn't apply to a 'int' object",
                "TypeError descriptor 'upper' for 'bytearray' objects doesn't apply to a 'bytes' object"
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
