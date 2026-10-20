using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `io` module's in-memory streams against CPython 3.14: `StringIO` and `BytesIO`
/// construction, reads, writes, seeking, truncation, iteration, close/context rules,
/// newline modes and the argument errors. The file-backed classes are the recorded gap
/// and are pinned in the paired execution test.
/// </summary>
public sealed class IoModuleCompatibilityTests
{
    [Fact]
    public Task TheInMemoryStreamsMatchCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            import io
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            print("consts", io.SEEK_SET, io.SEEK_CUR, io.SEEK_END, io.DEFAULT_BUFFER_SIZE)
            s = io.StringIO("abc")
            print("type", type(s).__name__, type(s).__module__)
            print("read", s.read(1), s.tell(), s.read(), s.tell(), s.read())
            s.seek(0); print("readline", s.readline(), s.readline(), s.readline())
            s.seek(0); print("iter", list(s))
            s = io.StringIO()
            print("write", s.write("ab"), s.write("cd"), s.getvalue(), s.tell())
            s.seek(1); print("seek-write", s.write("X"), s.getvalue())
            s.seek(10); print("seek-end", s.tell(), s.write("Z"), repr(s.getvalue()))
            show("neg-seek", lambda: s.seek(-1))
            s.seek(0, io.SEEK_END); print("end", s.tell())
            print("trunc", s.truncate(3), s.getvalue())
            show("trunc-none", lambda: io.StringIO("abcd").truncate())
            show("closed", lambda: (lambda t: (t.close(), t.closed, t.read(), t.write("x"), t.getvalue()))(io.StringIO("a")))
            show("close-twice", lambda: (lambda t: (t.close(), t.close()))(io.StringIO()))
            show("fileno", lambda: io.StringIO().fileno())
            show("readable", lambda: (io.StringIO().readable(), io.StringIO().writable(), io.StringIO().seekable()))
            show("ctx", lambda: (lambda t: (list(t), t.closed))(io.StringIO("a\nb")))
            show("newline-bad", lambda: io.StringIO("", newline="x"))
            show("newline-int", lambda: io.StringIO("", newline=5))
            show("init-int", lambda: io.StringIO(5))
            show("write-int", lambda: io.StringIO().write(5))
            show("readline-size", lambda: (lambda t: (t.readline(2), t.readline(2), t.readline()))(io.StringIO("a\nb")))
            s = io.StringIO("a\r\nb\rc")
            print("nl-default-read", [s.readline() for _ in range(4)])
            s = io.StringIO("a\r\nb\rc", newline="")
            print("nl-empty-read", [s.readline() for _ in range(4)])
            s = io.StringIO(newline="")
            s.write("a\nb"); print("nl-empty-write", repr(s.getvalue()))
            s = io.StringIO(newline="\r\n"); s.write("a\nb"); print("nl-crlf", repr(s.getvalue()))
            show("kw", lambda: io.StringIO(initial_value="a", newline="").getvalue())
            show("enter-closed", lambda: (lambda t: (t.close(), t.__enter__()))(io.StringIO()))
            show("getvalue-closed", lambda: (lambda t: (t.close(), t.getvalue()))(io.StringIO("a")))
            show("read-none", lambda: io.StringIO("ab").read(None))
            show("read-int", lambda: io.StringIO("ab").read("x"))
            show("write-none", lambda: io.StringIO().write(None))
            show("seek-cur", lambda: io.StringIO("abc").seek(1, io.SEEK_CUR))
            show("seek-end-neg", lambda: io.StringIO("abc").seek(-1, io.SEEK_END))
            show("seek-whence", lambda: io.StringIO("a").seek(0, 9))
            show("trunc-big", lambda: (lambda t: (t.write("ab"), t.truncate(5), t.getvalue().encode()))(io.StringIO()))
            b = io.BytesIO(b"abc")
            print("b", type(b).__name__, type(b).__module__, b.read(1), b.getvalue())
            b = io.BytesIO()
            print("b-write", b.write(b"ab"), b.write(bytearray(b"c")), b.getvalue(), b.tell())
            b.seek(1); b.write(b"X"); print("b-seek-write", b.getvalue())
            b.seek(10); b.write(b"Z"); print("b-pad", b.getvalue())
            show("b-init-str", lambda: io.BytesIO("x"))
            show("b-write-str", lambda: io.BytesIO().write("x"))
            show("b-write-mem", lambda: io.BytesIO().write(memoryview(b"x")))
            print("b-init-mem", io.BytesIO(memoryview(b"ab")).getvalue())
            show("b-trunc", lambda: (lambda t: (t.write(b"abcd"), t.truncate(2), t.getvalue()))(io.BytesIO()))
            show("b-closed", lambda: (lambda t: (t.close(), t.read()))(io.BytesIO(b"a")))
            print("b-iter", list(io.BytesIO(b"a\nb")))
            show("b-fileno", lambda: io.BytesIO().fileno())
            show("b-seek-cur", lambda: io.BytesIO(b"abc").seek(1, io.SEEK_CUR))
            show("b-seek-end", lambda: io.BytesIO(b"abc").seek(-1, io.SEEK_END))
            show("b-ctx", lambda: (lambda t: (t.read(), t.closed))(io.BytesIO(b"x")))
            show("b-read-int", lambda: io.BytesIO(b"ab").read("x"))
            show("b-seek-neg", lambda: io.BytesIO(b"a").seek(-1))
            show("b-kw", lambda: io.BytesIO(initial_bytes=b"a").getvalue())
            """"
        );
}
