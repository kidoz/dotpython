using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DefaultDictCompatibilityTests
{
    [Fact]
    public Task DefaultDictSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def shape(value):
                text = repr(value)
                result = ''
                while True:
                    head, sep, tail = text.partition(' at 0x')
                    if not sep:
                        return result + text
                    cut = 0
                    while cut < len(tail) and tail[cut] in '0123456789abcdef':
                        cut += 1
                    result = result + head + ' at 0xADDR'
                    text = tail[cut:]
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            import collections
            from collections import defaultdict
            print('type', defaultdict.__module__, defaultdict.__qualname__, defaultdict.__name__, defaultdict.__mro__, defaultdict.__bases__, issubclass(defaultdict, dict))
            d = defaultdict(list)
            print('factory', repr(d), d.default_factory, d['a'], repr(d), len(d), list(d.keys()))
            print('none', repr(defaultdict(None)), repr(defaultdict()), defaultdict().default_factory)
            d3 = defaultdict(int, {'a': 1}, b=2)
            print('build', repr(d3), d3['c'], repr(d3), repr(defaultdict(int, [('z', 9)])), repr(defaultdict(list, b=2)), repr(defaultdict(list, {'a': 1}, b=2)))
            probe('missing-none', lambda: defaultdict(None)['x'])
            probe('bad-seq', lambda: defaultdict(int, [1, 2], c=3))
            probe('three-pos', lambda: defaultdict(1, 2, 3))
            probe('int-factory', lambda: defaultdict(1))
            probe('str-factory', lambda: defaultdict('str'))
            probe('four-args', lambda: defaultdict(list, {}, {}, {}))
            probe('bad-second', lambda: defaultdict(list, 5))
            probe('none-second', lambda: defaultdict(None, None))
            print('member', repr(defaultdict.__missing__), repr(defaultdict.__init__), repr(defaultdict.copy), repr(defaultdict.default_factory))
            print('missing-direct', repr(defaultdict(int).__missing__('k')), repr(defaultdict(int)['k']))
            probe('missing-args', lambda: defaultdict(list).__missing__())
            probe('missing-two', lambda: defaultdict(list).__missing__('a', 'b'))
            d6 = defaultdict(list)
            d6.default_factory = float
            print('set-factory', repr(d6), d6['q'])
            d6.default_factory = 1
            print('set-any', repr(d6))
            del d6.default_factory
            print('del-factory', repr(d6))
            probe('missing-after-del', lambda: d6['zz'])
            c = d3.copy()
            print('copy', type(c).__name__, repr(c), c is d3, c.default_factory)
            print('fromkeys', repr(defaultdict.fromkeys('ab')), type(defaultdict.fromkeys('ab')).__name__, repr(defaultdict.fromkeys('ab', 0)))
            import copy
            print('copy-module', repr(copy.copy(d3)), repr(copy.deepcopy(d3)))
            print('or', repr(defaultdict(list, {'a': 1}) | {'z': 9}), type(defaultdict(list, {'a': 1}) | {'z': 9}).__name__, repr({'z': 9} | defaultdict(list, {'a': 1})))
            d8 = defaultdict(int, a=1)
            print('inherited', list(d8.items()), d8.pop('a'), repr(d8), d8.setdefault('m', 5), repr(d8), d8.get('zz'), d8.get('zz', 'fallback'))
            d8 |= {'z': 3}
            print('ior', type(d8).__name__, repr(d8))
            probe('kw-factory', lambda: defaultdict(default_factory=list))
            probe('no-dict', lambda: setattr(defaultdict(list), 'custom', 5))
            from collections import defaultdict as dd
            probe('no-dict-attr', lambda: dd(list).__dict__)
            print('shape-reduce', shape(defaultdict(int, {'a': 1}).__reduce__()))

            class Sub(defaultdict):
                pass

            s = Sub(list, {'k': 1})
            print('subclass', type(s).__name__, repr(s), type(s['new']).__name__, isinstance(s, defaultdict), isinstance(s, dict))

            class Mysterious(defaultdict):
                def __missing__(self, key):
                    return 'custom:' + str(key)

            print('override', Mysterious(list)['k'])
            """
        );

    [Fact]
    public Task DefaultDictInitRuntime() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def shape(value):
                text = repr(value)
                result = ''
                while True:
                    head, sep, tail = text.partition(' at 0x')
                    if not sep:
                        return result + text
                    cut = 0
                    while cut < len(tail) and tail[cut] in '0123456789abcdef':
                        cut += 1
                    result = result + head + ' at 0xADDR'
                    text = tail[cut:]
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            from collections import defaultdict
            d = defaultdict(list, {'a': [1]})
            print('init', (lambda x: (x.__init__(int), repr(x), x['q'])[1:])(d))
            print('init-none', (lambda x: (x.__init__(), repr(x))[1:])(defaultdict(int, {'a': 1})))
            print('init-args', repr((lambda x: (x.__init__(None, {'b': 2}), x)[1])(defaultdict(list))))
            d2 = defaultdict(list)
            d2['a'] = 1
            print('update', (lambda x: (x.update({'a': 5}), repr(x))[1:])(d2))
            print('missing-insert', d2['fresh'], repr(d2))
            def seven():
                return 7
            print('missing-fn', defaultdict(seven)['k'], shape(defaultdict(seven)))
            def boom():
                return 1 / 0
            probe('factory-raises', lambda: defaultdict(boom)['k'])
            """
        );
}
