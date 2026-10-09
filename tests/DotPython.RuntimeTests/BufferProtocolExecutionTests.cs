using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BufferProtocolExecutionTests
{
    [Fact]
    public void ProtocolRoundTrip()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            class Buffer:
                def __init__(self, data=b'abc'):
                    self.data = bytearray(data)
                    self.log = []
                def __buffer__(self, flags):
                    self.log.append(('buffer', flags))
                    self.handed = memoryview(self.data)
                    return self.handed
                def __release_buffer__(self, view):
                    self.log.append(('release', view is self.handed))
            b = Buffer()
            view = memoryview(b)
            print('view', view.tobytes(), view.readonly, view.format, view.itemsize, view.nbytes)
            print('held', b.log)
            view.release()
            print('released', b.log)
            probe(lambda: view.tobytes())
            c = Buffer(b'xy')
            print('bytes', bytes(c), c.log)
            d = Buffer(b'xy')
            print('bytearray', bytearray(d), d.log)
            e = Buffer(b'xy')
            print('percent', b'<%b>' % e, e.log)
            f = Buffer(b'xy')
            print('from bytes', int.from_bytes(f, 'big'), f.log)
            g = Buffer(b'xy')
            print('concat', b'pre' + g, g.log)
            h = Buffer(b'xy')
            print('starts', b'xyz'.startswith(h), h.log)
            i = Buffer(b'xy')
            print('find', b'axy'.find(i), i.log)
            j = Buffer(b'xy')
            print('join', b'-'.join([j, b'z']), j.log)
            k = Buffer(b'61 62')
            print('fromhex', bytes.fromhex(k), k.log)
            m = Buffer(b'xy')
            print('extend', (lambda x: (x.extend(m), bytes(x))[1])(bytearray(b'z')), m.log)
            n = Buffer(b'xy')
            print('assign', (lambda v: (v.__setitem__(slice(0, 2), n), bytes(v))[1])(memoryview(bytearray(b'zz'))), n.log)
            o = Buffer(b'xy')
            import codecs
            print('decode', codecs.decode(o), o.log)
            """
        );

        Assert.Equal(
            Lines(
                "view b'abc' False B 1 3",
                "held [('buffer', 284)]",
                "released [('buffer', 284), ('release', True)]",
                "ValueError operation forbidden on released memoryview object",
                "bytes b'xy' [('buffer', 284), ('release', True)]",
                "bytearray bytearray(b'xy') [('buffer', 284), ('release', True)]",
                "percent b'<xy>' [('buffer', 284), ('release', True)]",
                "from bytes 30841 [('buffer', 284), ('release', True)]",
                "concat b'prexy' [('buffer', 0), ('release', True)]",
                "starts True [('buffer', 0), ('release', True)]",
                "find 1 [('buffer', 0), ('release', True)]",
                "join b'xy-z' [('buffer', 0), ('release', True)]",
                "fromhex b'ab' [('buffer', 0), ('release', True)]",
                "extend b'zxy' [('buffer', 0), ('release', True)]",
                "assign b'xy' [('buffer', 284), ('release', True)]",
                "decode xy [('buffer', 0), ('release', True)]"
            ),
            output
        );
    }

    [Fact]
    public void ProtocolErrors()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            class NoRelease:
                def __buffer__(self, flags):
                    return memoryview(bytearray(b'z'))
            probe(lambda: memoryview(NoRelease()).tobytes())
            class BadReturn:
                def __buffer__(self, flags):
                    return b'nope'
            probe(lambda: memoryview(BadReturn()))
            probe(lambda: bytes(BadReturn()))
            probe(lambda: bytearray(BadReturn()))
            class NoneReturn:
                def __buffer__(self, flags):
                    return None
            probe(lambda: memoryview(NoneReturn()))
            class Raiser:
                def __buffer__(self, flags):
                    raise ValueError('boom')
            probe(lambda: memoryview(Raiser()))
            probe(lambda: bytes(Raiser()))
            class NotCallable:
                __buffer__ = 5
            probe(lambda: memoryview(NotCallable()))
            class Both:
                def __buffer__(self, flags):
                    return memoryview(bytearray(b'buf'))
                def __bytes__(self):
                    return b'byt'
            probe(lambda: bytes(Both()))
            probe(lambda: bytearray(Both()))
            probe(lambda: b'%b' % Both())
            probe(lambda: b'%b' % 3)
            class OnlyBytes:
                def __bytes__(self):
                    return b'x'
            probe(lambda: bytes(OnlyBytes()))
            probe(lambda: bytearray(OnlyBytes()))
            class Plain:
                pass
            probe(lambda: memoryview(Plain()))
            probe(lambda: bytes(Plain()))
            probe(lambda: b'x' + Plain())
            probe(lambda: Plain() + b'x')
            probe(lambda: b'x' == Plain())
            probe(lambda: b'x' < Plain())
            """
        );

        Assert.Equal(
            Lines(
                "b'z'",
                "TypeError __buffer__ returned non-memoryview object",
                "TypeError __buffer__ returned non-memoryview object",
                "TypeError __buffer__ returned non-memoryview object",
                "TypeError __buffer__ returned non-memoryview object",
                "ValueError boom",
                "ValueError boom",
                "TypeError 'int' object is not callable",
                "b'byt'",
                "bytearray(b'buf')",
                "b'byt'",
                "TypeError %b requires a bytes-like object, or an object that implements __bytes__, not 'int'",
                "b'x'",
                "TypeError cannot convert 'OnlyBytes' object to bytearray",
                "TypeError memoryview: a bytes-like object is required, not 'Plain'",
                "TypeError cannot convert 'Plain' object to bytes",
                "TypeError can't concat Plain to bytes",
                "TypeError unsupported operand type(s) for +: 'Plain' and 'bytes'",
                "False",
                "TypeError '<' not supported between instances of 'bytes' and 'Plain'"
            ),
            output
        );
    }

    [Fact]
    public void ExportersAndRelease()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print('bytes', b'ab'.__buffer__(284).tobytes(), b'ab'.__buffer__(284).readonly, b'ab'.__buffer__(284).format)
            print('bytearray', bytearray(b'ab').__buffer__(284).readonly, bytearray(b'ab').__buffer__(1).readonly)
            print('memoryview', memoryview(b'ab').__buffer__(284).tobytes(), memoryview(bytearray(b'ab')).__buffer__(1).readonly)
            probe(lambda: b'ab'.__buffer__(1))
            probe(lambda: memoryview(b'ab').__buffer__(1))
            probe(lambda: memoryview(b'ab').__buffer__())
            probe(lambda: b'ab'.__buffer__(284, 1))
            probe(lambda: bytes.__release_buffer__(memoryview(b'ab')))
            def own_release():
                target = bytearray(b'ab')
                view = memoryview(target)
                return target.__release_buffer__(view)
            print('own release', own_release())
            def other_release():
                first = bytearray(b'ab')
                second = bytearray(b'ab')
                return first.__release_buffer__(memoryview(second))
            probe(other_release)
            def release_after():
                target = bytearray(b'ab')
                view = target.__buffer__(284)
                target.__release_buffer__(view)
                try:
                    target.append(1)
                    return 'resized'
                except BufferError:
                    return 'BufferError'
            print('released export', release_after())
            def export_blocks():
                target = bytearray(b'ab')
                view = target.__buffer__(284)
                try:
                    target.append(1)
                    return 'resized'
                except BufferError:
                    return 'BufferError'
            print('held export', export_blocks())
            def view_release():
                source = memoryview(b'ab')
                inner = source.__buffer__(284)
                return source.__release_buffer__(inner)
            probe(view_release)
            def view_release_other():
                source = memoryview(b'ab')
                return source.__release_buffer__(memoryview(b'ab'))
            probe(view_release_other)
            """
        );

        Assert.Equal(
            Lines(
                "bytes b'ab' True B",
                "bytearray False False",
                "memoryview b'ab' False",
                "BufferError Object is not writable.",
                "BufferError memoryview: underlying buffer is not writable",
                "TypeError __buffer__ expected 1 argument, got 0",
                "TypeError __buffer__ expected 1 argument, got 2",
                "AttributeError type object 'bytes' has no attribute '__release_buffer__'",
                "own release None",
                "ValueError memoryview's buffer is not this object",
                "released export resized",
                "held export BufferError",
                "None",
                "ValueError memoryview's buffer is not this object"
            ),
            output
        );
    }

    [Fact]
    public void SizesAndViewDescriptors()
    {
        var output = Run(
            """
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print((1, 2).__sizeof__(), ().__sizeof__(), ((1,) * 20).__sizeof__())
            print(b'abc'.__sizeof__(), b''.__sizeof__(), (b'x' * 100).__sizeof__())
            print((...).__sizeof__(), range(3).__sizeof__(), slice(1, 2).__sizeof__(), slice(None, 5, 2).__sizeof__())
            print(1 .__sizeof__(), 1.5.__sizeof__(), True.__sizeof__(), None.__sizeof__())
            print(repr(tuple.__sizeof__), repr(bytes.__sizeof__), repr(range.__sizeof__))
            print(repr(memoryview.cast), repr(memoryview.tobytes), repr(memoryview.obj), repr(memoryview.shape))
            print(memoryview.cast(memoryview(b'abcd'), 'I').format)
            print(memoryview.tobytes(memoryview(b'ab')), memoryview.count(memoryview(b'ab'), 97), memoryview.index(memoryview(b'abc'), 98))
            print(memoryview.shape.__get__(memoryview(b'abc')), memoryview.readonly.__get__(memoryview(b'a')), memoryview.nbytes.__get__(memoryview(b'ab')))
            print(memoryview.__len__(memoryview(b'abc')), memoryview.__getitem__(memoryview(b'abc'), 0))
            probe(lambda: memoryview.cast(b'abcd', 'B'))
            probe(lambda: memoryview.cast())
            probe(lambda: memoryview.cast(memoryview(b'abcd'), 'B', (4,), 1))
            probe(lambda: memoryview.obj(memoryview(b'ab')))
            probe(lambda: memoryview.tobytes(memoryview(b'ab'), 'C', 1))
            probe(lambda: bytes.__sizeof__(b'ab', 1))
            probe(lambda: (...).__sizeof__(1))
            print('cast' in dir(memoryview(b'a')), 'obj' in dir(memoryview(b'a')))
            """
        );

        Assert.Equal(
            Lines(
                "64 48 208",
                "52 49 149",
                "32 64 56 56",
                "44 40 44 32",
                "<method '__sizeof__' of 'object' objects> <method '__sizeof__' of 'object' objects> <method '__sizeof__' of 'object' objects>",
                "<method 'cast' of 'memoryview' objects> <method 'tobytes' of 'memoryview' objects> <attribute 'obj' of 'memoryview' objects> <attribute 'shape' of 'memoryview' objects>",
                "I",
                "b'ab' 1 1",
                "(3,) True 2",
                "3 97",
                "TypeError descriptor 'cast' for 'memoryview' objects doesn't apply to a 'bytes' object",
                "TypeError unbound method memoryview.cast() needs an argument",
                "TypeError cast() takes at most 2 arguments (3 given)",
                "TypeError 'getset_descriptor' object is not callable",
                "TypeError tobytes() takes at most 1 argument (2 given)",
                "TypeError object.__sizeof__() takes no arguments (1 given)",
                "TypeError object.__sizeof__() takes no arguments (1 given)",
                "True True"
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
