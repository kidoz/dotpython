using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SequenceContainmentCompatibilityTests
{
    [Fact]
    public Task BytesContainmentAcceptsSubsequencesAndByteValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            b = b'abc'
            print(b'a' in b, b'b' in b, b'bc' in b, b'd' in b, b'' in b)
            print(97 in b, 98 in b, 100 in b)
            print(b'ab' in b, b'abc' in b, b'abcd' in b)
            print(97 in b'ab', 100 in b'ab', True in b'\x01', False in b'\x00')
            print(b'a' not in b, b'z' not in b)
            print(b'\x00' in b'\x00\xff', 255 in b'\x00\xff')
            """
        );

    [Fact]
    public Task ContainmentRejectsWrongOperandTypes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(thunk())
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: -1 in b'abc')
            probe(lambda: 300 in b'abc')
            probe(lambda: 1.0 in b'abc')
            probe(lambda: None in b'abc')
            probe(lambda: 'a' in b'abc')
            probe(lambda: b'a' in 'abc')
            probe(lambda: [97] in b'abc')
            probe(lambda: 1 in 'abc')
            probe(lambda: None in 'abc')
            probe(lambda: 1.5 in 'abc')
            print('a' in 'abc', '' in 'abc')
            """
        );
}
