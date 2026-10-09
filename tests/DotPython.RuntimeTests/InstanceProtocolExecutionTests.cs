using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class InstanceProtocolExecutionTests
{
    [Fact]
    public void InstanceDunderMethodsAnswerDirectly()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print([1, 2].__len__(), [1, 2].__getitem__(0), list([1, 2].__iter__()), 'a'.__add__('b'))
            print({1: 2}.__getitem__(1), (1,).__hash__() == hash((1,)), b'ab'.__getitem__(0), (-1.5).__abs__())
            print((5).__add__(1), (5).__radd__(1), (5).__divmod__(2), (1.5).__add__(1), True.__add__(1))
            print((5).__and__(3), (5).__lshift__(1), (5).__invert__(), (5).__index__(), (5).__truediv__(2))
            print([1].__eq__([1]), [1].__eq__((1,)), (5).__add__('a'), (5).__and__(1.5), (1.5).__add__('a'))
            print([].__hash__, {}.__hash__)
            box = [1, 2]
            print(box.__setitem__(0, 5), box, box.__delitem__(0), box)
            items = [1]
            print(items.__iadd__([2]), items, items.__imul__(2), items)
            probe(lambda: [1].__len__(1))
            probe(lambda: [1].__len__(x=1))
            probe(lambda: [1].__getitem__())
            probe(lambda: [1].__getitem__(0, 1))
            probe(lambda: [1].__contains__())
            probe(lambda: [1].__setitem__(0))
            probe(lambda: (5).__add__())
            probe(lambda: (5).__add__(x=1))
            probe(lambda: (5).__truediv__(0))
            probe(lambda: (5).__lshift__(-1))
            probe(lambda: (5).__divmod__(1.5))
            """
        );

        Assert.Equal(
            Lines(
                "2 1 [1, 2] ab",
                "2 True 97 1.5",
                "6 6 (2, 1) 2.5 2",
                "1 10 -6 5 2.5",
                "True NotImplemented NotImplemented NotImplemented NotImplemented",
                "None None",
                "None [2] None [2]",
                "[1, 2, 1, 2] [1, 2, 1, 2] [1, 2, 1, 2] [1, 2, 1, 2]",
                "TypeError expected 0 arguments, got 1",
                "TypeError wrapper __len__() takes no keyword arguments",
                "TypeError list.__getitem__() takes exactly one argument (0 given)",
                "TypeError list.__getitem__() takes exactly one argument (2 given)",
                "TypeError expected 1 argument, got 0",
                "TypeError __setitem__ expected 2 arguments, got 1",
                "TypeError expected 1 argument, got 0",
                "TypeError wrapper __add__() takes no keyword arguments",
                "ZeroDivisionError division by zero",
                "ValueError negative shift count",
                "NotImplemented"
            ),
            output
        );
    }

    [Fact]
    public void BytesKeywordsAndPercentFormatting()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(b'a,b'.split(sep=b','), b'a,b,c'.split(b',', maxsplit=1), b'a,b,c'.rsplit(maxsplit=1))
            print(b'a\n'.splitlines(keepends=True), b'a\tb'.expandtabs(tabsize=4))
            print(b'abcd'.hex(':', 2), b'abcdefg'.hex(':', -2), b'ab'.hex(), b'abcd'.hex('.', bytes_per_sep=2))
            print(b'%s' % b'a', b'%b' % b'a', b'%d' % 42, b'%#X' % 255, b'%f' % 1.5, b'%c' % 65)
            print(b'%5s|%-5s|%05d' % (b'a', b'b', 42), b'%.2s' % b'abcd', b'%(k)s' % {b'k': b'v'})
            print(b'100%%' % (), b'%s %s' % (b'a', b'b'), bytearray(b'%d' % 7))
            for bad in (lambda: b'a,b'.split(sep=b','), lambda: b'%s' % 'a', lambda: b'%d' % 'x',
                        lambda: b'%c' % 300, lambda: b'%c' % 'A', lambda: b'%y' % b'a',
                        lambda: b'%s %s' % b'a', lambda: b'%s' % (b'a', b'b'), lambda: b'%(k)s' % b'a',
                        lambda: b'abcd'.hex(':', 2, 3), lambda: b'ab'.hex(sep=b'::'), lambda: b'ab'.hex(':', ())):
                probe(bad)
            probe(lambda: b'abc'.hex(sep=':'))
            probe(lambda: b'abc'.hex(bytes_per_sep=2))
            """
        );

        Assert.Equal(
            Lines(
                "[b'a', b'b'] [b'a', b'b,c'] [b'a,b,c']",
                "[b'a\\n'] b'a   b'",
                "6162:6364 6162:6364:6566:67 6162 6162.6364",
                "b'a' b'a' b'42' b'0XFF' b'1.500000' b'A'",
                "b'    a|b    |00042' b'ab' b'v'",
                "b'100%' b'a b' bytearray(b'7')",
                "[b'a', b'b']",
                "TypeError %b requires a bytes-like object, or an object that implements __bytes__, not 'str'",
                "TypeError %d format: a real number is required, not str",
                "OverflowError %c arg not in range(256)",
                "TypeError %c requires an integer in range(256) or a single byte, not str",
                "ValueError unsupported format character 'y' (0x79) at index 1",
                "TypeError not enough arguments for format string",
                "TypeError not all arguments converted during bytes formatting",
                "TypeError format requires a mapping",
                "TypeError hex() takes at most 2 arguments (3 given)",
                "ValueError sep must be length 1.",
                "TypeError 'tuple' object cannot be interpreted as an integer",
                "'61:62:63'",
                "'616263'"
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
