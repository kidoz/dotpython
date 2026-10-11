using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    /// <summary>
    /// The errno codes a plain `OSError` retypes itself to. CPython keys its table by
    /// the names `errno.h` gives the codes, so the numbers are the host's.
    /// </summary>
    private static readonly Dictionary<int, string> ErrnoSubclasses = CreateErrnoSubclasses();

    private static Dictionary<int, string> CreateErrnoSubclasses()
    {
        static int Host(int macos, int windows, int linux) =>
            OperatingSystem.IsMacOS() ? macos
            : OperatingSystem.IsWindows() ? windows
            : linux;
        return new Dictionary<int, string>
        {
            [1] = "PermissionError", // EPERM
            [2] = "FileNotFoundError", // ENOENT
            [3] = "ProcessLookupError", // ESRCH
            [4] = "InterruptedError", // EINTR
            [10] = "ChildProcessError", // ECHILD
            [13] = "PermissionError", // EACCES
            [17] = "FileExistsError", // EEXIST
            [20] = "NotADirectoryError", // ENOTDIR
            [21] = "IsADirectoryError", // EISDIR
            [32] = "BrokenPipeError", // EPIPE
            [Host(35, 11, 11)] = "BlockingIOError", // EAGAIN
            [Host(35, 140, 11)] = "BlockingIOError", // EWOULDBLOCK
            [Host(36, 112, 115)] = "BlockingIOError", // EINPROGRESS
            [Host(37, 103, 114)] = "BlockingIOError", // EALREADY
            [Host(53, 106, 103)] = "ConnectionAbortedError", // ECONNABORTED
            [Host(54, 108, 104)] = "ConnectionResetError", // ECONNRESET
            [Host(60, 138, 110)] = "TimeoutError", // ETIMEDOUT
            [Host(61, 107, 111)] = "ConnectionRefusedError", // ECONNREFUSED
        };
    }

    /// <summary>
    /// The class a plain `OSError` takes from its errno: only the type itself is
    /// retyped, only in the two-to-five-argument shape, and only for an integer the
    /// table knows.
    /// </summary>
    private static string? MappedOSErrorName(PythonValue[] arguments) =>
        arguments.Length is >= 2 and <= 5
        && ErrnoValue(arguments[0]) is { } errno
        && ErrnoSubclasses.TryGetValue(errno, out var subclass)
            ? subclass
            : null;

    private static int? ErrnoValue(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole
                when whole.Value >= int.MinValue && whole.Value <= int.MaxValue => (int)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => null,
        };

    /// <summary>`OSError`'s errno retypes the value; slots and arguments carry over.</summary>
    private static PythonExceptionValue RetypeException(
        PythonExceptionValue exception,
        string typeName
    )
    {
        var retyped = new PythonExceptionValue(typeName, exception.Message)
        {
            Arguments = exception.Arguments,
            OSErrorSlots = exception.OSErrorSlots,
        };
        retyped.InitializeSpecializedArguments(exception.ConstructorArguments);
        return retyped;
    }

    PythonExceptionValue IUserObjectDispatcher.AllocateException(
        string allocator,
        PythonValue type,
        PythonValue[] arguments,
        TextSpan span
    ) => AllocateException(allocator, type, arguments, span);

    private PythonExceptionValue AllocateException(
        string allocator,
        PythonValue type,
        PythonValue[] arguments,
        TextSpan span
    )
    {
        var layout = PythonTypeLayout.GetSolidBase(type);
        if (
            layout is not PythonExceptionTypeValue
            || layout is PythonExceptionTypeValue { Name: "BaseExceptionGroup" }
                && allocator != "BaseExceptionGroup"
            || layout is PythonExceptionTypeValue { Name: "OSError" } && allocator != "OSError"
        )
        {
            var name = PythonTypeLayout.Name(type);
            throw Fault(
                "DPY4003",
                $"{allocator}.__new__({name}) is not safe, use {name}.__new__()",
                span,
                "TypeError"
            );
        }
        if (allocator == "BaseExceptionGroup")
            return CreateExceptionGroupValue(arguments, type as PythonManagedTypeValue);

        var typeName = PythonTypeLayout.Name(type);
        var unicodeEncode = IsSubclassOf(
            type,
            PythonBuiltinTypes.GetExceptionType("UnicodeEncodeError"),
            span
        );
        var unicodeTranslate = IsSubclassOf(
            type,
            PythonBuiltinTypes.GetExceptionType("UnicodeTranslateError"),
            span
        );
        var unicodeError =
            unicodeTranslate
            || unicodeEncode
            || IsSubclassOf(type, PythonBuiltinTypes.GetExceptionType("UnicodeDecodeError"), span);
        var exception = new PythonExceptionValue(
            typeName,
            unicodeError ? string.Empty
                : arguments.Length == 1
                && IsSubclassOf(type, PythonBuiltinTypes.GetExceptionType("KeyError"), span)
                    ? arguments[0].ToRepresentationString()
                : ComposeExceptionMessage(arguments)
        )
        {
            Arguments = [.. arguments],
            ManagedType = type as PythonManagedTypeValue,
            UnicodeErrorState = unicodeError
                ? new()
                {
                    ErrorKind =
                        unicodeTranslate ? PythonUnicodeErrors.Kind.Translate
                        : unicodeEncode ? PythonUnicodeErrors.Kind.Encode
                        : PythonUnicodeErrors.Kind.Decode,
                }
                : null,
        };
        // BaseException allocation initializes only args. The additional builtin
        // value/code fields are populated later by their own initializer slots.
        exception.InitializeSpecializedArguments([]);
        if (IsSubclassOf(type, PythonBuiltinTypes.GetExceptionType("SyntaxError"), span))
        {
            ManagedObjectProtocols.ApplySyntaxErrorShape(exception, arguments, span);
            return exception;
        }
        if (
            !unicodeError
            && IsSubclassOf(type, PythonBuiltinTypes.GetExceptionType("OSError"), span)
        )
        {
            ManagedObjectProtocols.ApplyOSErrorShape(exception, arguments);
            if (
                type is PythonExceptionTypeValue { Name: "OSError" }
                && MappedOSErrorName(arguments) is { } mapped
            )
            {
                return RetypeException(exception, mapped);
            }
        }
        return exception;
    }
}
