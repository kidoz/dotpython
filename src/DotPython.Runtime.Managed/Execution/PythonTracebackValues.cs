using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The public traceback and frame objects CPython exposes through `__traceback__`.
/// A traceback captures the frames the exception unwound through; because this runtime
/// pops a frame as it unwinds, the frame is a snapshot taken at that moment rather than
/// the live frame CPython keeps.
/// </summary>
internal static class PythonTracebacks
{
    internal static PythonValue Get(PythonExceptionValue exception)
    {
        if (exception.TracebackOverride is { } assigned)
        {
            return assigned;
        }
        if (exception.TracebackFrames is not { Count: > 0 } frames)
        {
            return PythonNoneValue.Instance;
        }
        return exception.TracebackCache ??= CreateChain(frames);
    }

    /// <summary>`__traceback__ = value` and `with_traceback(value)`.</summary>
    internal static void Assign(PythonExceptionValue exception, PythonValue value, TextSpan span)
    {
        if (value is PythonNoneValue)
        {
            exception.TracebackOverride = PythonNoneValue.Instance;
            exception.TracebackFrames = null;
            exception.TracebackCache = null;
            return;
        }
        if (value is PythonExternalObjectValue { Protocol: PythonTracebackEntry entry })
        {
            exception.TracebackOverride = value;
            exception.TracebackFrames = FramesOf(entry);
            exception.TracebackCache = null;
            return;
        }
        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "__traceback__ must be a traceback or None",
            span,
            "TypeError"
        );
    }

    internal static PythonExternalObjectValue CreateChain(
        IReadOnlyList<PythonTracebackFrame> frames
    )
    {
        PythonValue? back = null;
        PythonTracebackEntry? head = null;
        PythonTracebackEntry? previous = null;
        // The list is innermost first; build outermost first so `tb_next` and `f_back`
        // point at the caller, as CPython's live frames do.
        for (var index = frames.Count - 1; index >= 0; index--)
        {
            var frame = new PythonExternalObjectValue(new PythonFrameEntry(frames[index], back));
            var entry = new PythonTracebackEntry(frames[index], frame);
            if (previous is null)
            {
                head = entry;
            }
            else
            {
                previous.Next = entry;
            }
            previous = entry;
            back = frame;
        }
        return new PythonExternalObjectValue(head!);
    }

    private static List<PythonTracebackFrame> FramesOf(PythonTracebackEntry head)
    {
        var frames = new List<PythonTracebackFrame>();
        for (var entry = head; entry is not null; entry = entry.Next)
        {
            frames.Add(entry.Data);
        }
        frames.Reverse();
        return frames;
    }
}

/// <summary>One `traceback` object: a snapshot frame, its line, and the caller's entry.</summary>
internal sealed class PythonTracebackEntry(PythonTracebackFrame data, PythonValue frame)
    : PythonExternalObjectProtocol,
        IPythonNamedExternalValue
{
    internal PythonTracebackFrame Data { get; } = data;

    internal PythonValue Frame { get; } = frame;

    internal PythonTracebackEntry? Next { get; set; }

    string IPythonNamedExternalValue.TypeName => "traceback";

    PythonValue PythonExternalObjectProtocol.Call(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => throw Error("'traceback' object is not callable", span);

    PythonValue PythonExternalObjectProtocol.CallWithKeywords(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) => throw Error("'traceback' object is not callable", span);

    PythonValue PythonExternalObjectProtocol.GetAttribute(string attribute, TextSpan span) =>
        attribute switch
        {
            "tb_frame" => Frame,
            "tb_lineno" => PythonWholeNumberValue.Create(Data.Line),
            "tb_lasti" => PythonWholeNumberValue.Create(Data.Span.Start),
            "tb_next" => Next is null
                ? PythonNoneValue.Instance
                : new PythonExternalObjectValue(Next),
            _ => throw Error($"'traceback' object has no attribute '{attribute}'", span),
        };

    PythonValue PythonExternalObjectProtocol.GetItem(PythonValue index, TextSpan span) =>
        throw Error("'traceback' object is not subscriptable", span);

    long PythonExternalObjectProtocol.GetHash(TextSpan span) => RuntimeHelpers.GetHashCode(this);

    int PythonExternalObjectProtocol.GetLength(TextSpan span) =>
        throw Error("object of type 'traceback' has no len()", span);

    PythonTruthValue PythonExternalObjectProtocol.RichCompare(
        PythonValue other,
        PythonRichComparison comparison,
        TextSpan span
    )
    {
        var equal =
            other is PythonExternalObjectValue { Protocol: PythonTracebackEntry entry }
            && ReferenceEquals(entry, this);
        return comparison switch
        {
            PythonRichComparison.Equal => PythonTruthValue.FromBoolean(equal),
            PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(!equal),
            _ => PythonTruthValue.False,
        };
    }

    bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) =>
        value is PythonExternalObjectValue { Protocol: PythonTracebackEntry };

    string PythonExternalObjectProtocol.ToDisplayString() => Representation();

    string PythonExternalObjectProtocol.ToRepresentationString() => Representation();

    private string Representation() =>
        $"<traceback object at 0x{RuntimeHelpers.GetHashCode(this):x}>";

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4039", message, span, "TypeError");
}

/// <summary>
/// A snapshot of one frame an exception unwound through. `f_lineno` is the line the
/// frame was executing then; the live line a caught exception's frame reports in CPython
/// belongs to frames this runtime no longer keeps.
/// </summary>
internal sealed class PythonFrameEntry(PythonTracebackFrame data, PythonValue? back)
    : PythonExternalObjectProtocol,
        IPythonNamedExternalValue
{
    internal PythonTracebackFrame Data { get; } = data;

    internal PythonValue? Back { get; } = back;

    string IPythonNamedExternalValue.TypeName => "frame";

    PythonValue PythonExternalObjectProtocol.Call(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => throw Error("'frame' object is not callable", span);

    PythonValue PythonExternalObjectProtocol.CallWithKeywords(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) => throw Error("'frame' object is not callable", span);

    PythonValue PythonExternalObjectProtocol.GetAttribute(string attribute, TextSpan span) =>
        attribute switch
        {
            "f_code" => new PythonCodeValue(Data.Code, Data.FileName)
            {
                NameOverride = Data.CodeName,
            },
            "f_lineno" => PythonWholeNumberValue.Create(Data.Line),
            "f_lasti" => PythonWholeNumberValue.Create(Data.Span.Start),
            "f_back" => Back ?? PythonNoneValue.Instance,
            "f_globals" => Data.Globals,
            "f_locals" => Data.Locals,
            "f_builtins" => Builtins(),
            "f_trace" => PythonNoneValue.Instance,
            "f_trace_lines" => PythonTruthValue.False,
            "f_trace_opcodes" => PythonTruthValue.False,
            _ => throw Error($"'frame' object has no attribute '{attribute}'", span),
        };

    /// <summary>The builtins a frame resolves against, as a snapshot of their names.</summary>
    private static PythonDictionaryValue Builtins()
    {
        var dictionary = ManagedObjectProtocols.CreateDictionary();
        if (UserObjectProtocols.Dispatcher is { } dispatcher)
        {
            foreach (var (name, value) in dispatcher.Builtins)
            {
                ManagedObjectProtocols.SetDictionaryItem(
                    dictionary,
                    new PythonTextValue(name),
                    value,
                    default
                );
            }
        }
        return dictionary;
    }

    PythonValue PythonExternalObjectProtocol.GetItem(PythonValue index, TextSpan span) =>
        throw Error("'frame' object is not subscriptable", span);

    long PythonExternalObjectProtocol.GetHash(TextSpan span) => RuntimeHelpers.GetHashCode(this);

    int PythonExternalObjectProtocol.GetLength(TextSpan span) =>
        throw Error("object of type 'frame' has no len()", span);

    PythonTruthValue PythonExternalObjectProtocol.RichCompare(
        PythonValue other,
        PythonRichComparison comparison,
        TextSpan span
    )
    {
        var equal =
            other is PythonExternalObjectValue { Protocol: PythonFrameEntry entry }
            && ReferenceEquals(entry, this);
        return comparison switch
        {
            PythonRichComparison.Equal => PythonTruthValue.FromBoolean(equal),
            PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(!equal),
            _ => PythonTruthValue.False,
        };
    }

    bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) =>
        value is PythonExternalObjectValue { Protocol: PythonFrameEntry };

    string PythonExternalObjectProtocol.ToDisplayString() => Representation();

    string PythonExternalObjectProtocol.ToRepresentationString() => Representation();

    private string Representation() =>
        $"<frame at 0x{RuntimeHelpers.GetHashCode(this):x}, file '{Data.FileName}', "
        + $"line {Data.Line}, code {Data.CodeName}>";

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4039", message, span, "TypeError");
}
