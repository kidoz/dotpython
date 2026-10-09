// The PEP 688 buffer protocol follows CPython 3.14.7 Objects/memoryobject.c (the
// `_buffer_wrapper` an exported buffer is read through) and the flags PyObject_GetBuffer
// passes down to `__buffer__`:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The buffer an object hands out through `__buffer__`, and the hook that releases it: the
/// Python-level half of the protocol `memoryview`, `bytes`, `bytearray` and the bytes methods
/// read bytes-like arguments through.
/// </summary>
/// <remarks>
/// A consumer asks for a buffer with a set of flags, and the exporter answers with a
/// `memoryview` this runtime then reads. The export is released through
/// `__release_buffer__` when the view is released, so a class that lends its bytes out can
/// see them come back.
/// </remarks>
internal static class PythonBufferProtocol
{
    /// <summary>PyBUF_SIMPLE: plain contiguous bytes, which the bytes methods ask for.</summary>
    internal const int Simple = 0;

    /// <summary>
    /// PyBUF_FULL_RO: shape, strides and format as well, which construction, formatting and
    /// slice assignment ask for.
    /// </summary>
    internal const int FullReadOnly = 284;

    /// <summary>PyBUF_WRITABLE: the creator will write through the buffer it asked for.</summary>
    private const int Writable = 1;

    /// <summary>The `__buffer__` an object implements, or null when it exports nothing.</summary>
    private static PythonValue? Hook(PythonValue value) =>
        value is PythonManagedObjectValue
        && UserObjectProtocols.TryGetSpecialMethod(value, "__buffer__", out var hook, out _)
            ? hook
            : null;

    /// <summary>
    /// `memoryview(obj)` and the other creators: the view the object's `__buffer__` hands
    /// back, which is the export that must later be released.
    /// </summary>
    internal static bool TryCreateView(
        PythonValue value,
        int flags,
        TextSpan span,
        out PythonMemoryViewValue view
    )
    {
        view = null!;
        if (Hook(value) is not { } hook)
            return false;
        var produced = UserObjectProtocols.Dispatcher!.Invoke(
            hook,
            [PythonWholeNumberValue.Create(flags)],
            span
        );
        if (produced is not PythonMemoryViewValue exported)
            throw Fault("__buffer__ returned non-memoryview object", "TypeError", span);
        RequireWritable(exported, flags, span);
        exported.BufferOwner = value;
        view = exported;
        return true;
    }

    /// <summary>
    /// The bytes a consumer that takes a buffer reads. The bytes-like values come first; an
    /// object that implements the protocol is asked for its buffer, which is released again
    /// as soon as the consumer has what it asked for.
    /// </summary>
    internal static bool TryGetContent(
        PythonValue value,
        int flags,
        TextSpan span,
        out byte[] content
    )
    {
        if (ManagedObjectProtocols.TryGetByteContent(value, out content))
            return true;
        if (!TryCreateView(value, flags, span, out var view))
            return false;
        content = view.Materialize();
        PythonMemoryViewMethods.Release(view);
        return true;
    }

    /// <summary>
    /// `bytes.__buffer__(flags)`, `bytearray.__buffer__(flags)` and
    /// `memoryview.__buffer__(flags)`: the builtin exporters answer with a view of
    /// themselves, refusing one a caller asked to write through when they cannot be written.
    /// </summary>
    internal static PythonMemoryViewValue Export(
        PythonValue receiver,
        PythonValue flags,
        TextSpan span
    )
    {
        var requested = Flags(flags, span);
        var writable = receiver switch
        {
            PythonByteArrayValue => true,
            PythonMemoryViewValue sourceView => sourceView.Writable,
            _ => false,
        };
        if ((requested & Writable) != 0 && !writable)
            // Each exporter refuses a writable request in its own words.
            throw Fault(
                receiver is PythonMemoryViewValue
                    ? "memoryview: underlying buffer is not writable"
                    : "Object is not writable.",
                "BufferError",
                span
            );
        var view = PythonMemoryViewMethods.Create(receiver, span);
        // A view the exporter handed out is released through it again, which is what
        // `memoryview.__release_buffer__` checks for.
        if (receiver is PythonMemoryViewValue source)
            view.BufferOwner = source;
        return view;
    }

    /// <summary>
    /// `bytearray.__release_buffer__(view)` and `memoryview.__release_buffer__(view)`: the
    /// export comes back only to the object it came from.
    /// </summary>
    internal static PythonValue ReleaseExport(
        PythonValue receiver,
        PythonValue export,
        TextSpan span
    )
    {
        var ownerIsReceiver =
            export is PythonMemoryViewValue exported
            && (
                receiver is PythonByteArrayValue mutable
                    && ReferenceEquals(exported.Source, mutable)
                || receiver is PythonMemoryViewValue source
                    && ReferenceEquals(exported.BufferOwner, source)
            );
        if (!ownerIsReceiver)
            throw Fault("memoryview's buffer is not this object", "ValueError", span);
        var released = (PythonMemoryViewValue)export;
        // The hook itself must not run: the caller is releasing on its own behalf.
        released.BufferOwner = null;
        PythonMemoryViewMethods.Release(released);
        return PythonNoneValue.Instance;
    }

    private static int Flags(PythonValue value, TextSpan span)
    {
        BigInteger flags;
        if (value is PythonWholeNumberValue whole)
            flags = whole.Value;
        else if (value is PythonTruthValue truth)
            flags = truth.Value ? BigInteger.One : BigInteger.Zero;
        else
            throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted "
                    + "as an integer",
                "TypeError",
                span
            );
        return (int)BigInteger.Clamp(flags, int.MinValue, int.MaxValue);
    }

    private static void RequireWritable(PythonMemoryViewValue view, int flags, TextSpan span)
    {
        if ((flags & Writable) != 0 && !view.Writable)
            throw Fault("Object is not writable.", "BufferError", span);
    }

    private static PythonRuntimeException Fault(string message, string type, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
