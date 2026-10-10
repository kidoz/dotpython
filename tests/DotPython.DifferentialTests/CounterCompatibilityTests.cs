using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CounterCompatibilityTests
{
    [Fact]
    public Task CounterSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            import collections
            from collections import Counter
            print('type', Counter.__module__, Counter.__qualname__, Counter.__name__, Counter.__mro__, Counter.__bases__, issubclass(Counter, dict))
            print('build', Counter(), Counter('gallahad'), Counter({'a': 2, 'b': 1}), Counter(a=2, b=1), Counter('aabb', x=3), Counter([1, 2, 2, 3, 3, 3]))
            c = Counter('aabb')
            print('missing', c['zzz'], repr(c), Counter().__missing__('k'), repr(Counter(a=1)['k']))
            probe('missing-args', lambda: Counter().__missing__())
            c = Counter()
            c['x'] += 1
            print('setitem', repr(c))
            print('total', Counter('aab').total(), Counter(a=1, b=-2).total(), Counter().total())
            print('elements', list(Counter('aabbc').elements()), list(Counter(a=2, b=-1, c=0).elements()), type(Counter('ab').elements()).__name__)
            c = Counter('aabbbcc')
            print('most_common', c.most_common(), c.most_common(2), c.most_common(None), c.most_common(0), c.most_common(-1), Counter('cab').most_common(), Counter().most_common())
            probe('most-common-float', lambda: Counter('ab').most_common(1.0))
            probe('most-common-noninteger', lambda: Counter(a=1, b=1).most_common(1.5))
            probe('most-common-str', lambda: Counter('ab').most_common('1'))
            a = Counter('aab')
            b = Counter('abbc')
            print('arithmetic', a + b, a - b, a & b, a | b, +a, -a, Counter(a=1) - Counter(b=1), Counter(a=1) + Counter(a=-2))
            print('unary', +Counter(a=1, b=-1), -Counter(a=1, b=-1), +Counter(a=0), Counter() + Counter())
            probe('add-dict', lambda: Counter(a=1) + {'a': 1})
            probe('add-int', lambda: Counter(a=1) + 1)
            probe('sub-none', lambda: Counter(a=1) - None)
            probe('radd', lambda: {'a': 1} + Counter(a=1))
            print('or-dict', Counter('a') | {'b': 1}, {'b': 1} | Counter('a'), Counter('a') | Counter('b'))
            print('inplace', (lambda x: (x.__iadd__(Counter(a=1)), x)[1])(Counter(a=1)), (lambda x: (x.__isub__(Counter(a=1)), x)[1])(Counter(a=1)), (lambda x: (x.__iand__(Counter(b=1)), x)[1])(Counter(a=1)), (lambda x: (x.__ior__(Counter(a=2, b=5)), x)[1])(Counter(a=1)))
            print('inplace-dict', (lambda x: (x.__iadd__({'a': 1}), x)[1])(Counter(a=1)), (lambda x: (x.__isub__({'a': 1}), x)[1])(Counter(a=1)), (lambda x: (x.__iand__({'a': 3}), x)[1])(Counter(a=1)), (lambda x: (x.__ior__({'b': 2}), x)[1])(Counter(a=1)))
            probe('iand-partial', lambda: Counter(a=1, b=2).__iand__({'a': 5}))
            probe('ior-list', lambda: Counter(a=1).__ior__([('b', 2)]))
            c = Counter()
            c.update('aab')
            c.update({'a': 5, 'z': 1})
            c.subtract('a')
            c.subtract({'z': 1})
            print('update', repr(c), c.update(a=10) and 'None', repr(c))
            print('update-none', Counter().update(None), Counter().subtract(None))
            probe('update-int', lambda: Counter().update(1))
            print('update-pairs', repr((lambda x: (x.update([('a', 3)]), x)[1])(Counter())))
            print('copy', type(Counter('aab').copy()).__name__, Counter('aab').copy() is Counter('aab'), Counter(Counter(a=3)))
            print('fromkeys', 'skipped' if False else 'see below')
            probe('fromkeys', lambda: Counter.fromkeys('abc'))
            probe('fromkeys2', lambda: Counter.fromkeys('abc', 2))
            print('eq', Counter(a=1) == {'a': 1}, Counter(a=1) == Counter(a=1, b=0), Counter(a=1) != {'a': 1}, Counter(a=1) != Counter(a=1, b=0))
            print('compare', Counter(a=1) <= Counter(a=1, b=1), Counter(a=1) < Counter(a=1, b=1), Counter(a=1) >= Counter(a=1), Counter(a=2) > Counter(a=1))
            probe('compare-notimpl', lambda: Counter(a=1) < 5)
            print('mixed-values', repr(Counter(a='x', b=1)))
            probe('mixed-most-common', lambda: Counter(a='x', b=1).most_common())
            c = Counter(a=1, b=2, c=3)
            print('dict-methods', c.popitem(), repr(c), c.setdefault('z'), repr(c), c.get('zz'), c.get('zz', 'd'), list(c))
            print('delitem', (lambda x: (x.__delitem__('zz'), repr(x))[1])(Counter(a=1)))
            print('keep-positive', repr((lambda x: (x.__setitem__('n', -1), x._keep_positive(), x)[2])(Counter(a=1))))
            print('hash', hash(Counter) != 0, isinstance(Counter(a=1), dict))
            probe('hash-value', lambda: hash(Counter(a=1)))
            print('reduce', repr(Counter('aab').__reduce__()))
            """
        );

    [Fact]
    public Task CounterSubclasses() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            from collections import Counter

            class Sub(Counter):
                pass

            print('subclass', repr(Sub('aa')), repr(Sub()), list(Sub('aab')), type(Sub('aa') + Sub('aa')).__name__, Sub('aab').total(), list(Sub('aab').elements()))
            print('subclass-ops', repr(Sub('aa') - Sub('a')), type(Sub('aa') | Sub('aa')).__name__, type(+Sub('aa')).__name__, repr(+Sub(a=1, b=-1)), repr(-Sub(a=1, b=-1)))
            print('subclass-copy', type(Sub('aab').copy()).__name__, repr(Sub({'b': 1, 'a': 2})))
            print('subclass-eq', Sub(a=1) == Counter(a=1), Sub(a=1) == {'a': 1}, Sub(a=1) == Sub(a=1, b=0))
            print('init', repr(Sub(['x', 'y', 'x'])), repr(Sub(x=2)), repr(Sub(('a', 'b'))))
            probe('init-two', lambda: Counter('a', 'b'))
            probe('init-three', lambda: Counter('a', 'b', 'c'))
            probe('unbound-init', lambda: Counter.__init__(Counter(), 'a', 'b'))
            probe('unbound-total', lambda: Counter.total(Counter(a=2, b=3)))
            probe('unbound-elements', lambda: list(Counter.elements(Counter(a=2))))
            probe('elements-arg', lambda: Counter(a=1).elements(1))
            probe('total-arg', lambda: Counter(a=1).total(1))
            probe('most-common-two', lambda: Counter(a=1).most_common(1, 2))
            probe('most-common-kw', lambda: Counter(a=1, b=2).most_common(n=1))
            probe('most-common-bad-kw', lambda: Counter(a=1).most_common(zz=1))
            probe('missing-two', lambda: Counter().__missing__('a', 'b'))
            """
        );
}
