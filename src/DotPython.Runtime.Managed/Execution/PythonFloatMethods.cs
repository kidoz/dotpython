// The `float` method surface follows CPython 3.14.7 Objects/floatobject.c and the numbers
// exposed alongside it (`real`, `imag`, `conjugate`):
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The members of <c>float</c>: the exact-ratio and exact-hex views of a value, plus the
/// imaginary part a real number does not have.
/// </summary>
internal static class PythonFloatMethods
{
    internal static Dictionary<string, PythonProtocolFunctionValue> CreateTable() =>
        new(StringComparer.Ordinal)
        {
            ["is_integer"] = NoArguments(
                "is_integer",
                target =>
                    double.IsFinite(Number(target))
                    && Math.Truncate(Number(target)) == Number(target)
                        ? PythonTruthValue.True
                        : PythonTruthValue.False
            ),
            ["as_integer_ratio"] = NoArguments("as_integer_ratio", IntegerRatio),
            ["hex"] = NoArguments(
                "hex",
                target => new PythonTextValue(PythonHexFloat.Format(Number(target)))
            ),
            ["conjugate"] = NoArguments("conjugate", target => target),
            ["fromhex"] = FromHex(),
        };

    /// <summary>The members a value answers without a table: both parts of a real number.</summary>
    internal static bool TryGetMember(PythonValue target, string name, out PythonValue member)
    {
        if (target is not PythonFloatingPointValue)
        {
            member = null!;
            return false;
        }
        member = name switch
        {
            "real" => target,
            "imag" => new PythonFloatingPointValue(0.0),
            _ => null!,
        };
        return member is not null;
    }

    internal static bool IsReadOnlyMember(string name) => name is "real" or "imag";

    /// <summary>
    /// `float.fromhex`: an exact hexadecimal reading, so a text wider than a double rounds
    /// half to even rather than truncating, and one beyond it overflows.
    /// </summary>
    internal static PythonProtocolFunctionValue CreateFromHex() => FromHex();

    private static PythonProtocolFunctionValue FromHex() =>
        new(
            "fromhex",
            (_, arguments) =>
            {
                if (arguments.Count != 1)
                    throw Fault(
                        $"float.fromhex() takes exactly one argument ({arguments.Count} given)",
                        "TypeError"
                    );
                return Parse(arguments[0]);
            },
            (_, _, _, _) => throw Fault("float.fromhex() takes no keyword arguments", "TypeError")
        );

    private static PythonFloatingPointValue Parse(PythonValue value)
    {
        if (value is not PythonTextValue text)
            throw Fault("bad argument type for built-in operation", "TypeError");
        return new PythonFloatingPointValue(PythonHexFloat.Parse(text.Value, default));
    }

    /// <summary>
    /// The exact ratio the value represents: the significand over a power of two. An
    /// infinity has no ratio and a NaN is not a ratio at all, so each raises its own error.
    /// </summary>
    private static PythonValue IntegerRatio(PythonValue target)
    {
        var value = Number(target);
        if (double.IsNaN(value))
            throw Fault("cannot convert NaN to integer ratio", "ValueError");
        if (double.IsInfinity(value))
            throw Fault("cannot convert Infinity to integer ratio", "OverflowError");

        // A subnormal has no implicit leading one, and both forms are `significand * 2 **
        // shift` down to the same 2 ** -1074 the storage uses.
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xF_FFFF_FFFF_FFFF;
        var numerator =
            exponent == 0 ? new BigInteger(fraction) : new BigInteger(fraction | (1UL << 52));
        var shift = exponent == 0 ? -1074 : exponent - 1075;
        if ((bits >> 63) != 0)
            numerator = -numerator;
        if (numerator.IsZero)
            return new PythonTupleValue([
                PythonWholeNumberValue.Create(BigInteger.Zero),
                PythonWholeNumberValue.Create(BigInteger.One),
            ]);

        if (shift >= 0)
        {
            numerator <<= shift;
            shift = 0;
        }
        // A whole significand over the power of two it is scaled by; the factors of two the
        // numerator still carries belong to the denominator.
        var trailing = BigInteger.Min(-shift, TrailingZeroes(numerator));
        numerator >>= (int)trailing;
        var denominator = BigInteger.One << (int)-(shift + (long)trailing);
        return new PythonTupleValue([
            PythonWholeNumberValue.Create(numerator),
            PythonWholeNumberValue.Create(denominator),
        ]);
    }

    private static double Number(PythonValue target) => ((PythonFloatingPointValue)target).Value;

    /// <summary>How many times a nonzero magnitude divides by two.</summary>
    private static long TrailingZeroes(BigInteger value)
    {
        var magnitude = BigInteger.Abs(value);
        return magnitude.IsZero ? 0 : (magnitude & -magnitude).GetBitLength() - 1;
    }

    private static PythonProtocolFunctionValue NoArguments(
        string name,
        Func<PythonValue, PythonValue> implementation
    ) =>
        new(
            name,
            (target, arguments) =>
                arguments.Count == 0
                    ? implementation(target!)
                    : throw Fault(
                        $"float.{name}() takes no arguments ({arguments.Count} given)",
                        "TypeError"
                    ),
            (_, _, _, _) => throw Fault($"float.{name}() takes no keyword arguments", "TypeError")
        );

    private static PythonRuntimeException Fault(string message, string pythonType) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, pythonType);
}
