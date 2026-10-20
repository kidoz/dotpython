using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class IoModuleExecutionTests
{
    /// <summary>
    /// `StringIO` and `BytesIO` over their in-memory buffers: construction and keyword
    /// forms, read/readline/readlines/iteration, write/writelines and their return
    /// counts, seek/tell/truncate (including the zero padding past the end), the close
    /// and context-manager rules, the newline modes, `UnsupportedOperation` and the
    /// argument wording.
    /// </summary>
    [Fact]
    public void TheInMemoryStreamsMatchPython()
    {
        var output = Run(
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

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "consts 0 1 2 131072",
                "type StringIO _io",
                "read a 1 bc 3 ",
                "readline abc  ",
                "iter ['abc']",
                "write 2 2 abcd 4",
                "seek-write 1 aXcd",
                "seek-end 10 1 'aXcd\\x00\\x00\\x00\\x00\\x00\\x00Z'",
                "neg-seek ValueError Negative seek position -1",
                "end 11",
                "trunc 3 aXc",
                "trunc-none ok 0",
                "closed ValueError I/O operation on closed file",
                "close-twice ok (None, None)",
                "fileno UnsupportedOperation fileno",
                "readable ok (True, True, True)",
                "ctx ok (['a\\n', 'b'], False)",
                "newline-bad ValueError illegal newline value: 'x'",
                "newline-int TypeError newline must be str or None, not int",
                "init-int TypeError initial_value must be str or None, not int",
                "write-int TypeError string argument expected, got 'int'",
                "readline-size ok ('a\\n', 'b', '')",
                "nl-default-read ['a\\r\\n', 'b\\rc', '', '']",
                "nl-empty-read ['a\\r\\n', 'b\\r', 'c', '']",
                "nl-empty-write 'a\\nb'",
                "nl-crlf 'a\\r\\nb'",
                "kw ok 'a'",
                "enter-closed ValueError I/O operation on closed file.",
                "getvalue-closed ValueError I/O operation on closed file",
                "read-none ok 'ab'",
                "read-int TypeError argument should be integer or None, not 'str'",
                "write-none TypeError string argument expected, got 'NoneType'",
                "seek-cur OSError Can't do nonzero cur-relative seeks",
                "seek-end-neg OSError Can't do nonzero cur-relative seeks",
                "seek-whence ValueError Invalid whence (9, should be 0, 1 or 2)",
                "trunc-big ok (2, 5, b'ab')",
                "b BytesIO _io b'a' b'abc'",
                "b-write 2 1 b'abc' 3",
                "b-seek-write b'aXc'",
                "b-pad b'aXc\\x00\\x00\\x00\\x00\\x00\\x00\\x00Z'",
                "b-init-str TypeError a bytes-like object is required, not 'str'",
                "b-write-str TypeError a bytes-like object is required, not 'str'",
                "b-write-mem ok 1",
                "b-init-mem b'ab'",
                "b-trunc ok (4, 2, b'ab')",
                "b-closed ValueError I/O operation on closed file.",
                "b-iter [b'a\\n', b'b']",
                "b-fileno UnsupportedOperation fileno",
                "b-seek-cur ok 1",
                "b-seek-end ok 2",
                "b-ctx ok (b'x', False)",
                "b-read-int TypeError argument should be integer or None, not 'str'",
                "b-seek-neg ValueError negative seek value -1",
                "b-kw ok b'a'",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The recorded gap: only the in-memory streams, the seek constants and
    /// `UnsupportedOperation` are published; the file-backed `io` classes and `io.open`
    /// are absent, and the stream constructors are builtin functions rather than classes.
    /// </summary>
    [Fact]
    public void TheRuntimePinsItsOwnIoGaps()
    {
        var output = Run(
            """"
            import io
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            print(sorted(n for n in dir(io) if not n.startswith("_")))
            print(type(io.StringIO).__name__, type(io.StringIO()).__name__, type(io.StringIO()).__module__)
            show("textwrapper", lambda: io.TextIOWrapper)
            show("open", lambda: io.open)
            show("fileio", lambda: io.FileIO)
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "['BytesIO', 'DEFAULT_BUFFER_SIZE', 'SEEK_CUR', 'SEEK_END', 'SEEK_SET', 'StringIO', 'UnsupportedOperation']",
                "builtin_function_or_method StringIO _io",
                "textwrapper AttributeError module 'io' has no attribute 'TextIOWrapper'",
                "open AttributeError module 'io' has no attribute 'open'",
                "fileio AttributeError module 'io' has no attribute 'FileIO'",
                ""
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
}
