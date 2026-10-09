// The hex float surface follows CPython 3.14.7 Objects/floatobject.c (`float_hex` and
// `float_fromhex`) and the IEEE 754 binary64 layout:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Globalization;
using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The exact hexadecimal form of a <c>double</c>, as <c>float.hex()</c> writes it and
/// <c>float.fromhex()</c> reads it back.
/// </summary>
/// <remarks>
/// <c>hex()</c> prints the IEEE 754 fields directly: thirteen fraction nibbles cover the
/// fifty-two significand bits exactly, so the text is full precision with no trimming —
/// only the two zeros are written short. <c>fromhex()</c> converts with round-half-to-even
/// over the whole input, so the text it accepts is wider than a double can hold and the
/// rounding must be decided from every digit.
/// </remarks>
internal static class PythonHexFloat
{
    /// <summary>
    /// The significand digits kept for rounding. Fifty-three bits are needed plus a sticky
    /// region; twenty-five nibbles is a hundred bits, so every rounding decision below the
    /// kept bits only has to know whether anything was dropped at all.
    /// </summary>
    private const int KeptDigits = 25;

    internal static string Format(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var negative = (bits >> 63) != 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xF_FFFF_FFFF_FFFF;

        // NaN carries no sign in its text form; the infinities do.
        if (exponent == 0x7FF)
            return fraction != 0 ? "nan"
                : negative ? "-inf"
                : "inf";
        if (exponent == 0 && fraction == 0)
            return negative ? "-0x0.0p+0" : "0x0.0p+0";

        // A subnormal has no implicit leading one, so its hex form leads with `0` and the
        // whole significand sits after the point, exactly as CPython prints it.
        var lead = exponent == 0 ? '0' : '1';
        var power = exponent == 0 ? -1022 : exponent - 1023;
        var sign = negative ? "-" : "";
        var biased = power >= 0 ? $"+{power}" : power.ToString(CultureInfo.InvariantCulture);
        return $"{sign}0x{lead}.{fraction:x13}p{biased}";
    }

    /// <summary>
    /// Parses the accepted spelling exactly, raising CPython's two errors: a malformed
    /// string is a <c>ValueError</c> and a magnitude beyond a double is an
    /// <c>OverflowError</c>.
    /// </summary>
    internal static double Parse(string text, TextSpan span)
    {
        var index = 0;
        while (index < text.Length && IsSpace(text[index]))
            index++;

        var negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            index++;
        }

        if (SkipKeyword(text, ref index, "infinity") || SkipKeyword(text, ref index, "inf"))
        {
            RequireEnd(text, index, span);
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }
        if (SkipKeyword(text, ref index, "nan"))
        {
            RequireEnd(text, index, span);
            return double.NaN;
        }

        return ParseHexadecimal(text, index, negative, span);
    }

    private static double ParseHexadecimal(string text, int index, bool negative, TextSpan span)
    {
        if (
            index + 1 < text.Length
            && text[index] == '0'
            && (text[index + 1] == 'x' || text[index + 1] == 'X')
        )
        {
            index += 2;
        }

        // The value is `significand * 16 ** -fractionDigits * 2 ** exponent`; leading zeros
        // do not change the significand, but every digit after the point counts toward the
        // scale even when it is zero.
        var significand = BigInteger.Zero;
        var significantDigits = 0;
        long fractionDigits = 0;
        var dropped = false;
        var sawDigit = false;
        var sawPoint = false;

        while (index < text.Length)
        {
            var character = text[index];
            if (character == '.')
            {
                if (sawPoint)
                    throw Invalid(span);
                sawPoint = true;
                index++;
                continue;
            }

            if (!TryGetDigit(character, out var digit))
                break;
            index++;
            sawDigit = true;

            if (sawPoint)
                fractionDigits++;
            if (significantDigits == 0 && digit == 0)
                continue;
            significantDigits++;
            if (significantDigits <= KeptDigits)
                significand = (significand << 4) | digit;
            else if (digit != 0)
                dropped = true;
        }

        if (!sawDigit)
            throw Invalid(span);

        long exponent = 0;
        if (index < text.Length && (text[index] == 'p' || text[index] == 'P'))
        {
            index++;
            var exponentNegative = false;
            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                exponentNegative = text[index] == '-';
                index++;
            }
            var exponentDigits = 0;
            while (index < text.Length && text[index] is >= '0' and <= '9')
            {
                // Saturate: an exponent this large can only overflow or underflow, and the
                // sign decides which.
                exponent = Math.Min(exponent * 10 + (text[index] - '0'), 1_000_000_000);
                exponentDigits++;
                index++;
            }
            if (exponentDigits == 0)
                throw Invalid(span);
            if (exponentNegative)
                exponent = -exponent;
        }

        RequireEnd(text, index, span);

        if (significand.IsZero)
            return negative ? -0.0 : 0.0;

        // `msbExponent` is the binary exponent of the leading bit; it follows from the digit
        // counts alone, so a capped significand never changes which bits are kept.
        var msbExponent = 4L * (significantDigits - 1) - 4 * fractionDigits + exponent;
        if (msbExponent < -1075)
            return negative ? -0.0 : 0.0;

        // Normals keep fifty-three bits; a subnormal keeps only as many as its magnitude
        // reaches, which is what makes the low bits of a tiny value round away.
        var targetBits = msbExponent >= -1022 ? 53 : msbExponent + 1075;
        // Dropping digits costs nothing when the significand was never capped.
        var cappedDigits = Math.Max(0, significantDigits - KeptDigits);
        var binaryExponent = 4L * (cappedDigits - fractionDigits) + exponent;
        var rounded = Round(significand, targetBits, dropped, ref binaryExponent);

        var lead = binaryExponent + rounded.GetBitLength() - 1;
        if (lead > 1023)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "hexadecimal value too large to represent as a float",
                span,
                "OverflowError"
            );

        ulong bits;
        if (lead >= -1022)
        {
            var shift = rounded.GetBitLength() - 53;
            var biased = (int)(lead + 1023);
            var fraction = (rounded >> (int)shift) - (BigInteger.One << 52);
            bits = ((ulong)biased << 52) | (ulong)fraction;
        }
        else
        {
            // A subnormal rounds onto the 2**-1074 grid, so the scaled significand is the
            // bit pattern itself.
            bits = (ulong)(rounded << (int)(binaryExponent + 1074));
        }

        if (negative)
            bits |= 1UL << 63;
        return BitConverter.UInt64BitsToDouble(bits);
    }

    /// <summary>
    /// Rounds a positive significand to <paramref name="targetBits"/> bits, half to even,
    /// treating anything below the kept digits as a sticky bit.
    /// </summary>
    private static BigInteger Round(
        BigInteger significand,
        long targetBits,
        bool dropped,
        ref long binaryExponent
    )
    {
        var shift = significand.GetBitLength() - targetBits;
        binaryExponent += shift;
        if (shift <= 0)
            return significand << (int)(-shift);

        var kept = significand >> (int)shift;
        var remainder = significand - (kept << (int)shift);
        var half = BigInteger.One << (int)(shift - 1);
        if (remainder > half || (remainder == half && (dropped || !kept.IsEven)))
            return kept + 1;
        return kept;
    }

    /// <summary>
    /// Matches one of the three keywords, case-insensitively, and consumes it when it is
    /// there. The longer spelling is tried first so `infinity` never matches as `inf`.
    /// </summary>
    private static bool SkipKeyword(string text, ref int index, string keyword)
    {
        if (index + keyword.Length > text.Length)
            return false;
        for (var offset = 0; offset < keyword.Length; offset++)
        {
            if (char.ToLowerInvariant(text[index + offset]) != keyword[offset])
                return false;
        }
        index += keyword.Length;
        return true;
    }

    /// <summary>
    /// Only ASCII whitespace may surround the text, and nothing else may follow it — the
    /// parser that CPython uses here is the byte-oriented one, not the Unicode one.
    /// </summary>
    private static void RequireEnd(string text, int index, TextSpan span)
    {
        while (index < text.Length && IsSpace(text[index]))
            index++;
        if (index != text.Length)
            throw Invalid(span);
    }

    private static bool IsSpace(char character) =>
        character is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static bool TryGetDigit(char character, out int digit)
    {
        if (character is >= '0' and <= '9')
        {
            digit = character - '0';
            return true;
        }
        if (character is >= 'a' and <= 'f')
        {
            digit = character - 'a' + 10;
            return true;
        }
        if (character is >= 'A' and <= 'F')
        {
            digit = character - 'A' + 10;
            return true;
        }
        digit = 0;
        return false;
    }

    private static PythonRuntimeException Invalid(TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4003",
            "invalid hexadecimal floating-point string",
            span,
            "ValueError"
        );
}
