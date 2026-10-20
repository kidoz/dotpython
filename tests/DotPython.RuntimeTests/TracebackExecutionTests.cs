using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

/// <summary>
/// The traceback objects an exception carries and the `traceback` module's formatting.
/// Frames are snapshots taken as the exception unwinds; a frame's `f_lineno` is where it
/// stood then, and a source line is shown only when the runtime has the source.
/// </summary>
public sealed class TracebackExecutionTests
{
    [Fact]
    public void TracebackFramesExposeTheUnwindPath()
    {
        var output = Run(
            """
            def inner():
                raise ValueError("boom")
            def outer():
                inner()
            try:
                outer()
            except ValueError as e:
                tb = e.__traceback__
                print(type(tb).__name__, type(tb).__module__)
                entries = []
                cur = tb
                while cur is not None:
                    f = cur.tb_frame
                    entries.append((type(f).__name__, f.f_code.co_name, f.f_code.co_filename == "<test>", cur.tb_lineno, type(cur.tb_next).__name__ if cur.tb_next else None))
                    cur = cur.tb_next
                print(entries)
                print(tb.tb_frame.f_back is None, tb.tb_next.tb_frame.f_back is tb.tb_frame, tb.tb_next.tb_next.tb_frame.f_back is tb.tb_next.tb_frame)
                print(tb.tb_frame.f_code.co_name, tb.tb_next.tb_frame.f_code.co_name, tb.tb_next.tb_next.tb_frame.f_code.co_name)
                print(tb.tb_frame.f_globals is globals(), isinstance(tb.tb_frame.f_locals, dict))
                print(isinstance(tb.tb_lasti, int), isinstance(tb.tb_lineno, int))
                print(e.with_traceback(tb) is e, e.__traceback__ is tb)
                for label, fn in [("with5", lambda: e.with_traceback(5)), ("with0", lambda: e.with_traceback()), ("with2", lambda: e.with_traceback(None, None)), ("set5", lambda: setattr(e, "__traceback__", 5)), ("del", lambda: delattr(e, "__traceback__"))]:
                    try: print(label, "ok", repr(fn()))
                    except Exception as x: print(label, type(x).__name__, x)
                e.__traceback__ = tb
                e2 = ValueError("other")
                print(e2.__traceback__)
                e2.__traceback__ = tb
                print(e2.__traceback__ is tb, e2.with_traceback(None) is e2, e2.__traceback__)
            print(ValueError("fresh").__traceback__)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "traceback builtins",
                "[('frame', '<module>', True, 6, 'traceback'), ('frame', 'outer', True, 4, 'traceback'), ('frame', 'inner', True, 2, None)]",
                "True True True",
                "<module> outer inner",
                "True True",
                "True True",
                "True True",
                "with5 TypeError __traceback__ must be a traceback or None",
                "with0 TypeError BaseException.with_traceback() takes exactly one argument (0 given)",
                "with2 TypeError BaseException.with_traceback() takes exactly one argument (2 given)",
                "set5 TypeError __traceback__ must be a traceback or None",
                "del TypeError __traceback__ may not be deleted",
                "None",
                "True True None",
                "None",
                ""
            ),
            output
        );
    }

    [Fact]
    public void TracebackModuleFormatsCapturedFrames()
    {
        var output = Run(
            """
            import traceback
            try:
                raise ValueError("v")
            except ValueError as e:
                print(repr(traceback.format_exc()))
                print(repr(traceback.format_exception(e)))
                print(repr(traceback.format_exception_only(e)))
                print(repr(traceback.format_tb(e.__traceback__)))
                print(repr(traceback.format_exception(e, limit=0)))
                print(repr(traceback.format_exception(e, e, None)))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "'Traceback (most recent call last):\\n  File \"<test>\", line 3, in <module>\\nValueError: v\\n'",
                "['Traceback (most recent call last):\\n', '  File \"<test>\", line 3, in <module>\\n', 'ValueError: v\\n']",
                "['ValueError: v\\n']",
                "['  File \"<test>\", line 3, in <module>\\n']",
                "['ValueError: v\\n']",
                "['ValueError: v\\n']",
                ""
            ),
            output
        );
    }

    [Fact]
    public void WithExitAndSysExceptionShareTheCapturedTraceback()
    {
        var output = Run(
            """
            import sys, traceback
            def helper(): return traceback.format_exc()
            def deeper(): return helper()
            try:
                raise ValueError("n")
            except ValueError as e:
                print(repr(helper()))
                print(repr(deeper()))
                print(sys.exception() is e, sys.exc_info()[1] is e, type(sys.exc_info()[2]).__name__)
            print(sys.exception(), sys.exc_info())
            class M:
                def __enter__(self): return self
                def __exit__(self, typ, value, tb):
                    print(typ.__name__, type(value).__name__, type(tb).__name__, tb.tb_frame.f_code.co_name, tb.tb_lineno)
                    return True
            try:
                with M():
                    raise TypeError("inside")
            except Exception as e:
                print("escaped", e)
            print(repr(traceback.format_exception_only(KeyError("k"))))
            print(repr(traceback.format_exception_only(Exception("a", "b"))))
            print(repr(traceback.format_exception_only(Exception())))
            print(repr(traceback.format_exception(None)))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "'Traceback (most recent call last):\\n  File \"<test>\", line 5, in <module>\\nValueError: n\\n'",
                "'Traceback (most recent call last):\\n  File \"<test>\", line 5, in <module>\\nValueError: n\\n'",
                "True True traceback",
                "None (None, None, None)",
                "TypeError TypeError traceback <module> 18",
                "[\"KeyError: 'k'\\n\"]",
                "[\"Exception: ('a', 'b')\\n\"]",
                "['Exception\\n']",
                "['NoneType: None\\n']",
                ""
            ),
            output
        );
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
