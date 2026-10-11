using Xunit;

namespace DotPython.DifferentialTests;

public sealed class OSErrorCompatibilityTests
{
    [Fact]
    public Task OSErrorFollowsCpythonErrnoSemantics() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
