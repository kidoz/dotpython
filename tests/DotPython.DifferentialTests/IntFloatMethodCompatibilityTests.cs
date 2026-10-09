using Xunit;

namespace DotPython.DifferentialTests;

public sealed class IntFloatMethodCompatibilityTests
{
    [Fact]
    public Task BitInspectionCountsMagnitudesAndNamesTheDefiningType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            for value in (0, 1, 2, 255, 256, -1, -255, -256, 2 ** 100, -(2 ** 100), 2 ** 100 - 1):
                print(value.bit_length(), value.bit_count())
            print(True.bit_length(), False.bit_count(), True.is_integer())
            probe(lambda: (5).bit_length(1))
            probe(lambda: (5).bit_length(x=1))
            probe(lambda: (5).bit_count(1))
            probe(lambda: (5).bit_count(x=1))
            probe(lambda: (5).is_integer(1))
            probe(lambda: (5).is_integer(x=1))
            probe(lambda: (True).bit_length(1))
            """
        );

    [Fact]
    public Task ByteConversionRoundTripsAndReportsItsOwnArgumentErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            print((1024).to_bytes(2, 'big'), (1024).to_bytes(2, 'little'), (5).to_bytes())
            print((5).to_bytes(byteorder='little'), (-1).to_bytes(1, 'big', signed=True))
            print((-256).to_bytes(2, 'big', signed=True), (-32768).to_bytes(2, 'big', signed=True))
            print((0).to_bytes(0, 'big'), (2 ** 70).to_bytes(10, 'big').hex())
            print(True.to_bytes(1, 'big'), (True).to_bytes(True, 'big'))
            print(int.from_bytes(True.to_bytes(1, 'big')), float.fromhex(1.5.hex()))
            probe(lambda: (-1).to_bytes(1, 'big'))
            probe(lambda: (256).to_bytes(1, 'big'))
            probe(lambda: (128).to_bytes(1, 'big', signed=True))
            probe(lambda: (0).to_bytes(0, 'little'))
            probe(lambda: (1).to_bytes(0, 'big'))
            probe(lambda: (1).to_bytes('x', 'big'))
            probe(lambda: (1).to_bytes(1.0, 'big'))
            probe(lambda: (1).to_bytes(-1, 'big'))
            probe(lambda: (1).to_bytes(-1, 'middle'))
            probe(lambda: (1).to_bytes(1, 'middle'))
            probe(lambda: (1).to_bytes(1, b'big'))
            probe(lambda: (1).to_bytes(1, 5))
            probe(lambda: (1).to_bytes('x', 'middle'))
            probe(lambda: (1).to_bytes(2 ** 70, 'big'))
            probe(lambda: (1).to_bytes(1, 'big', True))
            probe(lambda: (1).to_bytes(1, 'big', extra=1))
            probe(lambda: (1).to_bytes(1, 'big', signed=True, extra=1))
            probe(lambda: (1).to_bytes(1, length=1))
            probe(lambda: (1).to_bytes(length=1, byteorder='big', signed=True))
            """
        );

    [Fact]
    public Task FromBytesReadsEveryAcceptedSourceAndRefusesTheRest() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            print(int.from_bytes(b'\x04\x00', 'big'), int.from_bytes(b'\x04\x00', 'little'))
            print(int.from_bytes(b'\x04'), int.from_bytes(bytearray(b'\x04\x01')))
            print(int.from_bytes(b''), int.from_bytes(b'', 'little'))
            print(int.from_bytes(b'\xff', 'big', signed=True), int.from_bytes(b'\xff', signed=False))
            print(int.from_bytes([1, 2], 'big'), int.from_bytes((1, 2), 'big'), int.from_bytes({1: 2}, 'big'))
            print(int.from_bytes([True, False], 'big'), int.from_bytes(b'\x80', signed=True))
            print(int.from_bytes(bytes=b'\x01'), int.from_bytes(b'\x01', byteorder='little'))
            print((5).from_bytes(b'\x04', 'big'), int.from_bytes(b'\x00' * 3, 'big'))
            probe(lambda: int.from_bytes(b'\x04', 'middle'))
            probe(lambda: int.from_bytes(b'\x01', 5))
            probe(lambda: int.from_bytes('ab', 'big'))
            probe(lambda: int.from_bytes(5, 'big'))
            probe(lambda: int.from_bytes(None, 'big'))
            probe(lambda: int.from_bytes('a', 'middle'))
            probe(lambda: int.from_bytes([1, 300], 'big'))
            probe(lambda: int.from_bytes([-1], 'big'))
            probe(lambda: int.from_bytes(['a'], 'big'))
            probe(lambda: int.from_bytes([1.0], 'big'))
            probe(lambda: int.from_bytes([[1]], 'big'))
            probe(lambda: int.from_bytes())
            probe(lambda: int.from_bytes(b'\x01', 'big', 1))
            probe(lambda: int.from_bytes(b'\x01', 'big', 1, 2))
            probe(lambda: int.from_bytes(b'\x01', extra=1))
            probe(lambda: int.from_bytes(b'\x01', length=1))
            """
        );

    [Fact]
    public Task NumericMembersAnswerFromTheValueAndRefuseAssignment() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            for value in (0, 5, -5, 2 ** 100, True, False):
                print(value.as_integer_ratio(), value.real, value.imag, value.numerator,
                      value.denominator, value.conjugate())
            probe(lambda: (5).as_integer_ratio(1))
            probe(lambda: (5).as_integer_ratio(x=1))
            probe(lambda: (5).conjugate(1))
            probe(lambda: setattr(5, 'real', 1))
            probe(lambda: setattr(5, 'imag', 1))
            probe(lambda: setattr(5, 'numerator', 1))
            probe(lambda: setattr(5, 'denominator', 1))
            probe(lambda: setattr(True, 'real', 1))
            probe(lambda: delattr(5, 'real'))
            probe(lambda: setattr(1.5, 'real', 1))
            probe(lambda: setattr(1.5, 'imag', 1))
            probe(lambda: delattr(1.5, 'imag'))
            """
        );

    [Fact]
    public Task FloatIntegerViewsAreExact() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            for value in (1.0, 1.5, -2.0, 0.0, -0.0, 1e308, 5e-324, 2.5e-320, 2.0 ** -25):
                print(value.is_integer(), value.as_integer_ratio())
            print(float('inf').is_integer(), float('nan').is_integer(), (-0.0).is_integer())
            print((1.5).real, (1.5).imag, (1.5).conjugate(), float('inf').conjugate())
            probe(lambda: float('inf').as_integer_ratio())
            probe(lambda: float('-inf').as_integer_ratio())
            probe(lambda: float('nan').as_integer_ratio())
            probe(lambda: (1.0).as_integer_ratio(1))
            probe(lambda: (1.0).is_integer(1))
            probe(lambda: (1.0).conjugate(1))
            probe(lambda: (1.0).numerator)
            probe(lambda: (1.0).denominator)
            """
        );

    [Fact]
    public Task HexFormattingPrintsTheExactBitPattern() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (1.0, 0.0, -0.0, 1.5, -1.5, 0.1, 3.141592653589793, 5e-324, -5e-324,
                          1.7976931348623157e308, 2.2250738585072014e-308, 1e16, 1024.0,
                          1e-300, 2.5e-320, 2.0 ** -25, 0.5, 7.0):
                print(value.hex())
            print(float('inf').hex(), float('-inf').hex(), float('nan').hex())
            print((-float('nan')).hex())
            """
        );

    [Fact]
    public Task HexParsingRoundsHalfToEvenOverTheWholeInput() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            def hex(value):
                return value.hex()

            for text in ('0x1p0', '0x1.8p1', '1', '0x1', '  0x1.8p1  ', '0x1.8P-1', '.5', '0x.8p1',
                         '0x1p+2', '0x1.', '0x.1p4', '1e5', 'f.f', '0xAbCdEf.123p-4', '-0.0',
                         '+0x1p0', '0x1p-0', '0X1P0', 'inf', '-inf', 'nan', '-nan', 'infinity',
                         'INF', 'Infinity', '0x0', '0x0p1000000', '0x0p-1000000', '0x1p-1074',
                         '0x1p-1075', '0x1.8p-1074', '0x1.8p-1075', '0x1p-2000',
                         '0x0.0000000000001p-1022', '0x1p-1022', '0x1p1023', '0x1.fffffffffffffp1023',
                         '0x' + 'f' * 100 + 'p0', '0x1.' + '0' * 60 + '8p0'):
                probe(lambda text=text: hex(float.fromhex(text)))
            for text in ('0x1.00000000000008p0', '0x1.00000000000018p0', '0x1.00000000000038p0',
                         '0x1.00000000000012p0', '0x1.fffffffffffff8p0', '0x1.fffffffffffffffp0',
                         '0x1.fffffffffffff7p1023', '0x1.fffffffffffff8p1023', '0x1p1024',
                         '0x0.' + '0' * 99 + '1p0'):
                probe(lambda text=text: hex(float.fromhex(text)))
            for text in ('p0', '0x1p', '0x1p+', '', '-', '.', '0x', '0xp+290', '0X', '-0X',
                         '0x.p1', '0x1.2.3p0', '1_0', '0x1 p0', '0x1p0x', '0x1p1x', '0x1p0\x00',
                         '�0x1p0', '0x1p0�'):
                probe(lambda text=text: hex(float.fromhex(text)))
            probe(lambda: float.fromhex(5))
            probe(lambda: float.fromhex(['0x1p0']))
            probe(lambda: float.fromhex())
            probe(lambda: float.fromhex('0x1p0', 'x'))
            probe(lambda: float.fromhex(string='0x1p0'))
            probe(lambda: (1.0).fromhex('0x1p0'))
            """
        );

    [Fact]
    public Task GeneratedValuesAgreeAcrossTheWholeSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            state = [20261009]

            def rnd(n):
                state[0] = (state[0] * 1103515245 + 12345) % 2147483648
                return state[0] % n

            for i in range(150):
                m = rnd(2 ** 53)
                e = rnd(2045) - 1080
                value = m * 2.0 ** e
                print(value.hex(), value.is_integer(), value == float.fromhex(value.hex()))
                length = rnd(9)
                ordered = 'big' if rnd(2) else 'little'
                signed = rnd(2) == 0
                whole = rnd(2 ** 80) * (-1 if rnd(2) else 1)
                try:
                    image = whole.to_bytes(length, ordered, signed=signed)
                    print(image.hex(), int.from_bytes(image, ordered, signed=signed) == whole)
                except Exception as error:
                    print(type(error).__name__, error)
                print(whole.bit_length(), whole.bit_count(), whole.as_integer_ratio())

            digits = '0123456789abcdef'
            for i in range(150):
                text = '0x'
                for k in range(rnd(20)):
                    text += digits[rnd(16)]
                if rnd(3) == 0:
                    text += '.' + digits[rnd(16)] * rnd(4)
                text += 'p' + str(rnd(80) - 40)
                try:
                    print(repr(text), float.fromhex(text).hex())
                except Exception as error:
                    print(type(error).__name__, error)
            """
        );
}
