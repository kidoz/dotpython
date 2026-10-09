using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// Cases found by a randomized sweep over the `str` surface — astral indices, the whitespace
/// set, `center`'s padding bias and the per-method argument diagnostics.
/// </summary>
public sealed class TextMethodFidelityCompatibilityTests
{
    [Fact]
    public Task SearchPositionsCountCharactersNotUtf16Units() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            s = '😀ab😀cd'
            print(len(s), s.find('a'), s.find('c'), s.find('😀'), s.find('d'))
            print(s.rfind('a'), s.rfind('c'), s.rfind('😀'), s.rfind('z'))
            print(s.index('c'), s.rindex('c'), s.count('😀'), s.count('b'))
            print(s.find('c', 2), s.rfind('a', 0, 3), s.find('b', 1, 3), s.rfind('b', 1, 3))
            print('aé😀b'.rindex('b'), 'aé😀b'.rfind('é'), 'aé😀b'.count('a'))
            """
        );

    [Fact]
    public Task EmptyNeedleSitsAtItsBoundUnlessTheStartIsPastTheEnd() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            s = 'abc'
            for start in (0, 1, 3, 4, 5, -1, -3, -5):
                print(start, s.find('', start), s.rfind('', start))
            for start, end in ((0, 0), (0, 1), (1, 0), (3, 4), (1, 3), (4, 4), (2, 2)):
                print(start, end, s.find('', start, end), s.rfind('', start, end))
            print('ﬁ'.find(''), 'ﬁ'.rfind(''), 'ﬁ'.find('', 1), 'ﬁ'.rfind('', 2))
            print(''.find(''), ''.rfind(''), ''.find('', 5), ''.rfind('', 2))
            """
        );

    [Fact]
    public Task WhitespaceIsCPythonsSetNotThePlatforms() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for ch in ('\x1c', '\x1d', '\x1e', '\x1f', '\x0b', '\x0c', '\x85', '\u2028',
                       '\u2029', '\xa0', '\t', '\n', '\r', ' ', '　'):
                print(repr(ch), ch.isspace(), ('x' + ch + 'y').split(), ('a' + ch).strip(),
                      (ch + 'a').lstrip(), (ch + 'a' + ch).rstrip())
            print('a\x1cb\x1cc'.split(None, 1), 'a\x1cb'.rsplit(None, 1))
            print(repr(' \x1c\ta '.strip()), repr('  a  b  '.split()), repr('a b c'.rsplit(None, 1)))
            """
        );

    [Fact]
    public Task CenterBiasesItsLeftPaddingLikeCPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for width in range(4, 12):
                for text in ('a', 'ab', 'abc', 'abcd', ''):
                    print(width, len(text), repr(text.center(width, '*')), repr(text.ljust(width, '.')),
                          repr(text.rjust(width, '.')))
            for width in range(4, 12):
                for raw in (b'a', b'ab', b'abc', b''):
                    print(width, len(raw), repr(raw.center(width, b'*')), repr(raw.ljust(width, b'.')))
            print(repr('ab'.center(5, '😀')), repr('a'.center(4)))
            """
        );

    [Fact]
    public Task ArgumentDiagnosticsNameTheMethodTheWayCPythonDoes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: 'a'.find(1))
            probe(lambda: 'a'.rfind(1))
            probe(lambda: 'a'.index(1))
            probe(lambda: 'a'.rindex(1))
            probe(lambda: 'a'.count(1))
            probe(lambda: 'a'.replace(1, 'b'))
            probe(lambda: 'a'.replace('b', 1))
            probe(lambda: 'a'.strip(1))
            probe(lambda: 'a'.lstrip(1))
            probe(lambda: 'a'.rstrip(1))
            probe(lambda: 'a'.partition(1))
            probe(lambda: 'a'.rpartition(1))
            probe(lambda: 'a'.split(1))
            probe(lambda: 'a'.rsplit(1))
            probe(lambda: 'a'.removeprefix(1))
            probe(lambda: 'a'.removesuffix(1))
            probe(lambda: 'a'.center(5, 1))
            probe(lambda: 'a'.center('x'))
            probe(lambda: 'a'.zfill('x'))
            probe(lambda: 'a'.expandtabs('x'))
            probe(lambda: 'a'.find('a', 'x'))
            probe(lambda: 'a'.rfind('a', 'x'))
            probe(lambda: 'a'.startswith(1))
            probe(lambda: 'a'.endswith(1))
            """
        );
}
