using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ViewAlgebraCompatibilityTests
{
    [Fact]
    public Task ViewSetAlgebra() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def show(label, fn):
                try:
                    value = fn()
                    if type(value) is set or type(value) is frozenset:
                        value = sorted(value, key=repr)
                    print(label, "=>", repr(value))
                except BaseException as e:
                    print(label, "!!", type(e).__name__, str(e))


            d = {'a': 1, 'b': 2}
            k, it, v = d.keys(), d.items(), d.values()
            show("k-and-set", lambda: k & {'a'})
            show("k-or-set", lambda: k | {'z'})
            show("k-sub-set", lambda: k - {'a'})
            show("k-xor-set", lambda: k ^ {'a', 'z'})
            show("k-and-keys", lambda: k & {'b': 1}.keys())
            show("k-and-items", lambda: k & d.items())
            show("k-and-list", lambda: k & ['a', 'z'])
            show("k-and-tuple", lambda: k & ('a',))
            show("k-and-dict", lambda: k & {'a': 9})
            show("k-and-str", lambda: k & 'ab')
            show("k-and-int", lambda: k & 5)
            show("k-and-values", lambda: k & v)
            show("k-or-list", lambda: k | ['z'])
            show("i-and-set", lambda: it & {('a', 1)})
            show("i-or-set", lambda: it | {('z', 9)})
            show("i-and-items", lambda: it & {'b': 2}.items())
            show("i-and-list", lambda: it & [('a', 1)])
            show("v-and-set", lambda: v & {1})
            show("v-le", lambda: v <= {1})
            show("k-lt", lambda: k < {'a', 'b', 'c'})
            show("k-le", lambda: k <= {'a', 'b'})
            show("k-gt", lambda: k > {'a'})
            show("k-ge", lambda: k >= {'a', 'b'})
            show("k-lt-list", lambda: k < ['a'])
            show("k-lt-dict", lambda: k < {'a': 1, 'b': 2})
            show("k-lt-keys", lambda: k < {'a': 1}.keys())
            show("k-gt-set", lambda: k > {('a', 1)})
            show("it-lt-set", lambda: it < {('a', 1), ('b', 2), ('c', 3)})
            show("k-isdisjoint", lambda: k.isdisjoint({'z'}))
            show("k-isdisjoint-list", lambda: k.isdisjoint(['z']))
            show("k-disjoint-int", lambda: k.isdisjoint(5))
            show("i-disjoint", lambda: it.isdisjoint({('z', 9)}))
            show("v-isdisjoint", lambda: v.isdisjoint({'z'}))
            show("eq-keys-set", lambda: k == {'a', 'b'})
            show("eq-keys-list", lambda: k == ['a', 'b'])
            show("eq-keys-dict", lambda: k == {'a': 1, 'b': 2})
            show("eq-keys-keys", lambda: k == {'b': 2, 'a': 1}.keys())
            show("eq-items-set", lambda: it == {('a', 1), ('b', 2)})
            show("eq-items-partial", lambda: it == {('a', 1)})
            show("eq-items-list", lambda: it == [('a', 1), ('b', 2)])
            show("eq-items-items", lambda: it == {'b': 2, 'a': 1}.items())
            show("eq-values-set", lambda: v == {1, 2})
            show("eq-values-list", lambda: v == [1, 2])
            show("eq-values-self", lambda: v == v)
            show("eq-values-other", lambda: d.values() == {'a': 1, 'b': 2}.values())
            show("values-ne", lambda: v != {1, 2})
            show("and-chain", lambda: (k & {'a'}) | {'z'})
            show("set-and-keys", lambda: {'a'} & k)
            show("set-or-keys", lambda: {'z'} | k)
            show("set-sub-keys", lambda: {'a', 'z'} - k)
            show("set-xor-keys", lambda: {'z'} ^ k)
            show("set-lt-keys", lambda: {'a'} < k)
            show("keys-frozenset", lambda: k & frozenset({'a'}))
            show("keys-nested-list", lambda: k & [['a']])
            show("contains-list", lambda: ['a'] in d.keys())
            show("contains-int", lambda: 5 in d.keys())
            show("items-contains", lambda: ('a', 1) in d.items())
            show("items-contains-partial", lambda: ('a', 9) in d.items())
            show("hash-keys", lambda: hash(k))
            show("types", lambda: type(k & {'a'}).__name__)
            show("cmp-chain", lambda: (d.keys() & {'b': 2}.keys()) | {'c'})
            import types
            proxy = types.MappingProxyType({'a': 1, 'b': 2})
            show("proxy-and", lambda: proxy.keys() & {'a'})
            show("proxy-items-eq", lambda: proxy.items() == {('a', 1), ('b', 2)})
            show("proxy-values", lambda: proxy.values() == [1, 2])
            show("proxy-values-self", lambda: proxy.values() == proxy.values())
            import collections
            od = collections.OrderedDict([('a', 1)])
            show("odict-and", lambda: od.keys() & {'a'})
            show("odict-items-eq", lambda: od.items() == {('a', 1)})
            show("odict-values-ops", lambda: od.values() & {1})
            show("odict-lt", lambda: od.keys() < {'a', 'b'})
            show("odict-disjoint", lambda: od.keys().isdisjoint({'z'}))
            show("odict-type-name", lambda: type(od.keys()).__name__)
            show("counter-keys", lambda: collections.Counter('ab').keys() & {'a'})
            show("defaultdict-values", lambda: collections.defaultdict(list).values() & {1})
            """
        );
}
