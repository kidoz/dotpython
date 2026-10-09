using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BufferProtocolCompatibilityTests
{
    [Fact]
    public Task ProtocolRoundTrip() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task ProtocolErrors() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task ExportersAndRelease() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task SizesAndViewDescriptors() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
