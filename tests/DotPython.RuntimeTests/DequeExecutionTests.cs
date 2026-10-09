using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DequeExecutionTests
{
    [Fact]
    public void DequeSurface()
    {
        var output = Run(
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
            from collections import deque
            import collections
            d = deque([1, 2, 3])
            print('repr', d, deque(), deque([1]), deque([1], 2), deque([1], maxlen=2), type(d).__name__, type(d).__module__, deque.__name__, deque.__module__)
            print('len', len(d), bool(deque()), bool(d), 2 in d, 5 in d, 1 in deque([True]))
            print('index', d[0], d[-1], d.__getitem__(1), d.__contains__(2))
            print('iter', list(d), list(reversed(d)), type(iter(d)).__name__, type(reversed(d)).__name__)
            print('maxlen', d.maxlen, deque([1], 2).maxlen, deque(maxlen=0).maxlen, deque([1, 2, 3], maxlen=2))
            print('append', (lambda x: (x.append(4), x)[1])(deque([1], 2)), (lambda x: (x.appendleft(0), x)[1])(deque([1], 2)))
            print('pop', (lambda x: (x.pop(), x)[1])(deque([1, 2])), (lambda x: (x.popleft(), x)[1])(deque([1, 2])))
            print('extend', (lambda x: (x.extend([4, 5]), x)[1])(deque([1], 3)), (lambda x: (x.extendleft([4, 5]), x)[1])(deque([1], 3)))
            print('insert', (lambda x: (x.insert(1, 9), x)[1])(deque([1, 2])), (lambda x: (x.insert(99, 9), x)[1])(deque([1, 2])), (lambda x: (x.insert(-1, 9), x)[1])(deque([1, 2])))
            print('rotate', (lambda x: (x.rotate(), x)[1])(deque([1, 2, 3])), (lambda x: (x.rotate(2), x)[1])(deque([1, 2, 3])), (lambda x: (x.rotate(-1), x)[1])(deque([1, 2, 3])))
            print('remove', (lambda x: (x.remove(2), x)[1])(deque([1, 2, 2])), (lambda x: (x.clear(), x)[1])(deque([1, 2])))
            print('count/index', deque([1, 2, 1]).count(1), deque([1, 2, 1]).index(1), deque([1, 2, 1]).index(1, 1), deque([1, 2]).index(1, 0, 1))
            print('copy/reverse', (lambda x: (x.copy(), x.copy() is x, type(x.copy()).__name__)[1:])(deque([1])), (lambda x: (x.reverse(), x)[1])(deque([1, 2, 3])))
            print('zero', (lambda x: (x.append(1), x.appendleft(2), x.extend([3]), x.rotate(1), x)[1])(deque(maxlen=0)))
            print('init', (lambda x: (x.__init__([1, 2], 1), x)[1])(deque([1], 3)), (lambda x: (x.__init__(), x)[1])(deque([1])))
            print('module', collections.__name__, collections.deque is deque)
            print('construct', deque('abc'), deque((1, 2)), deque({1: 2, 3: 4}), deque(x for x in [1, 2]))
            probe('bad iterable', lambda: deque(5))
            probe('two args', lambda: deque([1], 2, 3))
            probe('maxlen neg', lambda: deque([1], -1))
            probe('maxlen type', lambda: deque([1], 'x'))
            probe('maxlen float', lambda: deque([1], 2.0))
            probe('maxlen kw', lambda: deque([1, 2], maxlen=1))
            probe('insert full', lambda: (lambda x: x.insert(1, 9))(deque([1, 2], 2)))
            probe('pop empty', lambda: deque().pop())
            probe('popleft empty', lambda: deque().popleft())
            probe('remove missing', lambda: deque([1]).remove(2))
            probe('index missing', lambda: deque([1]).index(2))
            probe('getitem oob', lambda: deque([1])[5])
            probe('getitem str', lambda: deque([1])['a'])
            probe('slice', lambda: deque([1])[1:])
            probe('pop arg', lambda: deque([1]).pop(1))
            probe('insert one', lambda: deque([1]).insert(1))
            probe('index four', lambda: deque([1]).index(1, 0, 3, 4))
            probe('rotate two', lambda: deque([1]).rotate(1, 2))
            probe('append kw', lambda: deque([1]).append(x=1))
            """
        );

        Assert.Equal(
            Lines(
                "repr deque([1, 2, 3]) deque([]) deque([1]) deque([1], maxlen=2) deque([1], maxlen=2) deque collections deque collections",
                "len 3 False True True False True",
                "index 1 3 2 True",
                "iter [1, 2, 3] [3, 2, 1] _deque_iterator _deque_reverse_iterator",
                "maxlen None 2 0 deque([2, 3], maxlen=2)",
                "append deque([1, 4], maxlen=2) deque([0, 1], maxlen=2)",
                "pop deque([1]) deque([2])",
                "extend deque([1, 4, 5], maxlen=3) deque([5, 4, 1], maxlen=3)",
                "insert deque([1, 9, 2]) deque([1, 2, 9]) deque([1, 9, 2])",
                "rotate deque([3, 1, 2]) deque([2, 3, 1]) deque([2, 3, 1])",
                "remove deque([1, 2]) deque([])",
                "count/index 2 0 2 0",
                "copy/reverse (False, 'deque') deque([3, 2, 1])",
                "zero None",
                "init deque([2], maxlen=1) deque([])",
                "module collections True",
                "construct deque(['a', 'b', 'c']) deque([1, 2]) deque([1, 3]) deque([1, 2])",
                "bad iterable -> TypeError 'int' object is not iterable",
                "two args -> TypeError deque() takes at most 2 arguments (3 given)",
                "maxlen neg -> ValueError maxlen must be non-negative",
                "maxlen type -> TypeError an integer is required",
                "maxlen float -> TypeError an integer is required",
                "maxlen kw -> deque([2], maxlen=1)",
                "insert full -> IndexError deque already at its maximum size",
                "pop empty -> IndexError pop from an empty deque",
                "popleft empty -> IndexError pop from an empty deque",
                "remove missing -> ValueError deque.remove(x): x not in deque",
                "index missing -> ValueError deque.index(x): x not in deque",
                "getitem oob -> IndexError deque index out of range",
                "getitem str -> TypeError sequence index must be integer, not 'str'",
                "slice -> TypeError sequence index must be integer, not 'slice'",
                "pop arg -> TypeError deque.pop() takes no arguments (1 given)",
                "insert one -> TypeError insert expected 2 arguments, got 1",
                "index four -> TypeError index expected at most 3 arguments, got 4",
                "rotate two -> TypeError rotate expected at most 1 argument, got 2",
                "append kw -> TypeError deque.append() takes no keyword arguments"
            ),
            output
        );
    }

    [Fact]
    public void DequeSemantics()
    {
        var output = Run(
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
            from collections import deque
            def run(label, thunk):
                try:
                    return label + ' ' + repr(thunk())
                except Exception as error:
                    return label + ' ' + type(error).__name__ + ' ' + str(error)
            print('extend dup', (lambda x: (x.extend([1, 1, 1]), x)[1])(deque([0], 2)))
            print('extendleft full', (lambda x: (x.extendleft([1, 2, 3]), x)[1])(deque([0], 2)))
            print('concat', deque([1]) + deque([2]), deque([1], 2) + deque([2, 3]), deque([1]) * 2, 2 * deque([1]), deque([1, 2, 3], 2) * 2)
            print('iadd/imul', (lambda x: (x.__iadd__([3]), x)[1])(deque([1])), (lambda x: (x.__imul__(2), x)[1])(deque([1, 2])), (lambda x: (x.__imul__(2), x.maxlen)[1])(deque([1, 2], 3)))
            print('set/del', (lambda x: (x.__setitem__(0, 5), x)[1])(deque([1, 2])), (lambda x: (x.__delitem__(0), x)[1])(deque([1, 2, 3])))
            print('eq', deque([1, 2]) == deque([1, 2]), deque('ab') == deque('ab'), deque([1]) == [1], deque([1]) != deque([2]), deque([1, 2]) == deque([1, 2], 3))
            print('order', deque([1]) < deque([2]), deque([1]) < deque([1, 0]), deque([1]) <= deque([1]), deque([2]) > deque([1]))
            print('hash', deque([1]).__hash__, deque.__hash__)
            print('self ref', (lambda x: (x.append(x), x)[1])(deque([1])))
            print('maxlen after', (lambda x: (x.append(x), repr(x))[1])(deque([1], 2)))
            print('shape reduce', shape(deque([1, 2]).__reduce__()))
            print('shape desc', repr(deque.maxlen), repr(deque.append), repr(deque.__reversed__), repr(deque.__len__), repr(deque.__init__))
            print('shape iter', shape(iter(deque([1]))), shape(reversed(deque([1]))), shape(deque([1]).append))
            print('names', deque.append.__qualname__, deque.maxlen.__qualname__, deque.append.__objclass__, deque.__qualname__)
            print('descriptor', deque.append(deque([1]), 2), (lambda x: (deque.append(x, 2), x)[1])(deque([1])))
            print('copy module', __import__('copy').__name__ if False else 'skip')
            import copy
            print('copy', copy.copy(deque([1, 2])), copy.deepcopy(deque([1])))
            print('mutate', run('iter', lambda: (lambda x: [x.append(9) for _ in x])(deque([1, 2, 3]))))
            print('mutate2', run('iter', lambda: (lambda x: ([x.popleft() for _ in x], x)[1])(deque([1, 2, 3]))))
            print('mutate3', run('iter', lambda: (lambda x: ([x.rotate(1) for _ in x], x)[1])(deque([1, 2, 3]))))
            print('safe', [x for x in deque([1, 2])])
            probe('unbound', lambda: deque.append(1))
            probe('wrong recv', lambda: deque.append([], 1))
            probe('maxlen write', lambda: setattr(deque([1]), 'maxlen', 2))
            probe('hash call', lambda: hash(deque([1])))
            probe('setitem str', lambda: deque([1]).__setitem__('a', 5))
            probe('setitem slice', lambda: deque([1]).__setitem__(slice(0, 1), [7]))
            probe('setitem oob', lambda: deque([1]).__setitem__(9, 5))
            probe('delitem oob', lambda: deque([1]).__delitem__(9))
            probe('radd list', lambda: [1] + deque([2]))
            probe('add list', lambda: deque([1]) + [2])
            probe('mul str', lambda: deque([1]) * 'x')
            probe('mul neg', lambda: deque([1]) * -1)
            probe('imul str', lambda: deque([1]).__imul__('x'))
            probe('lt list', lambda: deque([1]) < [2])
            """
        );

        Assert.Equal(
            Lines(
                "extend dup deque([1, 1], maxlen=2)",
                "extendleft full deque([3, 2], maxlen=2)",
                "concat deque([1, 2]) deque([2, 3], maxlen=2) deque([1, 1]) deque([1, 1]) deque([2, 3], maxlen=2)",
                "iadd/imul deque([1, 3]) deque([1, 2, 1, 2]) 3",
                "set/del deque([5, 2]) deque([2, 3])",
                "eq True True False True True",
                "order True True True True",
                "hash None None",
                "self ref deque([1, [...]])",
                "maxlen after deque([1, [...]], maxlen=2)",
                "shape reduce (<class 'collections.deque'>, (), None, <collections._deque_iterator object at 0xADDR>)",
                "shape desc <attribute 'maxlen' of 'collections.deque' objects> <method 'append' of 'collections.deque' objects> <method '__reversed__' of 'collections.deque' objects> <slot wrapper '__len__' of 'collections.deque' objects> <slot wrapper '__init__' of 'collections.deque' objects>",
                "shape iter <collections._deque_iterator object at 0xADDR> <collections._deque_reverse_iterator object at 0xADDR> <built-in method append of collections.deque object at 0xADDR>",
                "names deque.append deque.maxlen <class 'collections.deque'> deque",
                "descriptor None deque([1, 2])",
                "copy module skip",
                "copy deque([1, 2]) deque([1])",
                "mutate iter RuntimeError deque mutated during iteration",
                "mutate2 iter RuntimeError deque mutated during iteration",
                "mutate3 iter RuntimeError deque mutated during iteration",
                "safe [1, 2]",
                "unbound -> TypeError descriptor 'append' for 'collections.deque' objects doesn't apply to a 'int' object",
                "wrong recv -> TypeError descriptor 'append' for 'collections.deque' objects doesn't apply to a 'list' object",
                "maxlen write -> AttributeError attribute 'maxlen' of 'collections.deque' objects is not writable",
                "hash call -> TypeError unhashable type: 'collections.deque'",
                "setitem str -> TypeError 'str' object cannot be interpreted as an integer",
                "setitem slice -> TypeError 'slice' object cannot be interpreted as an integer",
                "setitem oob -> IndexError deque index out of range",
                "delitem oob -> IndexError deque index out of range",
                "radd list -> TypeError can only concatenate list (not \"collections.deque\") to list",
                "add list -> TypeError can only concatenate deque (not \"list\") to deque",
                "mul str -> TypeError can't multiply sequence by non-int of type 'str'",
                "mul neg -> deque([])",
                "imul str -> TypeError 'str' object cannot be interpreted as an integer",
                "lt list -> TypeError '<' not supported between instances of 'collections.deque' and 'list'"
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
