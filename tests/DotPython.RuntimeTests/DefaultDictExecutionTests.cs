using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DefaultDictExecutionTests
{
    [Fact]
    public void DefaultDictSurface()
    {
        var output = Run(
            """
            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)
            from collections import defaultdict
            d = defaultdict(list)
            print('defaultdict', repr(d), d.default_factory, d['a'], repr(d), len(d))
            print('empty', repr(defaultdict()), repr(defaultdict(None)))
            d3 = defaultdict(int, {'a': 1}, b=2)
            print('build', repr(d3), d3['c'], repr(d3))
            probe('bad', lambda: defaultdict(1))
            probe('bad-seq', lambda: defaultdict(int, [1, 2], c=3))
            probe('four', lambda: defaultdict(list, {}, {}, {}))
            print('missing', repr(defaultdict(int).__missing__('k')), repr(defaultdict(int)['k']))
            print('member', repr(defaultdict.__missing__), repr(defaultdict.__init__), repr(defaultdict.default_factory))
            d6 = defaultdict(list)
            d6.default_factory = float
            print('set', repr(d6), d6['q'])
            del d6.default_factory
            print('del', repr(d6))
            c = d3.copy()
            print('copy', type(c).__name__, repr(c), c is d3, c.default_factory)
            print('fromkeys', repr(defaultdict.fromkeys('ab')), type(defaultdict.fromkeys('ab')).__name__)
            print('or', repr(defaultdict(list, {'a': 1}) | {'z': 9}), type(defaultdict(list, {'a': 1}) | {'z': 9}).__name__, repr({'z': 9} | defaultdict(list, {'a': 1})))
            d8 = defaultdict(int, a=1)
            print('inherited', list(d8.items()), d8.pop('a'), repr(d8), d8.setdefault('m', 5), repr(d8))
            d8 |= {'z': 3}
            print('ior', type(d8).__name__, repr(d8))
            probe('kw', lambda: defaultdict(default_factory=list))
            probe('nodict', lambda: setattr(defaultdict(list), 'custom', 5))
            probe('nodict-read', lambda: defaultdict(list).__dict__)
            def seven():
                return 7
            print('fn-factory', defaultdict(seven)['k'])


            class Sub(defaultdict):
                pass


            s = Sub(list, {'k': 1})
            print('subclass', type(s).__name__, repr(s), type(s['new']).__name__, isinstance(s, dict))
            """
        );
        Assert.Equal(
            Lines(
                "defaultdict defaultdict(<class 'list'>, {}) <class 'list'> [] defaultdict(<class 'list'>, {'a': []}) 1",
                "empty defaultdict(None, {}) defaultdict(None, {})",
                "build defaultdict(<class 'int'>, {'a': 1, 'b': 2}) 0 defaultdict(<class 'int'>, {'a': 1, 'b': 2, 'c': 0})",
                "bad -> TypeError first argument must be callable or None",
                "bad-seq -> TypeError object is not iterable",
                "four -> TypeError dict expected at most 1 argument, got 3",
                "missing 0 0",
                "member <method '__missing__' of 'collections.defaultdict' objects> <slot wrapper '__init__' of 'collections.defaultdict' objects> <member 'default_factory' of 'collections.defaultdict' objects>",
                "set defaultdict(<class 'float'>, {}) 0.0",
                "del defaultdict(None, {'q': 0.0})",
                "copy defaultdict defaultdict(<class 'int'>, {'a': 1, 'b': 2, 'c': 0}) False <class 'int'>",
                "fromkeys defaultdict(None, {'a': None, 'b': None}) defaultdict",
                "or defaultdict(<class 'list'>, {'a': 1, 'z': 9}) defaultdict defaultdict(<class 'list'>, {'z': 9, 'a': 1})",
                "inherited [('a', 1)] 1 defaultdict(<class 'int'>, {}) 5 defaultdict(<class 'int'>, {'m': 5})",
                "ior defaultdict defaultdict(<class 'int'>, {'m': 5, 'z': 3})",
                "kw -> defaultdict(None, {'default_factory': <class 'list'>})",
                "nodict -> AttributeError 'collections.defaultdict' object has no attribute 'custom' and no __dict__ for setting new attributes",
                "nodict-read -> AttributeError 'collections.defaultdict' object has no attribute '__dict__'",
                "fn-factory 7",
                "subclass Sub Sub(<class 'list'>, {'k': 1}) list True"
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
