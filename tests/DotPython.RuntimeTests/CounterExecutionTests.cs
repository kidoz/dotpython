using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CounterExecutionTests
{
    [Fact]
    public void CounterSurface()
    {
        var output = Run(
            """
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            from collections import Counter
            print('type', Counter.__module__, Counter.__name__, Counter.__bases__, issubclass(Counter, dict))
            print('build', Counter(), Counter('gallahad'), Counter({'a': 2, 'b': 1}), Counter(a=2, b=1), Counter('aabb', x=3))
            c = Counter('aabb')
            print('missing', c['zzz'], repr(c), repr(Counter(a=1)['k']))
            print('total', Counter('aab').total(), Counter(a=1, b=-2).total())
            print('elements', list(Counter('aabbc').elements()), list(Counter(a=2, b=-1, c=0).elements()), type(Counter('ab').elements()).__name__)
            c = Counter('aabbbcc')
            print('most_common', c.most_common(), c.most_common(2), c.most_common(None), c.most_common(0), c.most_common(-1))
            probe('mc-float', lambda: Counter(a=1, b=1).most_common(1.5))
            a = Counter('aab')
            b = Counter('abbc')
            print('arith', a + b, a - b, a & b, a | b, +a, -a)
            print('unary', +Counter(a=1, b=-1), -Counter(a=1, b=-1))
            probe('add-dict', lambda: Counter(a=1) + {'a': 1})
            print('or-dict', Counter('a') | {'b': 1}, {'b': 1} | Counter('a'))
            print('inplace', (lambda x: (x.__iadd__(Counter(a=1)), x)[1])(Counter(a=1)), (lambda x: (x.__isub__(Counter(a=1)), x)[1])(Counter(a=1)), (lambda x: (x.__iand__(Counter(b=1)), x)[1])(Counter(a=1)), (lambda x: (x.__ior__(Counter(a=2, b=5)), x)[1])(Counter(a=1)))
            probe('iand-partial', lambda: Counter(a=1, b=2).__iand__({'a': 5}))
            c = Counter()
            c.update('aab')
            c.update({'a': 5, 'z': 1})
            c.subtract('a')
            print('update', repr(c))
            probe('update-int', lambda: Counter().update(1))
            print('copy', type(Counter('aab').copy()).__name__, Counter('aab').copy() is Counter('aab'))
            probe('fromkeys', lambda: Counter.fromkeys('abc'))
            print('eq', Counter(a=1) == {'a': 1}, Counter(a=1) == Counter(a=1, b=0), Counter(a=1) != {'a': 1})
            print('compare', Counter(a=1) <= Counter(a=1, b=1), Counter(a=1) < Counter(a=1, b=1), Counter(a=2) > Counter(a=1))
            print('mixed', repr(Counter(a='x', b=1)))
            c = Counter(a=1, b=2, c=3)
            print('dict-methods', c.popitem(), repr(c), c.setdefault('z'), repr(c), c.get('zz', 'd'), list(c))
            print('delitem', (lambda x: (x.__delitem__('zz'), repr(x))[1])(Counter(a=1)))
            print('keep', repr((lambda x: (x.__setitem__('n', -1), x._keep_positive(), x)[2])(Counter(a=1))))
            probe('hash', lambda: hash(Counter(a=1)))
            print('reduce', repr(Counter('aab').__reduce__()))


            class Sub(Counter):
                pass


            print('subclass', repr(Sub('aa')), repr(Sub()), list(Sub('aab')), type(Sub('aa') + Sub('aa')).__name__, Sub('aab').total())
            print('subclass-ops', repr(Sub('aa') - Sub('a')), type(+Sub('aa')).__name__, repr(+Sub(a=1, b=-1)), repr(-Sub(a=1, b=-1)))
            probe('unbound-total', lambda: Counter.total(Counter(a=2, b=3)))
            probe('init-two', lambda: Counter('a', 'b'))
            probe('elements-arg', lambda: Counter(a=1).elements(1))
            probe('mc-two', lambda: Counter(a=1).most_common(1, 2))
            """
        );
        Assert.Equal(
            Lines(
                "type collections Counter (<class 'dict'>,) True",
                "build Counter() Counter({'a': 3, 'l': 2, 'g': 1, 'h': 1, 'd': 1}) Counter({'a': 2, 'b': 1}) Counter({'a': 2, 'b': 1}) Counter({'x': 3, 'a': 2, 'b': 2})",
                "missing 0 Counter({'a': 2, 'b': 2}) 0",
                "total 3 -1",
                "elements ['a', 'a', 'b', 'b', 'c'] ['a', 'a'] chain",
                "most_common [('b', 3), ('a', 2), ('c', 2)] [('b', 3), ('a', 2)] [('b', 3), ('a', 2), ('c', 2)] [] []",
                "mc-float -> TypeError 'float' object cannot be interpreted as an integer",
                "arith Counter({'a': 3, 'b': 3, 'c': 1}) Counter({'a': 1}) Counter({'a': 1, 'b': 1}) Counter({'a': 2, 'b': 2, 'c': 1}) Counter({'a': 2, 'b': 1}) Counter()",
                "unary Counter({'a': 1}) Counter({'b': 1})",
                "add-dict -> TypeError unsupported operand type(s) for +: 'Counter' and 'dict'",
                "or-dict {'a': 1, 'b': 1} {'b': 1, 'a': 1}",
                "inplace Counter({'a': 2}) Counter() Counter() Counter({'b': 5, 'a': 2})",
                "iand-partial -> KeyError 'b'",
                "update Counter({'a': 6, 'b': 1, 'z': 1})",
                "update-int -> TypeError 'int' object is not iterable",
                "copy Counter False",
                "fromkeys -> NotImplementedError Counter.fromkeys() is undefined.  Use Counter(iterable) instead.",
                "eq True True False",
                "compare True True True",
                "mixed Counter({'a': 'x', 'b': 1})",
                "dict-methods ('c', 3) Counter({'b': 2, 'a': 1}) None Counter({'a': 1, 'b': 2, 'z': None}) d ['a', 'b', 'z']",
                "delitem Counter({'a': 1})",
                "keep Counter({'a': 1})",
                "hash -> TypeError unhashable type: 'Counter'",
                "reduce (<class 'collections.Counter'>, ({'a': 2, 'b': 1},))",
                "subclass Sub({'a': 2}) Sub() ['a', 'b'] Counter 3",
                "subclass-ops Counter({'a': 1}) Counter Counter({'a': 1}) Counter({'b': 1})",
                "unbound-total -> 5",
                "init-two -> TypeError Counter.__init__() takes from 1 to 2 positional arguments but 3 were given",
                "elements-arg -> TypeError Counter.elements() takes 1 positional argument but 2 were given",
                "mc-two -> TypeError Counter.most_common() takes from 1 to 2 positional arguments but 3 were given"
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
