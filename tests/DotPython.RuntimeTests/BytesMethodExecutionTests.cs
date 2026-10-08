using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BytesMethodExecutionTests
{
    [Fact]
    public void CaseMappingAndStrippingStayByteOriented()
    {
        var output = Run(
            """
            print(b'aBc dEf'.title(), b'aBc dEf'.swapcase(), b'aBc dEf'.capitalize())
            print(b'caf\xe9'.upper(), b'\tab\n '.strip(), b'xxabxx'.strip(b'x'))
            """
        );

        Assert.Equal(Lines("b'Abc Def' b'AbC DeF' b'Abc def'", "b'CAF\\xe9' b'ab' b'ab'"), output);
    }

    [Fact]
    public void SearchAcceptsByteValuesAndReportsMissingMatches()
    {
        var output = Run(
            """
            print(b'abcabc'.find(b'b'), b'abc'.find(b'z'), b'abc'.find(98))
            print(b'abcabc'.count(b'a'), b'abc'.count(b''), b'aaa'.count(b'a', 1, 2))
            try:
                b'abc'.index(b'z')
            except ValueError as error:
                print(type(error).__name__, error)
            print(b'abcabc'.find(b'a', 1), b'abc'.startswith((b'z', b'a')), b'abc'.endswith(b'c'))
            """
        );

        Assert.Equal(
            Lines("1 -1 1", "2 4 1", "ValueError subsection not found", "3 True True"),
            output
        );
    }

    [Fact]
    public void ReplacementSplittingAndJoiningMatchTheSeparatorRules()
    {
        var output = Run(
            """
            print(b'abcabc'.replace(b'a', b'X', 1), b'abc'.replace(b'', b'-'))
            print(b'a b  c'.split(), b'a,b,,c'.split(b','), b'a,b,c'.rsplit(b',', 1))
            print(b'a\nb\r\nc'.splitlines(), b','.join([b'a', b'b']))
            print(b'a=b'.partition(b'='), b'abc'.partition(b'='), b'abc'.rpartition(b'='))
            """
        );

        Assert.Equal(
            Lines(
                "b'Xbcabc' b'-a-b-c-'",
                "[b'a', b'b', b'c'] [b'a', b'b', b'', b'c'] [b'a,b', b'c']",
                "[b'a', b'b', b'c'] b'a,b'",
                "(b'a', b'=', b'b') (b'abc', b'', b'') (b'', b'', b'abc')"
            ),
            output
        );
    }

    [Fact]
    public void PaddingZeroFillAndTabsNeedASingleByte()
    {
        var output = Run(
            """
            print(b'abc'.center(6, b'-'), b'-abc'.zfill(5), b'a\tb'.expandtabs(4))
            print(b'abc'.removeprefix(b'a'), b'abc'.removesuffix(b'c'))
            try:
                b'abc'.ljust(6, b'ab')
            except TypeError as error:
                print(type(error).__name__, error)
            """
        );

        Assert.Equal(
            Lines(
                "b'-abc--' b'-0abc' b'a   b'",
                "b'bc' b'ab'",
                "TypeError ljust(): argument 2 must be a byte string of length 1, not a bytes object of length 2"
            ),
            output
        );
    }

    [Fact]
    public void HexAndTranslationRoundTripThroughTheType()
    {
        var output = Run(
            """
            print(b'ab'.hex(), b'abcd'.hex(':'), bytes.fromhex('41 42'))
            print(b'abc'.translate(bytes.maketrans(b'ab', b'xy')))
            try:
                bytes.fromhex('abc')
            except ValueError as error:
                print(type(error).__name__, error)
            try:
                bytes.fromhex('41 4 2')
            except ValueError as error:
                print(type(error).__name__, error)
            try:
                bytes.maketrans(b'ab', b'xyz')
            except ValueError as error:
                print(type(error).__name__, error)
            """
        );

        Assert.Equal(
            Lines(
                "6162 61:62:63:64 b'AB'",
                "b'xyc'",
                "ValueError fromhex() arg must contain an even number of hexadecimal digits",
                "ValueError non-hexadecimal number found in fromhex() arg at position 4",
                "ValueError maketrans arguments must have same length"
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
