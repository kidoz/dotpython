using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class FailureWordingExecutionTests
{
    [Fact]
    public void OperatorFailuresNameTheTypes()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            probe(lambda: [1] + (1,))
            probe(lambda: (1,) + [1])
            probe(lambda: 'a' + 1)
            probe(lambda: 1 + 'a')
            probe(lambda: 1 - 'a')
            probe(lambda: [1] - (1,))
            probe(lambda: 'a' / 1)
            probe(lambda: 'a' // 1)
            probe(lambda: [1] % (1,))
            probe(lambda: 'a' ** 1)
            probe(lambda: [1] & (1,))
            probe(lambda: [1] | (1,))
            probe(lambda: [1] ^ (1,))
            probe(lambda: [1] << (1,))
            probe(lambda: 1.5 & 1)
            probe(lambda: None + 1)
            probe(lambda: {1} - 1)
            probe(lambda: {1} + {2})
            probe(lambda: [1] > (1,))
            probe(lambda: 'a' > 1)
            probe(lambda: {1} > 1)
            probe(lambda: 1 > 'a')
            probe(lambda: [1] <= (1,))
            probe(lambda: sorted([1, 'a']))
            number = 5
            items = [1]
            text = 'a'
            nothing = None
            descriptor = int.real
            for bad in (number, items, text, nothing, descriptor):
                probe(lambda bad=bad: bad())
            print([1] + [2], (1,) + (2,), 'a' + 'b', 1 + 1, 'a,b'.split(','))
            """
        );

        Assert.Equal(
            Lines(
                "TypeError can only concatenate list (not \"tuple\") to list",
                "TypeError can only concatenate tuple (not \"list\") to tuple",
                "TypeError can only concatenate str (not \"int\") to str",
                "TypeError unsupported operand type(s) for +: 'int' and 'str'",
                "TypeError unsupported operand type(s) for -: 'int' and 'str'",
                "TypeError unsupported operand type(s) for -: 'list' and 'tuple'",
                "TypeError unsupported operand type(s) for /: 'str' and 'int'",
                "TypeError unsupported operand type(s) for //: 'str' and 'int'",
                "TypeError unsupported operand type(s) for %: 'list' and 'tuple'",
                "TypeError unsupported operand type(s) for ** or pow(): 'str' and 'int'",
                "TypeError unsupported operand type(s) for &: 'list' and 'tuple'",
                "TypeError unsupported operand type(s) for |: 'list' and 'tuple'",
                "TypeError unsupported operand type(s) for ^: 'list' and 'tuple'",
                "TypeError unsupported operand type(s) for <<: 'list' and 'tuple'",
                "TypeError unsupported operand type(s) for &: 'float' and 'int'",
                "TypeError unsupported operand type(s) for +: 'NoneType' and 'int'",
                "TypeError unsupported operand type(s) for -: 'set' and 'int'",
                "TypeError unsupported operand type(s) for +: 'set' and 'set'",
                "TypeError '>' not supported between instances of 'list' and 'tuple'",
                "TypeError '>' not supported between instances of 'str' and 'int'",
                "TypeError '>' not supported between instances of 'set' and 'int'",
                "TypeError '>' not supported between instances of 'int' and 'str'",
                "TypeError '<=' not supported between instances of 'list' and 'tuple'",
                "TypeError '<' not supported between instances of 'str' and 'int'",
                "TypeError 'int' object is not callable",
                "TypeError 'list' object is not callable",
                "TypeError 'str' object is not callable",
                "TypeError 'NoneType' object is not callable",
                "TypeError 'getset_descriptor' object is not callable",
                "[1, 2] (1, 2) ab 2 ['a', 'b']"
            ),
            output
        );
    }

    [Fact]
    public void MethodArityUsesItsOwnSentence()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            probe(lambda: [1].append())
            probe(lambda: [1].append(1, 2))
            probe(lambda: [1].copy(1, 2))
            probe(lambda: [1].sort(1, 1, 1))
            probe(lambda: [1].sort(1))
            probe(lambda: [1].index())
            probe(lambda: [1].index(1, 1, 1, 1))
            probe(lambda: [1].pop(1, 2))
            probe(lambda: [1].insert(1))
            probe(lambda: 'a'.upper(1))
            probe(lambda: 'a'.center())
            probe(lambda: 'a'.center(1, 2, 3))
            probe(lambda: 'a'.split(1, 2, 3))
            probe(lambda: 'a'.find())
            probe(lambda: 'a'.strip(1, 2))
            probe(lambda: 'a'.replace(1))
            probe(lambda: 'a'.replace(zzz=1))
            probe(lambda: 'a'.replace(old='x', new='y'))
            probe(lambda: 'a'.encode(1, 2))
            probe(lambda: 'a'.encode('utf-8', 1))
            probe(lambda: 'a'.encode(1, 2, 3))
            probe(lambda: {1: 2}.get())
            probe(lambda: {1: 2}.get(1, 2, 3))
            probe(lambda: {1: 2}.pop(1, 2, 3))
            probe(lambda: {1: 2}.update(1, 2))
            probe(lambda: {1: 2}.keys(1))
            probe(lambda: {1}.add())
            probe(lambda: {1}.add(1, 2))
            probe(lambda: {1}.pop(1, 2))
            probe(lambda: (1,).count())
            probe(lambda: bytearray(b'a').append())
            probe(lambda: bytearray(b'a').append(1, 2))
            probe(lambda: 'a'.split(zzz=1))
            probe(lambda: 'a'.strip(zzz=1))
            probe(lambda: [1].append(x=1))
            print([1].pop(), 'a'.split(','), {1: 2}.get(1), {1}.add(2), [1].append(2))
            """
        );

        Assert.Equal(
            Lines(
                "TypeError list.append() takes exactly one argument (0 given)",
                "TypeError list.append() takes exactly one argument (2 given)",
                "TypeError list.copy() takes no arguments (2 given)",
                "TypeError sort() takes at most 2 arguments (3 given)",
                "TypeError sort() takes no positional arguments",
                "TypeError index expected at least 1 argument, got 0",
                "TypeError index expected at most 3 arguments, got 4",
                "TypeError pop expected at most 1 argument, got 2",
                "TypeError insert expected 2 arguments, got 1",
                "TypeError str.upper() takes no arguments (1 given)",
                "TypeError center expected at least 1 argument, got 0",
                "TypeError center expected at most 2 arguments, got 3",
                "TypeError split() takes at most 2 arguments (3 given)",
                "TypeError find expected at least 1 argument, got 0",
                "TypeError strip expected at most 1 argument, got 2",
                "TypeError replace() takes at least 2 positional arguments (1 given)",
                "TypeError replace() takes at least 2 positional arguments (0 given)",
                "TypeError replace() takes at least 2 positional arguments (0 given)",
                "TypeError encode() argument 'encoding' must be str, not int",
                "TypeError encode() argument 'errors' must be str, not int",
                "TypeError encode() takes at most 2 arguments (3 given)",
                "TypeError get expected at least 1 argument, got 0",
                "TypeError get expected at most 2 arguments, got 3",
                "TypeError pop expected at most 2 arguments, got 3",
                "TypeError update expected at most 1 argument, got 2",
                "TypeError dict.keys() takes no arguments (1 given)",
                "TypeError set.add() takes exactly one argument (0 given)",
                "TypeError set.add() takes exactly one argument (2 given)",
                "TypeError set.pop() takes no arguments (2 given)",
                "TypeError tuple.count() takes exactly one argument (0 given)",
                "TypeError bytearray.append() takes exactly one argument (0 given)",
                "TypeError bytearray.append() takes exactly one argument (2 given)",
                "TypeError split() got an unexpected keyword argument 'zzz'",
                "TypeError str.strip() takes no keyword arguments",
                "TypeError list.append() takes no keyword arguments",
                "1 ['a'] 2 None None"
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
