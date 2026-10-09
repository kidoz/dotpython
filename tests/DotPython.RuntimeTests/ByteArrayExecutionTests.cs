using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ByteArrayExecutionTests
{
    [Fact]
    public void ConstructionCoversEverySourceForm()
    {
        var output = Run(
            """
            print(bytearray(), bytearray(3), bytearray(b'ab'), bytearray('café', 'utf-8'))
            print(bytearray([97, 98]), bytearray((97,)), bytearray(range(2)))
            print(repr(bytearray(b'a\x00\xff')), type(bytearray()).__name__)
            """
        );

        Assert.Equal(
            Lines(
                "bytearray(b'') bytearray(b'\\x00\\x00\\x00') bytearray(b'ab') bytearray(b'caf\\xc3\\xa9')",
                "bytearray(b'ab') bytearray(b'a') bytearray(b'\\x00\\x01')",
                "bytearray(b'a\\x00\\xff') bytearray"
            ),
            output
        );
    }

    [Fact]
    public void SequenceBehaviourMatchesBytesAndIsUnhashable()
    {
        var output = Run(
            """
            b = bytearray(b'abc')
            print(len(b), b[0], b[-1], b[1:], list(b), list(reversed(b)))
            print(97 in b, b'bc' in b, bytearray(b'ab') == b'ab', bytearray(b'ab') < b'ac')
            print(bytearray(b'a') + b'b', bytearray(b'ab') * 2, bool(bytearray()))
            for bad in (hash,):
                try:
                    bad(bytearray(b'ab'))
                except TypeError as error:
                    print(type(error).__name__, error)
            """
        );

        Assert.Equal(
            Lines(
                "3 97 99 bytearray(b'bc') [97, 98, 99] [99, 98, 97]",
                "True True True True",
                "bytearray(b'ab') bytearray(b'abab') False",
                "TypeError unhashable type: 'bytearray'"
            ),
            output
        );
    }

    [Fact]
    public void MutationChangesContentsAndKeepsIdentity()
    {
        var output = Run(
            """
            b = bytearray(b'ab')
            c = b
            b.append(99)
            b += b'd'
            b *= 2
            b[0] = 90
            del b[0]
            print(repr(b), c is b)
            print(repr(b.copy()), b.copy() is b)
            b.reverse()
            print(repr(b), repr(b.pop()), repr(b.clear()) + ' ' + repr(b))
            """
        );

        Assert.Equal(
            Lines(
                "bytearray(b'bcdabcd') True",
                "bytearray(b'bcdabcd') False",
                "bytearray(b'dcbadcb') 98 None bytearray(b'')"
            ),
            output
        );
    }

    [Fact]
    public void MethodsReturnTheMutableTypeAndBytesLikeConsumersAcceptIt()
    {
        var output = Run(
            """
            import json, codecs
            print(repr(bytearray(b'ab').upper()), type(bytearray(b'a,b').split(b',')[0]).__name__)
            print(bytearray(b'ab').decode(), bytearray(b'ab').hex())
            print(json.loads(bytearray(b'[1, 2]')), codecs.getdecoder('utf-8')(bytearray(b'ab')))
            print(type(bytearray(b'a').center(3, b'-')).__name__)
            """
        );

        Assert.Equal(
            Lines("bytearray(b'AB') bytearray", "ab 6162", "[1, 2] ('ab', 2)", "bytearray"),
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
