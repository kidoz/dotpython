using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class OSErrorExecutionTests
{
    [Fact]
    public void OSErrorFollowsCpythonErrnoSemantics()
    {
        var output = Run(
            """
            import builtins
            names = (
                "BlockingIOError", "BrokenPipeError", "ChildProcessError", "ConnectionAbortedError",
                "ConnectionError", "ConnectionRefusedError", "ConnectionResetError", "EnvironmentError",
                "FloatingPointError", "IndentationError", "InterruptedError", "IOError", "MemoryError",
                "NotADirectoryError", "ProcessLookupError", "PythonFinalizationError", "ReferenceError",
                "SystemError", "TabError", "TimeoutError",
            )
            for name in names:
                type_ = getattr(builtins, name)
                print("class", name, type_.__name__, type_.__module__,
                      [base.__name__ for base in type_.__bases__])
            print("aliases", builtins.IOError is builtins.OSError,
                  builtins.EnvironmentError is builtins.OSError, builtins.EnvironmentError.__name__)
            print("mro", [type_.__name__ for type_ in builtins.ConnectionRefusedError.__mro__])
            print("subclass", issubclass(builtins.TabError, builtins.IndentationError),
                  issubclass(builtins.IndentationError, builtins.SyntaxError),
                  issubclass(builtins.TimeoutError, builtins.OSError),
                  issubclass(builtins.PythonFinalizationError, builtins.RuntimeError),
                  issubclass(builtins.FloatingPointError, builtins.ArithmeticError),
                  issubclass(builtins.MemoryError, builtins.Exception))
            for arguments in ((), (2,), (2, "m"), ("x", "y"), (2, "m", "/x"), (2, "m", "/x", "/y"),
                              (2, "m", "/x", "/y", "/z"), (1, 2, 3, 4, 5, 6), (2, None), (True, "m")):
                error = builtins.OSError(*arguments)
                print("ctor", arguments, type(error).__name__, repr(error.args), repr(error.errno),
                      repr(error.strerror), repr(error.filename), repr(error.filename2),
                      repr(str(error)), repr(error))
            for code in (1, 2, 3, 4, 10, 13, 17, 20, 21, 32, 35, 36, 37, 53, 54, 60, 61, 9, 999, -1,
                         True, 2.0, 2 ** 70):
                print("errno", code, type(builtins.OSError(code, "m")).__name__)
            error = builtins.OSError(2, "m")
            print("slots", hasattr(error, "errno"), hasattr(builtins.OSError, "errno"),
                  builtins.OSError.errno.__qualname__, builtins.OSError.filename2.__doc__)
            error.errno = 5
            print("write", repr(error.errno), repr(str(error)))
            error.strerror = None
            print("none", repr(str(error)))
            error.filename = "/f"
            print("file", repr(str(error)))
            del error.errno
            print("delete", repr(error.errno), repr(str(error)))
            class Sub(builtins.OSError):
                def __init__(self, *arguments):
                    super().__init__(*arguments)
                    self.extra = 1
            sub = Sub(3, "msg", "/f")
            print("subclass-instance", type(sub).__name__, repr(sub.errno), repr(sub.strerror),
                  repr(sub.filename), repr(str(sub)), repr(sub.args), sub.extra)
            print("subclass-filter", type(builtins.ChildProcessError(2, "m")).__name__,
                  type(builtins.FileNotFoundError(13, "m")).__name__)
            try:
                builtins.ValueError(1).errno
            except AttributeError as attributeError:
                print("no-slots", attributeError)
            """
        );
        Assert.Equal(
            Lines(
                "class BlockingIOError BlockingIOError builtins ['OSError']",
                "class BrokenPipeError BrokenPipeError builtins ['ConnectionError']",
                "class ChildProcessError ChildProcessError builtins ['OSError']",
                "class ConnectionAbortedError ConnectionAbortedError builtins ['ConnectionError']",
                "class ConnectionError ConnectionError builtins ['OSError']",
                "class ConnectionRefusedError ConnectionRefusedError builtins ['ConnectionError']",
                "class ConnectionResetError ConnectionResetError builtins ['ConnectionError']",
                "class EnvironmentError OSError builtins ['Exception']",
                "class FloatingPointError FloatingPointError builtins ['ArithmeticError']",
                "class IndentationError IndentationError builtins ['SyntaxError']",
                "class InterruptedError InterruptedError builtins ['OSError']",
                "class IOError OSError builtins ['Exception']",
                "class MemoryError MemoryError builtins ['Exception']",
                "class NotADirectoryError NotADirectoryError builtins ['OSError']",
                "class ProcessLookupError ProcessLookupError builtins ['OSError']",
                "class PythonFinalizationError PythonFinalizationError builtins ['RuntimeError']",
                "class ReferenceError ReferenceError builtins ['Exception']",
                "class SystemError SystemError builtins ['Exception']",
                "class TabError TabError builtins ['IndentationError']",
                "class TimeoutError TimeoutError builtins ['OSError']",
                "aliases True True OSError",
                "mro ['ConnectionRefusedError', 'ConnectionError', 'OSError', 'Exception', 'BaseException', 'object']",
                "subclass True True True True True True",
                "ctor () OSError () None None None None '' OSError()",
                "ctor (2,) OSError (2,) None None None None '2' OSError(2)",
                "ctor (2, 'm') FileNotFoundError (2, 'm') 2 'm' None None '[Errno 2] m' FileNotFoundError(2, 'm')",
                "ctor ('x', 'y') OSError ('x', 'y') 'x' 'y' None None '[Errno x] y' OSError('x', 'y')",
                "ctor (2, 'm', '/x') FileNotFoundError (2, 'm') 2 'm' '/x' None \"[Errno 2] m: '/x'\" FileNotFoundError(2, 'm')",
                "ctor (2, 'm', '/x', '/y') FileNotFoundError (2, 'm') 2 'm' '/x' None \"[Errno 2] m: '/x'\" FileNotFoundError(2, 'm')",
                "ctor (2, 'm', '/x', '/y', '/z') FileNotFoundError (2, 'm') 2 'm' '/x' '/z' \"[Errno 2] m: '/x' -> '/z'\" FileNotFoundError(2, 'm')",
                "ctor (1, 2, 3, 4, 5, 6) OSError (1, 2, 3, 4, 5, 6) None None None None '(1, 2, 3, 4, 5, 6)' OSError(1, 2, 3, 4, 5, 6)",
                "ctor (2, None) FileNotFoundError (2, None) 2 None None None '[Errno 2] None' FileNotFoundError(2, None)",
                "ctor (True, 'm') PermissionError (True, 'm') True 'm' None None '[Errno True] m' PermissionError(True, 'm')",
                "errno 1 PermissionError",
                "errno 2 FileNotFoundError",
                "errno 3 ProcessLookupError",
                "errno 4 InterruptedError",
                "errno 10 ChildProcessError",
                "errno 13 PermissionError",
                "errno 17 FileExistsError",
                "errno 20 NotADirectoryError",
                "errno 21 IsADirectoryError",
                "errno 32 BrokenPipeError",
                "errno 35 BlockingIOError",
                "errno 36 BlockingIOError",
                "errno 37 BlockingIOError",
                "errno 53 ConnectionAbortedError",
                "errno 54 ConnectionResetError",
                "errno 60 TimeoutError",
                "errno 61 ConnectionRefusedError",
                "errno 9 OSError",
                "errno 999 OSError",
                "errno -1 OSError",
                "errno True PermissionError",
                "errno 2.0 OSError",
                "errno 1180591620717411303424 OSError",
                "slots True True OSError.errno second exception filename",
                "write 5 '[Errno 5] m'",
                "none '[Errno 5] None'",
                "file \"[Errno 5] None: '/f'\"",
                "delete None \"[Errno None] None: '/f'\"",
                "subclass-instance Sub 3 'msg' '/f' \"[Errno 3] msg: '/f'\" (3, 'msg') 1",
                "subclass-filter ChildProcessError FileNotFoundError",
                "no-slots 'ValueError' object has no attribute 'errno'"
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
