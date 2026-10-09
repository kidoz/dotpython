using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class IntFloatMethodExecutionTests
{
    [Fact]
    public void BitInspectionAndMembersFollowTheValue()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print((0, 255, 256, -256, 2 ** 100)[0].bit_length(), (255).bit_count(), (256).bit_count(), (-255).bit_count())
            print((2 ** 100).bit_length(), True.bit_length(), True.is_integer())
            print((5).as_integer_ratio(), (-5).conjugate(), (5).real, (5).imag, (5).numerator, (5).denominator)
            probe(lambda: (5).bit_length(1))
            probe(lambda: (5).bit_length(x=1))
            """
        );

        Assert.Equal(
            Lines(
                "0 8 1 8",
                "101 1 True",
                "(5, 1) -5 5 0 5 1",
                "TypeError int.bit_length() takes no arguments (1 given)",
                "TypeError int.bit_length() takes no keyword arguments"
            ),
            output
        );
    }

    [Fact]
    public void ByteConversionRoundTripsAndReportsItsOwnErrors()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print((1024).to_bytes(2, 'big'), (1024).to_bytes(2, 'little'), (5).to_bytes())
            print((-256).to_bytes(2, 'big', signed=True), (0).to_bytes(0, 'big'))
            print(int.from_bytes(b'\x04\x00', 'little'), int.from_bytes(b'\xff', signed=True), int.from_bytes(b''))
            print(int.from_bytes([1, 2], 'big'), int.from_bytes({1: 2}, 'big'), (5).from_bytes(b'\x04'))
            probe(lambda: (1).to_bytes(1, 'middle'))
            probe(lambda: (1).to_bytes('x', 'big'))
            probe(lambda: (-1).to_bytes(1, 'big'))
            probe(lambda: (256).to_bytes(1, 'big'))
            probe(lambda: (1).to_bytes(1, 'big', extra=1))
            probe(lambda: (1).to_bytes(1, length=1))
            probe(lambda: int.from_bytes(b'\x01', 'middle'))
            probe(lambda: int.from_bytes('ab', 'big'))
            probe(lambda: int.from_bytes([1, 300], 'big'))
            probe(lambda: int.from_bytes())
            """
        );

        Assert.Equal(
            Lines(
                """b'\x04\x00' b'\x00\x04' b'\x05'""",
                """b'\xff\x00' b''""",
                "4 -1 0",
                "258 1 4",
                "ValueError byteorder must be either 'little' or 'big'",
                "TypeError 'str' object cannot be interpreted as an integer",
                "OverflowError can't convert negative int to unsigned",
                "OverflowError int too big to convert",
                "TypeError to_bytes() got an unexpected keyword argument 'extra'",
                "TypeError argument for to_bytes() given by name ('length') and position (1)",
                "ValueError byteorder must be either 'little' or 'big'",
                "TypeError cannot convert 'str' object to bytes",
                "ValueError bytes must be in range(0, 256)",
                "TypeError from_bytes() missing required argument 'bytes' (pos 1)"
            ),
            output
        );
    }

    [Fact]
    public void FloatViewsAndReadOnlyMembers()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print((1.0).is_integer(), (1.5).is_integer(), float('inf').is_integer(), (-0.0).is_integer())
            print((1.5).as_integer_ratio(), (-0.0).as_integer_ratio())
            print((1.5).real, (1.5).imag, (1.5).conjugate())
            probe(lambda: float('inf').as_integer_ratio())
            probe(lambda: float('nan').as_integer_ratio())
            probe(lambda: (1.0).is_integer(1))
            probe(lambda: (1.0).numerator)
            probe(lambda: setattr(1.5, 'real', 1))
            probe(lambda: setattr(5, 'real', 1))
            """
        );

        Assert.Equal(
            Lines(
                "True False False True",
                "(3, 2) (0, 1)",
                "1.5 0.0 1.5",
                "OverflowError cannot convert Infinity to integer ratio",
                "ValueError cannot convert NaN to integer ratio",
                "TypeError float.is_integer() takes no arguments (1 given)",
                "AttributeError 'float' object has no attribute 'numerator'",
                "AttributeError attribute 'real' of 'float' objects is not writable",
                "AttributeError attribute 'real' of 'int' objects is not writable"
            ),
            output
        );
    }

    [Fact]
    public void HexFormattingAndParsingAreExact()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print((1.0).hex(), (0.0).hex(), (-0.0).hex(), (0.1).hex(), (5e-324).hex())
            print(float('inf').hex(), float('-inf').hex(), float('nan').hex())
            for text in ('0x1p0', '0x1.8p1', '.5', '1e5', 'inf', '-nan', '0x1.', '0x.1p4'):
                print(text, float.fromhex(text).hex())
            for text in ('0x1.00000000000008p0', '0x1.00000000000018p0', '0x1.00000000000038p0',
                         '0x1.00000000000012p0'):
                print(text, float.fromhex(text).hex())
            print(float.fromhex('0x1p-1075').hex(), float.fromhex('0x1.8p-1075').hex())
            probe(lambda: float.fromhex('0x1p1024'))
            probe(lambda: float.fromhex('p0'))
            probe(lambda: float.fromhex(5))
            probe(lambda: float.fromhex())
            probe(lambda: float.fromhex(string='0x1p0'))
            """
        );

        Assert.Equal(
            Lines(
                "0x1.0000000000000p+0 0x0.0p+0 -0x0.0p+0 0x1.999999999999ap-4 0x0.0000000000001p-1022",
                "inf -inf nan",
                "0x1p0 0x1.0000000000000p+0",
                "0x1.8p1 0x1.8000000000000p+1",
                ".5 0x1.4000000000000p-2",
                "1e5 0x1.e500000000000p+8",
                "inf inf",
                "-nan nan",
                "0x1. 0x1.0000000000000p+0",
                "0x.1p4 0x1.0000000000000p+0",
                "0x1.00000000000008p0 0x1.0000000000000p+0",
                "0x1.00000000000018p0 0x1.0000000000002p+0",
                "0x1.00000000000038p0 0x1.0000000000004p+0",
                "0x1.00000000000012p0 0x1.0000000000001p+0",
                "0x0.0p+0 0x0.0000000000001p-1022",
                "OverflowError hexadecimal value too large to represent as a float",
                "ValueError invalid hexadecimal floating-point string",
                "TypeError bad argument type for built-in operation",
                "TypeError float.fromhex() takes exactly one argument (0 given)",
                "TypeError float.fromhex() takes no keyword arguments"
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
