// The `is*` predicates follow CPython 3.14.7 Objects/unicodeobject.c and
// Objects/unicodectype.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Buffers.Binary;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The character predicates behind <c>str.isalpha</c> and its siblings, read from the
/// pinned Unicode 16.0.0 resource.
/// </summary>
/// <remarks>
/// The platform's own character categories cannot answer these: .NET 10 carries a newer
/// Unicode than the 16.0.0 the language target pins, so a code point added after that
/// release would be classified here where CPython leaves it unassigned.
/// </remarks>
internal static class PythonTextPredicates
{
    private const string ResourceName = "DotPython.UnicodePredicates16";
    private static readonly Lazy<PredicateTable> Sets = new(Load);

    /// <summary>`str.isalpha`: the letter categories, which is not the `Alphabetic` property.</summary>
    internal static bool IsAlpha(int codePoint) => Sets.Value.Has(Predicate.Alpha, codePoint);

    /// <summary>`str.isdecimal`: `Numeric_Type=Decimal`.</summary>
    internal static bool IsDecimal(int codePoint) => Sets.Value.Has(Predicate.Decimal, codePoint);

    /// <summary>`str.isdigit`: decimal digits plus the other digits such as `²`.</summary>
    internal static bool IsDigit(int codePoint) => Sets.Value.Has(Predicate.Digit, codePoint);

    /// <summary>`str.isnumeric`: digits plus every other numeric character such as `½`.</summary>
    internal static bool IsNumeric(int codePoint) => Sets.Value.Has(Predicate.Numeric, codePoint);

    /// <summary>CPython's whitespace set, which is not the `White_Space` property.</summary>
    internal static bool IsSpace(int codePoint) => Sets.Value.Has(Predicate.Space, codePoint);

    /// <summary>Everything outside the control, format, surrogate and separator categories.</summary>
    internal static bool IsPrintable(int codePoint) =>
        Sets.Value.Has(Predicate.Printable, codePoint);

    /// <summary>`XID_Start`, which `str.isidentifier` accepts in first position.</summary>
    internal static bool IsXidStart(int codePoint) => Sets.Value.Has(Predicate.XidStart, codePoint);

    /// <summary>`XID_Continue`, which `str.isidentifier` accepts in every other position.</summary>
    internal static bool IsXidContinue(int codePoint) =>
        Sets.Value.Has(Predicate.XidContinue, codePoint);

    /// <summary>The `Lt` category, which `str.istitle` treats as upper-case-ish.</summary>
    internal static bool IsTitlecase(int codePoint) =>
        Sets.Value.Has(Predicate.Titlecase, codePoint);

    private enum Predicate
    {
        Alpha,
        Decimal,
        Digit,
        Numeric,
        Space,
        Printable,
        XidStart,
        XidContinue,
        Titlecase,
    }

    private static PredicateTable Load()
    {
        using var stream =
            typeof(PythonTextPredicates).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Unicode predicate resource is missing.");
        if (stream.Length is < 40 or > 4_000_000)
            throw new InvalidDataException("The Unicode predicate resource has an invalid size.");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return new PredicateTable(data);
    }

    private sealed class PredicateTable
    {
        private const int HeaderSize = 18;
        private const int RangeSize = 8;

        private readonly int[][] _sets;

        internal PredicateTable(byte[] data)
        {
            if (!data.AsSpan(0, 10).SequenceEqual("DPYPRED16\0"u8))
                throw new InvalidDataException(
                    "The Unicode predicate resource has an invalid version."
                );
            var setCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10));
            var rangeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14));
            if (setCount != Enum.GetValues<Predicate>().Length)
                throw new InvalidDataException(
                    "The Unicode predicate resource has an unexpected set count."
                );
            if (HeaderSize + setCount * 4 + rangeCount * RangeSize != data.Length)
                throw new InvalidDataException("The Unicode predicate resource is truncated.");

            _sets = new int[setCount][];
            var at = HeaderSize;
            for (var set = 0; set < setCount; set++)
            {
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                at += 4;
                var ranges = new int[count * 2];
                for (var index = 0; index < count; index++)
                {
                    ranges[index * 2] = (int)
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    ranges[index * 2 + 1] = (int)
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
                    at += RangeSize;
                }
                _sets[set] = ranges;
            }
            if (at != data.Length)
                throw new InvalidDataException("The Unicode predicate resource is malformed.");
        }

        internal bool Has(Predicate predicate, int codePoint) =>
            InRanges(_sets[(int)predicate], codePoint);

        private static bool InRanges(int[] ranges, int codePoint)
        {
            var low = 0;
            var high = ranges.Length / 2 - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (codePoint < ranges[middle * 2])
                    high = middle - 1;
                else if (codePoint > ranges[middle * 2 + 1])
                    low = middle + 1;
                else
                    return true;
            }
            return false;
        }
    }
}
