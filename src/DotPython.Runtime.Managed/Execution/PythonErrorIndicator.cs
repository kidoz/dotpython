namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Holds the exception currently propagating through one managed VM invocation.
/// </summary>
/// <remarks>
/// This is distinct from the exception currently handled by an <c>except</c> block. Fetching the
/// raised exception clears the indicator, while setting one replaces any existing raised exception.
/// The owning VM confines access to its execution thread.
/// </remarks>
internal sealed class PythonErrorIndicator
{
    private PythonRaisedException? _raisedException;

    internal bool IsSet => _raisedException is not null;

    internal PythonExceptionValue? Occurred => _raisedException?.Value;

    internal void Clear() => _raisedException = null;

    internal PythonRaisedException? GetRaisedException()
    {
        var raisedException = _raisedException;
        _raisedException = null;
        return raisedException;
    }

    internal void SetRaisedException(PythonRaisedException raisedException)
    {
        ArgumentNullException.ThrowIfNull(raisedException);
        _raisedException = raisedException;
    }

    internal bool TrySetFromRuntimeFault(PythonRuntimeException fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        var exceptionTypeName =
            fault.PythonExceptionTypeName ?? GetPythonExceptionTypeName(fault.Code);
        if (exceptionTypeName is null)
        {
            return false;
        }

        SetRaisedException(
            new PythonRaisedException(
                new PythonExceptionValue(exceptionTypeName, fault.Message),
                fault
            )
        );
        return true;
    }

    private static string? GetPythonExceptionTypeName(string code) =>
        code switch
        {
            "DPY4002" => "NameError",
            "DPY4003" => "TypeError",
            "DPY4004" => "ZeroDivisionError",
            "DPY4005" => "TypeError",
            "DPY4006" => "OverflowError",
            "DPY4008" => "UnboundLocalError",
            "DPY4009" => "TypeError",
            "DPY4010" => "NameError",
            "DPY4011" => "TypeError",
            "DPY4012" => "IndexError",
            "DPY4013" => "KeyError",
            "DPY4014" => "TypeError",
            "DPY4015" => "TypeError",
            "DPY4016" => "RuntimeError",
            "DPY4020" => "ModuleNotFoundError",
            "DPY4021" => "SyntaxError",
            "DPY4022" => "AttributeError",
            "DPY4023" => "AttributeError",
            "DPY4024" => "RecursionError",
            "DPY4025" => "ImportError",
            "DPY4026" => "ImportError",
            "DPY4027" => "ImportError",
            _ => null,
        };
}
