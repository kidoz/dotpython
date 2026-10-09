using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SubclassStorageCompatibilityTests
{
    [Fact]
    public Task SubclassingStorageBuiltins() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk())[:70])
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            class D(dict):
                def doubled(self):
                    return len(self) * 2
            d = D({'a': 1})
            print('repr', d, str(d), type(d).__name__, type(d).__module__)
            print('isinstance', isinstance(d, dict), isinstance(d, D), issubclass(D, dict), D.__mro__[1] is dict)
            print('len/in/iter', len(d), 'a' in d, list(d), list(d.keys()), list(d.items()), list(d.values()))
            print('index', d['a'], d.get('b'), d.get('b', 9), d.doubled())
            d['b'] = 2
            print('set', d, len(d))
            del d['a']
            print('del', d)
            d.update({'c': 3})
            print('update', d, dict(d))
            print('copy', type(d.copy()).__name__, d.copy())
            print('pop', d.pop('c'), d)
            print('setdefault', d.setdefault('z', 1), d)
            class T(dict):
                pass
            print('attrs', (lambda x: (setattr(x, 'tag', 5), x.tag)[1])(T()))
            print('eq', D({'a': 1}) == {'a': 1}, {'a': 1} == D({'a': 1}), D({'a': 1}) == T({'a': 1}), D({'a': 1}) == D({'a': 1}))
            probe('hash', lambda: hash(D({'a': 1})))
            print('bool', bool(D()), bool(D({'a': 1})))
            print('init args', D(a=1, b=2), D([('a', 1)]))
            probe('init int', lambda: D(5))
            print('or', D({'a': 1}) | {'b': 2}, type(D({'a': 1}) | {'b': 2}).__name__)
            D.tagged = 'x'
            print('class attr', d.tagged)
            class L(list):
                def total(self):
                    return sum(self)
            l = L([1, 2])
            print('list', l, type(l).__name__, len(l), l[0], list(l), l.total())
            print('list types', type(l.copy()).__name__, type(l + [3]).__name__, type(l[0:1]).__name__, type(l * 2).__name__)
            l.append(3)
            print('append', l, isinstance(l, list), isinstance(l, L))
            print('list eq', L([1]) == [1], L([1]) == L([1]))
            print('list init', L('abc'), L((1, 2)))
            print('list reversed', list(reversed(l)), sorted(L([2, 1])))
            print('nested', L([L([1])]), L([1]) == L([1]))
            class P:
                pass
            print('plain unaffected', type(P()).__name__, isinstance(P(), list), isinstance(P(), dict))
            class E(ValueError):
                pass
            print('exception still fine', isinstance(E('x'), ValueError), E('x'))
            probe('bool base', lambda: type('B', (bool,), {}))
            probe('range base', lambda: type('R', (range,), {}))
            """
        );
}
