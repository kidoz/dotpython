using System.Numerics;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using DotPython.Runtime.Native;

namespace DotPython.Worker.Host;

internal sealed class QualifiedStableAbiObjectProtocol(
    WorkerSessionState session,
    StableAbiObject nativeObject
) : PythonExternalObjectProtocol
{
    private WorkerSessionState Session { get; } = session;

    private StableAbiObject NativeObject { get; } = nativeObject;

    public PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        Invoke(
            () =>
                Session.InvokeNative(() =>
                {
                    var temporary = new List<StableAbiObject>();
                    try
                    {
                        var nativeArguments = arguments
                            .Select(value => ToNative(value, temporary, span))
                            .ToArray();
                        return ToManaged(Session, NativeObject.Call(nativeArguments));
                    }
                    finally
                    {
                        for (var index = temporary.Count - 1; index >= 0; index--)
                        {
                            temporary[index].Dispose();
                        }
                    }
                }),
            span
        );

    public PythonValue CallWithKeywords(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) =>
        Invoke(
            () =>
                Session.InvokeNative(() =>
                {
                    var temporary = new List<StableAbiObject>();
                    try
                    {
                        var nativeArguments = arguments
                            .Select(value => ToNative(value, temporary, span))
                            .ToArray();
                        var nativeKeywordValues = keywordValues
                            .Select(value => ToNative(value, temporary, span))
                            .ToArray();
                        return ToManaged(
                            Session,
                            NativeObject.CallWithKeywords(
                                nativeArguments,
                                keywordNames,
                                nativeKeywordValues
                            )
                        );
                    }
                    finally
                    {
                        for (var index = temporary.Count - 1; index >= 0; index--)
                        {
                            temporary[index].Dispose();
                        }
                    }
                }),
            span
        );

    public PythonValue GetAttribute(string name, TextSpan span) =>
        Invoke(
            () => Session.InvokeNative(() => ToManaged(Session, NativeObject.GetAttribute(name))),
            span
        );

    public PythonValue GetItem(PythonValue index, TextSpan span)
    {
        return Invoke(
            () =>
                Session.InvokeNative(() =>
                {
                    var temporary = new List<StableAbiObject>();
                    try
                    {
                        var key = ToNative(index, temporary, span);
                        return ToManaged(Session, NativeObject.GetItem(key));
                    }
                    finally
                    {
                        for (var item = temporary.Count - 1; item >= 0; item--)
                        {
                            temporary[item].Dispose();
                        }
                    }
                }),
            span
        );
    }

    public int GetLength(TextSpan span) =>
        Invoke(
            () =>
                Session.InvokeNative(() =>
                {
                    var length = NativeObject.GetSize();
                    return length <= int.MaxValue
                        ? (int)length
                        : throw Fault(
                            "Native object length exceeds the managed runtime limit.",
                            span,
                            "OverflowError"
                        );
                }),
            span
        );

    public PythonTruthValue RichCompare(
        PythonValue other,
        PythonRichComparison comparison,
        TextSpan span
    ) =>
        Invoke(
            () =>
                Session.InvokeNative(() =>
                {
                    var temporary = new List<StableAbiObject>();
                    try
                    {
                        var right = ToNative(other, temporary, span);
                        using var result = NativeObject.RichCompare(
                            right,
                            comparison switch
                            {
                                PythonRichComparison.LessThan => StableAbiRichComparison.LessThan,
                                PythonRichComparison.LessThanOrEqual =>
                                    StableAbiRichComparison.LessThanOrEqual,
                                PythonRichComparison.Equal => StableAbiRichComparison.Equal,
                                PythonRichComparison.NotEqual => StableAbiRichComparison.NotEqual,
                                PythonRichComparison.GreaterThan =>
                                    StableAbiRichComparison.GreaterThan,
                                PythonRichComparison.GreaterThanOrEqual =>
                                    StableAbiRichComparison.GreaterThanOrEqual,
                                _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
                            }
                        );
                        return PythonTruthValue.FromBoolean(result.AsBoolean());
                    }
                    finally
                    {
                        for (var index = temporary.Count - 1; index >= 0; index--)
                        {
                            temporary[index].Dispose();
                        }
                    }
                }),
            span
        );

    public string ToDisplayString() =>
        Invoke(() => Session.InvokeNative(NativeObject.ToDisplayString), default);

    public string ToRepresentationString() =>
        Invoke(() => Session.InvokeNative(NativeObject.ToRepresentationString), default);

    internal static PythonValue ToManaged(WorkerSessionState session, StableAbiObject value)
    {
        switch (value.Kind)
        {
            case StableAbiObjectKind.None:
                value.Dispose();
                return PythonNoneValue.Instance;
            case StableAbiObjectKind.Boolean:
            {
                var result = PythonTruthValue.FromBoolean(value.AsBoolean());
                value.Dispose();
                return result;
            }
            case StableAbiObjectKind.Integer:
            {
                var result = PythonWholeNumberValue.Create(value.AsInt64());
                value.Dispose();
                return result;
            }
            case StableAbiObjectKind.Text:
            {
                var result = new PythonTextValue(value.AsText());
                value.Dispose();
                return result;
            }
            case StableAbiObjectKind.List:
            case StableAbiObjectKind.Tuple:
                return ToManagedSequence(session, value);
            case StableAbiObjectKind.Bytes:
            case StableAbiObjectKind.Dictionary:
            case StableAbiObjectKind.Module:
            case StableAbiObjectKind.Callable:
            case StableAbiObjectKind.Type:
            case StableAbiObjectKind.Instance:
                return new PythonExternalObjectValue(
                    new QualifiedStableAbiObjectProtocol(session, value)
                );
            default:
                value.Dispose();
                throw Fault("The native bridge returned an unsupported object kind.", default);
        }
    }

    private static PythonValue ToManagedSequence(
        WorkerSessionState session,
        StableAbiObject sequence
    )
    {
        var kind = sequence.Kind;
        var size = sequence.GetSize();
        if (size > 4096)
        {
            sequence.Dispose();
            throw Fault("A native result sequence exceeds the 4096 item limit.", default);
        }

        var values = new PythonValue[checked((int)size)];
        for (var index = 0; index < values.Length; index++)
        {
            using var key = sequence.Owner.CreateInt64(index);
            values[index] = ToManaged(session, sequence.GetItem(key));
        }

        sequence.Dispose();
        return kind == StableAbiObjectKind.List
            ? new PythonListValue(values.ToList())
            : new PythonTupleValue(values);
    }

    private StableAbiObject ToNative(
        PythonValue value,
        List<StableAbiObject> temporary,
        TextSpan span
    )
    {
        if (
            value
                is PythonExternalObjectValue { Protocol: QualifiedStableAbiObjectProtocol external }
            && ReferenceEquals(Session, external.Session)
        )
        {
            return external.NativeObject;
        }

        StableAbiObject created = value switch
        {
            PythonNoneValue => GetModule().CreateNone(),
            PythonTruthValue truth => GetModule().CreateBoolean(truth.Value),
            PythonWholeNumberValue whole
                when whole.Value >= long.MinValue && whole.Value <= long.MaxValue => GetModule()
                .CreateInt64((long)whole.Value),
            PythonTextValue text => GetModule().CreateText(text.Value),
            PythonListValue list => CreateSequence(
                StableAbiObjectKind.List,
                list.Elements,
                temporary,
                span
            ),
            PythonTupleValue tuple => CreateSequence(
                StableAbiObjectKind.Tuple,
                tuple.Elements,
                temporary,
                span
            ),
            _ => throw Fault(
                "This managed value cannot cross the qualified Stable-ABI boundary.",
                span,
                "TypeError"
            ),
        };
        temporary.Add(created);
        return created;
    }

    private StableAbiObject CreateSequence(
        StableAbiObjectKind kind,
        IReadOnlyList<PythonValue> values,
        List<StableAbiObject> temporary,
        TextSpan span
    )
    {
        if (values.Count > 4096)
        {
            throw Fault("A managed argument sequence exceeds the 4096 item limit.", span);
        }

        var items = values.Select(value => ToNative(value, temporary, span)).ToArray();
        return GetModule().CreateSequence(kind, items);
    }

    private StableAbiModule GetModule()
    {
        // Every object operation validates that the object belongs to this module; this lightweight
        // factory access is obtained by creating values through the object's owning module methods.
        return NativeObject.Owner;
    }

    private static T Invoke<T>(Func<T> operation, TextSpan span)
    {
        try
        {
            return operation();
        }
        catch (StableAbiLoadException exception)
        {
            throw new PythonRuntimeException(
                exception.Code,
                exception.Message,
                span,
                "RuntimeError"
            );
        }
    }

    private static PythonRuntimeException Fault(
        string message,
        TextSpan span,
        string exceptionType = "RuntimeError"
    ) => new("DPY4029", message, span, exceptionType);
}
