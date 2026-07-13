using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

public sealed class PythonRuntimeException : Exception
{
    public PythonRuntimeException()
        : this("A managed Python runtime error occurred.") { }

    public PythonRuntimeException(string message)
        : base(message)
    {
        Code = "DPY4000";
    }

    public PythonRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "DPY4000";
    }

    internal PythonRuntimeException(string code, string message, TextSpan span)
        : base(message)
    {
        Code = code;
        Span = span;
    }

    public string Code { get; }

    public TextSpan Span { get; }
}
