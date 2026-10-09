using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class MemoryViewExecutionTests
{
    [Fact]
    public void ConstructionAndMembers()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "members b'abcdef' B 1 1",
                "shape (6,) (1,) True 6",
                "contig True True True ()",
                "len 6 97 102 5 1",
                "tobytes b'abcdef' 616263646566 [97, 98, 99, 100, 101, 102]",
                "cast B b 2",
                "types True memoryview True <memory a",
                "keyword b'a'",
                "nested b'ab'",
                "bytearray source bytearray"
            ),
            output
        );
    }

    [Fact]
    public void ConstructionFailures()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "TypeError memoryview() missing required argument 'object' (pos 1)",
                "TypeError memoryview() takes at most 1 argument (3 given)",
                "TypeError memoryview() missing required argument 'object' (pos 1)",
                "TypeError memoryview() takes at most 1 argument (2 given)",
                "TypeError memoryview() takes at most 1 keyword argument (2 given)",
                "TypeError memoryview: a bytes-like object is required, not 'list'",
                "ValueError memoryview: destination format must be a native single character format prefixed with an optional '@'",
                "TypeError memoryview: length is not a multiple of itemsize",
                "TypeError memoryview: product(shape) * itemsize != buffer size",
                "ValueError memoryview.cast(): elements of shape must be integers > 0",
                "TypeError shape must be a list or a tuple",
                "TypeError memoryview: casts are restricted to C-contiguous views",
                "TypeError cast() missing required argument 'format' (pos 1)",
                "TypeError cast() takes at most 2 arguments (3 given)",
                "TypeError memoryview.count() takes exactly one argument (0 given)",
                "TypeError index expected at most 3 arguments, got 4",
                "TypeError tobytes() argument 'order' must be str or None, not int",
                "ValueError order must be 'C', 'F' or 'A'",
                "TypeError tobytes() takes at most 1 argument (2 given)",
                "TypeError memoryview.tolist() takes no arguments (1 given)",
                "TypeError memoryview.release() takes no arguments (1 given)",
                "TypeError memoryview.count() takes no keyword arguments",
                "TypeError hex() takes at most 2 arguments (3 given)"
            ),
            output
        );
    }

    [Fact]
    public void SlicesStridesAndDimensions()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "slices b'bc' b'ace' b'fedcba'",
                "slice attrs (2,) (2,) 6 [102, 101, 100, 99, 98, 97]",
                "slice obj True bytes",
                "slice read b'bc' [98, 99] True",
                "2d [[97, 98], [99, 100]] b'abcd'",
                "2d attrs (2, 2) (2, 1)",
                "2d order b'acbd' True False",
                "2d slice b'cd' (4, 1)",
                "cast values [25185, 25699] b'a'",
                "TypeError memoryview: casts are restricted to C-contiguous views",
                "reversed [99, 98, 97] reversed",
                "NotImplementedError multi-dimensional sub-views are not implemented",
                "NotImplementedError multi-dimensional sub-views are not implemented",
                "NotImplementedError multi-dimensional lookup is not implemented"
            ),
            output
        );
    }

    [Fact]
    public void MutationAndExports()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "mutable False True b'abcd'",
                "written b'Zxyd' b'Zxyd'",
                "TypeError cannot modify read-only memory",
                "TypeError cannot modify read-only memory",
                "TypeError cannot delete memory",
                "exports BufferError: Existing exports of data: object cannot be re-sized",
                "after release b'abc'",
                "readonly released BufferError",
                "both released b'abc'",
                "toreadonly True bytearray",
                "nested export bytearray",
                "None"
            ),
            output
        );
    }

    [Fact]
    public void EqualityHashingAndReleasedViews()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "equality True True False",
                "equality2 False True True",
                "equality3 True False",
                "equality4 False False False",
                "hash True True 1",
                "contains True False False",
                "ValueError cannot hash writable memoryview object",
                "ValueError memoryview: hashing is restricted to formats 'B', 'b' or 'c'",
                "released eq True False False",
                "ValueError operation forbidden on released memoryview object",
                "ValueError operation forbidden on released memoryview object",
                "ValueError operation forbidden on released memoryview object",
                "ValueError operation forbidden on released memoryview object",
                "ValueError operation forbidden on released memoryview object"
            ),
            output
        );
    }

    [Fact]
    public void OperatorsAndConsumers()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "TypeError unsupported operand type(s) for +: 'memoryview' and 'bytes'",
                "b'ab'",
                "bytearray(b'ab')",
                "TypeError '<' not supported between instances of 'memoryview' and 'bytes'",
                "ordering True True True",
                "TypeError '<' not supported between instances of 'memoryview' and 'memoryview'",
                "TypeError '<' not supported between instances of 'memoryview' and 'memoryview'",
                "TypeError '<' not supported between instances of 'memoryview' and 'memoryview'",
                "consumers b'ab' bytearray(b'ab') b'ab'",
                "TypeError the JSON object must be str, bytes or bytearray, not memoryview",
                "from_bytes 258",
                "dir True True False"
            ),
            output
        );
    }

    [Fact]
    public void ResizesUnderAnExport()
    {
        var output = Run(
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

        Assert.Equal(
            Lines(
                "set item b'Zbcd'",
                "set same slice b'xycd'",
                "stepped same b'xbyd'",
                "stepped diff ValueError attempt to assign bytes of size 3 to extended slice of size 2",
                "insert empty b'abcd'",
                "insert bytes BufferError Existing exports of data: object cannot be re-sized",
                "set longer slice BufferError Existing exports of data: object cannot be re-sized",
                "del empty b'abcd'",
                "del slice BufferError Existing exports of data: object cannot be re-sized",
                "del item BufferError Existing exports of data: object cannot be re-sized",
                "pop oob IndexError pop index out of range",
                "iadd empty b'abcd'",
                "iadd bytes BufferError Existing exports of data: object cannot be re-sized",
                "imul one b'abcd'",
                "imul zero BufferError Existing exports of data: object cannot be re-sized",
                "imul two BufferError Existing exports of data: object cannot be re-sized",
                "pop BufferError Existing exports of data: object cannot be re-sized",
                "append BufferError Existing exports of data: object cannot be re-sized",
                "remove BufferError Existing exports of data: object cannot be re-sized",
                "clear BufferError Existing exports of data: object cannot be re-sized",
                "reverse b'dcba'"
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
