using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>CPython's 64-bit numeric hash contract, independent of CLR numeric hashes.</summary>
internal static class PythonNumericHash
{
    private const ulong Modulus = (1UL << 61) - 1;

    internal static BigInteger Integer(BigInteger value)
    {
        var hash = value.Sign < 0 ? -((-value) % Modulus) : value % Modulus;
        return hash == -1 ? -2 : hash;
    }

    internal static BigInteger UserResult(BigInteger value)
    {
        // slot_tp_hash accepts a signed Py_hash_t unchanged and only applies
        // integer hashing if conversion overflows. -1 is reserved for errors.
        if (value < long.MinValue || value > long.MaxValue)
            return Integer(value);
        return value == -1 ? -2 : value;
    }

    internal static long Float(PythonValue owner, double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        var significand = bits & ((1UL << 52) - 1);
        var negative = (bits >> 63) != 0;
        if (exponentBits == 0x7ff)
        {
            if (significand != 0)
                return RuntimeHelpers.GetHashCode(owner);
            return negative ? -314159 : 314159;
        }

        // Every binary64 value is significand * 2**exponent, including subnormals.
        // Modulo 2**61-1, multiplying by a power of two rotates 61 bits.
        var exponent = -1074;
        if (exponentBits != 0)
        {
            significand |= 1UL << 52;
            exponent = exponentBits - 1023 - 52;
        }
        var rotation = ((exponent % 61) + 61) % 61;
        var reduced = ((significand << rotation) & Modulus) | (significand >> (61 - rotation));
        var hash = negative ? -(long)reduced : (long)reduced;
        return hash == -1 ? -2 : hash;
    }

    internal static long Complex(PythonComplexValue value)
    {
        var real = Float(value, value.Value.Real);
        var imaginary = Float(value, value.Value.Imaginary);
        var hash = unchecked(real + 1000003L * imaginary);
        return hash == -1 ? -2 : hash;
    }
}
