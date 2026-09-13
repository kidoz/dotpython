using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal static class PythonBytesOperations
{
    internal static PythonValue GetItem(
        PythonByteSequenceValue bytes,
        PythonValue index,
        TextSpan span
    )
    {
        if (index is PythonSliceValue slice)
            return Slice(bytes, slice, span);

        BigInteger position;
        if (index is PythonWholeNumberValue whole)
            position = whole.Value;
        else if (index is PythonTruthValue truth)
            position = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(index, span, out position))
            throw ManagedObjectProtocols.Fault(
                "DPY4011",
                $"byte indices must be integers or slices, not {ManagedObjectProtocols.GetTypeName(index)}",
                span,
                "TypeError"
            );
        var minimum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MinValue) : int.MinValue;
        var maximum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MaxValue) : int.MaxValue;
        if (position < minimum || position > maximum)
            throw ManagedObjectProtocols.Fault(
                "DPY4012",
                $"cannot fit '{ManagedObjectProtocols.GetTypeName(index)}' into an index-sized integer",
                span,
                "IndexError"
            );
        if (position < 0)
            position += bytes.Value.Length;
        if (position < 0 || position >= bytes.Value.Length)
            throw ManagedObjectProtocols.Fault("DPY4012", "index out of range", span, "IndexError");
        return PythonWholeNumberValue.Create(bytes.Value[(int)position]);
    }

    private static PythonByteSequenceValue Slice(
        PythonByteSequenceValue bytes,
        PythonSliceValue slice,
        TextSpan span
    )
    {
        var (start, stop, step) = ManagedObjectProtocols.GetSliceIndices(
            slice,
            bytes.Value.Length,
            span
        );
        var length =
            step > 0
                ? start < stop
                    ? 1 + ((long)stop - start - 1) / step
                    : 0
                : start > stop
                    ? 1 + ((long)start - stop - 1) / -(long)step
                    : 0;
        if (length == 0)
            return PythonByteSequenceValue.Empty;
        if (start == 0 && step == 1 && length == bytes.Value.Length)
            return bytes;

        var result = new byte[(int)length];
        long cursor = start;
        for (var offset = 0; offset < result.Length; offset++, cursor += step)
        {
            if ((offset & 255) == 0)
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            result[offset] = bytes.Value[(int)cursor];
        }
        // Contiguous slices use the bytes constructor's singleton cache; stepped
        // slices allocate their storage first and retain their own identity.
        return step == 1
            ? PythonByteSequenceValue.Create(result)
            : new PythonByteSequenceValue(result);
    }

    internal static PythonByteSequenceValue Repeat(
        PythonByteSequenceValue bytes,
        BigInteger repetitions,
        TextSpan span
    )
    {
        if (bytes.Value.Length == 0 || repetitions == BigInteger.One)
            return bytes;
        if (repetitions <= 0)
            return new PythonByteSequenceValue([]);
        var maximum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MaxValue) : int.MaxValue;
        if (repetitions * bytes.Value.Length > maximum)
            throw ManagedObjectProtocols.Fault(
                "DPY4011",
                "repeated bytes are too long",
                span,
                "OverflowError"
            );
        var count = PythonSequenceRepetition.GetBoundedCount(
            bytes.Value.Length,
            repetitions,
            10_000_000,
            span
        );
        var result = new byte[bytes.Value.Length * count];
        for (var offset = 0; offset < result.Length; offset++)
        {
            if ((offset & 255) == 0)
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            result[offset] = bytes.Value[offset % bytes.Value.Length];
        }
        return new PythonByteSequenceValue(result);
    }
}
