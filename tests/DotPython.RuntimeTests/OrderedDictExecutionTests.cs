using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class OrderedDictExecutionTests
{
    [Fact]
    public void OrderedDictSurface()
    {
        var output = Run(
            """
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            from collections import OrderedDict
            import copy
            print('type', OrderedDict.__module__, OrderedDict.__name__, OrderedDict.__bases__, issubclass(OrderedDict, dict))
            print('build', repr(OrderedDict()), OrderedDict([('a', 1), ('b', 2)]), OrderedDict(a=1, b=2), OrderedDict({'a': 1}, b=2))
            od = OrderedDict([('a', 1), ('b', 2), ('c', 3)])
            print('order', list(od), list(od.keys()), list(od.values()), list(od.items()), len(od), 'a' in od, od['b'])
            print('eq', od == OrderedDict([('c', 3), ('b', 2), ('a', 1)]), OrderedDict([('a', 1)]) == OrderedDict([('b', 2), ('a', 1)]), od == {'a': 1, 'b': 2, 'c': 3}, OrderedDict([('a', 1), ('b', 2)]) == {'b': 2, 'a': 1})
            probe('lt', lambda: OrderedDict([('a', 1)]) < OrderedDict([('a', 2)]))
            probe('hash', lambda: hash(OrderedDict([('a', 1)])))
            print('popitem', (lambda x: (x.popitem(), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])), (lambda x: (x.popitem(last=False), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])))
            probe('popitem-empty', lambda: OrderedDict().popitem())
            od = OrderedDict([('a', 1), ('b', 2), ('c', 3)])
            od.move_to_end('a')
            print('move', list(od))
            od.move_to_end('a', last=False)
            print('move-first', list(od))
            probe('move-missing', lambda: od.move_to_end('zz'))
            probe('move-args', lambda: od.move_to_end())
            probe('move-three', lambda: od.move_to_end('a', True, 3))
            od = OrderedDict([('a', 1), ('b', 2), ('c', 3)])
            print('reversed', list(reversed(od)), list(reversed(od.keys())), type(reversed(od)).__name__, type(od.keys()).__name__, type(od.items()).__name__, type(od.values()).__name__)
            print('or', repr(od | OrderedDict([('z', 3)])), type(od | OrderedDict([('z', 3)])).__name__, repr(od | {'z': 3}), repr({'z': 9} | od))
            od2 = OrderedDict([('z', 3)])
            od2 |= OrderedDict([('a', 1)])
            print('ior', repr(od2), type(od2).__name__)
            od2 = OrderedDict([('a', 1)])
            od2 |= [('z', 9)]
            print('ior-pairs', repr(od2))
            probe('or-list', lambda: OrderedDict([('a', 1)]).__or__([('z', 9)]))
            print('setdefault', (lambda x: (x.setdefault('b'), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])))
            print('update', (lambda x: (x.update({'c': 3, 'a': 9}), list(x))[1:])(OrderedDict([('a', 1), ('b', 2)])))
            print('pop', OrderedDict([('a', 1)]).pop('a'), OrderedDict([('a', 1)]).pop('zz', 'dflt'))
            probe('pop-missing', lambda: OrderedDict([('a', 1)]).pop('zz'))
            print('copy', repr(OrderedDict([('a', 1), ('b', 2)]).copy()), type(copy.copy(OrderedDict([('a', 1)]))).__name__)
            print('fromkeys', repr(OrderedDict.fromkeys('ab')), type(OrderedDict.fromkeys('ab')).__name__)
            print('desc', repr(OrderedDict.__init__), repr(OrderedDict.popitem), repr(OrderedDict.move_to_end))
            probe('init-arity', lambda: OrderedDict([], []))
            print('clear', repr((lambda x: (x.clear(), x)[1])(OrderedDict([('a', 1)]))))


            class Sub(OrderedDict):
                pass


            print('subclass', repr(Sub([('a', 1)])), type(Sub([('a', 1)]).copy()).__name__, Sub([('a', 1)]) == OrderedDict([('a', 1)]))
            print('subclass-or', repr(Sub([('a', 1)]) | Sub([('z', 9)])), type(Sub([('a', 1)]) | Sub([('z', 9)])).__name__, repr(OrderedDict([('a', 1)]) | Sub([('z', 9)])))
            print('nested', OrderedDict([('x', OrderedDict([('y', 1)]))]))
            """
        );
        Assert.Equal(
            Lines(
                "type collections OrderedDict (<class 'dict'>,) True",
                "build OrderedDict() OrderedDict({'a': 1, 'b': 2}) OrderedDict({'a': 1, 'b': 2}) OrderedDict({'a': 1, 'b': 2})",
                "order ['a', 'b', 'c'] ['a', 'b', 'c'] [1, 2, 3] [('a', 1), ('b', 2), ('c', 3)] 3 True 2",
                "eq False False True True",
                "lt -> TypeError '<' not supported between instances of 'collections.OrderedDict' and 'collections.OrderedDict'",
                "hash -> TypeError unhashable type: 'collections.OrderedDict'",
                "popitem (['a'],) (['b'],)",
                "popitem-empty -> KeyError 'dictionary is empty'",
                "move ['b', 'c', 'a']",
                "move-first ['a', 'b', 'c']",
                "move-missing -> KeyError 'zz'",
                "move-args -> TypeError move_to_end() missing required argument 'key' (pos 1)",
                "move-three -> TypeError move_to_end() takes at most 2 arguments (3 given)",
                "reversed ['c', 'b', 'a'] ['c', 'b', 'a'] odict_iterator odict_keys odict_items odict_values",
                "or OrderedDict({'a': 1, 'b': 2, 'c': 3, 'z': 3}) OrderedDict OrderedDict({'a': 1, 'b': 2, 'c': 3, 'z': 3}) OrderedDict({'z': 9, 'a': 1, 'b': 2, 'c': 3})",
                "ior OrderedDict({'z': 3, 'a': 1}) OrderedDict",
                "ior-pairs OrderedDict({'a': 1, 'z': 9})",
                "or-list -> NotImplemented",
                "setdefault (['a', 'b'],)",
                "update (['a', 'b', 'c'],)",
                "pop 1 dflt",
                "pop-missing -> KeyError 'zz'",
                "copy OrderedDict({'a': 1, 'b': 2}) OrderedDict",
                "fromkeys OrderedDict({'a': None, 'b': None}) OrderedDict",
                "desc <slot wrapper '__init__' of 'collections.OrderedDict' objects> <method 'popitem' of 'collections.OrderedDict' objects> <method 'move_to_end' of 'collections.OrderedDict' objects>",
                "init-arity -> TypeError expected at most 1 argument, got 2",
                "clear OrderedDict()",
                "subclass Sub({'a': 1}) Sub True",
                "subclass-or Sub({'a': 1, 'z': 9}) Sub OrderedDict({'a': 1, 'z': 9})",
                "nested OrderedDict({'x': OrderedDict({'y': 1})})"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
