using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ByteArrayCompatibilityTests
{
    [Fact]
    public Task ConstructionAndDisplayFollowCPython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: bytearray())
            probe(lambda: bytearray(3))
            probe(lambda: bytearray(b'abc'))
            probe(lambda: bytearray(bytearray(b'ab')))
            probe(lambda: bytearray('café', 'utf-8'))
            probe(lambda: bytearray([97, 98, 99]))
            probe(lambda: bytearray((97, 98)))
            probe(lambda: bytearray(range(3)))
            probe(lambda: bytearray(-1))
            probe(lambda: bytearray('abc'))
            probe(lambda: bytes(bytearray(b'ab')))
            print(repr(bytearray(b'ab\x00\xff')), str(bytearray(b'ab')), type(bytearray()).__name__)
            """
        );

    [Fact]
    public Task SequenceBehaviourMatchesBytesAndOrdersAgainstIt() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            b = bytearray(b'abc')
            print(len(b), b[0], b[-1], b[1:], type(b[1:]).__name__)
            print(list(b), list(reversed(b)), bool(b), bool(bytearray()))
            print(97 in b, b'a' in b, b'bc' in b, 98 not in b)
            print(bytearray(b'ab') == b'ab', b'ab' == bytearray(b'ab'), bytearray(b'ab') == bytearray(b'ab'))
            print(bytearray(b'ab') != b'ac', bytearray(b'ab') < b'ac', bytearray(b'b') >= b'a')
            print(bytearray(b'a') + b'b', b'a' + bytearray(b'b'), bytearray(b'a') + bytearray(b'b'))
            print(bytearray(b'ab') * 2, 2 * bytearray(b'ab'))

            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)

            probe(lambda: hash(bytearray(b'ab')))
            probe(lambda: {bytearray(b'ab'): 1})
            probe(lambda: 300 in bytearray(b'ab'))
            """
        );

    [Fact]
    public Task ItemAssignmentAndDeletionFollowTheSliceRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def show(label, mutate):
                target = bytearray(b'abc')
                try:
                    mutate(target)
                    print(label, '->', repr(target))
                except Exception as error:
                    print(label, '->', type(error).__name__, error, repr(target))

            def set_index(index, value):
                def run(b):
                    b[index] = value
                return run

            def set_slice(start, stop, value):
                def run(b):
                    b[start:stop] = value
                return run

            def set_step(step, value):
                def run(b):
                    b[::step] = value
                return run

            def set_pair(first, second):
                def run(b):
                    b[0], b[1] = first, second
                return run

            def delete(index):
                def run(b):
                    del b[index]
                return run

            show('assign', set_index(0, 122))
            show('assign neg', set_index(-1, 121))
            show('assign pair', set_pair(80, 81))
            show('assign big', set_index(0, 300))
            show('assign oob', set_index(9, 1))
            show('assign bytes', set_index(0, b'x'))
            show('slice same', set_slice(0, 1, b'X'))
            show('slice grow', set_slice(1, 2, b'XYZ'))
            show('slice shrink', set_slice(0, 2, b'Q'))
            show('slice iterable', set_slice(0, 1, [81, 82]))
            show('slice bytearray', set_slice(0, 1, bytearray(b'Z')))
            show('slice int', set_slice(0, 1, 5))
            show('ext ok', set_step(2, b'XY'))
            show('ext bad', set_step(2, b'X'))
            show('del item', delete(0))
            show('del neg', delete(-1))
            show('del oob', delete(9))
            show('del slice', delete(slice(0, 2)))
            show('del step', delete(slice(None, None, 2)))
            show('del step back', delete(slice(None, None, -1)))
            """
        );

    [Fact]
    public Task MutatingMethodsReturnWhatCPythonReturns() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def run(label, thunk):
                target = bytearray(b'abc')
                try:
                    result = thunk(target)
                    print(label, '->', repr(result), repr(target))
                except Exception as error:
                    print(label, '->', type(error).__name__, error, repr(target))

            run('append', lambda b: b.append(100))
            run('append big', lambda b: b.append(300))
            run('append bytes', lambda b: b.append(b'x'))
            run('extend', lambda b: b.extend(b'de'))
            run('extend list', lambda b: b.extend([100, 101]))
            run('extend range', lambda b: b.extend(range(2)))
            run('insert', lambda b: b.insert(1, 120))
            run('insert neg', lambda b: b.insert(-1, 120))
            run('pop', lambda b: b.pop())
            run('pop 0', lambda b: b.pop(0))
            run('pop empty', lambda b: bytearray().pop())
            run('remove', lambda b: b.remove(98))
            run('remove miss', lambda b: b.remove(122))
            run('clear', lambda b: b.clear())
            run('reverse', lambda b: b.reverse())
            run('copy', lambda b: b.copy())

            print('--- in place identity ---')
            b = bytearray(b'ab')
            c = b
            b += b'cd'
            print(c is b, repr(b))
            b *= 2
            print(c is b, repr(b))
            """
        );

    [Fact]
    public Task ByteArrayIsBytesLikeWhereBytesAreExpected() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import json, codecs

            print(json.loads(bytearray(b'[1, 2, 3]')))
            print(json.loads(bytearray(b'{"a": 1}')))
            print(json.dumps(json.loads(bytearray(b'[1]'))))
            print(codecs.getdecoder('utf-8')(bytearray(b'ab')))
            print(codecs.lookup('utf-8').decode(bytearray(b'abc')))
            print(bytearray(b'ab').decode('utf-8'), bytearray(b'ab').decode())
            print(bytearray(b'ab').hex(), bytearray(b'ab').upper(), bytearray(b'ab').center(4, b'-'))
            print(bytearray(b'a,b').split(b','), bytearray(b'a,b').replace(b',', b'-'))
            print(bytearray(b'ab').translate(bytes.maketrans(b'ab', b'xy')))
            """
        );
}
