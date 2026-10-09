// The bytearray sequence operations follow CPython 3.14.7 Objects/bytearrayobject.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>The sequence operations `bytearray` shares with `bytes`, over mutable storage.</summary>
internal static class PythonByteArrayOperations
{
    /// <summary>
    /// `b[i]` yields an integer, while `b[a:b]` yields a <em>bytearray</em> — and always a fresh
    /// one, since the bytes slice can hand back an interned singleton that must not be aliased
    /// by mutable storage.
    /// </summary>
    internal static PythonValue GetItem(
        PythonByteArrayValue mutable,
        PythonValue index,
        TextSpan span
    )
    {
        var read = PythonBytesOperations.GetItem(
            new PythonByteSequenceValue(mutable.Value),
            index,
            span
        );
        return read is PythonByteSequenceValue sliced
            ? new PythonByteArrayValue([.. sliced.Value])
            : read;
    }
}

/// <summary>The item assignment and deletion a mutable sequence adds to the read surface.</summary>
internal static class PythonByteArrayMutation
{
    /// <summary>
    /// A bytearray with a memoryview open over it cannot change size, which is the guarantee
    /// the buffer protocol gives the view. An operation that keeps the length is still
    /// allowed: writing an element, reversing, or assigning a slice the same size.
    /// </summary>
    internal static void RequireResizable(
        PythonByteArrayValue mutable,
        int newLength,
        TextSpan span = default
    )
    {
        if (mutable.ExportCount > 0 && newLength != mutable.Value.Length)
            throw Fault("Existing exports of data: object cannot be re-sized", "BufferError", span);
    }

    /// <summary>`b[i] = x` and `b[a:b] = ...`, including the extended-slice length rule.</summary>
    internal static void SetItem(
        PythonByteArrayValue mutable,
        PythonValue index,
        PythonValue value,
        TextSpan span
    )
    {
        if (index is not PythonSliceValue slice)
        {
            // A single index takes one byte value; anything else is refused rather than
            // silently ignored.
            if (!TryGetByte(value, span, out var assigned))
            {
                throw Fault(
                    $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be "
                        + "interpreted as an integer",
                    "TypeError",
                    span
                );
            }
            mutable.Value[ResolveIndex(mutable, index, span)] = assigned;
            return;
        }

        var (start, stop, step) = ManagedObjectProtocols.GetSliceIndices(
            slice,
            mutable.Value.Length,
            span
        );
        if (step != 1)
        {
            var positions = ManagedObjectProtocols
                .EnumerateSliceIndices(slice, mutable.Value.Length, span)
                .ToArray();
            var replacement = AssignedBytes(value, span);
            if (replacement.Length != positions.Length)
            {
                throw Fault(
                    $"attempt to assign bytes of size {replacement.Length} to extended "
                        + $"slice of size {positions.Length}",
                    "ValueError",
                    span
                );
            }
            for (var position = 0; position < positions.Length; position++)
                mutable.Value[positions[position]] = replacement[position];
            return;
        }

        var bytes = AssignedBytes(value, span);
        RequireResizable(mutable, mutable.Value.Length - (stop - start) + bytes.Length, span);
        byte[] updated =
        [
            .. mutable.Value.AsSpan(0, start),
            .. bytes,
            .. mutable.Value.AsSpan(stop),
        ];
        mutable.Value = updated;
    }

    /// <summary>`del b[i]` and `del b[a:b]`, including a stepped selection.</summary>
    internal static void DeleteItem(PythonByteArrayValue mutable, PythonValue index, TextSpan span)
    {
        if (index is not PythonSliceValue slice)
        {
            var position = ResolveIndex(mutable, index, span);
            RequireResizable(mutable, mutable.Value.Length - 1, span);
            byte[] shrunk =
            [
                .. mutable.Value.AsSpan(0, position),
                .. mutable.Value.AsSpan(position + 1),
            ];
            mutable.Value = shrunk;
            return;
        }

        var removed = new HashSet<int>(
            ManagedObjectProtocols.EnumerateSliceIndices(slice, mutable.Value.Length, span)
        );
        if (removed.Count == 0)
            return;
        RequireResizable(mutable, mutable.Value.Length - removed.Count, span);
        var kept = new List<byte>(mutable.Value.Length - removed.Count);
        for (var position = 0; position < mutable.Value.Length; position++)
        {
            if (!removed.Contains(position))
                kept.Add(mutable.Value[position]);
        }
        mutable.Value = [.. kept];
    }

    /// <summary>
    /// The bytes an assignment supplies: a bytes-like object contributes its contents, and any
    /// other iterable contributes integers that must each be a byte value.
    /// </summary>
    private static byte[] AssignedBytes(PythonValue value, TextSpan span)
    {
        if (ManagedObjectProtocols.TryGetByteContent(value, out var content))
            return content;
        // A slice takes a bytes-like object or an iterable of byte values; a bare integer is
        // only meaningful for a single index, which the caller handles.
        if (value is PythonWholeNumberValue or PythonTruthValue)
        {
            throw Fault(
                "can assign only bytes, buffers, or iterables of ints in range(0, 256)",
                "TypeError",
                span
            );
        }
        // Anything else must be an iterable of byte values.
        return PythonBytesConstruction.ConstructNamed("bytearray", [value], [], [], span).Value;
    }

    private static bool TryGetByte(PythonValue value, TextSpan span, out byte assigned)
    {
        var number = value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => (BigInteger?)null,
        };
        if (number is null)
        {
            assigned = 0;
            return false;
        }
        if (number < 0 || number > 255)
            throw Fault("byte must be in range(0, 256)", "ValueError", span);
        assigned = (byte)number;
        return true;
    }

    private static int ResolveIndex(PythonByteArrayValue mutable, PythonValue index, TextSpan span)
    {
        BigInteger position;
        if (index is PythonWholeNumberValue whole)
            position = whole.Value;
        else if (index is PythonTruthValue truth)
            position = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(index, span, out position))
            throw Fault(
                $"bytearray indices must be integers or slices, not "
                    + $"{ManagedObjectProtocols.GetTypeName(index)}",
                "TypeError",
                span
            );
        if (position < 0)
            position += mutable.Value.Length;
        if (position < 0 || position >= mutable.Value.Length)
            throw Fault("bytearray index out of range", "IndexError", span);
        return (int)position;
    }

    private static PythonRuntimeException Fault(string message, string type, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
