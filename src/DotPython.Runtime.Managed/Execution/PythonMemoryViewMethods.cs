// The memoryview type surface follows CPython 3.14.7 Objects/memoryobject.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `memoryview` type: what it can be built from, the members a view carries, and the
/// methods that read, convert and release it.
/// </summary>
internal static class PythonMemoryViewMethods
{
    /// <summary>The `memoryview` type object.</summary>
    internal static readonly PythonBuiltinTypeValue Type = new(
        "memoryview",
        Construct,
        ConstructWithKeywords
    );

    /// <summary>The names a view answers beyond the slots every value has.</summary>
    private static readonly string[] MemberNames =
    [
        "cast",
        "c_contiguous",
        "contiguous",
        "count",
        "f_contiguous",
        "format",
        "hex",
        "index",
        "itemsize",
        "nbytes",
        "ndim",
        "obj",
        "readonly",
        "release",
        "shape",
        "strides",
        "suboffsets",
        "tobytes",
        "tolist",
        "toreadonly",
    ];

    /// <summary>The methods a view publishes through its type object.</summary>
    private static readonly string[] MethodNames =
    [
        "cast",
        "count",
        "hex",
        "index",
        "release",
        "tobytes",
        "tolist",
        "toreadonly",
    ];

    private static readonly Dictionary<string, PythonMethodDescriptorValue> Descriptors = [];

    /// <summary>
    /// The descriptor `memoryview.cast` and its siblings are: the member a view answers,
    /// seen through the type and callable with the receiver as its first argument.
    /// </summary>
    internal static PythonMethodDescriptorValue? GetTypeDescriptor(string typeName, string name)
    {
        if (typeName != "memoryview" || Array.IndexOf(MethodNames, name) < 0)
            return null;
        lock (Descriptors)
        {
            if (!Descriptors.TryGetValue(name, out var descriptor))
            {
                descriptor = new PythonMethodDescriptorValue(
                    Type,
                    name,
                    new PythonProtocolFunctionValue(
                        name,
                        (receiver, arguments) =>
                        {
                            var view = (PythonMemoryViewValue)receiver!;
                            var bound = (PythonBoundMethodValue)GetAttribute(view, name, default)!;
                            return bound.Function.Invoke(view, arguments);
                        },
                        (_, _, _, _) =>
                            throw Fault(
                                $"memoryview.{name}() takes no keyword arguments",
                                "TypeError",
                                default
                            )
                    )
                );
                Descriptors[name] = descriptor;
            }
            return descriptor;
        }
    }

    /// <summary>The names a view adds to `dir()`.</summary>
    internal static void AddMemberNames(List<string> names)
    {
        foreach (var name in MemberNames)
        {
            if (!names.Contains(name))
                names.Add(name);
        }
    }

    /// <summary>
    /// A view over a bytes-like object. The view shares the storage, so a view over a
    /// bytearray is writable and holds an export on it until it is released.
    /// </summary>
    internal static PythonMemoryViewValue Create(PythonValue source, TextSpan span)
    {
        switch (source)
        {
            case PythonMemoryViewValue view:
                RequireLive(view);
                return CreateView(
                    view.Source,
                    view.Bytes,
                    view.Offset,
                    [.. view.Shape],
                    [.. view.Strides],
                    view.Format,
                    view.Writable
                );
            case PythonByteSequenceValue bytes:
                return CreateView(source, bytes.Value, 0, [bytes.Value.Length], [1], "B", false);
            case PythonByteArrayValue mutable:
                return CreateView(
                    mutable,
                    mutable.Value,
                    0,
                    [mutable.Value.Length],
                    [1],
                    "B",
                    true
                );
            default:
                // Anything else may hand a buffer out through `__buffer__`.
                if (
                    PythonBufferProtocol.TryCreateView(
                        source,
                        PythonBufferProtocol.FullReadOnly,
                        span,
                        out var exported
                    )
                )
                    return exported;
                throw Fault(
                    $"memoryview: a bytes-like object is required, not "
                        + $"'{ManagedObjectProtocols.GetTypeName(source)}'",
                    "TypeError",
                    span
                );
        }
    }

    /// <summary>
    /// A view that holds an export on its source and remembers where it starts, how it is
    /// shaped and how it is walked.
    /// </summary>
    internal static PythonMemoryViewValue CreateView(
        PythonValue source,
        byte[] bytes,
        int offset,
        int[] shape,
        int[] strides,
        string format,
        bool writable
    )
    {
        // Every view holds an export of its own, so a bytearray behind a view — or behind a
        // slice of one, or a view of one — refuses to resize until all of them are released.
        if (source is PythonByteArrayValue mutable)
            mutable.ExportCount++;
        return new PythonMemoryViewValue(source, bytes, offset, shape, strides, format, writable);
    }

    private static PythonMemoryViewValue Construct(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
            throw Fault(
                "memoryview() missing required argument 'object' (pos 1)",
                "TypeError",
                span
            );
        if (arguments.Count > 1)
            throw Fault(
                $"memoryview() takes at most 1 argument ({arguments.Count} given)",
                "TypeError",
                span
            );
        return Create(arguments[0], span);
    }

    /// <summary>
    /// `memoryview(object)`: the one argument is positional-or-keyword, and CPython reports
    /// the two bounds before it reports an argument it never got.
    /// </summary>
    private static PythonMemoryViewValue ConstructWithKeywords(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (keywordNames.Count > 1)
            throw Fault(
                $"memoryview() takes at most 1 keyword argument ({keywordNames.Count} given)",
                "TypeError",
                span
            );
        if (positional.Count + keywordNames.Count > 1)
            throw Fault(
                $"memoryview() takes at most 1 argument "
                    + $"({positional.Count + keywordNames.Count} given)",
                "TypeError",
                span
            );
        if (positional.Count == 1)
            return Create(positional[0], span);
        if (keywordNames.Count == 1 && keywordNames[0] == "object")
            return Create(keywordValues[0], span);
        throw Fault("memoryview() missing required argument 'object' (pos 1)", "TypeError", span);
    }

    /// <summary>Whether a value is a view, and one that has not been released.</summary>
    internal static bool IsLive(PythonValue value) =>
        value is PythonMemoryViewValue { Released: false };

    internal static void RequireLive(PythonValue value)
    {
        if (value is PythonMemoryViewValue { Released: true })
            throw Fault("operation forbidden on released memoryview object", "ValueError", default);
    }

    /// <summary>The members a view answers, or null when the name is not one of them.</summary>
    internal static PythonValue? GetAttribute(PythonValue target, string name, TextSpan span)
    {
        var view = (PythonMemoryViewValue)target;
        RequireLive(view);
        switch (name)
        {
            case "obj":
                return view.Source;
            case "format":
                return new PythonTextValue(view.Format);
            case "itemsize":
                return PythonWholeNumberValue.Create(view.ItemSize);
            case "ndim":
                return PythonWholeNumberValue.Create(view.Shape.Length);
            case "shape":
                return Tuple(view.Shape);
            case "strides":
                return Tuple(view.Strides);
            // A one-dimensional buffer has no indirect dimensions to report.
            case "suboffsets":
                return new PythonTupleValue([]);
            case "readonly":
                return view.Writable ? PythonTruthValue.False : PythonTruthValue.True;
            case "nbytes":
                return PythonWholeNumberValue.Create(view.ByteCount);
            case "contiguous":
                return Truth(view.IsContiguous);
            case "c_contiguous":
                return Truth(view.IsCContiguous);
            case "f_contiguous":
                return Truth(view.IsFContiguous);
            case "tobytes":
                return Method(
                    view,
                    "tobytes",
                    arguments => ToBytes(view, arguments, span),
                    ["order"],
                    [null]
                );
            case "hex":
                return HexMethod(view);
            case "tolist":
                return Method(
                    view,
                    "tolist",
                    arguments =>
                    {
                        RequireNoArguments("tolist", arguments);
                        return view.ToList();
                    }
                );
            case "release":
                return Method(
                    view,
                    "release",
                    arguments =>
                    {
                        RequireNoArguments("release", arguments);
                        Release(view);
                        return PythonNoneValue.Instance;
                    }
                );
            case "toreadonly":
                return Method(
                    view,
                    "toreadonly",
                    arguments =>
                    {
                        RequireNoArguments("toreadonly", arguments);
                        return ReadOnly(view);
                    }
                );
            case "cast":
                return Method(
                    view,
                    "cast",
                    arguments => Cast(view, arguments, span),
                    ["format", "shape"],
                    [null, null]
                );
            case "count":
                return Method(
                    view,
                    "count",
                    arguments =>
                    {
                        if (arguments.Count != 1)
                            throw Fault(
                                "memoryview.count() takes exactly one argument "
                                    + $"({arguments.Count} given)",
                                "TypeError",
                                span
                            );
                        return Count(view, arguments[0], span);
                    }
                );
            case "index":
                return Method(view, "index", arguments => Index(view, arguments, span));
            default:
                return null;
        }
    }

    /// <summary>
    /// Releases the view, dropping the export it holds so the object behind it may resize.
    /// Releasing twice is not an error.
    /// </summary>
    internal static void Release(PythonMemoryViewValue view)
    {
        if (view.Released)
            return;
        // The view counts as released before its owner hears about it, so a hook that
        // releases what it was handed does not start over.
        var owner = view.BufferOwner;
        view.Released = true;
        if (owner is not null)
            NotifyRelease(owner, view);
        // Every view holds exactly one export of its own on the object it reads, so
        // releasing a slice or a view of a view drops that view's export alone.
        if (view.Source is PythonByteArrayValue mutable)
            mutable.ExportCount--;
    }

    /// <summary>
    /// `__release_buffer__(view)`. CPython reports a failure here as unraisable and lets the
    /// release stand, so the hook's own errors never reach the caller.
    /// </summary>
    private static void NotifyRelease(PythonValue owner, PythonMemoryViewValue view)
    {
        if (
            !UserObjectProtocols.TryGetSpecialMethod(
                owner,
                "__release_buffer__",
                out var hook,
                out _
            )
        )
            return;
        try
        {
            UserObjectProtocols.Dispatcher!.Invoke(hook, [view], default);
        }
        catch (Exception error) when (error is PythonRaisedException or PythonRuntimeException)
        {
            // Ignored, as CPython's unraisable reporting does.
        }
    }

    /// <summary>`toreadonly()`: the same bytes through a view that refuses to write.</summary>
    private static PythonMemoryViewValue ReadOnly(PythonMemoryViewValue view) =>
        CreateView(
            view.Source,
            view.Bytes,
            view.Offset,
            [.. view.Shape],
            [.. view.Strides],
            view.Format,
            false
        );

    /// <summary>`tobytes(order='C')`: the bytes the view exposes, column-major for 'F'.</summary>
    private static PythonByteSequenceValue ToBytes(
        PythonMemoryViewValue view,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count > 1)
            throw Fault(
                $"tobytes() takes at most 1 argument ({arguments.Count} given)",
                "TypeError",
                span
            );
        var fortran = false;
        if (arguments.Count == 1 && arguments[0] is not PythonNoneValue)
        {
            if (arguments[0] is not PythonTextValue order)
                throw Fault(
                    "tobytes() argument 'order' must be str or None, "
                        + $"not {ManagedObjectProtocols.GetTypeName(arguments[0])}",
                    "TypeError",
                    span
                );
            fortran = order.Value switch
            {
                "C" or "A" => false,
                "F" => true,
                _ => throw Fault("order must be 'C', 'F' or 'A'", "ValueError", span),
            };
        }
        return PythonByteSequenceValue.Create(view.Materialize(fortran));
    }

    /// <summary>
    /// `cast(format)` and `cast(format, shape)`: the same bytes read as another format. The
    /// buffer itself must be C-contiguous, and the new shape has to cover it exactly.
    /// </summary>
    private static PythonMemoryViewValue Cast(
        PythonMemoryViewValue view,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
            throw Fault("cast() missing required argument 'format' (pos 1)", "TypeError", span);
        if (arguments.Count > 2)
            throw Fault(
                $"cast() takes at most 2 arguments ({arguments.Count} given)",
                "TypeError",
                span
            );
        if (arguments[0] is not PythonTextValue formatText)
            throw Fault(
                $"cast() argument 'format' must be str, not "
                    + $"{ManagedObjectProtocols.GetTypeName(arguments[0])}",
                "TypeError",
                span
            );
        if (!view.IsCContiguous)
            throw Fault(
                "memoryview: casts are restricted to C-contiguous views",
                "TypeError",
                span
            );
        if (!IsNativeFormat(formatText.Value))
            throw Fault(
                "memoryview: destination format must be a native single character format "
                    + "prefixed with an optional '@'",
                "ValueError",
                span
            );
        var format = formatText.Value;
        var itemSize = PythonMemoryViewValue.MemoryViewItemSize(format[^1]);
        int[] shape;
        if (arguments.Count == 2)
        {
            var elements = ShapeElements(arguments[1], span);
            var product = 1;
            foreach (var element in elements)
            {
                if (element <= 0)
                    throw Fault(
                        "memoryview.cast(): elements of shape must be integers > 0",
                        "ValueError",
                        span
                    );
                product *= element;
            }
            if (product * itemSize != view.ByteCount)
                throw Fault(
                    "memoryview: product(shape) * itemsize != buffer size",
                    "TypeError",
                    span
                );
            shape = elements;
        }
        else
        {
            if (view.ByteCount % itemSize != 0)
                throw Fault("memoryview: length is not a multiple of itemsize", "TypeError", span);
            shape = [view.ByteCount / itemSize];
        }
        return CreateView(
            view.Source,
            view.Bytes,
            view.Offset,
            shape,
            PythonMemoryViewValue.ContiguousStrides(shape, itemSize),
            format,
            view.Writable
        );
    }

    /// <summary>`count(value)`: how many elements equal the value.</summary>
    private static PythonWholeNumberValue Count(
        PythonMemoryViewValue view,
        PythonValue value,
        TextSpan span
    )
    {
        RequireOneDimensional(view, "multi-dimensional sub-views are not implemented", span);
        var count = 0;
        for (var index = 0; index < view.Length; index++)
        {
            if (ManagedObjectProtocols.AreEqual(view.Read(index), value))
                count++;
        }
        return PythonWholeNumberValue.Create(count);
    }

    /// <summary>
    /// `index(value, start, stop)`: where the value first appears, or the sentence CPython
    /// reports when it does not.
    /// </summary>
    private static PythonWholeNumberValue Index(
        PythonMemoryViewValue view,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
            throw Fault("index expected at least 1 argument, got 0", "TypeError", span);
        if (arguments.Count > 3)
            throw Fault(
                $"index expected at most 3 arguments, got {arguments.Count}",
                "TypeError",
                span
            );
        RequireOneDimensional(view, "multi-dimensional lookup is not implemented", span);
        var (start, stop) = Bounds(view.FirstDimension, arguments, span);
        for (var index = start; index < stop; index++)
        {
            if (ManagedObjectProtocols.AreEqual(view.Read(index), arguments[0]))
                return PythonWholeNumberValue.Create(index);
        }
        throw Fault("memoryview.index(x): x not found", "ValueError", span);
    }

    private static void RequireOneDimensional(
        PythonMemoryViewValue view,
        string message,
        TextSpan span
    )
    {
        if (view.Shape.Length != 1)
            throw Fault(message, "NotImplementedError", span);
    }

    /// <summary>The `start` and `stop` an `index` call searches between.</summary>
    private static (int Start, int Stop) Bounds(
        int length,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var start = arguments.Count > 1 ? Bound(arguments[1], span) : 0;
        var stop = arguments.Count > 2 ? Bound(arguments[2], span) : length;
        if (start < 0)
            start += length;
        if (stop < 0)
            stop += length;
        return ((int)BigInteger.Max(0, start), (int)BigInteger.Min(length, stop));
    }

    private static BigInteger Bound(PythonValue value, TextSpan span)
    {
        if (value is PythonWholeNumberValue whole)
            return whole.Value;
        if (value is PythonTruthValue truth)
            return truth.Value ? 1 : 0;
        throw Fault(
            "slice indices must be integers or have an __index__ method",
            "TypeError",
            span
        );
    }

    /// <summary>The elements of a `cast` shape, which is any sequence of positive integers.</summary>
    private static int[] ShapeElements(PythonValue value, TextSpan span)
    {
        IReadOnlyList<PythonValue> elements = value switch
        {
            PythonListValue list => list.Elements,
            PythonTupleValue tuple => tuple.Elements,
            _ => throw Fault("shape must be a list or a tuple", "TypeError", span),
        };
        var shape = new int[elements.Count];
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index] is not PythonWholeNumberValue number)
                throw Fault(
                    "memoryview.cast(): elements of shape must be integers",
                    "TypeError",
                    span
                );
            shape[index] = (int)number.Value;
        }
        return shape;
    }

    /// <summary>Whether a string is a native format character, optionally prefixed with `@`.</summary>
    private static bool IsNativeFormat(string format)
    {
        if (format.Length is < 1 or > 2)
            return false;
        if (format.Length == 2 && format[0] != '@')
            return false;
        foreach (var known in "bBc?hHiIlLqQnNefdP")
        {
            if (known == format[^1])
                return true;
        }
        return false;
    }

    private static PythonTupleValue Tuple(int[] values) =>
        new([.. values.Select(value => PythonWholeNumberValue.Create(value))]);

    private static PythonTruthValue Truth(bool value) =>
        value ? PythonTruthValue.True : PythonTruthValue.False;

    private static void RequireNoArguments(string name, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count != 0)
            throw Fault(
                $"memoryview.{name}() takes no arguments ({arguments.Count} given)",
                "TypeError",
                default
            );
    }

    /// <summary>
    /// A method on the view, bound to it. Most of the family refuses keywords, as the
    /// wrappers CPython generates for those methods do; the ones with named parameters —
    /// `tobytes`, `cast` and `hex` — bind them instead.
    /// </summary>
    private static PythonBoundMethodValue Method(
        PythonMemoryViewValue view,
        string name,
        Func<IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null
    )
    {
        var function = new PythonProtocolFunctionValue(name, (_, arguments) => body(arguments));
        function = parameters is null
            ? function with
            {
                InvokeWithKeywords = (_, _, _, _) =>
                    throw Fault(
                        $"memoryview.{name}() takes no keyword arguments",
                        "TypeError",
                        default
                    ),
            }
            : function.WithSignature(parameters, defaults!);
        return new PythonBoundMethodValue(name, view, function);
    }

    /// <summary>`hex(sep=…, bytes_per_sep=…)` is `bytes.hex` over the bytes the view exposes.</summary>
    private static PythonBoundMethodValue HexMethod(PythonMemoryViewValue view) =>
        new PythonBoundMethodValue(
            "hex",
            view,
            new PythonProtocolFunctionValue(
                "hex",
                (_, arguments) =>
                    PythonBytesMethods.HexOver(
                        PythonByteSequenceValue.Create(view.Materialize()),
                        arguments
                    ),
                (_, positional, names, values) =>
                    PythonBytesMethods.HexOverWithKeywords(
                        PythonByteSequenceValue.Create(view.Materialize()),
                        positional,
                        names,
                        values
                    )
            )
        );

    private static PythonRuntimeException Fault(string message, string type, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
