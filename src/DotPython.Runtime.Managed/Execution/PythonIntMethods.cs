// The `int` method surface follows CPython 3.14.7 Objects/longobject.c and the numbers
// exposed alongside it (`real`, `imag`, `numerator`, `denominator`):
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The members of <c>int</c> — bit inspection, byte conversion and the numeric-tower
/// answers. A <c>bool</c> answers all of them as an <c>int</c> does, and its results are
/// ints: <c>True.conjugate()</c> is <c>1</c>.
/// </summary>
/// <remarks>
/// The diagnostics name the defining type, so they read <c>int.bit_length()</c> even on a
/// bool, and the clinic-generated methods spell their own names without it.
/// </remarks>
internal static class PythonIntMethods
{
    private static void AddName(List<string> names, string name)
    {
        if (!names.Contains(name))
            names.Add(name);
    }

    private static readonly string[] ToBytesParameters = ["length", "byteorder", "signed"];
    private static readonly string[] FromBytesParameters = ["bytes", "byteorder", "signed"];

    internal static Dictionary<string, PythonProtocolFunctionValue> CreateTable() =>
        new(StringComparer.Ordinal)
        {
            ["bit_length"] = NoArguments("bit_length", BitLength),
            ["bit_count"] = NoArguments("bit_count", BitCount),
            ["is_integer"] = NoArguments("is_integer", _ => PythonTruthValue.True),
            ["conjugate"] = NoArguments("conjugate", value => AsInteger(value)),
            ["as_integer_ratio"] = NoArguments(
                "as_integer_ratio",
                value => new PythonTupleValue([AsInteger(value), PythonWholeNumberValue.Create(1)])
            ),
            ["to_bytes"] = ToBytes(),
            ["from_bytes"] = FromBytes(boolean: false),
        };

    /// <summary>
    /// Whether a name is one of the numeric members. They are answered from the value
    /// itself, so the lookup never reaches a method table.
    /// </summary>
    internal static bool TryGetMember(PythonValue target, string name, out PythonValue member)
    {
        if (target is not (PythonWholeNumberValue or PythonTruthValue))
        {
            member = null!;
            return false;
        }
        member = name switch
        {
            "real" => AsInteger(target),
            "numerator" => AsInteger(target),
            "imag" => PythonWholeNumberValue.Create(0),
            "denominator" => PythonWholeNumberValue.Create(1),
            _ => null!,
        };
        return member is not null;
    }

    /// <summary>The member names an int or bool answers, for `dir` and `__dir__`.</summary>
    internal static void AddMemberNames(PythonValue value, List<string> names)
    {
        if (value is not (PythonWholeNumberValue or PythonTruthValue))
            return;
        foreach (var name in new[] { "real", "imag", "numerator", "denominator" })
            AddName(names, name);
    }

    /// <summary>The name CPython reports when one of those members is assigned to.</summary>
    internal static bool IsReadOnlyMember(string name) =>
        name is "real" or "imag" or "numerator" or "denominator";

    /// <summary>
    /// `int.from_bytes(bytes, byteorder='big', *, signed=False)`. It is a classmethod, so
    /// `bool` reaches the same implementation and constructs a bool.
    /// </summary>
    internal static PythonProtocolFunctionValue CreateFromBytes(bool boolean) => FromBytes(boolean);

    internal static bool TryGetInteger(PythonValue value, out BigInteger integer)
    {
        switch (value)
        {
            case PythonWholeNumberValue whole:
                integer = whole.Value;
                return true;
            case PythonTruthValue truth:
                integer = truth.Value ? 1 : 0;
                return true;
            default:
                integer = default;
                return false;
        }
    }

    /// <summary>The value as an `int`, which turns a bool into its 0 or 1.</summary>
    private static PythonValue AsInteger(PythonValue value) =>
        TryGetInteger(value, out var integer) ? PythonWholeNumberValue.Create(integer) : value;

    private static PythonValue BitLength(PythonValue target) =>
        TryGetInteger(target, out var integer)
            ? PythonWholeNumberValue.Create(BigInteger.Abs(integer).GetBitLength())
            : PythonWholeNumberValue.Create(0);

    private static PythonValue BitCount(PythonValue target) =>
        TryGetInteger(target, out var integer)
            ? PythonWholeNumberValue.Create(BigInteger.PopCount(BigInteger.Abs(integer)))
            : PythonWholeNumberValue.Create(0);

    /// <summary>
    /// A method that takes nothing: CPython reports the argument count or the keyword, and
    /// names the type that defines it.
    /// </summary>
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
                        $"int.{name}() takes no arguments ({arguments.Count} given)",
                        "TypeError"
                    ),
            (_, _, _, _) => throw Fault($"int.{name}() takes no keyword arguments", "TypeError")
        );

    /// <summary>
    /// `to_bytes`: the byte image of the value in the requested width and order. The
    /// arguments are read in CPython's order — the width converts first, then the order is
    /// validated, and only then is a negative width refused.
    /// </summary>
    private static PythonProtocolFunctionValue ToBytes() =>
        new(
            "to_bytes",
            (target, arguments) => ToBytes(target!, arguments, arguments.Count, [], []),
            (target, positional, names, values) =>
                ToBytes(target!, positional, positional.Count, names, values)
        );

    private static PythonByteSequenceValue ToBytes(
        PythonValue target,
        IReadOnlyList<PythonValue> positional,
        int positionalCount,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        // The total is counted first: CPython reports the wider bound when the call is over
        // the slot count outright, and only then the positional bound.
        var total = positionalCount + names.Count;
        if (total > 3)
            throw Fault($"to_bytes() takes at most 3 arguments ({total} given)", "TypeError");
        if (positionalCount > 2)
            throw Fault(
                $"to_bytes() takes at most 2 positional arguments ({positionalCount} given)",
                "TypeError"
            );
        var slots = new PythonValue?[3];
        for (var index = 0; index < positionalCount; index++)
            slots[index] = positional[index];
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(ToBytesParameters, names[index]);
            if (slot < 0)
                throw Fault(
                    $"to_bytes() got an unexpected keyword argument '{names[index]}'",
                    "TypeError"
                );
            if (slots[slot] is not null)
                throw Fault(
                    $"argument for to_bytes() given by name ('{names[index]}') and position "
                        + $"({slot + 1})",
                    "TypeError"
                );
            slots[slot] = values[index];
        }

        var length = slots[0] is null ? 1 : RequireIndex(slots[0]!);
        var byteOrder = RequireByteOrder(slots[1], "to_bytes");
        if (length < 0)
            throw Fault("length argument must be non-negative", "ValueError");
        // Only an int or a bool reaches here, so the conversion always succeeds.
        TryGetInteger(target, out var integer);
        return WriteBytes(integer, length, byteOrder, IsTrue(slots[2]));
    }

    /// <summary>`int.from_bytes`: the value the given bytes spell, in the requested order.</summary>
    private static PythonProtocolFunctionValue FromBytes(bool boolean) =>
        new(
            "from_bytes",
            (_, arguments) => AsClass(boolean, FromBytesCore(arguments, arguments.Count, [], [])),
            (_, positional, names, values) =>
                AsClass(boolean, FromBytesCore(positional, positional.Count, names, values))
        );

    /// <summary>A classmethod answers with the class it was reached through.</summary>
    private static PythonValue AsClass(bool boolean, PythonWholeNumberValue value) =>
        boolean
            ? value.Value.IsZero
                ? PythonTruthValue.False
                : PythonTruthValue.True
            : value;

    private static PythonWholeNumberValue FromBytesCore(
        IReadOnlyList<PythonValue> positional,
        int positionalCount,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var total = positionalCount + names.Count;
        if (total > 3)
            throw Fault($"from_bytes() takes at most 3 arguments ({total} given)", "TypeError");
        if (positionalCount > 2)
            throw Fault(
                $"from_bytes() takes at most 2 positional arguments ({positionalCount} given)",
                "TypeError"
            );
        var slots = new PythonValue?[3];
        for (var index = 0; index < positionalCount; index++)
            slots[index] = positional[index];
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(FromBytesParameters, names[index]);
            if (slot < 0)
                throw Fault(
                    $"from_bytes() got an unexpected keyword argument '{names[index]}'",
                    "TypeError"
                );
            if (slots[slot] is not null)
                throw Fault(
                    $"argument for from_bytes() given by name ('{names[index]}') and position "
                        + $"({slot + 1})",
                    "TypeError"
                );
            slots[slot] = values[index];
        }

        if (slots[0] is null)
            throw Fault("from_bytes() missing required argument 'bytes' (pos 1)", "TypeError");
        // The order is validated before the first argument is converted, as CPython does.
        var byteOrder = RequireByteOrder(slots[1], "from_bytes");
        var contents = RequireContents(slots[0]!);
        return PythonWholeNumberValue.Create(ReadBytes(contents, byteOrder, IsTrue(slots[2])));
    }

    private static BigInteger ReadBytes(byte[] contents, string byteOrder, bool signed)
    {
        var value = BigInteger.Zero;
        if (byteOrder == "big")
        {
            foreach (var octet in contents)
                value = (value << 8) | octet;
        }
        else
        {
            for (var index = contents.Length - 1; index >= 0; index--)
                value = (value << 8) | contents[index];
        }
        if (signed && contents.Length > 0)
        {
            var signOctet = byteOrder == "big" ? contents[0] : contents[^1];
            if ((signOctet & 0x80) != 0)
                value -= BigInteger.One << (8 * contents.Length);
        }
        return value;
    }

    private static PythonByteSequenceValue WriteBytes(
        BigInteger value,
        long length,
        string byteOrder,
        bool signed
    )
    {
        if (length > PythonBytesConstruction.MaximumSize)
            throw Fault("The bytes result exceeds the supported size.", "OverflowError");
        if (!signed && value < 0)
            throw Fault("can't convert negative int to unsigned", "OverflowError");

        var magnitude = BigInteger.Abs(value);
        if (length == 0)
        {
            if (!magnitude.IsZero)
                throw Fault("int too big to convert", "OverflowError");
        }
        else if (!signed)
        {
            if (magnitude >= BigInteger.One << (int)(8 * length))
                throw Fault("int too big to convert", "OverflowError");
        }
        else
        {
            var limit = BigInteger.One << (int)(8 * length - 1);
            if (value < -limit || value >= limit)
                throw Fault("int too big to convert", "OverflowError");
        }

        var buffer = new byte[length];
        if (value < 0)
        {
            // A negative value fills from the left with ones; `~(magnitude - 1)` is its
            // two's complement, and taking it from the decremented magnitude avoids
            // materializing `1 << (8 * length)` for a wide width.
            Array.Fill(buffer, (byte)0xFF);
            var inverted = (magnitude - 1).ToByteArray(isUnsigned: true, isBigEndian: true);
            for (var index = 0; index < inverted.Length; index++)
                buffer[length - inverted.Length + index] = (byte)~inverted[index];
        }
        else
        {
            // Zero has no digits of its own, but `ToByteArray` still reports one, which
            // would not fit a zero-width result.
            var digits = magnitude.IsZero
                ? []
                : magnitude.ToByteArray(isUnsigned: true, isBigEndian: true);
            digits.CopyTo(buffer, length - digits.Length);
        }

        if (byteOrder == "little")
            Array.Reverse(buffer);
        return PythonByteSequenceValue.Create(buffer);
    }

    /// <summary>
    /// The first argument of `from_bytes`: a bytes-like object, or anything else that
    /// iterates integers — a list, a tuple, even a dict's keys. A string is refused
    /// outright, as it is by CPython's byte conversion.
    /// </summary>
    private static byte[] RequireContents(PythonValue value)
    {
        if (
            PythonBufferProtocol.TryGetContent(
                value,
                PythonBufferProtocol.FullReadOnly,
                default,
                out var contents
            )
        )
            return contents;
        if (value is PythonTextValue)
            throw Fault("cannot convert 'str' object to bytes", "TypeError");
        return PythonBytesConstruction.FromIterable(value, default).Value;
    }

    private static string RequireByteOrder(PythonValue? value, string method)
    {
        if (value is null)
            return "big";
        if (value is not PythonTextValue text)
            throw Fault(
                $"{method}() argument 'byteorder' must be str, not "
                    + $"{ManagedObjectProtocols.GetTypeName(value)}",
                "TypeError"
            );
        if (text.Value != "little" && text.Value != "big")
            throw Fault("byteorder must be either 'little' or 'big'", "ValueError");
        return text.Value;
    }

    /// <summary>The width as a C ssize_t, which is what the byte count is measured in.</summary>
    private static long RequireIndex(PythonValue value)
    {
        if (!TryGetInteger(value, out var integer))
            throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as "
                    + "an integer",
                "TypeError"
            );
        if (integer < long.MinValue || integer > long.MaxValue)
            throw Fault("Python int too large to convert to C ssize_t", "OverflowError");
        return (long)integer;
    }

    private static bool IsTrue(PythonValue? value) =>
        value is not null && ManagedObjectProtocols.IsTrue(value);

    private static PythonRuntimeException Fault(string message, string pythonType) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, pythonType);
}
