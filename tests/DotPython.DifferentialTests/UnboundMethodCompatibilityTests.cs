using Xunit;

namespace DotPython.DifferentialTests;

public sealed class UnboundMethodCompatibilityTests
{
    [Fact]
    public Task TypeMethodsBindAnExplicitReceiver() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task EveryBuiltinTypeAnswersItsMethods() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            for row in surface:
                for name in row[1:]:
                    probe(lambda row=row, name=name: type(getattr(row[0], name)).__name__)
            print(type(list.append), type(str.upper), type(int.bit_length))
            print(bytearray.append(bytearray(b'a'), 98))
            print(bytearray.upper(bytearray(b'ab')), frozenset.copy(frozenset([1])), list.copy([1, 2]))
            print(bytes.upper(b'ab'), set.discard({1, 2}, 1), str.format('{}', 5))
            print(int.to_bytes(5, 1, 'big'), float.is_integer(1.0), dict.update({}, {'a': 1}))
            probe(lambda: (lambda f: f(1))(list.append))
            probe(lambda: bytearray.upper(b'ab'))
            """
        );
}
