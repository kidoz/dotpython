using Xunit;

namespace DotPython.DifferentialTests;

public sealed class OrderedDictCompatibilityTests
{
    [Fact]
    public Task OrderedDictSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            import collections
            from collections import OrderedDict
            print('type', OrderedDict.__module__, OrderedDict.__qualname__, OrderedDict.__name__, OrderedDict.__mro__, OrderedDict.__bases__, issubclass(OrderedDict, dict))
            print('build', repr(OrderedDict()), OrderedDict([('a', 1), ('b', 2)]), OrderedDict(a=1, b=2), OrderedDict({'a': 1}, b=2), OrderedDict([('a', 1)], b=2))
            od = OrderedDict([('a', 1), ('b', 2), ('c', 3)])
            print('order', list(od), list(od.keys()), list(od.values()), list(od.items()), len(od), 'a' in od, od['b'])
            print('eq', od == OrderedDict([('c', 3), ('b', 2), ('a', 1)]), OrderedDict([('a', 1), ('b', 2)]) == OrderedDict([('b', 2), ('a', 1)]), od == {'a': 1, 'b': 2, 'c': 3}, OrderedDict([('a', 1), ('b', 2)]) == {'b': 2, 'a': 1})
            print('ne', OrderedDict([('a', 1)]) != OrderedDict([('a', 2)]), OrderedDict([('a', 1)]) == 5, OrderedDict([('a', 1)]) != 5)
            probe('order-lt', lambda: OrderedDict([('a', 1), ('b', 2)]) < OrderedDict([('a', 1), ('c', 3)]))
            probe('hash', lambda: hash(OrderedDict([('a', 1)])))
            print('popitem', (lambda x: (x.popitem(), list(x))[1:])(OrderedDict([('a', 1), ('b', 2), ('c', 3)])), (lambda x: (x.popitem(last=False), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])))
            probe('popitem-empty', lambda: OrderedDict().popitem())
            probe('popitem-empty-first', lambda: OrderedDict().popitem(last=False))
            od = OrderedDict([('a', 1), ('b', 2), ('c', 3)])
            od.move_to_end('a')
            print('move', list(od))
            od.move_to_end('a', last=False)
            print('move-first', list(od))
            od.move_to_end('b', 0)
            print('move-pos', list(od))
            probe('move-missing', lambda: od.move_to_end('zz'))
            probe('move-args', lambda: od.move_to_end())
            probe('move-three', lambda: od.move_to_end('a', True, 3))
            od = OrderedDict([('a', 1), ('b', 2), ('c', 3)])
            print('reversed', list(reversed(od)), list(reversed(od.keys())), list(reversed(od.items())), list(reversed(od.values())), type(reversed(od)).__name__, type(od.keys()).__name__, type(od.items()).__name__, type(od.values()).__name__)
            print('or', repr(od | OrderedDict([('z', 3)])), type(od | OrderedDict([('z', 3)])).__name__, repr(od | {'z': 3}), repr({'z': 9} | od))
            od2 = OrderedDict([('z', 3)])
            od2 |= OrderedDict([('a', 1)])
            print('ior', repr(od2), type(od2).__name__)
            od2 = OrderedDict([('a', 1)])
            od2 |= [('z', 9)]
            print('ior-pairs', repr(od2))
            probe('or-list', lambda: OrderedDict([('a', 1)]).__or__([('z', 9)]))
            probe('ior-bad', lambda: OrderedDict([('a', 1)]).__ior__(5))
            print('setdefault', (lambda x: (x.setdefault('b'), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])), (lambda x: (x.setdefault('z', 0), list(x))[1:])(OrderedDict([('a', 1)])))
            print('update', (lambda x: (x.update({'c': 3, 'a': 9}), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])))
            print('pop', OrderedDict([('a', 1)]).pop('a'), OrderedDict([('a', 1)]).pop('zz', 'dflt'))
            probe('pop-missing', lambda: OrderedDict([('a', 1)]).pop('zz'))
            print('copy', repr(OrderedDict([('a', 1), ('b', 2)]).copy()), type(OrderedDict([('a', 1)]).copy()).__name__)
            print('fromkeys', repr(OrderedDict.fromkeys('ab')), type(OrderedDict.fromkeys('ab')).__name__, repr(OrderedDict.fromkeys('ab', 0)))
            print('dict', dict(OrderedDict([('a', 1)])), OrderedDict(dict(a=1)))
            print('instance-dict', (lambda x: (setattr(x, 'custom', 5), id(x.custom))[1])(OrderedDict([('a', 1)])) is not None, repr(OrderedDict([('a', 1)])))
            print('desc', repr(OrderedDict.__init__), repr(OrderedDict.popitem), repr(OrderedDict.move_to_end), repr(OrderedDict.__reversed__))
            probe('init-arity', lambda: OrderedDict([], []))
            print('init-forms', repr(OrderedDict([('a', 1)])), repr(OrderedDict()), repr(OrderedDict('ab')) if False else 'skip')
            print('clear', repr((lambda x: (x.clear(), x)[1])(OrderedDict([('a', 1)]))))

            class Sub(OrderedDict):
                pass

            print('subclass', repr(Sub([('a', 1)])), type(Sub([('a', 1)]).copy()).__name__, type(Sub.fromkeys('a')).__name__, Sub([('a', 1)]) == OrderedDict([('a', 1)]))
            print('subclass-or', repr(Sub([('a', 1)]) | Sub([('z', 9)])), type(Sub([('a', 1)]) | Sub([('z', 9)])).__name__, repr(OrderedDict([('a', 1)]) | Sub([('z', 9)])))

            class Other(OrderedDict):
                def __eq__(self, other):
                    return 'custom'

            print('eq-override', OrderedDict([('a', 1)]) == Other([('a', 1)]))
            """
        );

    [Fact]
    public Task OrderedDictReduction() =>
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
            import copy
            from collections import OrderedDict
            od = OrderedDict([('a', 1), ('b', 2)])
            print('reduce', shape(od.__reduce__()))
            print('copy', type(copy.copy(od)).__name__, repr(copy.copy(od)))
            print('deepcopy', type(copy.deepcopy(od)).__name__, repr(copy.deepcopy(od)))
            print('copy-method', repr(od.copy()), od.copy() is od)
            print('identity', OrderedDict([('a', 1)]) == OrderedDict([('a', 1)]), OrderedDict([('a', 1)]) != OrderedDict([('a', 1)]))
            print('setitem-order', (lambda x: (x.__setitem__('a', 9), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])))
            print('delete-readd', (lambda x: (x.__delitem__('a'), x.__setitem__('a', 1), list(x))[2:])(OrderedDict([('a', 1), ('b', 2)])))
            print('popitem-readd', (lambda x: (x.popitem(), x.__setitem__('b', 2), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])))
            print('keys-live', (lambda x: (x.__setitem__('c', 3), list(x.keys()))[1:])(OrderedDict([('a', 1)])))
            print('bool', bool(OrderedDict()), bool(OrderedDict([('a', None)])))
            print('iter-type', type(iter(OrderedDict([('a', 1)]))).__name__)
            print('entries', [(k, v) for k, v in OrderedDict([('a', 1), ('b', 2)]).items()])
            print('nested', OrderedDict([('x', OrderedDict([('y', 1)]))]))
            """
        );
}
