using System.ComponentModel;
using System.Diagnostics;
using DotPython.Cli;
using DotPython.Language;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ManagedCliDifferentialTests
{
    [Theory]
    [InlineData("print(40 + 2)")]
    [InlineData("value = 7; print(value ** 2)")]
    [InlineData("print(-7 // 3, -7 % 3, 7 / 2)")]
    [InlineData("print('ab' * 3)")]
    [InlineData("print(2 ** 3 ** 2)")]
    [InlineData("print(True + 1, +True, ~False, None)")]
    [InlineData("print(2j, 1 + 2j)")]
    [InlineData("print(False and missing, True or missing)")]
    [InlineData("print('' or 'fallback', 'value' and 42)")]
    [InlineData("print(not 0, not 'value', 1 < 2 < 3, 1 < 2 > 3)")]
    [InlineData("print(1 == True, None != 0, 'a' < 'b', b'a' <= b'ab')")]
    [InlineData("print([], [1, 'two', (True, None)], (), (1,), (1, 2))")]
    [InlineData("print(not [], not (), [1, [2]] == [1, [2]], [1] != (1,))")]
    [InlineData(
        "def mark(value): print(value); return value\nprint([mark(1), mark(2)], (mark(3),))"
    )]
    [InlineData(
        "values = [10, 20]\nvalues[-1] = 42\nmapping = {'a': 1, 'a': 2}\nmapping['b'] = values[0]\nprint(values, mapping, mapping['a'], '🐍x'[0], b'ab'[1])"
    )]
    [InlineData("print(not {}, {'a': [1]} == {'a': [1]}, {1: 'int', True: 'bool'})")]
    [InlineData(
        "def mark(value): print(value); return value\nvalues = [0]\nvalues[mark(2) - 2] = mark(1)\nprint({mark(3): mark(4)}, values)"
    )]
    [InlineData(
        "for item in [1, 2]: print(item)\nelse: print('done')\nfor item in (3,): print(item)\nfor item in '🐍a': print(item)\nfor item in b'BC': print(item)\nfor key in {'x': 1, 'y': 2}: print(key)"
    )]
    [InlineData(
        "value = 0\nwhile value < 3:\n    if value != 1:\n        print(value)\n    value = value + 1\nelse:\n    print('done')"
    )]
    [InlineData(
        "factor = 2\ndef calculate(value):\n    local = value * factor\n    if local > 10:\n        return local\n    return 0\nprint(calculate(21), calculate(2))"
    )]
    [InlineData(
        "def factorial(value):\n    if value <= 1:\n        return 1\n    return value * factorial(value - 1)\nprint(factorial(6))"
    )]
    [InlineData("def procedure(value):\n    value = value + 1\nprint(procedure(4))")]
    [InlineData(
        "def double(value): return value * 2\ndef apply(function, value): return function(value)\nalias = double\nprint(apply(alias, 21), alias == double, alias != double)"
    )]
    [InlineData(
        "def make(value):\n    def add(other): return value + other\n    return add\nfirst = make(40)\nsecond = make(10)\nprint(first(2), second(5))"
    )]
    [InlineData(
        "def outer(seed):\n    value = seed\n    def middle():\n        def inner(): return value\n        return inner\n    value = value + 2\n    return middle()\nread = outer(40)\nprint(read())"
    )]
    [InlineData(
        "def outer():\n    def factorial(value):\n        if value <= 1: return 1\n        return value * factorial(value - 1)\n    return factorial(6)\nprint(outer())"
    )]
    [InlineData(
        "for value in [1, 2, 3, 4]:\n    if value == 3:\n        break\n    print(value)\nelse:\n    print('else')"
    )]
    [InlineData(
        "count = 0\nwhile count < 5:\n    count = count + 1\n    if count % 2 == 0:\n        continue\n    print(count)\nelse:\n    print('done', count)"
    )]
    [InlineData(
        "for value in [1, 2, 3]:\n    try:\n        if value == 2:\n            break\n        print('try', value)\n    finally:\n        print('finally', value)\nprint('after')"
    )]
    [InlineData(
        "for outer in [1, 2]:\n    for inner in [10, 20, 30]:\n        if inner == 20:\n            break\n        print(outer, inner)\n    print('row', outer)"
    )]
    [InlineData("pass\nif True:\n    pass\nprint('ok')")]
    [InlineData(
        "def greet(name, greeting='hello', punctuation='!'):\n    return greeting + ', ' + name + punctuation\nprint(greet('world'))\nprint(greet('world', 'hi'))\nprint(greet('world', punctuation='?'))\nprint(greet(punctuation='.', name='all', greeting='hey'))"
    )]
    [InlineData(
        "base = 10\ndef scaled(value, factor=base):\n    return value * factor\nbase = 99\nprint(scaled(3), scaled(3, 2), scaled(3, factor=4))"
    )]
    [InlineData(
        "counter = 0\ndef bump(step=1):\n    global counter\n    counter = counter + step\nbump()\nbump(step=5)\nprint(counter)"
    )]
    [InlineData(
        "def outer():\n    total = 0\n    def add(amount=2):\n        nonlocal total\n        total = total + amount\n    add()\n    add(amount=10)\n    return total\nprint(outer())"
    )]
    [InlineData(
        "def act(first, second=2):\n    return first\ntry:\n    act(1, wrong=3)\nexcept TypeError:\n    print('unexpected-keyword')\ntry:\n    act(second=3)\nexcept TypeError:\n    print('missing-argument')"
    )]
    [InlineData(
        "values = [3, 1, 2]\nvalues.append(4)\nvalues.extend([5])\nvalues.insert(0, 0)\nprint(values.pop(), values.pop(0), values)\nvalues.remove(3)\nvalues.sort()\nprint(values, values.index(2), values.count(1))\nvalues.reverse()\nprint(values, values.copy())"
    )]
    [InlineData(
        "print(' hi '.strip(), 'a,b,,c'.split(','), 'a b  c'.split(), '-'.join(['x', 'y']))\nprint('banana'.replace('an', 'A'), 'abc'.upper(), 'AbC'.lower(), 'hello world'.capitalize())\nprint('banana'.find('na'), 'banana'.find('xx'), 'banana'.count('a'), 'banana'.startswith('ban'), 'banana'.endswith('x'))"
    )]
    [InlineData(
        "d = {'a': 1, 'b': 2}\nprint(d.get('a'), d.get('z'), d.get('z', 9))\nprint(d.keys(), d.values(), d.items())\nd.update({'c': 3})\nprint(d.pop('b'), d.pop('zz', -1), d.setdefault('d', 4), d.setdefault('a', 99), d)\nprint((1, 2, 2).count(2), (1, 2, 2).index(2))"
    )]
    [InlineData(
        "print(2 in [1, 2], 3 not in (1, 2), 'an' in 'banana', 'xx' not in 'banana', 'k' in {'k': 1})\nprint(None is None, [] is [], 1 is not None)"
    )]
    [InlineData(
        "letters = ['a', 'b', 'c', 'd', 'e']\nprint(letters[1:3], letters[:2], letters[3:], letters[::2], letters[::-1], letters[-2:])\nprint('abcdef'[1:4], 'abcdef'[::-1], (1, 2, 3, 4)[1:3])\nletters[1:3] = ['B', 'C', 'X']\nprint(letters)\nletters[::3] = ['1', '2']\nprint(letters)"
    )]
    [InlineData(
        "n = 10\nn += 5\nn -= 3\nn *= 2\nn //= 5\nn **= 3\nn %= 7\nprint(n)\nlst = [1, 2]\nalias = lst\nlst += [3, 4]\nlst *= 2\nlst[0] += 9\nprint(lst, alias is lst)\ngrid = [[0, 1], [2, 3]]\ngrid[0][1] += 5\nprint(grid)"
    )]
    [InlineData(
        "try:\n    [1, 'a'].sort()\nexcept TypeError:\n    print('unorderable')\ntry:\n    [1, 2][::0]\nexcept ValueError:\n    print('zero-step')\ntry:\n    {'a': 1}.pop('zz')\nexcept KeyError:\n    print('pop-missing')\ntry:\n    'abc'.nope\nexcept AttributeError:\n    print('attr')"
    )]
    [InlineData(
        "total = 0\nfor value in range(2, 20, 3):\n    total += value\nprint(total, range(5), range(1, 7, 2), len(range(10)), len(range(10, 0, -3)))\nprint(range(10)[3], range(10)[-1], range(0, 20, 2)[2:5], range(5)[::-1])\nprint(3 in range(5), 7 in range(5), 4 in range(0, 10, 2))"
    )]
    [InlineData(
        "for pair in enumerate(['a', 'b', 'c']):\n    print(pair)\nfor pair in enumerate('xy', 10):\n    print(pair)\nfor triple in zip([1, 2, 3], 'abcd', range(9)):\n    print(triple)\nfor nothing in zip():\n    print('never')\nprint('done')"
    )]
    [InlineData(
        "a, b = 1, 2\na, b = b, a\nprint(a, b)\nx = 1,\nprint(x)\n(c, d), e = (5, 6), 7\nprint(c, d, e)\nm, n = [10, 20]\nprint(m, n)"
    )]
    [InlineData(
        "data = {'a': 1, 'b': 2}\nfor key, value in data.items():\n    print(key, value)\nfor index, (key, value) in enumerate(data.items()):\n    print(index, key, value)\nfor left, right in zip('ab', range(2)):\n    print(left, right)"
    )]
    [InlineData(
        "def swap(p, q):\n    return q, p\nr, s = swap(1, 2)\nprint(r, s)\nmatrix = [[1, 2], [3, 4]]\nmatrix[0][0], matrix[1][1] = 9, 8\nprint(matrix)\ntry:\n    r, s, t = swap(1, 2)\nexcept ValueError:\n    print('short')"
    )]
    [InlineData(
        "class Counter:\n    kind = 'counter'\n    def __init__(self, value):\n        self.value = value\n    def increment(self, amount=1):\n        self.value += amount\n        return self.value\ncounter = Counter(value=40)\nprint(counter.increment(), counter.increment(amount=1), counter.value, Counter.kind)"
    )]
    [InlineData(
        "x = 99\nprint([x * 2 for x in range(5)], x)\nprint([n for n in range(10) if n % 2 == 0])\nprint([a + b for a in range(3) for b in range(3) if a != b])"
    )]
    [InlineData(
        "print({k: v * 10 for k, v in zip('abc', range(3))})\nprint({v: k for k, v in {'a': 1, 'b': 2}.items()})\nprint([[y for y in range(n)] for n in range(4)])\nprint([cell for row in [[1, 2], [3, 4]] for cell in row])"
    )]
    [InlineData(
        "factor = 3\nprint([n * factor for n in [1, 2]])\ndef scale(values, factor):\n    return [v * factor for v in values]\nprint(scale([1, 2, 3], 5))\nwords = ['hello', 'world', 'hi']\nprint([w.upper() for w in words if len(w) > 2])\nprint([x for x in [y * 2 for y in range(3)]])"
    )]
    [InlineData(
        "assert True\ntry:\n    assert 1 == 2, 'one is not two'\nexcept AssertionError as error:\n    print('caught', error)\ntry:\n    assert False\nexcept AssertionError:\n    print('bare')"
    )]
    [InlineData(
        "values = [1, 2, 3, 4, 5]\ndel values[0]\ndel values[-1]\nprint(values)\ndel values[::2]\nprint(values)\nd = {'a': 1, 'b': 2}\ndel d['a']\nprint(d)\nname = 'temp'\ndel name\ntry:\n    print(name)\nexcept NameError:\n    print('deleted')\nclass Sample:\n    pass\ninstance = Sample()\ninstance.value = 42\ndel instance.value\ntry:\n    print(instance.value)\nexcept AttributeError:\n    print('attribute-deleted')\ndef capture_error():\n    try:\n        raise ValueError('captured')\n    except ValueError as captured:\n        def read():\n            return captured\n    return read\nread_error = capture_error()\ntry:\n    read_error()\nexcept NameError:\n    print('captured-target-deleted')"
    )]
    [InlineData(
        "try:\n    raise ValueError('boom')\nexcept ValueError as error:\n    print('handled', error)\ntry:\n    print(error)\nexcept NameError:\n    print('target-deleted')"
    )]
    [InlineData(
        "print([1, 2] + [3], (1,) + (2, 3), [0] * 3, 2 * (1, 2), [1] * 0)\ntry:\n    print([] * (10 ** 100))\nexcept OverflowError:\n    print('repeat-overflow')\nprint(int(), int('42'), int('  -7 '), int(3.9), int(-3.9), int(True))\nprint(float('2.5'), float(3), str(42), str([1, 2]), bool([]), bool('x'))\nprint(list('abc'), list(range(3)), tuple([1, 2]), dict([('a', 1), ('b', 2)]))"
    )]
    [InlineData(
        "print(isinstance(1, int), isinstance(True, int), isinstance(True, bool), isinstance(1, bool))\nprint(isinstance('x', str), isinstance([1], (int, list)), isinstance(1.5, (int, str)))\nprint(type(1), type('x'), type([]), type(1) is int, type('a') is type('b'))\nprint(isinstance(ValueError('v'), Exception), isinstance(ValueError('v'), LookupError))\nclass Animal:\n    pass\nprint(isinstance(Animal(), Animal), type(Animal()) is Animal)"
    )]
    [InlineData(
        "print(sum([1, 2, 3]), sum(range(5), 100), sum([0.5, 0.25]))\nprint(min([3, 1, 2]), max([3, 1, 2]), min(4, 2, 9), max('a', 'c', 'b'))\nprint(sorted([3, 1, 2]), sorted(['b', 'a']), sorted((5, 4)), abs(-5), abs(-2.5), abs(-3 + 4j))\ntry:\n    int('abc')\nexcept ValueError:\n    print('bad-int')\ntry:\n    min([])\nexcept ValueError:\n    print('empty-min')"
    )]
    [InlineData(
        "class M:\n    def __init__(self, name, suppress=False):\n        self.name = name\n        self.suppress = suppress\n    def __enter__(self):\n        print('enter', self.name)\n        return self.name\n    def __exit__(self, t, v, tb):\n        print('exit', self.name, t is None)\n        return self.suppress\nwith M('plain'):\n    print('body')\nwith M('bound') as name:\n    print('got', name)\nwith M('a') as x, M('b') as y:\n    print('nested', x, y)"
    )]
    [InlineData(
        "class M:\n    def __init__(self, name, suppress=False):\n        self.name = name\n        self.suppress = suppress\n    def __enter__(self):\n        print('enter', self.name)\n        return self.name\n    def __exit__(self, t, v, tb):\n        print('exit', self.name, t is None)\n        return self.suppress\ntry:\n    with M('leaky'):\n        raise ValueError('boom')\nexcept ValueError as error:\n    print('caught', error)\nwith M('quiet', suppress=True):\n    raise ValueError('silenced')\nprint('after')"
    )]
    [InlineData(
        "class M:\n    def __init__(self, name, suppress=False):\n        self.name = name\n        self.suppress = suppress\n    def __enter__(self):\n        print('enter', self.name)\n        return self.name\n    def __exit__(self, t, v, tb):\n        print('exit', self.name, t is None)\n        return self.suppress\ndef run():\n    with M('ret'):\n        return 'early'\nprint(run())\nfor i in range(4):\n    with M('loop'):\n        if i == 2:\n            break\n        print('iter', i)\nprint('after-loop')"
    )]
    [InlineData(
        "class BadEnter:\n    def __enter__(self):\n        raise ValueError('enter-fail')\n    def __exit__(self, t, v, tb):\n        print('never')\n        return False\ntry:\n    with BadEnter():\n        print('unreached')\nexcept ValueError as error:\n    print('enter-error', error)\nclass BadExit:\n    def __enter__(self):\n        return self\n    def __exit__(self, t, v, tb):\n        raise KeyError('exit-fail')\ntry:\n    with BadExit():\n        raise ValueError('original')\nexcept KeyError:\n    print('exit-error-replaced')"
    )]
    [InlineData(
        "add = lambda a, b=10: a + b\nprint(add(1), add(1, 2))\nfactor = 3\nprint((lambda v: v * factor)(5))\ndef make_adder(n):\n    return lambda v: v + n\nprint(make_adder(100)(1))\nprint([f(10) for f in [lambda v: v + 1, lambda v: v * 2]])\napply = lambda f, v: f(v)\nprint(apply(lambda x: x + 1, 41))"
    )]
    [InlineData(
        "s = {1, 2, 2, 3, 1}\nprint(len(s), 2 in s, 9 in s, bool(s), bool(set()), sorted(s))\nprint({42}, set(), len({1, 1.0, True}))\ns.add(9)\ns.discard(1)\nprint(sorted(s), {1, 2} == {2, 1}, {1} == {1, 2}, set() == set())\nprint(isinstance(s, set), type(s) is set, sorted(set([3, 1, 2, 3])))"
    )]
    [InlineData(
        "try:\n    bad = {[1], 2}\nexcept TypeError:\n    print('unhashable')\ntry:\n    {1}.remove(9)\nexcept KeyError:\n    print('remove-missing')\nversions = ['2.0', '10.0', '1.0']\nprint(sorted([int(float(v)) for v in versions]))"
    )]
    [InlineData(
        "name = 'world'\nvalue = 42\nprint(f'hello {name}', f'{value} and {value + 1} and {name.upper()}')\nprint(f'braces {{literal}} and {value}', f'{name!r} {value!s}')"
    )]
    [InlineData(
        "pi = 3.14159265\nvalue = 42\nname = 'world'\nprint(f'{pi:.2f} {pi:10.3f} {pi:<10.1f}|')\nprint(f'{value:5d}|{value:<5d}|{value:^6d}|{value:06d}')\nprint(f'{name:>10}|{name:<10}|{name:^11}|{name:*^11}|{name:.3}')"
    )]
    [InlineData(
        "value = 42\nprint(f'{255:x} {255:X} {255:o} {255:b}')\nprint(f'{0.25:%} {0.25:.1%} {1234567:,d} {1234567.891:,.2f}')\nprint(f'{value:+d} {-value:+d} {value: d}')\nprint(f'{1.5:e} {12345.6789:.2e} {1.5:E}')"
    )]
    [InlineData(
        "d = {'k': 'v'}\nprint(f'{d[\"k\"]} {[x * 2 for x in range(3)]} {(lambda: 7)()}')\na, b = 1, 2\nprint(f'{a} should be <= {b}')\ntry:\n    print(f'{\"text\":d}')\nexcept ValueError:\n    print('bad-code')"
    )]
    [InlineData(
        "def trace(func):\n    print('decorating', func.__name__)\n    return func\n@trace\ndef greet():\n    return 'hi'\nprint(greet(), greet.__name__)"
    )]
    [InlineData(
        "order = []\ndef first(func):\n    order.append('first')\n    return func\ndef second(func):\n    order.append('second')\n    return func\n@first\n@second\ndef target():\n    return 1\nprint(order, target())"
    )]
    [InlineData(
        "registry = {}\nclass Mark:\n    def parametrize(self, names, values):\n        def apply(func):\n            registry[func.__name__] = (names, values)\n            return func\n        return apply\nmark = Mark()\nclass TestThing:\n    @mark.parametrize('a,b', [(1, 2), (3, 4)])\n    def test_add(self, a, b):\n        return a + b\nentry = registry['test_add']\nprint(entry[0], entry[1], TestThing().test_add(1, 2))"
    )]
    [InlineData(
        "def register(cls):\n    print('registered', cls.__name__)\n    return cls\n@register\nclass Widget:\n    def size(self):\n        return 3\nprint(Widget().size())"
    )]
    [InlineData(
        "versions = ['2.10', '2.2', '10.1']\nprint(sorted(versions), sorted(versions, key=lambda v: len(v)))\nprint(sorted(versions, key=lambda v: v, reverse=True), sorted([3, 1, 2], reverse=True))\npairs = [(2, 'b'), (1, 'a'), (2, 'a')]\nprint(sorted(pairs, key=lambda p: p[0]))\nprint(sorted([1, 2, 3], key=lambda v: 0))"
    )]
    [InlineData(
        "def bad(v):\n    raise ValueError('boom')\ntry:\n    sorted([1, 2], key=bad)\nexcept ValueError as error:\n    print('caught', error)\ntry:\n    sorted([1], bad_kw=1)\nexcept TypeError:\n    print('bad-kw')\ntry:\n    print('x', sep=1)\nexcept TypeError:\n    print('bad-sep')"
    )]
    [InlineData(
        "print(1, 2, 3, sep='-', end='!\\n')\nprint('x', 'y', sep='', end='')\nprint('tail')\nprint('a', 'b', sep=None, end=None)\nprint(sep='-')\nprint('solo', end='|')\nprint()"
    )]
    [InlineData(
        "print(sorted([(2, 'b'), (1, 'z'), (2, 'a')]))\nprint((1, 2) < (1, 3), (1, 2) < (1, 2, 0), (2,) > (1, 9, 9), (1, 'a') == (1, 'a'))\nprint(sorted([[3], [1, 2], [1]]), [1, 2] < [1, 2, 0], [2] > [1, 9])\nprint(min([(2, 0), (1, 5)]), max([(2, 0), (1, 5)]))\nnested = [((1, 'b'), 0), ((1, 'a'), 1)]\nprint(sorted(nested), sorted(['b1', 'a2'], key=lambda s: (s[1], s[0])))"
    )]
    [InlineData(
        "try:\n    sorted([(1,), 2])\nexcept TypeError:\n    print('mixed-rejected')\ntry:\n    (1,) < [1]\nexcept TypeError:\n    print('tuple-list-rejected')\nprint(sorted([(1.5, 'x'), (1, 'y')]), (True, 2) < (1, 3))"
    )]
    [InlineData(
        "print(hash(5), hash(-5), hash(0), hash(True), hash(False), hash(-1), hash(-2))\nprint(hash(2**61 - 1), hash(2**61), hash(10**30))\nprint(hash('a') == hash('a'), hash((1, 2)) == hash((1, 2)), hash(1) == hash(True))\ntry:\n    hash([1])\nexcept TypeError:\n    print('unhashable-list')\ntry:\n    hash({})\nexcept TypeError:\n    print('unhashable-dict')"
    )]
    [InlineData(
        "print(1 if True else 2, 'a' if [] else 'b')\nprint([x if x > 1 else 0 for x in [1, 2, 3]], [x for x in [1, 2, 3] if x > 1])\nf = lambda v: 'big' if v > 5 else 'small'\nprint(f(9), f(1), (1 if False else 2) if True else 3)\ndef side(tag):\n    print('eval', tag)\n    return tag\nprint(side('yes') if True else side('no'))"
    )]
    [InlineData(
        "print(any([0, 0, 1]), any([]), any([0]), all([1, 2]), all([]), all([1, 0]))\nprint(chr(65), chr(0x10437), chr(955), ord('A'), ord('\U0001F40D'), ord('λ'))\ntry:\n    chr(0x110000)\nexcept ValueError:\n    print('chr-range')\ntry:\n    ord('ab')\nexcept TypeError:\n    print('ord-len')"
    )]
    [InlineData(
        "print(divmod(7, 2), divmod(-7, 2), divmod(7, -2), divmod(7.5, 2), divmod(-7.5, 2))\ntry:\n    divmod(1, 0)\nexcept ZeroDivisionError:\n    print('divmod-zero')\nprint(round(2.5), round(3.5), round(-2.5), round(2.675, 2), round(1234, -2), round(1250, -2))\nprint(round(2.5, 0), round(1.005, 2), round(123.456, -1), round(7), round(True), round(0.125, 2))"
    )]
    [InlineData(
        "print(list(map(str, [1, 2])), list(map(lambda a, b: a + b, [1, 2], [10, 20, 30])))\nprint(list(filter(None, [0, 1, '', 'x'])), list(filter(lambda v: v > 1, [1, 2, 3])))\nfor pair in map(lambda v: (v, v * 2), [1, 2]):\n    print(pair)\nprint(sorted(map(len, ['aaa', 'b', 'cc'])), any(map(lambda v: v > 2, [1, 3])))"
    )]
    [InlineData(
        "def f(*args):\n    return args\nprint(f(), f(1), f(1, 2, 3))\ndef g(a, b=2, *args, key=10, flag=None, **kw):\n    return (a, b, args, key, flag, sorted(kw.items()))\nprint(g(1))\nprint(g(1, 5, 6, 7, key=9, extra=1, more=2))\ndef wrapper(*args, **kwargs):\n    return g(*args, **kwargs)\nprint(wrapper(1, 5, 6, key=3, z=4))"
    )]
    [InlineData(
        "def h(a, *, b, c=3):\n    return (a, b, c)\nprint(h(1, b=2), h(1, b=2, c=9))\ntry:\n    h(1, 2)\nexcept TypeError:\n    print('pos-after-star')\ntry:\n    h(1)\nexcept TypeError:\n    print('missing-kwonly')\ndef g(a, b=2):\n    return (a, b)\ntry:\n    g(1, a=5)\nexcept TypeError:\n    print('multiple-values')\ntry:\n    g(1, **{'a': 1}, **{'a': 2})\nexcept TypeError:\n    print('dup-star-kw')"
    )]
    [InlineData(
        "items = [1, 2, 3]\ndef f(*args):\n    return args\nprint(f(*items, 4), max(*items), f(0, *items, *items))\nd = {'b': 20}\ndef g(a, b=2):\n    return (a, b)\nprint(g(1, **d), g(*[7], **{}))\nadd = lambda *a, scale=1: sum(a) * scale\nprint(add(1, 2, 3), add(1, 2, scale=10))"
    )]
    [InlineData(
        "a, *rest = [1, 2, 3, 4]\nprint(a, rest)\nfirst, *mid, last = 'abcde'\nprint(first, mid, last)\n*init, tail = (9,)\nprint(init, tail)\ntry:\n    a, *r, b, c = [1, 2]\nexcept ValueError:\n    print('too-few')\nx, *y = range(3)\nprint(x, y)"
    )]
    [InlineData(
        "print({x % 3 for x in range(9)} == {0, 1, 2}, sorted({c for c in 'hello'}))\nprint({len(w) for w in ['a', 'bb', 'a']}, {(v, v * 2) for v in [1, 1, 2]} == {(1, 2), (2, 4)})\nprint(sorted({x * y for x in [1, 2] for y in [3, 4] if x != y}))"
    )]
    [InlineData(
        "print('%s and %s' % ('a', 'b'), '%s' % 'solo', '%s' % (1,))\nprint('%d|%5d|%-5d|%05d|%+d|% d' % (42, 42, 42, 42, 42, 42))\nprint('%x %X %#x %#X %o %#o' % (255, 255, 255, 255, 8, 8))\nprint('%r %s' % ('x', [1, 2]), '%.3s' % 'abcdef', '%%done')\nprint('%s' % {'a': 1}, '%d' % True, '%s' % None, '%d' % -7, '%i' % 3)"
    )]
    [InlineData(
        "print('%f|%.2f|%10.3f|%-10.1f|%e|%.2E' % (3.14159, 3.14159, 3.14159, 3.14159, 12345.678, 12345.678))\nprint('%g %g %g %g %G' % (0.0001, 0.00001, 123456, 1234567, 1.5e-10))\nprint('%g %.3g %g' % (100.0, 3.14159, 1e20))\nprint('%c%c %c' % (72, 'i', 128013))\nprint('%(name)s is %(age)d' % {'name': 'Bob', 'age': 30})\nprint('%*d|%-*d|%.*f' % (6, 42, 6, 42, 2, 3.14159))"
    )]
    [InlineData(
        "try:\n    '%d %d' % (1,)\nexcept TypeError:\n    print('not-enough')\ntry:\n    '%s' % (1, 2)\nexcept TypeError:\n    print('too-many')\ntry:\n    '%z' % 1\nexcept ValueError:\n    print('bad-char')"
    )]
    [InlineData(
        "print('{} {} {}'.format(1, 'two', [3]))\nprint('{0} {1} {0}'.format('a', 'b'), '{1}{0}'.format('x', 'y'))\nprint('{name}: {value}'.format(name='k', value=42))\nprint('{:>8}|{:<8}|{:^8}|{:*^8}'.format('ab', 'ab', 'ab', 'ab'))\nprint('{:.3f} {:d} {:x} {:,}'.format(3.14159, 255, 255, 1234567))\nprint('{!r} {!s}'.format('q', 'q'), '{{literal}} {}'.format(1))"
    )]
    [InlineData(
        "print('{0[1]} {0[0]}'.format(['a', 'b']), '{m[k]}'.format(m={'k': 'v'}))\nprint('{:{}}|'.format('ab', 6), '{:{}.{}f}'.format(3.14159, 8, 2))\ntry:\n    '{} {0}'.format(1, 2)\nexcept ValueError:\n    print('mixed-numbering')\ntry:\n    '{2}'.format(1, 2)\nexcept IndexError:\n    print('index-range')\ntry:\n    '{missing}'.format(x=1)\nexcept KeyError:\n    print('missing-key')"
    )]
    [InlineData(
        "class Animal:\n    kind = 'animal'\n    def __init__(self, name):\n        self.name = name\n    def speak(self):\n        return self.name + ' makes a sound'\n    def label(self):\n        return self.kind + ':' + self.name\nclass Dog(Animal):\n    def speak(self):\n        return self.name + ' barks'\nd = Dog('Rex')\nprint(d.speak(), d.label(), d.kind)\nprint(isinstance(d, Dog), isinstance(d, Animal), type(d) is Dog)"
    )]
    [InlineData(
        "class Base:\n    def greet(self):\n        return 'base:' + self.tag()\n    def tag(self):\n        return 'B'\nclass Child(Base):\n    def tag(self):\n        return 'C'\nprint(Child().greet())\nclass A:\n    def __init__(self, log):\n        self.log = log\n        self.log.append('A')\nclass B(A):\n    def __init__(self, log):\n        super().__init__(log)\n        self.log.append('B')\nclass C(B):\n    def __init__(self, log):\n        super().__init__(log)\n        self.log.append('C')\ntrace = []\nc = C(trace)\nprint(trace, isinstance(c, A))"
    )]
    [InlineData(
        "class Dog:\n    def __init__(self, name):\n        self.name = name\n    def speak(self):\n        return self.name + ' barks'\nclass Puppy(Dog):\n    def __init__(self, name, age):\n        super().__init__(name)\n        self.age = age\n    def speak(self):\n        return super().speak() + ' softly'\np = Puppy('Bo', 1)\nprint(p.speak(), p.name, p.age)"
    )]
    [InlineData(
        "class MyError(ValueError):\n    pass\nclass DeepError(MyError):\n    pass\ntry:\n    raise MyError('boom')\nexcept MyError as e:\n    print('caught-mine', e)\ntry:\n    raise DeepError('deep')\nexcept ValueError:\n    print('caught-deep')\ntry:\n    raise ValueError('plain')\nexcept MyError:\n    print('never')\nexcept ValueError:\n    print('plain-not-mine')\ne = MyError('inst')\nprint(isinstance(e, MyError), isinstance(e, ValueError), isinstance(e, Exception), isinstance(e, KeyError))\ntry:\n    raise DeepError\nexcept MyError:\n    print('bare-raise-type')"
    )]
    [InlineData(
        "name = 'world'\nv = 42\nt = t'hello {name} num {v:>5} end'\nprint(type(t).__name__)\nprint(t.strings, len(t.interpolations), t.values)\ni = t.interpolations[0]\nprint(type(i).__name__, repr(i.value), repr(i.expression), repr(i.conversion), repr(i.format_spec))\nprint(repr(t.interpolations[1].expression), repr(t.interpolations[1].format_spec))\nfor item in t:\n    print('item:', type(item).__name__, repr(item) if isinstance(item, str) else repr(item.value))"
    )]
    [InlineData(
        "v = 42\nprint(repr(t'{v!r}'.interpolations[0].conversion))\nt2 = t'a' + t'{v}'\nprint(type(t2).__name__, t2.strings)\ntry:\n    t'x' + 'y'\nexcept TypeError:\n    print('concat-str-error')\nprint(t''.strings, t'{v}'.strings)\nprint(repr(t'{v}'), str(t'plain'))\nparts = [x for x in t'{1}mid{2}']\nprint(len(parts), parts[1])"
    )]
    [InlineData(
        "v = 'ab'\nprint(f'{v!r:>8}|', f'{v!s:^7}|')\nt = t'{v!r:>6}'\nprint(t.interpolations[0].conversion, t.interpolations[0].format_spec)"
    )]
    [InlineData(
        "if (n := 5) > 3:\n    print('big', n)\nwhile (m := n - 2) > 0:\n    n = m\nprint(n, m)\ndata = [1, 5, 2, 8]\nprint([y for x in data if (y := x * 2) > 5])\ndef f():\n    return (local := 10) + local\nprint(f(), (z := 1) + z)"
    )]
    [InlineData(
        "a = [2, 3]\nd1 = {'x': 1}\nd2 = {'y': 2, 'x': 9}\nprint([1, *a, 4], (0, *a), (*a,), [*a, *a])\nprint({*a, 1, *[3, 9]} == {1, 2, 3, 9}, sorted({*a, *a}))\nprint({**d1, 'z': 3}, {**d1, **d2}, {'x': 0, **d1})"
    )]
    [InlineData(
        "print('{a}-{b}'.format_map({'a': 1, 'b': 2}))\ntry:\n    '{missing}'.format_map({'a': 1})\nexcept KeyError:\n    print('map-missing')\npairs = [(1, 2, 3), (4, 5, 6)]\nfor first, *rest in pairs:\n    print(first, rest)\nfor *init, last in [(1, 2, 3)]:\n    print(init, last)"
    )]
    [InlineData(
        "def greet(name: str, count: int = 2, *args: int, sep: str = '-', **extra: str) -> str:\n    return sep.join([name] * count)\nprint(greet('hi'), greet('a', 3, sep='+'))\ntotal: int = 10\nlabel: str = 'x'\nprint(total, label)\ndef undefined_ann(x: NotDefinedAnywhere) -> AlsoMissing:\n    return x\nprint(undefined_ann(7))\nitems: list = [v * 2 for v in range(3)]\nprint(items)"
    )]
    [InlineData(
        "bare: float\ntry:\n    print(bare)\nexcept UnboundLocalError:\n    print('unbound')\nexcept NameError:\n    print('name-error')\ndef scoped():\n    inner: int\n    try:\n        return inner\n    except UnboundLocalError:\n        return 'inner-unbound'\nprint(scoped())\nclass Config:\n    retries: int = 3\n    timeout: float\n    def __init__(self, url: str) -> None:\n        self.url: str = url\nc = Config('http://x')\nprint(c.retries, c.url)"
    )]
    [InlineData(
        "def counter(limit):\n    n = 0\n    while n < limit:\n        yield n\n        n = n + 1\ng = counter(3)\nprint(type(g).__name__)\nprint(next(g), next(g), next(g))\ntry:\n    next(g)\nexcept StopIteration:\n    print('exhausted')\nprint(next(g, 'default'))\nfor v in counter(4):\n    print('for', v)\nprint(list(counter(3)), sum(counter(5)), sorted(counter(3), reverse=True))"
    )]
    [InlineData(
        "def fib():\n    a, b = 0, 1\n    while True:\n        yield a\n        a, b = b, a + b\nf = fib()\nprint([next(f) for _ in range(8)])\ndef tagged(prefix):\n    yield prefix + '1'\n    yield prefix + '2'\na = tagged('a')\nb = tagged('b')\nprint(next(a), next(b), next(a), next(b))\ndef echo():\n    got = yield 'first'\n    yield got\ne = echo()\nprint(next(e), next(e))"
    )]
    [InlineData(
        "def guarded():\n    try:\n        yield 'in-try'\n        yield 'still-in-try'\n    finally:\n        print('finally-ran')\ngg = guarded()\nprint(next(gg), next(gg))\ntry:\n    next(gg)\nexcept StopIteration:\n    print('done')\ndef failing():\n    yield 1\n    raise ValueError('boom')\nfg = failing()\nprint(next(fg))\ntry:\n    next(fg)\nexcept ValueError as e:\n    print('caught', e)\ntry:\n    next(fg)\nexcept StopIteration:\n    print('completed-after-error')"
    )]
    [InlineData(
        "def counter(limit):\n    n = 0\n    while n < limit:\n        yield n\n        n += 1\nprint(list(map(lambda v: v + 100, counter(3))), list(filter(None, counter(3))))\nprint(max(counter(4)), any(counter(2)), all(counter(2)))\nx, y, *rest = counter(5)\nprint(x, y, rest)\nclass Repo:\n    def __init__(self, items):\n        self.items = items\n    def scan(self):\n        for item in self.items:\n            yield item * 2\nr = Repo([1, 2, 3])\nprint(list(r.scan()), list(r.scan()))"
    )]
    [InlineData(
        "def make_gen(start):\n    def gen():\n        v = start\n        while v < start + 3:\n            yield v\n            v += 1\n    return gen\ng1 = make_gen(10)\nprint(list(g1()), list(g1()))\ndef defaults_gen(base=100, *rest, scale=2):\n    yield base * scale\n    for r in rest:\n        yield r\nprint(list(defaults_gen()), list(defaults_gen(5, 7, 8, scale=3)))"
    )]
    [InlineData(
        "xs = [1, 2, 3, 4]\ng = (x * 2 for x in xs)\nprint(type(g).__name__)\nprint(list(g), list(g))\nprint(sum(x * x for x in xs), max(x for x in xs if x % 2 == 0))\nprint(list(x + y for x in [1, 2] for y in [10, 20]))\nprint(sorted(len(w) for w in ['aaa', 'b', 'cc']))"
    )]
    [InlineData(
        "def trace(v):\n    print('eval', v)\n    return v\nlazy = (trace(x) for x in [1, 2])\nprint('created')\nprint(next(lazy))\nprint(next(lazy))\npairs = ((a, b) for a in 'xy' for b in [1, 2] if b > 1)\nprint(list(pairs))\nnested = list(list(inner for inner in range(n)) for n in [2, 3])\nprint(nested)"
    )]
    [InlineData(
        "xs = [1, 2, 3, 4]\nprint(any(v > 3 for v in xs), all(v > 0 for v in xs))\nprint(sum(y for y in (x * 10 for x in xs)))\nprint(list((x if x > 2 else -x) for x in xs))\nprint(tuple(c.upper() for c in 'abc'), set(v % 2 for v in xs) == {0, 1})\nfirst, *others = (n * n for n in range(4))\nprint(first, others)"
    )]
    [InlineData(
        "def describe(v):\n    match v:\n        case 0:\n            return 'zero'\n        case 1 | 2 | 3:\n            return 'small'\n        case -1:\n            return 'negative one'\n        case 'hello':\n            return 'greeting'\n        case None:\n            return 'nothing'\n        case n if n > 100:\n            return 'big ' + str(n)\n        case n:\n            return 'other ' + str(n)\nprint(describe(0), describe(2), describe(-1))\nprint(describe('hello'), describe(None))\nprint(describe(500), describe(42))"
    )]
    [InlineData(
        "class Color:\n    RED = 1\n    GREEN = 2\ndef name_of(c):\n    match c:\n        case Color.RED:\n            return 'red'\n        case Color.GREEN:\n            return 'green'\n        case _:\n            return 'unknown'\nprint(name_of(1), name_of(2), name_of(9))\nmatch 'x':\n    case ('a' | 'x') as letter:\n        print('letter', letter)\nmatch 99:\n    case 1:\n        print('never')\nprint('fell-through')\ndef order(v):\n    match v:\n        case 1 if False:\n            return 'guarded-out'\n        case 1:\n            return 'plain-one'\n    return 'none'\nprint(order(1), order(2))"
    )]
    [InlineData(
        "match = 5\nprint(match)\ncase = match + 1\nprint(case)\nprint(len([match, case]))\ndef f(match):\n    return match * 2\nprint(f(3))\nmatch (10):\n    case 10:\n        print('paren-subject')\nvalue = 7\nmatch value + 1:\n    case 8 as got:\n        print('expr-subject', got)"
    )]
    [InlineData(
        "def shape(v):\n    match v:\n        case []:\n            return 'empty'\n        case [x]:\n            return 'one:' + str(x)\n        case [x, y]:\n            return 'pair:' + str(x + y)\n        case [first, *rest]:\n            return 'head:' + str(first) + ' rest:' + str(rest)\n        case _:\n            return 'not-seq'\nprint(shape([]), shape([5]), shape([2, 3]), shape([1, 2, 3, 4]))\nprint(shape((7,)), shape('ab'), shape(9))\ndef route(cmd):\n    match cmd:\n        case ('go', direction):\n            return 'going ' + direction\n        case ('drop', *items):\n            return 'dropping ' + str(items)\n        case _:\n            return 'unknown'\nprint(route(('go', 'north')), route(('drop', 'a', 'b')), route(('sing',)))"
    )]
    [InlineData(
        "def config(d):\n    match d:\n        case {'host': h, 'port': p}:\n            return h + ':' + str(p)\n        case {'host': h, **extra}:\n            return h + ' extra=' + str(sorted(extra.items()))\n        case {}:\n            return 'any-dict'\n        case _:\n            return 'not-dict'\nprint(config({'host': 'a', 'port': 80}))\nprint(config({'host': 'b', 'timeout': 5, 'retry': 2}))\nprint(config({'x': 1}), config([1]))"
    )]
    [InlineData(
        "class Point:\n    __match_args__ = ('x', 'y')\n    def __init__(self, x, y):\n        self.x = x\n        self.y = y\ndef where(p):\n    match p:\n        case Point(0, 0):\n            return 'origin'\n        case Point(0, y):\n            return 'y-axis at ' + str(y)\n        case Point(x=x, y=0):\n            return 'x-axis at ' + str(x)\n        case Point(x, y):\n            return 'at ' + str(x) + ',' + str(y)\n        case _:\n            return 'not a point'\nprint(where(Point(0, 0)), where(Point(0, 5)), where(Point(3, 0)))\nprint(where(Point(2, 4)), where('hi'))\ndef kind(v):\n    match v:\n        case int(n):\n            return 'int:' + str(n)\n        case str(s):\n            return 'str:' + s\n        case _:\n            return 'other'\nprint(kind(42), kind('hey'), kind([1]))"
    )]
    [InlineData(
        "class Point:\n    __match_args__ = ('x', 'y')\n    def __init__(self, x, y):\n        self.x = x\n        self.y = y\ndef nested(data):\n    match data:\n        case {'points': [Point(x=0, y=y1), *others]} if y1 > 0:\n            return 'starts-on-y ' + str(y1) + ' others=' + str(len(others))\n        case {'points': []}:\n            return 'no points'\n        case _:\n            return 'other'\nprint(nested({'points': [Point(0, 9), Point(1, 1)]}))\nprint(nested({'points': []}), nested({'points': [Point(3, 3)]}))\nmatch [1, [2, {'k': 'deep'}]]:\n    case [1, [2, {'k': found}]]:\n        print('deep', found)\ndef guarded_bind(v):\n    match v:\n        case [x, y] if x > y:\n            return 'gt ' + str(x)\n        case _:\n            return 'bound anyway ' + str(x) + ',' + str(y)\nprint(guarded_bind([1, 9]), guarded_bind([9, 1]))"
    )]
    [InlineData(
        "def echo():\n    while True:\n        got = yield 'ready'\n        if got == 'stop':\n            return 'finished'\n        print('got', got)\ne = echo()\nprint(next(e))\nprint(e.send('hello'))\nprint(e.send('world'))\ntry:\n    e.send('stop')\nexcept StopIteration:\n    print('stopped')\ne2 = echo()\ntry:\n    e2.send('x')\nexcept TypeError as err:\n    print('fresh-send:', err)"
    )]
    [InlineData(
        "def g1():\n    yield 1\ng = g1()\ntry:\n    g.throw(ValueError('boom'))\nexcept ValueError as err:\n    print('fresh-throw:', err)\ntry:\n    next(g)\nexcept StopIteration:\n    print('closed-after-fresh-throw')\ndef catcher():\n    try:\n        yield 1\n    except ValueError:\n        yield 'caught'\nc = catcher()\nprint(next(c), c.throw(ValueError()))\ndef with_finally():\n    try:\n        yield 1\n    finally:\n        print('cleanup')\nw = with_finally()\nprint(next(w))\nprint(w.close())"
    )]
    [InlineData(
        "def inner():\n    yield 'a'\n    got = yield 'b'\n    print('inner-got', got)\n    return 'inner-result'\ndef outer():\n    result = yield from inner()\n    yield 'outer:' + result\no = outer()\nprint(next(o), next(o))\nprint(o.send('sent-through'))\ndef delegate_list():\n    result = yield from [10, 20]\n    yield result\nprint(list(delegate_list()))"
    )]
    [InlineData(
        "def counter(n):\n    total = 0\n    for i in range(n):\n        got = yield i\n        if got:\n            total += got\n    return total\ndef summing():\n    result = yield from counter(3)\n    yield 'sum:' + str(result)\ns = summing()\nprint(next(s), s.send(10), s.send(20), next(s))\ndef deep_inner():\n    yield 1\n    return 'deep'\ndef middle():\n    r = yield from deep_inner()\n    yield 'mid:' + r\n    return 'mid-done'\ndef top():\n    r = yield from middle()\n    yield 'top:' + r\nprint(list(top()))\ndef depths():\n    try:\n        yield 1\n        yield 2\n        yield 3\n    finally:\n        print('done')\ng = depths()\nprint(next(g))\nprint([next(g), len([next(g)])])\nprint(list(g))"
    )]
    [InlineData(
        "async def greet(name):\n    print('running', name)\n    return 'hi ' + name\nc = greet('ada')\nprint(type(c).__name__)\ntry:\n    c.send(None)\nexcept StopIteration:\n    print('done')\nc2 = greet('bob')\nc2.close()\nc2.close()\nprint('closed-fresh')"
    )]
    [InlineData(
        "async def base(x):\n    return x * 2\nasync def mid(x):\n    v = await base(x)\n    return v + 1\nasync def top(x):\n    return await mid(x) + await mid(x + 1)\nasync def report(x):\n    print('result', await top(x))\nr = report(5)\ntry:\n    r.send(None)\nexcept StopIteration:\n    print('finished')"
    )]
    [InlineData(
        "def trace(fn):\n    print('decorating', fn.__name__)\n    return fn\nclass Runner:\n    @trace\n    async def step(self, n=2, *extra, scale=10):\n        return n * scale + len(extra)\nasync def drive():\n    r = Runner()\n    print('a', await r.step())\n    print('b', await r.step(3, 'x', 'y', scale=100))\nd = drive()\ntry:\n    d.send(None)\nexcept StopIteration:\n    pass\ndef make(base):\n    async def add(n):\n        return base + n\n    return add\nasync def use():\n    print('c', await make(10)(5))\nu = use()\ntry:\n    u.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class Fut:\n    def __await__(self):\n        got = yield 'ready'\n        return got * 2\nasync def worker(name, count):\n    total = 0\n    for i in range(count):\n        total += await Fut()\n    print(name, 'total', total)\na = worker('a', 2)\nb = worker('b', 3)\nprint(a.send(None), b.send(None))\nprint(a.send(5), b.send(1))\ntry:\n    a.send(10)\nexcept StopIteration:\n    print('a done')\nprint(b.send(2))\ntry:\n    b.send(3)\nexcept StopIteration:\n    print('b done')"
    )]
    [InlineData(
        "async def boom():\n    raise ValueError('boom')\nc = boom()\ntry:\n    c.send(None)\nexcept ValueError as e:\n    print('caught', e)\ntry:\n    c.send(None)\nexcept RuntimeError as e:\n    print('reuse:', e)\nasync def plain():\n    return 1\np = plain()\ntry:\n    p.send(1)\nexcept TypeError as e:\n    print('fresh:', e)\np.close()\ntry:\n    p.throw(ValueError('x'))\nexcept RuntimeError as e:\n    print('throw-closed:', e)"
    )]
    [InlineData(
        "class Gate:\n    def __await__(self):\n        yield 'wait'\n        return 'open'\nasync def guarded():\n    try:\n        print('state', await Gate())\n        print('not-reached')\n    except ValueError as e:\n        print('caught inside:', e)\n    return 'done'\ng = guarded()\nprint('first', g.send(None))\ntry:\n    g.throw(ValueError('injected'))\nexcept StopIteration:\n    print('completed')\nasync def cleanup():\n    try:\n        await Gate()\n    finally:\n        print('cleanup ran')\nc = cleanup()\nc.send(None)\nprint('close', c.close())"
    )]
    [InlineData(
        "async def bad():\n    await 1\nc = bad()\ntry:\n    c.send(None)\nexcept TypeError as e:\n    print('e1:', e)\ndef gen():\n    yield 1\nasync def badgen():\n    await gen()\nc2 = badgen()\ntry:\n    c2.send(None)\nexcept TypeError as e:\n    print('e2:', e)\nclass Bad:\n    def __await__(self):\n        return 42\nasync def badclass():\n    await Bad()\nc3 = badclass()\ntry:\n    c3.send(None)\nexcept TypeError as e:\n    print('e3:', e)"
    )]
    [InlineData(
        "async def one():\n    return 1\nc = one()\ntry:\n    for x in c:\n        pass\nexcept TypeError as e:\n    print('e1:', e)\ntry:\n    next(c)\nexcept TypeError as e:\n    print('e2:', e)\nc.close()\nasync def two():\n    return 2\nasync def f():\n    print('pow', (await two()) ** 3)\nd = f()\ntry:\n    d.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class Counter:\n    def __init__(self, n):\n        self.n = n\n        self.i = 0\n    def __aiter__(self):\n        return self\n    async def __anext__(self):\n        if self.i >= self.n:\n            raise StopAsyncIteration\n        self.i += 1\n        return self.i\nasync def collect():\n    out = []\n    async for v in Counter(3):\n        out.append(v)\n    else:\n        out.append('else')\n    print(out)\nc = collect()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass\nasync def nested():\n    total = 0\n    async for a in Counter(2):\n        async for b in Counter(2):\n            total += a * 10 + b\n    print('total', total)\nn = nested()\ntry:\n    n.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class Ctx:\n    def __init__(self, suppress=False):\n        self.suppress = suppress\n    async def __aenter__(self):\n        print('enter')\n        return 'resource'\n    async def __aexit__(self, et, ev, tb):\n        print('exit', et.__name__ if et else None, ev)\n        return self.suppress\nasync def run1():\n    async with Ctx() as r:\n        print('using', r)\n    print('after')\nasync def run2():\n    async with Ctx(suppress=True) as r:\n        raise ValueError('boom')\n    print('suppressed-continues')\nasync def run3():\n    async with Ctx() as r:\n        raise ValueError('boom2')\nfor coro in [run1(), run2()]:\n    try:\n        coro.send(None)\n    except StopIteration:\n        pass\nc3 = run3()\ntry:\n    c3.send(None)\nexcept ValueError as e:\n    print('propagated:', e)"
    )]
    [InlineData(
        "async def bad1():\n    async for v in [1, 2]:\n        pass\nb = bad1()\ntry:\n    b.send(None)\nexcept TypeError as e:\n    print('e1:', e)\nclass NoNext:\n    def __aiter__(self):\n        return 42\nasync def bad2():\n    async for v in NoNext():\n        pass\nb2 = bad2()\ntry:\n    b2.send(None)\nexcept TypeError as e:\n    print('e2:', e)\nclass BadNext:\n    def __aiter__(self):\n        return self\n    def __anext__(self):\n        return 42\nasync def bad3():\n    async for v in BadNext():\n        pass\nb3 = bad3()\ntry:\n    b3.send(None)\nexcept TypeError as e:\n    print('e3:', e)"
    )]
    [InlineData(
        "async def bad4():\n    async with 5:\n        pass\nb4 = bad4()\ntry:\n    b4.send(None)\nexcept TypeError as e:\n    print('e4:', e)\nclass OnlyEnter:\n    async def __aenter__(self):\n        return 1\nasync def bad5():\n    async with OnlyEnter():\n        pass\nb5 = bad5()\ntry:\n    b5.send(None)\nexcept TypeError as e:\n    print('e5:', e)\nclass OnlyExit:\n    async def __aexit__(self, *a):\n        return False\nasync def bad6():\n    async with OnlyExit():\n        pass\nb6 = bad6()\ntry:\n    b6.send(None)\nexcept TypeError as e:\n    print('e6:', e)"
    )]
    [InlineData(
        "class Fut:\n    def __await__(self):\n        v = yield 'pause'\n        return v\nclass Ctx:\n    async def __aenter__(self):\n        return 'r'\n    async def __aexit__(self, et, ev, tb):\n        print('exit sees', et.__name__ if et else None)\n        got = await Fut()\n        print('resumed in aexit', got)\n        return True\nasync def f():\n    async with Ctx():\n        raise ValueError('inner')\n    print('suppressed')\nc = f()\nprint('paused at', c.send(None))\ntry:\n    c.send('go')\nexcept StopIteration:\n    print('done')\ndef g():\n    try:\n        raise ValueError('x')\n    finally:\n        yield 'cleanup'\ngen = g()\nprint('finally yielded', next(gen))\ntry:\n    next(gen)\nexcept ValueError as e:\n    print('resumed raise:', e)"
    )]
    [InlineData(
        "class Counter:\n    def __init__(self, n):\n        self.n = n\n        self.i = 0\n    def __aiter__(self):\n        return self\n    async def __anext__(self):\n        if self.i >= self.n:\n            raise StopAsyncIteration\n        self.i += 1\n        return self.i\nclass Ctx:\n    async def __aenter__(self):\n        return 'r'\n    async def __aexit__(self, et, ev, tb):\n        print('exit runs', et.__name__ if et else None)\n        return False\nasync def loop_break():\n    async for v in Counter(5):\n        if v == 3:\n            break\n        if v == 1:\n            continue\n        print('v', v)\n    else:\n        print('never')\n    print('after-break')\nc = loop_break()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass\nasync def early_return():\n    async with Ctx():\n        return 'early'\nasync def outer():\n    print('returned', await early_return())\no = outer()\ntry:\n    o.send(None)\nexcept StopIteration:\n    pass\nasync def break_out_of_with():\n    async for v in Counter(4):\n        async with Ctx() as r:\n            if v == 2:\n                break\n            print('kept', v)\n    print('after')\nb = break_out_of_with()\ntry:\n    b.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class Quiet:\n    def __init__(self, tag):\n        self.tag = tag\n    async def __aenter__(self):\n        print('enter', self.tag)\n        return self.tag\n    async def __aexit__(self, *a):\n        print('exit', self.tag)\n        return False\nasync def multi():\n    async with Quiet('a') as a, Quiet('b') as b:\n        print('multi', a, b)\nm = multi()\ntry:\n    m.send(None)\nexcept StopIteration:\n    pass\nclass Fut:\n    def __await__(self):\n        got = yield 'ready'\n        return got\nclass Feed:\n    def __init__(self, n):\n        self.n = n\n    def __aiter__(self):\n        return self\n    async def __anext__(self):\n        if self.n == 0:\n            raise StopAsyncIteration\n        self.n -= 1\n        return await Fut()\nasync def consume(tag):\n    async for item in Feed(2):\n        print(tag, 'got', item)\n    print(tag, 'done')\nx = consume('x')\ny = consume('y')\nprint(x.send(None), y.send(None))\nprint(x.send('one'), y.send('uno'))\ntry:\n    x.send('two')\nexcept StopIteration:\n    pass\ntry:\n    y.send('dos')\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class Counter:\n    def __init__(self, n):\n        self.n = n\n        self.i = 0\n    def __aiter__(self):\n        return self\n    async def __anext__(self):\n        if self.i >= self.n:\n            raise StopAsyncIteration\n        self.i += 1\n        return self.i\nasync def body_raises():\n    async for v in Counter(3):\n        if v == 2:\n            raise StopAsyncIteration('from body')\n        print('v', v)\n    print('never')\nb = body_raises()\ntry:\n    b.send(None)\nexcept StopAsyncIteration as e:\n    print('body raise escapes:', e)\nclass MyStop(StopAsyncIteration):\n    pass\nclass SubStop:\n    def __aiter__(self):\n        return self\n    async def __anext__(self):\n        raise MyStop\nasync def sub():\n    async for v in SubStop():\n        print('never')\n    print('subclass stopped cleanly')\ns = sub()\ntry:\n    s.send(None)\nexcept StopIteration:\n    pass\nasync def catcher():\n    try:\n        raise StopAsyncIteration('manual')\n    except StopAsyncIteration as e:\n        print('caught', e)\nk = catcher()\ntry:\n    k.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class It:\n    def __init__(self):\n        self.i = 0\n    def __iter__(self):\n        return self\n    def __next__(self):\n        self.i += 1\n        if self.i > 3:\n            raise StopIteration\n        return self.i * 10\ni1 = It()\nprint('iter-is-self:', iter(i1) is i1)\nprint('for:', [x for x in It()], list(It()), sum(It()), max(It()))\na, b, c = It()\nprint('unpack:', a, b, c)\nprint('zip:', list(zip(It(), 'ab')), 'map:', list(map(str, It())))\nprint('in:', 20 in It(), 99 in It())\nprint('sorted:', sorted(It(), reverse=True), 'tuple:', tuple(It()), 'set:', set(It()) == {10, 20, 30})\nprint('any-all:', any(It()), all(It()), 'min:', min(It()))\nprint('join:', '-'.join(str(x) for x in It()))"
    )]
    [InlineData(
        "class Wrap:\n    def __init__(self, data):\n        self.data = data\n    def __iter__(self):\n        return iter(self.data)\nprint('wrap:', list(Wrap([5, 6])), sum(Wrap((1, 2, 3))))\nclass GenIter:\n    def __iter__(self):\n        yield 'g1'\n        yield 'g2'\nprint('gen-iter:', list(GenIter()))\nclass It:\n    def __init__(self, n):\n        self.n = n\n        self.i = 0\n    def __iter__(self):\n        return self\n    def __next__(self):\n        if self.i >= self.n:\n            raise StopIteration\n        self.i += 1\n        return self.i\nprint(list(enumerate(It(3))), list(zip(It(2), It(5))))\ndef gen():\n    result = yield from It(3)\n    yield 'tail'\nprint(list(gen()))\nclass Pairs:\n    def __iter__(self):\n        yield ('a', 1)\n        yield ('b', 2)\nprint(dict(Pairs()) == {'a': 1, 'b': 2})\nclass Stop(StopIteration):\n    pass\nclass SubIt:\n    def __iter__(self):\n        return self\n    def __next__(self):\n        raise Stop\nprint('sub-stop:', list(SubIt()))\nfirst, *rest = It(4)\nprint('star:', first, rest)"
    )]
    [InlineData(
        "class NoIter:\n    pass\ntry:\n    for x in NoIter():\n        pass\nexcept TypeError as e:\n    print('m1:', e)\ntry:\n    iter(42)\nexcept TypeError as e:\n    print('m2:', e)\nclass BadIter:\n    def __iter__(self):\n        return 42\ntry:\n    for x in BadIter():\n        pass\nexcept TypeError as e:\n    print('m3:', e)\ntry:\n    next(42)\nexcept TypeError as e:\n    print('m4:', e)\nclass OnlyNext:\n    def __init__(self):\n        self.i = 0\n    def __next__(self):\n        self.i += 1\n        if self.i > 2:\n            raise StopIteration\n        return self.i\no = OnlyNext()\nprint('next-only:', next(o), next(o), next(o, 'done'))\ntry:\n    for x in OnlyNext():\n        pass\nexcept TypeError as e:\n    print('m5:', e)"
    )]
    [InlineData(
        "class Box:\n    def __init__(self):\n        self.value = 1\nb = Box()\nprint(getattr(b, 'value'), getattr(b, 'missing', 'dflt'), hasattr(b, 'value'), hasattr(b, 'nope'))\ntry:\n    getattr(b, 'nope')\nexcept AttributeError as e:\n    print('a1:', e)\nsetattr(b, 'extra', 99)\nprint(b.extra, getattr(b, 'extra'))\nprint(sorted(vars(b).items()))\ntry:\n    vars(5)\nexcept TypeError as e:\n    print('a2:', e)\nx = Box()\ny = Box()\nprint('id-rel:', id(x) == id(x), id(x) == id(y), isinstance(id(x), int))\ntry:\n    getattr(b, 5)\nexcept TypeError as e:\n    print('a3:', e)"
    )]
    [InlineData(
        "print('minmax:', min([3, 1, 2], key=lambda v: -v), max(['aa', 'b'], key=len))\nprint('default:', min([], default='empty'), max((), default=None))\ntry:\n    min([])\nexcept ValueError as e:\n    print('m6:', e)\ntry:\n    max(())\nexcept ValueError as e:\n    print('m7:', e)\nclass It:\n    def __init__(self, n):\n        self.n = n\n        self.i = 0\n    def __iter__(self):\n        return self\n    def __next__(self):\n        if self.i >= self.n:\n            raise StopIteration\n        self.i += 1\n        return self.i\nprint(min(It(4), key=lambda v: -v), max(It(4), key=lambda v: -v))\nprint(sorted(It(3), key=lambda v: -v))"
    )]
    [InlineData(
        "f = frozenset([3, 1, 2, 1])\nprint(f == {1, 2, 3}, len(f), 2 in f, 9 in f)\nprint(type(f).__name__, frozenset() == set(), frozenset('ab') == {'a', 'b'})\nd = {f: 'key-ok'}\nprint(d[frozenset([1, 2, 3])])\nprint({frozenset([1]), frozenset([1])} == {frozenset([1])})\nprint(hash(frozenset([1, 2])) == hash(frozenset([2, 1])))\nprint(isinstance(f, frozenset), isinstance({1}, frozenset), isinstance(f, set))\nprint(repr(frozenset([1])), repr(frozenset()))\nprint(frozenset({1: 'a', 2: 'b'}) == {1, 2})\nprint(f.copy() == f, type(f.copy()).__name__, list(sorted(f)))\nnested = frozenset([frozenset([1]), frozenset([2])])\nprint(len(nested), frozenset([1]) in nested)"
    )]
    [InlineData(
        "f = frozenset([1])\ntry:\n    f.add(4)\nexcept AttributeError as e:\n    print('f1:', e)\ns = {1}\ntry:\n    hash(s)\nexcept TypeError as e:\n    print('f2:', e)\ntry:\n    d2 = {{1}: 'x'}\nexcept TypeError as e:\n    print('f3:', e)\nd = {}\ntry:\n    d[[1]] = 2\nexcept TypeError as e:\n    print('f4:', e)\ntry:\n    bad = {[1], 2}\nexcept TypeError as e:\n    print('f5:', e)\ntry:\n    s.add([2])\nexcept TypeError as e:\n    print('f6:', e)\ntry:\n    hash([1])\nexcept TypeError as e:\n    print('f7:', e)"
    )]
    [InlineData(
        "async def agen():\n    yield 1\n    yield 2\ng = agen()\nprint('type:', type(g).__name__)\nasync def consume():\n    out = []\n    async for v in agen():\n        out.append(v)\n    print('collected:', out)\nc = consume()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass\ntry:\n    for x in g:\n        pass\nexcept TypeError as e:\n    print('e1:', e)\ntry:\n    next(g)\nexcept TypeError as e:\n    print('e2:', e)\nasync def bad():\n    await agen()\nb = bad()\ntry:\n    b.send(None)\nexcept TypeError as e:\n    print('e3:', e)\nclass Fut:\n    def __await__(self):\n        v = yield 'pause'\n        return v\nasync def slowgen():\n    got = await Fut()\n    yield 'a' + got\n    yield 'b'\nasync def drive():\n    async for v in slowgen():\n        print('drove', v)\nd = drive()\nprint('suspended at:', d.send(None))\ntry:\n    d.send('X')\nexcept StopIteration:\n    print('done')"
    )]
    [InlineData(
        "async def echo():\n    total = 0\n    while True:\n        got = yield total\n        if got is None:\n            break\n        total += got\nasync def driver():\n    g = echo()\n    print('first', await g.__anext__())\n    print('sent5', await g.asend(5))\n    print('sent7', await g.asend(7))\n    try:\n        await g.asend(None)\n    except StopAsyncIteration:\n        print('stopped')\nd = driver()\ntry:\n    d.send(None)\nexcept StopIteration:\n    pass\nasync def one():\n    yield 1\nasync def fresh():\n    g = one()\n    try:\n        await g.asend(5)\n    except TypeError as e:\n        print('fresh:', e)\n    print(await g.asend(None))\n    try:\n        await g.__anext__()\n    except StopAsyncIteration:\n        print('exhausted')\n    try:\n        await g.__anext__()\n    except StopAsyncIteration:\n        print('still stopped')\nf = fresh()\ntry:\n    f.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "async def cleaner():\n    try:\n        yield 1\n    finally:\n        print('cleanup ran')\nasync def close_driver():\n    g = cleaner()\n    print('got', await g.__anext__())\n    print('closed', await g.aclose())\ncd = close_driver()\ntry:\n    cd.send(None)\nexcept StopIteration:\n    pass\nasync def stubborn():\n    try:\n        yield 1\n    except GeneratorExit:\n        yield 2\nasync def stubborn_driver():\n    g = stubborn()\n    await g.__anext__()\n    try:\n        await g.aclose()\n    except RuntimeError as e:\n        print('ignored:', e)\nsd = stubborn_driver()\ntry:\n    sd.send(None)\nexcept StopIteration:\n    pass\nasync def catcher():\n    try:\n        yield 'ready'\n    except ValueError as e:\n        yield 'caught ' + str(e)\nasync def throw_driver():\n    g = catcher()\n    print(await g.__anext__())\n    print(await g.athrow(ValueError('boom')))\ntd = throw_driver()\ntry:\n    td.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "async def agen():\n    yield 1\ng = agen()\nasync def check():\n    print('aiter-is-self:', g.__aiter__() is g)\n    async for v in g:\n        print('v', v)\nc = check()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass\nclass Holder:\n    def __aiter__(self):\n        return agen()\nasync def via_holder():\n    async for v in Holder():\n        print('held', v)\nh = via_holder()\ntry:\n    h.send(None)\nexcept StopIteration:\n    pass\nasync def failing():\n    yield 1\n    raise ValueError('agen boom')\nasync def fail_driver():\n    try:\n        async for v in failing():\n            print('got', v)\n    except ValueError as e:\n        print('caught:', e)\nfd = fail_driver()\ntry:\n    fd.send(None)\nexcept StopIteration:\n    pass\nclass Fut:\n    def __await__(self):\n        got = yield 'tick'\n        return got\nasync def paced(tag):\n    for i in range(2):\n        v = await Fut()\n        yield tag + str(i) + v\nasync def consume(tag):\n    async for item in paced(tag):\n        print(tag, item)\nx = consume('x')\ny = consume('y')\nprint(x.send(None), y.send(None))\nprint(x.send('!'), y.send('?'))\ntry:\n    x.send('!')\nexcept StopIteration:\n    pass\ntry:\n    y.send('?')\nexcept StopIteration:\n    print('both done')"
    )]
    [InlineData(
        "eg = ExceptionGroup('problems', [ValueError('a'), TypeError('b')])\nprint('str:', str(eg))\nprint('repr:', repr(eg))\nprint('message:', eg.message)\nprint('exceptions:', [type(e).__name__ for e in eg.exceptions], [str(e) for e in eg.exceptions])\nprint('type:', type(eg).__name__)\nbeg = BaseExceptionGroup('base', [ValueError('x')])\nprint('auto-downcast:', type(beg).__name__)\nbeg2 = BaseExceptionGroup('base', [GeneratorExit()])\nprint('stays-base:', type(beg2).__name__)\ntry:\n    ExceptionGroup('bad', [GeneratorExit()])\nexcept TypeError as e:\n    print('t1:', e)\ntry:\n    ExceptionGroup('empty', [])\nexcept ValueError as e:\n    print('t2:', e)\ntry:\n    ExceptionGroup('notexc', [42])\nexcept ValueError as e:\n    print('t3:', e)\nnested = ExceptionGroup('outer', [ValueError('a'), ExceptionGroup('inner', [TypeError('b')])])\nprint('nested-str:', str(nested))\nprint('nested-repr:', repr(nested))\nprint('isinstance:', isinstance(eg, ExceptionGroup), isinstance(eg, Exception), isinstance(ValueError('v'), ExceptionGroup))"
    )]
    [InlineData(
        "try:\n    raise ExceptionGroup('g', [ValueError('v')])\nexcept ExceptionGroup as e:\n    print('caught-as-group:', e.message)\ntry:\n    raise ExceptionGroup('g', [ValueError('v')])\nexcept Exception as e:\n    print('caught-as-exception:', type(e).__name__)\ntry:\n    raise ExceptionGroup('g', [ValueError('v')])\nexcept ValueError as e:\n    print('never')\nexcept BaseExceptionGroup as e:\n    print('caught-as-basegroup:', type(e).__name__)\ntry:\n    raise ExceptionGroup('g', [ValueError('v1'), TypeError('t1'), ValueError('v2')])\nexcept* ValueError as e:\n    print('ve:', type(e).__name__, e.message, [str(x) for x in e.exceptions])\nexcept* TypeError as e:\n    print('te:', type(e).__name__, e.message, [str(x) for x in e.exceptions])\nprint('all-consumed')"
    )]
    [InlineData(
        "try:\n    try:\n        raise ExceptionGroup('g2', [ValueError('v'), OverflowError('o')])\n    except* ValueError as e:\n        print('matched:', [str(x) for x in e.exceptions])\nexcept ExceptionGroup as rest:\n    print('rest:', rest.message, [type(x).__name__ for x in rest.exceptions])\ntry:\n    try:\n        raise ValueError('naked')\n    except* ValueError as e:\n        print('naked-match:', type(e).__name__, repr(e.message), [str(x) for x in e.exceptions])\nexcept Exception as e:\n    print('escaped', e)\ntry:\n    try:\n        raise OverflowError('nk')\n    except* ValueError:\n        print('never')\nexcept OverflowError as e:\n    print('passthrough:', type(e).__name__, e)\ntry:\n    try:\n        raise ExceptionGroup('outer', [ValueError('a'), ExceptionGroup('inner', [ValueError('b'), TypeError('c')])])\n    except* ValueError as e:\n        print('split-matched:', repr(e))\nexcept ExceptionGroup as rest:\n    print('split-rest:', repr(rest))\ntry:\n    try:\n        raise ExceptionGroup('g3', [ValueError('v'), OverflowError('o')])\n    except* ValueError:\n        raise RuntimeError('from-handler')\nexcept BaseException as e:\n    print('combined:', type(e).__name__, repr(e))"
    )]
    [InlineData(
        "try:\n    try:\n        raise ExceptionGroup('g', [ValueError('v')])\n    except* ValueError:\n        raise RuntimeError('solo')\nexcept BaseException as e:\n    print('solo-raise:', type(e).__name__, repr(e))\ntry:\n    try:\n        raise ExceptionGroup('g', [OverflowError('o')])\n    except* ValueError:\n        print('never')\nexcept BaseException as e:\n    print('nomatch:', type(e).__name__, repr(e))\ntry:\n    pass\nexcept* ValueError:\n    print('never')\nelse:\n    print('else-ran')\ntry:\n    raise ExceptionGroup('g', [ValueError('v')])\nexcept* OverflowError as k:\n    print('never')\nexcept* ValueError as v:\n    print('second-clause:', [str(x) for x in v.exceptions])\ntry:\n    raise ExceptionGroup('g', [ValueError('v')])\nexcept* ValueError as a:\n    print('first', [str(x) for x in a.exceptions])\nexcept* ValueError as b:\n    print('second-never')\ntry:\n    raise ExceptionGroup('g', [ValueError('v'), OverflowError('o'), TypeError('t')])\nexcept* (ValueError, OverflowError) as e:\n    print('tuple:', sorted(type(x).__name__ for x in e.exceptions))\nexcept* TypeError as e:\n    print('rest-t:', [type(x).__name__ for x in e.exceptions])\ndef wrapped():\n    try:\n        raise ExceptionGroup('wf', [ValueError('a'), OverflowError('b')])\n    except* ValueError as e:\n        print('inner-handled:', [str(x) for x in e.exceptions])\n    finally:\n        print('finally-ran')\ntry:\n    wrapped()\nexcept ExceptionGroup as rest:\n    print('outer-rest:', [type(x).__name__ for x in rest.exceptions])"
    )]
    [InlineData(
        "import math\nprint(math.pi, math.e, math.tau, math.inf, -math.inf, math.nan)\nprint(math.sqrt(9), math.sqrt(2), math.sqrt(0))\ntry:\n    math.sqrt(-1)\nexcept ValueError as e:\n    print('e1:', e)\nprint(math.floor(3.7), math.floor(-3.7), math.ceil(3.2), math.ceil(-3.2), type(math.floor(3.7)).__name__)\nprint(math.floor(5), math.ceil(5), math.floor(True))\nprint(math.fabs(-3), type(math.fabs(-3)).__name__, math.trunc(-3.9), type(math.trunc(-3.9)).__name__)\nprint(math.gcd(12, 18), math.gcd(), math.gcd(7), math.gcd(0, 0), math.gcd(-12, 18))\nprint(math.lcm(4, 6), math.lcm(), math.lcm(0, 5))\nprint(math.factorial(0), math.factorial(5), math.factorial(20))\ntry:\n    math.factorial(-1)\nexcept ValueError as e:\n    print('e2:', e)\ntry:\n    math.factorial(3.5)\nexcept TypeError as e:\n    print('e3:', e)\nfrom math import sqrt, pi\nprint('from-import:', sqrt(pi) > 1.77)"
    )]
    [InlineData(
        "import math\nprint(math.log(math.e), math.log(8, 2), math.log2(8), math.log10(1000), math.exp(0), math.exp(1))\ntry:\n    math.log(0)\nexcept ValueError as e:\n    print('e4:', e)\nprint(math.isnan(math.nan), math.isnan(1.0), math.isinf(math.inf), math.isfinite(1.5), math.isfinite(math.inf))\nprint(math.pow(2, 10), type(math.pow(2, 10)).__name__, math.pow(2, 0.5))\nprint(math.hypot(3, 4), math.degrees(math.pi), math.radians(180))\nprint(math.sin(0), math.cos(0), math.tan(0))\nprint(math.fmod(7, 3), math.fmod(-7, 3))\nprint(math.copysign(3, -1), math.copysign(-3, 1))\nprint(math.comb(5, 2), math.perm(5, 2), math.comb(5, 9), math.perm(3, 0))\ntry:\n    math.sqrt('x')\nexcept TypeError as e:\n    print('e5:', e)\nprint(math.isclose(1.0, 1.0000000001), math.isclose(1.0, 1.1), math.isclose(math.inf, math.inf))"
    )]
    [InlineData(
        "import copy\na = [1, [2, 3], {'k': [4]}]\ns = copy.copy(a)\nd = copy.deepcopy(a)\nprint(s == a, s is a, s[1] is a[1])\nprint(d == a, d is a, d[1] is a[1], d[2]['k'] is a[2]['k'])\na[1].append(99)\nprint(s[1], d[1])\nt = (1, 2)\nprint(copy.copy(t) is t, copy.deepcopy(t) is t, copy.copy(5) == 5)\ntm = ([1], 2)\ndtm = copy.deepcopy(tm)\nprint(dtm is tm, dtm[0] is tm[0])\ncyc = [1]\ncyc.append(cyc)\ndc = copy.deepcopy(cyc)\nprint(dc[1] is dc, dc[1] is not cyc)\nclass Box:\n    def __init__(self, v):\n        self.v = v\n        self.items = [v]\nb = Box(7)\ncb = copy.copy(b)\ndb = copy.deepcopy(b)\nprint(type(cb).__name__, cb is b, cb.v, cb.items is b.items)\nprint(db.items is b.items, db.items == b.items)\nb.items.append(8)\nprint(cb.items, db.items)\nshared = [1]\npair = [shared, shared]\ndp = copy.deepcopy(pair)\nprint(dp[0] is dp[1], dp[0] is not shared)\nfs = frozenset([1, 2])\nprint(copy.copy(fs) is fs, copy.deepcopy(fs) == fs)"
    )]
    [InlineData(
        "e = ValueError('a', 'b')\nprint(e.args, str(e), repr(e))\ne2 = ValueError()\nprint(e2.args, repr(str(e2)), repr(e2))\ne3 = ValueError(42)\nprint(e3.args, str(e3), repr(e3))\nobj = [1, 2]\ne4 = TypeError(obj)\nprint(e4.args[0] is obj, str(e4))\nk = KeyError('nk')\nprint(k.args, str(k), repr(k))\nprint(str(KeyError(42)), str(KeyError('a', 'b')), repr(str(KeyError())))\ntry:\n    raise ValueError('boom')\nexcept ValueError as e:\n    print('plain:', e.args, str(e), repr(e))\ntry:\n    assert False, 'failed here'\nexcept AssertionError as e:\n    print('assert:', e.args, str(e))\nclass MyErr(ValueError):\n    pass\ntry:\n    raise MyErr('custom', 5)\nexcept MyErr as e:\n    print('subclass:', e.args, str(e), repr(e))\neg = ExceptionGroup('m', [ValueError('v')])\nprint('eg-args:', eg.args, type(eg.args[1]).__name__)"
    )]
    [InlineData(
        "d = {'a': 1}\ntry:\n    d['b']\nexcept KeyError as e:\n    print('sub:', e.args, str(e))\ntry:\n    d.pop('z')\nexcept KeyError as e:\n    print('pop:', e.args, str(e))\ntry:\n    del d['x']\nexcept KeyError as e:\n    print('del:', e.args, str(e))\ntry:\n    {1}.remove(2)\nexcept KeyError as e:\n    print('rem:', e.args, str(e))\ndef g():\n    yield 1\n    return 'result'\ngen = g()\nprint(next(gen))\ntry:\n    next(gen)\nexcept StopIteration as e:\n    print('si:', e.args, repr(e.value), str(e))\ndef g2():\n    yield 1\ngen2 = g2()\nnext(gen2)\ntry:\n    next(gen2)\nexcept StopIteration as e:\n    print('si-none:', e.args, repr(e.value))\ndef echo():\n    got = yield 'ready'\n    return got * 2\nec = echo()\nnext(ec)\ntry:\n    ec.send(21)\nexcept StopIteration as e:\n    print('send-si:', e.value)\nasync def coro():\n    return 99\nc = coro()\ntry:\n    c.send(None)\nexcept StopIteration as e:\n    print('coro-si:', e.value, e.args)"
    )]
    [InlineData(
        "import pickle\ndata = pickle.dumps([1, {'k': (2, 3)}, 'x'])\nprint(isinstance(data, bytes))\nrestored = pickle.loads(data)\nagain = pickle.loads(data)\nprint(restored, restored == [1, {'k': (2, 3)}, 'x'])\nprint(restored is not again, restored == again)\nclass Box:\n    def __init__(self, v):\n        self.v = v\nb = Box(9)\nrb = pickle.loads(pickle.dumps(b))\nprint(type(rb).__name__, rb.v, rb is b)\nimport os\nprint(os.path.join('a', 'b', 'c'), os.path.join('/x', 'y'), os.path.join('a', '/z'))\nprint(os.path.dirname('/a/b/c.py'), repr(os.path.dirname('name')), os.path.dirname('/top'))\nprint(os.path.basename('/a/b/c.py'), os.path.basename('plain'))"
    )]
    [InlineData(
        "print(bytes(), bytes(3), bytes([65, 66, 67]), bytes(b'copy'))\ntry:\n    bytes('text')\nexcept TypeError as e:\n    print('b1:', e)\ntry:\n    bytes([300])\nexcept ValueError as e:\n    print('b2:', e)\nprint(isinstance(b'x', bytes), isinstance('x', bytes), type(b'x').__name__)"
    )]
    [InlineData(
        "class ParseError(ValueError):\n    def __init__(self, line, message):\n        super().__init__(message)\n        self.line = line\ne = ParseError(42, 'bad token')\nprint(e.args, str(e), e.line, isinstance(e, ValueError))\ntry:\n    raise ParseError(7, 'boom')\nexcept ParseError as exc:\n    print('caught:', exc.line, str(exc), exc.args)\nexcept ValueError:\n    print('never')\nclass NoSuper(Exception):\n    def __init__(self, code):\n        self.code = code\nn = NoSuper(5)\nprint('nosuper:', n.args, repr(str(n)), n.code)\ne.extra = 'later'\nprint(e.extra, hasattr(e, 'line'), hasattr(e, 'nope'))\ntry:\n    e.missing\nexcept AttributeError as a:\n    print('a1:', a)\nclass KwErr(Exception):\n    def __init__(self, msg, *, code=0):\n        super().__init__(msg)\n        self.code = code\nk = KwErr('m', code=3)\nprint('kw:', k.args, k.code)\nclass Deep(ParseError):\n    def __init__(self, line):\n        super().__init__(line, 'deep default')\n        self.deep = True\nd = Deep(9)\nprint('deep:', d.line, d.deep, str(d), isinstance(d, ParseError))"
    )]
    [InlineData(
        "async def agen():\n    yield 1\n    yield 2\nasync def drive():\n    g = agen()\n    print(await anext(g), await anext(g))\n    print(await anext(g, 'end'))\n    try:\n        await anext(g)\n    except StopAsyncIteration:\n        print('exhausted')\n    class UserAiter:\n        def __aiter__(self):\n            return self\n        async def __anext__(self):\n            return 'ua'\n    print(await anext(UserAiter()))\nd = drive()\ntry:\n    d.send(None)\nexcept StopIteration:\n    pass\ntry:\n    anext(42)\nexcept TypeError as e:\n    print('t1:', e)"
    )]
    [InlineData(
        "async def nums():\n    for i in range(4):\n        yield i\nasync def collect():\n    squares = [x * x async for x in nums()]\n    evens = {x async for x in nums() if x % 2 == 0}\n    named = {x: str(x) async for x in nums() if x < 2}\n    print(squares, evens == {0, 2}, named)\nc = collect()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass\nasync def one(v):\n    return v * 10\nasync def awaited_comp():\n    vals = [await one(i) for i in range(3)]\n    filtered = [await one(i) for i in range(4) if i % 2]\n    print(vals, filtered)\na = awaited_comp()\ntry:\n    a.send(None)\nexcept StopIteration:\n    pass\nasync def mixed():\n    pairs = [(x, await one(x)) async for x in nums() if x < 3]\n    print(pairs)\nm = mixed()\ntry:\n    m.send(None)\nexcept StopIteration:\n    pass\nasync def plain_still_sync():\n    plain = [x + 1 for x in range(3)]\n    print('sync-comp', plain)\np = plain_still_sync()\ntry:\n    p.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class Fut:\n    def __await__(self):\n        got = yield 'tick'\n        return got\nasync def paced():\n    for i in range(2):\n        v = await Fut()\n        yield i * 100 + len(v)\nasync def suspended_comp():\n    values = [x async for x in paced()]\n    print('collected', values)\ns = suspended_comp()\nprint(s.send(None))\nprint(s.send('ab'))\ntry:\n    s.send('xyz')\nexcept StopIteration:\n    pass\nasync def nums():\n    for i in range(4):\n        yield i\nasync def nested_comp():\n    grid = [[y async for y in nums()] for _ in range(2)]\n    print(grid)\nn = nested_comp()\ntry:\n    n.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "class A:\n    def who(self): return 'A'\n    tag = 'a'\nclass B(A):\n    def who(self): return 'B'\nclass C(A):\n    def who(self): return 'C'\n    tag = 'c'\nclass D(B, C):\n    pass\nprint([t.__name__ for t in D.__mro__ if t.__name__ != 'object'])\nprint([t.__name__ for t in D.__bases__])\nd = D()\nprint(d.who(), d.tag, D().who())\nprint(isinstance(d, A), isinstance(d, B), isinstance(d, C))\ntry:\n    class Bad(A, A):\n        pass\nexcept TypeError as e:\n    print('e1:', e)\nclass X: pass\nclass Y(X): pass\ntry:\n    class Z(X, Y):\n        pass\nexcept TypeError as e:\n    print('e2:', e)\nmatch d:\n    case C():\n        print('pattern-matches-C')"
    )]
    [InlineData(
        "class WA:\n    def __init__(self):\n        self.trail = ['A']\nclass WB(WA):\n    def __init__(self):\n        super().__init__()\n        self.trail.append('B')\nclass WC(WA):\n    def __init__(self):\n        super().__init__()\n        self.trail.append('C')\nclass WD(WB, WC):\n    def __init__(self):\n        super().__init__()\n        self.trail.append('D')\nprint(WD().trail)\nclass M1:\n    def name(self): return 'M1'\nclass M2(M1):\n    def name(self): return 'M2+' + super().name()\nclass M3(M1):\n    def name(self): return 'M3+' + super().name()\nclass M4(M2, M3):\n    def name(self): return 'M4+' + super().name()\nprint(M4().name())\nclass Mix:\n    def helper(self): return 'mix:' + self.base()\nclass Base:\n    def base(self): return 'base'\nclass App(Mix, Base):\n    pass\nprint(App().helper())\nclass Wide(M2, M3, Mix, Base):\n    pass\nprint(Wide().name(), Wide().helper())\nprint([t.__name__ for t in Wide.__mro__ if t.__name__ != 'object'])"
    )]
    [InlineData(
        "print(callable(len), callable(print), callable(int), callable(lambda: 1), callable(5), callable('x'))\nclass NoCall: pass\nprint(callable(NoCall()), callable(NoCall))\nclass Box: pass\nb = Box()\nb.v = 1\ndelattr(b, 'v')\nprint(hasattr(b, 'v'))\ntry:\n    delattr(b, 'v')\nexcept AttributeError as e:\n    print('d1:', e)\ncounter = [0]\ndef step():\n    counter[0] += 1\n    return counter[0]\nprint(list(iter(step, 4)))\ndef f():\n    a = 1\n    b = 'x'\n    return vars()\nprint(f())\ne = ValueError('v')\nprint(e.with_traceback(None) is e)\ntry:\n    try:\n        raise OverflowError('inner')\n    except OverflowError:\n        raise ValueError('outer')\nexcept ValueError as exc:\n    print(type(exc.__context__).__name__, exc.__cause__, exc.__suppress_context__)\ntry:\n    raise ValueError('x') from TypeError('cause')\nexcept ValueError as exc:\n    print(type(exc.__cause__).__name__, exc.__suppress_context__)"
    )]
    [InlineData(
        "def g(a, b, /, c, *, d=4):\n    return (a, b, c, d)\nprint(g(1, 2, 3), g(1, 2, c=3, d=9))\ntry:\n    g(1, b=2, c=3)\nexcept TypeError as e:\n    print('p1:', e)\ndef kw_sink(a, /, **kw):\n    return (a, kw)\nprint(kw_sink(1, a=2))\ndef h(x, y, z=0):\n    return (x, y, z)\nrest = [5, 6]\nprint(h(z=9, *rest))\nlam = lambda a, /, b: (a, b)\nprint(lam(1, b=2))"
    )]
    [InlineData(
        "async def nums():\n    for i in range(4):\n        yield i\nasync def main():\n    g = (x * x async for x in nums())\n    print(type(g).__name__)\n    print([v async for v in g])\n    h = (x async for x in nums() if x % 2 == 0)\n    print(await anext(h), await anext(h))\n    try:\n        await anext(h)\n    except StopAsyncIteration:\n        print('done')\nc = main()\ntry:\n    c.send(None)\nexcept StopIteration:\n    print('finished')"
    )]
    [InlineData(
        "async def nums():\n    for i in range(4):\n        yield i\ndef build():\n    return (x + 100 async for x in nums())\ng = (n * 2 async for n in nums())\nprint(type(g).__name__)\nasync def one(v):\n    return v * 10\nasync def main():\n    print([v async for v in build()])\n    print([v async for v in g])\n    inner = (x * 2 async for x in nums())\n    print([y + 1 async for y in inner])\n    e = (await one(i) async for i in nums())\n    print([v async for v in e])\n    try:\n        for v in build():\n            pass\n    except TypeError as err:\n        print('E:', err)\nc = main()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "async def nums():\n    for i in range(3):\n        print('yield', i)\n        yield i\nasync def consume(agen):\n    total = 0\n    async for v in agen:\n        total += v\n    return total\nasync def main():\n    lazy = (x * x async for x in nums())\n    print('created')\n    print(await consume(lazy))\n    print(await consume(y + 1 async for y in nums()))\nc = main()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "async def nums():\n    for i in range(3):\n        yield i\nasync def one(v):\n    return v * 10\nasync def main():\n    g = (x * 10 async for x in nums())\n    print(await g.asend(None))\n    print(await g.asend(None))\n    await g.aclose()\n    try:\n        await anext(g)\n    except StopAsyncIteration:\n        print('closed')\n    h = (x async for x in nums())\n    await anext(h)\n    try:\n        await h.athrow(ValueError('boom'))\n    except ValueError as e:\n        print('caught', e)\n    m = (i * j async for i in nums() for j in [1, 2])\n    print([v async for v in m])\n    a = (await one(i) for i in range(4))\n    print(type(a).__name__)\n    print([v async for v in a])\nc = main()\ntry:\n    c.send(None)\nexcept StopIteration:\n    pass"
    )]
    [InlineData(
        "try:\n    open('dotpython_missing_file_84129.txt')\nexcept FileNotFoundError as e:\n    print('fnf:', isinstance(e, OSError), e)\ntry:\n    open('dotpython_missing_file_84129.txt', 'z')\nexcept ValueError as e:\n    print('vm:', e)\ntry:\n    open()\nexcept TypeError as e:\n    print('t:', e)\ntry:\n    open(None)\nexcept TypeError as e:\n    print('t2:', e)"
    )]
    public void CommandExecution_MatchesReferencePythonForSupportedSubset(string code)
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        var reference = RunReference(python, code);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", code],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(reference.ExitCode, exitCode);
        Assert.Equal(reference.StandardOutput, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void CommandExecution_UsesProcessStyleExitCodesAndErrorStream()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", "print(missing)"],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("DPY4002", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedImports_MatchReferenceForTheSupportedTopLevelSubset()
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        const string moduleSource =
            "print('initializing')\nanswer = 40\ndef add(value): return answer + value\n";
        const string code =
            "import helper as module\n"
            + "from helper import add as calculate\n"
            + "import helper\n"
            + "print(module.answer, calculate(2), helper == module)";
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-import-differential-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "helper.py"), moduleSource);
            var reference = RunReference(python, code, directory);
            var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
            {
                ["helper"] = new(moduleSource, "helper.py"),
            };
            using var output = new StringWriter();

            var result = new ManagedPythonEngine(modules).Execute(
                code,
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(result.Success);
            Assert.Equal(0, reference.ExitCode);
            Assert.Equal(reference.StandardOutput, output.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ManagedPackageImports_MatchReferenceForDottedAndRelativeImports()
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        const string packageSource = "print('package')\nfrom . import tools\n";
        const string toolsSource = "print('tools')\nfrom .values import answer\n";
        const string valuesSource = "answer = 42\n";
        const string code =
            "import package.tools\n"
            + "from package import (tools as imported_tools,)\n"
            + "print(package.tools.answer, imported_tools.answer, package.tools == imported_tools)";
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-package-import-differential-{Guid.NewGuid():N}"
        );
        var packageDirectory = Path.Combine(directory, "package");
        Directory.CreateDirectory(packageDirectory);
        try
        {
            File.WriteAllText(Path.Combine(packageDirectory, "__init__.py"), packageSource);
            File.WriteAllText(Path.Combine(packageDirectory, "tools.py"), toolsSource);
            File.WriteAllText(Path.Combine(packageDirectory, "values.py"), valuesSource);
            var reference = RunReference(python, code, directory);
            var modules = new Dictionary<string, SourceText>(StringComparer.Ordinal)
            {
                ["package"] = new(packageSource, "package/__init__.py"),
                ["package.tools"] = new(toolsSource, "package/tools.py"),
                ["package.values"] = new(valuesSource, "package/values.py"),
            };
            using var output = new StringWriter();

            var result = new ManagedPythonEngine(modules).Execute(
                code,
                "main.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.True(result.Success);
            Assert.Equal(0, reference.ExitCode);
            Assert.Equal(reference.StandardOutput, output.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScriptPackageAndDistributionMetadataDiscovery_MatchesReferencePython()
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-script-discovery-differential-{Guid.NewGuid():N}"
        );
        var packageDirectory = Path.Combine(directory, "sample");
        var metadataDirectory = Path.Combine(directory, "sample_dist-1.2.3.dist-info");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(metadataDirectory);
        var scriptPath = Path.Combine(directory, "main.py");
        try
        {
            File.WriteAllText(
                Path.Combine(packageDirectory, "__init__.py"),
                "from importlib.metadata import version\n"
                    + "from . import values\n"
                    + "__version__ = version('sample-dist')\n"
                    + "answer = values.answer\n"
            );
            File.WriteAllText(Path.Combine(packageDirectory, "values.py"), "answer = 42\n");
            File.WriteAllText(
                Path.Combine(metadataDirectory, "METADATA"),
                "Metadata-Version: 2.4\nName: sample-dist\nVersion: 1.2.3\n"
            );
            File.WriteAllText(
                scriptPath,
                "import sample\nprint(sample.answer, sample.__version__)\n"
            );
            var reference = RunReferenceScript(python, scriptPath);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = DotPythonCommand.Run(
                [scriptPath],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(reference.ExitCode, exitCode);
            Assert.Equal(reference.StandardOutput, output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScriptFileReads_MatchReferencePythonForTheOpenBuiltin()
    {
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dotpython-open-differential-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "main.py");
        try
        {
            var dataPath = Path.Combine(directory, "data.txt");
            var crlfPath = Path.Combine(directory, "crlf.txt");
            var missingPath = Path.Combine(directory, "missing.txt");
            var subdirPath = Path.Combine(directory, "subdir");
            File.WriteAllText(dataPath, "alpha\nbeta\ngamma");
            File.WriteAllText(crlfPath, "a\r\nb\r\nc");
            Directory.CreateDirectory(subdirPath);
            File.WriteAllText(
                scriptPath,
                $"f = open('{dataPath}')\n"
                    + "print(repr(f))\n"
                    + "print(f.mode, f.closed)\n"
                    + "print(repr(f.read(3)), repr(f.read()))\n"
                    + "f.close()\n"
                    + "print(f.closed)\n"
                    + "try:\n"
                    + "    f.readline()\n"
                    + "except ValueError as e:\n"
                    + "    print('closed:', e)\n"
                    + $"with open('{dataPath}') as g:\n"
                    + "    print(g.readlines())\n"
                    + "print(g.closed)\n"
                    + $"h = open('{crlfPath}')\n"
                    + "print(repr(h.read()))\n"
                    + "h.close()\n"
                    + "count = 0\n"
                    + "joined = ''\n"
                    + $"with open('{dataPath}') as it:\n"
                    + "    for line in it:\n"
                    + "        count += 1\n"
                    + "        joined += line\n"
                    + "print(count, repr(joined))\n"
                    + $"n = open('{dataPath}')\n"
                    + "print(repr(next(n)), repr(n.readline()))\n"
                    + "n.close()\n"
                    + "try:\n"
                    + $"    open('{missingPath}')\n"
                    + "except FileNotFoundError as e:\n"
                    + "    print('fnf:', isinstance(e, OSError), e)\n"
                    + "try:\n"
                    + $"    open('{subdirPath}')\n"
                    + "except IsADirectoryError as e:\n"
                    + "    print('dir:', e)\n"
                    + "try:\n"
                    + $"    open('{dataPath}', 'q')\n"
                    + "except ValueError as e:\n"
                    + "    print('mode:', e)\n"
            );
            var reference = RunReferenceScript(python, scriptPath);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = DotPythonCommand.Run(
                [scriptPath],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(reference.ExitCode, exitCode);
            Assert.Equal(reference.StandardOutput, output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StandardInputScripts_MatchReferencePythonForTheInputBuiltin()
    {
        const string code =
            "print('x:', input())\n"
            + "print('y:', repr(input('p> ')))\n"
            + "try:\n"
            + "    input('q> ')\n"
            + "except EOFError as e:\n"
            + "    print('eof:', e)\n"
            + "try:\n"
            + "    input('a', 'b')\n"
            + "except TypeError as e:\n"
            + "    print('args:', e)\n";
        const string standardInput = "one\ntwo\n";
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        var reference = RunReference(python, code, standardInput: standardInput);
        using var input = new StringReader(standardInput);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", code],
            input,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(reference.ExitCode, exitCode);
        Assert.Equal(reference.StandardOutput, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void RaisedExceptionHandlersAndFinally_MatchReferencePython()
    {
        const string code =
            "def classify(value):\n"
            + "    try:\n"
            + "        if value:\n"
            + "            raise ValueError('bad')\n"
            + "    except TypeError:\n"
            + "        print('wrong')\n"
            + "    except (ValueError, RuntimeError) as error:\n"
            + "        print('caught', error)\n"
            + "    else:\n"
            + "        print('clean')\n"
            + "    finally:\n"
            + "        print('done')\n"
            + "classify(False)\n"
            + "classify(True)\n"
            + "try:\n"
            + "    try:\n"
            + "        raise ValueError('again')\n"
            + "    except ValueError:\n"
            + "        raise\n"
            + "except Exception as error:\n"
            + "    print('reraised', error)\n";
        var python = FindReferencePython();
        if (python is null)
        {
            Assert.Skip(
                $"A Python {ReferenceVersion} executable is required for this differential test."
            );
        }

        var reference = RunReference(python, code);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = DotPythonCommand.Run(
            ["-c", code],
            TextReader.Null,
            output,
            error,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(reference.ExitCode, exitCode);
        Assert.Equal(reference.StandardOutput, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    private static ReferenceResult RunReference(
        string executable,
        string code,
        string? workingDirectory = null,
        string? standardInput = null
    )
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ReferenceResult(process.ExitCode, standardOutput);
    }

    private static ReferenceResult RunReferenceScript(string executable, string scriptPath)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ReferenceResult(process.ExitCode, standardOutput);
    }

    private static string ReferenceVersion => PythonLanguageVersion.Current.ToString(2);

    private static string? FindReferencePython()
    {
        var configured = Environment.GetEnvironmentVariable("DOTPYTHON_REFERENCE_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && IsReferencePython(configured))
        {
            return configured;
        }

        foreach (var candidate in new[] { $"python{ReferenceVersion}", "python3" })
        {
            if (IsReferencePython(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsReferencePython(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                && output.StartsWith($"Python {ReferenceVersion}.", StringComparison.Ordinal);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private sealed record ReferenceResult(int ExitCode, string StandardOutput);
}
