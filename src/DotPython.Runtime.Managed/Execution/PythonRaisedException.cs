using System.Diagnostics.CodeAnalysis;
using DotPython.Compiler.Bytecode;
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
        : base(value.UnicodeErrorState is null ? value.ToDisplayString() : value.Message)
    {
        Value = value;
        OriginatingFault = originatingFault;
        // `raise e` keeps the traceback the exception already carries and adds the
        // raising frames in front of it as it propagates.
        if (value.TracebackFrames is { Count: > 0 } existing)
        {
            _traceback.AddRange(existing);
        }
    }

    internal IReadOnlyList<PythonTracebackFrame> Traceback => _traceback;

    internal PythonExceptionValue Value { get; }

    internal PythonRuntimeException? OriginatingFault { get; }

    internal bool PreserveTracebackOnNextDispatch { get; set; }

    internal void AddTracebackFrame(
        string codeName,
        TextSpan span,
        object token,
        PythonCodeObject code,
        PythonDictionaryValue globals,
        PythonDictionaryValue locals
    )
    {
        if (
            _traceback.Count != 0
            && ReferenceEquals(_traceback[^1].Token, token)
            && _traceback[^1].Span == span
        )
        {
            return;
        }

        _traceback.Add(new PythonTracebackFrame(codeName, span, token, code, globals, locals));
        Value.TracebackFrames = _traceback;
        Value.TracebackOverride = null;
        Value.TracebackCache = null;
        Value.TracebackIdentity = new object();
    }
}

/// <summary>
/// One frame an exception passed through, captured when it unwound: the code it was
/// running, the instruction span it stood at, and a snapshot of the namespace CPython
/// would still show through the live frame.
/// </summary>
internal sealed class PythonTracebackFrame(
    string codeName,
    TextSpan span,
    object token,
    PythonCodeObject code,
    PythonDictionaryValue globals,
    PythonDictionaryValue locals
)
{
    internal string CodeName { get; } = codeName;

    internal TextSpan Span { get; } = span;

    internal object Token { get; } = token;

    internal PythonCodeObject Code { get; } = code;

    internal PythonDictionaryValue Globals { get; } = globals;

    internal PythonDictionaryValue Locals { get; } = locals;

    internal int Line =>
        PythonSourceLocations.TryLocate(Code, Span.Start, out _, out var line) ? line : 0;

    internal string FileName =>
        PythonSourceLocations.TryLocate(Code, Span.Start, out var fileName, out _)
            ? fileName
            : "<string>";
}
