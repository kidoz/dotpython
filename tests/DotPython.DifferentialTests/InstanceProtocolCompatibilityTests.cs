using Xunit;

namespace DotPython.DifferentialTests;

public sealed class InstanceProtocolCompatibilityTests
{
    [Fact]
    public Task InstanceDunderMethodsAnswerDirectly() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task BytesKeywordsAndPercentFormatting() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
