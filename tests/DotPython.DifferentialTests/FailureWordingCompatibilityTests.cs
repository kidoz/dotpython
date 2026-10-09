using Xunit;

namespace DotPython.DifferentialTests;

public sealed class FailureWordingCompatibilityTests
{
    [Fact]
    public Task OperatorFailuresNameTheTypes() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task MethodArityUsesItsOwnSentence() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
