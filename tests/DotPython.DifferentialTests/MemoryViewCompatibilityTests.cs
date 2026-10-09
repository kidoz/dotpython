using Xunit;

namespace DotPython.DifferentialTests;

public sealed class MemoryViewCompatibilityTests
{
    [Fact]
    public Task ConstructionAndMembers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            view = memoryview(b'abcdef')
            print('members', view.obj, view.format, view.itemsize, view.ndim)
            print('shape', view.shape, view.strides, view.readonly, view.nbytes)
            print('contig', view.contiguous, view.c_contiguous, view.f_contiguous, view.suboffsets)
            print('len', len(view), view[0], view[-1], view.index(102), view.count(97))
            print('tobytes', view.tobytes(), view.hex(), view.tolist())
            print('cast', memoryview(b'abcd').cast('B').format, memoryview(b'abcd').cast('b').format, memoryview(b'abcd').cast('@H').itemsize)
            print('types', isinstance(view, memoryview), type(view).__name__, type(view) is memoryview, str(view)[:9])
            print('keyword', memoryview(object=b'a').obj)
            print('nested', memoryview(memoryview(b'ab')).obj)
            print('bytearray source', memoryview(bytearray(b'abc')).obj.__class__.__name__)
            """
        );

    [Fact]
    public Task ConstructionFailures() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            probe(lambda: memoryview())
            probe(lambda: memoryview(b'a', 1, 2))
            probe(lambda: memoryview(nope=b'a'))
            probe(lambda: memoryview(b'a', nope=1))
            probe(lambda: memoryview(object=b'a', bogus=b'b'))
            probe(lambda: memoryview([1, 2]))
            probe(lambda: memoryview(b'abc').cast('Z'))
            probe(lambda: memoryview(b'abc').cast('I'))
            probe(lambda: memoryview(b'abcd').cast('B', (3,)))
            probe(lambda: memoryview(b'abcd').cast('B', (0,)))
            probe(lambda: memoryview(b'abcd').cast('B', 4))
            probe(lambda: memoryview(b'abcd')[::2].cast('H'))
            probe(lambda: memoryview(b'a').cast())
            probe(lambda: memoryview(b'a').cast('B', (1,), (1,)))
            probe(lambda: memoryview(b'ab').count())
            probe(lambda: memoryview(b'ab').index(1, 0, 3, 4))
            probe(lambda: memoryview(b'ab').tobytes(1))
            probe(lambda: memoryview(b'ab').tobytes('X'))
            probe(lambda: memoryview(b'ab').tobytes('C', 2))
            probe(lambda: memoryview(b'ab').tolist(1))
            probe(lambda: memoryview(b'ab').release(1))
            probe(lambda: memoryview(b'ab').count(value=1))
            probe(lambda: memoryview(b'ab').hex('-', 2, 3))
            """
        );

    [Fact]
    public Task SlicesStridesAndDimensions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            view = memoryview(b'abcdef')
            print('slices', view[1:3].tobytes(), view[::2].tobytes(), view[::-1].tobytes())
            print('slice attrs', view[1:3].shape, view[::2].strides, len(view[::-1]), view[::-1].tolist())
            print('slice obj', view[1:3].obj is view.obj, type(view[1:3].obj).__name__)
            print('slice read', bytes(view[1:3]), list(view[1:3]), view[1:3] == b'bc')
            print('2d', memoryview(b'abcd').cast('B', (2, 2)).tolist(), memoryview(b'abcd').cast('B', (2, 2)).tobytes())
            print('2d attrs', memoryview(b'abcd').cast('B', (2, 2)).shape, memoryview(b'abcd').cast('B', (2, 2)).strides)
            print('2d order', memoryview(b'abcd').cast('B', (2, 2)).tobytes('F'), memoryview(b'abcd').cast('B', (2, 2)).c_contiguous, memoryview(b'abcd').cast('B', (2, 2)).f_contiguous)
            print('2d slice', memoryview(b'abcd').cast('B', (2, 2))[1:].tobytes(), memoryview(b'abcd').cast('B', (2, 2))[::2].strides)
            print('cast values', memoryview(b'abcd').cast('H').tolist(), memoryview(b'abcd').cast('c')[0])
            probe(lambda: memoryview(b'abcd')[::-1].cast('H'))
            print('reversed', list(reversed(memoryview(b'abc'))), type(reversed(memoryview(b'abc'))).__name__)
            probe(lambda: list(memoryview(b'abcd').cast('B', (2, 2))))
            probe(lambda: memoryview(b'abcd').cast('B', (2, 2))[0])
            probe(lambda: memoryview(b'abcd').cast('B', (2, 2)).index(97))
            """
        );

    [Fact]
    public Task MutationAndExports() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            holder = bytearray(b'abcd')
            mutable = memoryview(holder)
            print('mutable', mutable.readonly, mutable.obj is holder, bytes(mutable))
            mutable[0] = 90
            mutable[1:3] = b'xy'
            print('written', bytes(holder), bytes(mutable))
            probe(lambda: memoryview(b'abc').__setitem__(0, 65))
            probe(lambda: memoryview(b'abc').__delitem__(0))
            probe(lambda: memoryview(bytearray(b'abc')).__delitem__(0))
            def exports():
                target = bytearray(b'ab')
                held = memoryview(target)
                try:
                    target.append(99)
                    return 'resized'
                except BufferError as error:
                    return 'BufferError: ' + str(error)
            print('exports', exports())
            def after_release():
                target = bytearray(b'ab')
                held = memoryview(target)
                held.release()
                target.append(99)
                return bytes(target)
            print('after release', after_release())
            def readonly_released():
                target = bytearray(b'ab')
                held = memoryview(target)
                copy = held.toreadonly()
                copy.release()
                try:
                    target.append(99)
                    return 'resized'
                except BufferError:
                    return 'BufferError'
            print('readonly released', readonly_released())
            def both_released():
                target = bytearray(b'ab')
                held = memoryview(target)
                copy = held.toreadonly()
                held.release()
                copy.release()
                target.append(99)
                return bytes(target)
            print('both released', both_released())
            print('toreadonly', memoryview(bytearray(b'ab')).toreadonly().readonly, memoryview(bytearray(b'ab')).toreadonly().obj.__class__.__name__)
            print('nested export', type(memoryview(memoryview(bytearray(b'a'))).obj).__name__)
            probe(lambda: memoryview(b'a').release())
            """
        );

    [Fact]
    public Task EqualityHashingAndReleasedViews() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            view = memoryview(b'abcdef')
            print('equality', view == b'abcdef', view == memoryview(b'abcdef'), view != b'abcdef')
            print('equality2', view == b'abc', view == bytearray(b'abcdef'), b'abcdef' == view)
            print('equality3', memoryview(b'abcd').cast('b') == memoryview(b'abcd'), memoryview(b'abcd').cast('B', (2, 2)) == memoryview(b'abcd'))
            print('equality4', view == 'abcdef', view == 1, view == None)
            print('hash', hash(memoryview(b'ab')) == hash(b'ab'), hash(memoryview(b'ab')[0:1]) == hash(b'a'), {memoryview(b'ab'): 1}[b'ab'])
            print('contains', 97 in view, b'a' in view, 12345 in view)
            probe(lambda: hash(memoryview(bytearray(b'a'))))
            probe(lambda: hash(memoryview(b'abcd').cast('h')))
            released = memoryview(b'a')
            released.release()
            print('released eq', released == released, released == b'a', b'a' == released)
            probe(lambda: released.tobytes())
            probe(lambda: len(released))
            probe(lambda: bool(released))
            probe(lambda: released.obj)
            probe(lambda: hash(released))
            """
        );

    [Fact]
    public Task OperatorsAndConsumers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            probe(lambda: memoryview(b'a') + b'b')
            probe(lambda: b'a' + memoryview(b'b'))
            probe(lambda: bytearray(b'a') + memoryview(b'b'))
            probe(lambda: memoryview(b'ab') < b'ab')
            print('ordering', memoryview(b'ab') < bytearray(b'ba'), bytearray(b'ab') < memoryview(b'ba'), bytearray(b'ab') <= memoryview(b'ab'))
            probe(lambda: memoryview(b'ab') < memoryview(b'ab'))
            probe(lambda: sorted([memoryview(b'b'), memoryview(b'a')]))
            probe(lambda: min([memoryview(b'a'), memoryview(b'b')]))
            print('consumers', bytes(memoryview(b'ab')), bytearray(memoryview(b'ab')), b'%s' % memoryview(b'ab'))
            import json
            probe(lambda: json.loads(memoryview(b'[1]')))
            print('from_bytes', int.from_bytes(memoryview(b'\x01\x02'), 'big'))
            print('dir', '__len__' in dir(memoryview(b'a')), 'cast' in dir(memoryview(b'a')), 'nope' in dir(memoryview(b'a')))
            """
        );

    [Fact]
    public Task ResizesUnderAnExport() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def case(label, thunk):
                target = bytearray(b'abcd')
                view = memoryview(target)
                try:
                    return label + ' ' + repr(thunk(target))
                except Exception as error:
                    return label + ' ' + type(error).__name__ + ' ' + str(error)
            print(case('set item', lambda t: (t.__setitem__(0, 90), bytes(t))[1]))
            print(case('set same slice', lambda t: (t.__setitem__(slice(0, 2), b'xy'), bytes(t))[1]))
            print(case('stepped same', lambda t: (t.__setitem__(slice(None, None, 2), b'xy'), bytes(t))[1]))
            print(case('stepped diff', lambda t: (t.__setitem__(slice(None, None, 2), b'xyz'), bytes(t))[1]))
            print(case('insert empty', lambda t: (t.__setitem__(slice(1, 1), b''), bytes(t))[1]))
            print(case('insert bytes', lambda t: (t.__setitem__(slice(1, 1), b'x'), bytes(t))[1]))
            print(case('set longer slice', lambda t: (t.__setitem__(slice(0, 2), b'xyz'), bytes(t))[1]))
            print(case('del empty', lambda t: (t.__delitem__(slice(3, 3)), bytes(t))[1]))
            print(case('del slice', lambda t: (t.__delitem__(slice(0, 1)), bytes(t))[1]))
            print(case('del item', lambda t: (t.__delitem__(0), bytes(t))[1]))
            print(case('pop oob', lambda t: t.pop(99)))
            print(case('iadd empty', lambda t: (t.__iadd__(b''), bytes(t))[1]))
            print(case('iadd bytes', lambda t: (t.__iadd__(b'x'), bytes(t))[1]))
            print(case('imul one', lambda t: (t.__imul__(1), bytes(t))[1]))
            print(case('imul zero', lambda t: (t.__imul__(0), bytes(t))[1]))
            print(case('imul two', lambda t: (t.__imul__(2), bytes(t))[1]))
            print(case('pop', lambda t: t.pop()))
            print(case('append', lambda t: t.append(1)))
            print(case('remove', lambda t: t.remove(97)))
            print(case('clear', lambda t: t.clear()))
            print(case('reverse', lambda t: (t.reverse(), bytes(t))[1]))
            """
        );
}
