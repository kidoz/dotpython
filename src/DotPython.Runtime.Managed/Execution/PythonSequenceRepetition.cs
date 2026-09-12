using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal static class PythonSequenceRepetition
{
    internal static BigInteger GetCount(PythonValue value, TextSpan span)
    {
        BigInteger count;
        if (value is PythonWholeNumberValue whole)
            count = whole.Value;
        else if (value is PythonTruthValue truth)
            count = truth.Value ? BigInteger.One : BigInteger.Zero;
        else if (!UserObjectProtocols.TryConvertToIndex(value, span, out count))
            throw ManagedObjectProtocols.Fault(
                "DPY4011",
                $"can't multiply sequence by non-int of type '{ManagedObjectProtocols.GetTypeName(value)}'",
                span,
                "TypeError"
            );

        var minimum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MinValue) : int.MinValue;
        var maximum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MaxValue) : int.MaxValue;
        if (count < minimum || count > maximum)
            throw ManagedObjectProtocols.Fault(
                "DPY4011",
                $"cannot fit '{ManagedObjectProtocols.GetTypeName(value)}' into an index-sized integer",
                span,
                "OverflowError"
            );
        return count;
    }

    internal static int GetBoundedCount(
        int length,
        BigInteger count,
        int maximumSize,
        TextSpan span
    )
    {
        if (length == 0 || count <= 0)
            return 0;
        if (count * length > maximumSize)
            throw ManagedObjectProtocols.Fault(
                "DPY4011",
                "The repeated sequence exceeds the supported size.",
                span,
                "OverflowError"
            );
        return (int)count;
    }
}
