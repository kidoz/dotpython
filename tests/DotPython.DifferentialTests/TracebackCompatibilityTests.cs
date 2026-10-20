using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// Traceback objects and the `traceback` module against CPython 3.14. The scripts use
/// module-level raise statements, whose tracebacks carry no column-anchor lines: this
/// runtime's instruction spans do not record CPython's per-range positions.
/// </summary>
public sealed class TracebackCompatibilityTests
{
    [Fact]
    public Task TracebackFramesDescribeTheUnwindPath() =>
        CompatibilityOracle.AssertMatchesAsync(
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
                    entries.append((type(f).__name__, f.f_code.co_name, f.f_code.co_filename == "<string>", cur.tb_lineno, type(cur.tb_next).__name__ if cur.tb_next else None))
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

    [Fact]
    public Task TracebackModuleFormatsCapturedChains() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import io
            import traceback

            print(repr(traceback.format_exc()))
            try:
                raise ValueError("v")
            except ValueError as e:
                print(repr(traceback.format_exc()))
                print(repr(traceback.format_exception(e)))
                print(repr(traceback.format_tb(e.__traceback__)))
                print(repr(traceback.format_exception_only(e)))
                print(repr(traceback.format_exception(e, limit=0)))
                print(repr(traceback.format_exception(e, limit=-1)))
                print(repr(traceback.format_exception(e, e, None)))
                buf = io.StringIO()
                print(repr(traceback.print_exc(file=buf)))
                print(repr(buf.getvalue()))
            try:
                raise ValueError("a")
            except ValueError as first:
                try:
                    raise TypeError("b")
                except TypeError as second:
                    print(repr(traceback.format_exc()))
                    print(repr(traceback.format_exception(second, chain=False)))
            """
        );

    [Fact]
    public Task TracebackLimitsAndExplicitTracebacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import traceback
            try:
                raise ValueError("v")
            except ValueError as e:
                for limit in (0, 1, -1):
                    print(limit, repr(traceback.format_exception(e, limit=limit)))
                print(repr(traceback.format_tb(e.__traceback__, limit=0)))
                print(repr(traceback.format_exception(e, e, None)))
            """
        );

    [Fact]
    public Task HelpersAndContextManagersSeeTheHandledException() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task ReraisingAndRecursionKeepEveryFrame() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def reraise_bare():
                try:
                    raise ValueError("x")
                except ValueError:
                    raise
            def reraise_exc():
                try:
                    raise ZeroDivisionError("y")
                except ZeroDivisionError as e:
                    raise e
            def rec(n):
                if n:
                    rec(n - 1)
                else:
                    raise TypeError("deep")
            def chain(e):
                out = []
                tb = e.__traceback__
                while tb is not None:
                    out.append((tb.tb_frame.f_code.co_name, tb.tb_lineno))
                    tb = tb.tb_next
                return out
            for fn in (reraise_bare, reraise_exc):
                try:
                    fn()
                except Exception as e:
                    print(fn.__name__, chain(e))
            try:
                rec(3)
            except Exception as e:
                print("rec", chain(e))
            """
        );
}
