using System.Diagnostics.CodeAnalysis;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This internal exception is a VM control-flow carrier and is never exposed."
)]
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "This internal exception is translated before it crosses the managed runtime boundary."
)]
internal sealed class PythonRaisedException : Exception
{
    private readonly List<PythonTracebackFrame> _traceback = [];

    internal PythonRaisedException(
        PythonExceptionValue value,
        PythonRuntimeException? originatingFault = null
    )
        : base(value.ToDisplayString())
    {
        Value = value;
        OriginatingFault = originatingFault;
    }

    internal IReadOnlyList<PythonTracebackFrame> Traceback => _traceback;

    internal PythonExceptionValue Value { get; }

    internal PythonRuntimeException? OriginatingFault { get; }

    internal bool PreserveTracebackOnNextDispatch { get; set; }

    internal void AddTracebackFrame(string codeName, TextSpan span)
    {
        if (
            _traceback.Count != 0
            && _traceback[^1] is { CodeName: var previousName, Span: var previousSpan }
            && string.Equals(previousName, codeName, StringComparison.Ordinal)
            && previousSpan == span
        )
        {
            return;
        }

        _traceback.Add(new PythonTracebackFrame(codeName, span));
    }
}

internal readonly record struct PythonTracebackFrame(string CodeName, TextSpan Span);
