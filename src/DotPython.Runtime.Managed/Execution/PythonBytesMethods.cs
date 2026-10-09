// The bytes method surface follows CPython 3.14.7 Objects/bytesobject.c and
// Lib/test/test_bytes.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The method surface of <c>bytes</c>, kept byte-oriented: case mapping is ASCII-only, a
/// missing byte match reports CPython's "subsection not found", and every search accepts an
/// integer byte value or a bytes-like object where CPython does.
/// </summary>
internal static class PythonBytesMethods
{
    internal static Dictionary<string, PythonProtocolFunctionValue> CreateTable() =>
        new(StringComparer.Ordinal)
        {
            ["upper"] = Transform("upper", ToUpper),
            ["lower"] = Transform("lower", ToLower),
            ["capitalize"] = Transform("capitalize", Capitalize),
            ["title"] = Transform("title", Title),
            ["swapcase"] = Transform("swapcase", SwapCase),
            ["strip"] = Strip("strip"),
            ["lstrip"] = Strip("lstrip"),
            ["rstrip"] = Strip("rstrip"),
            ["find"] = Search("find", rfind: false, raiseWhenMissing: false),
            ["rfind"] = Search("rfind", rfind: true, raiseWhenMissing: false),
            ["index"] = Search("index", rfind: false, raiseWhenMissing: true),
            ["rindex"] = Search("rindex", rfind: true, raiseWhenMissing: true),
            ["count"] = Count(),
            ["startswith"] = Affix("startswith", start: true),
            ["endswith"] = Affix("endswith", start: false),
            ["replace"] = Replace(),
            ["split"] = Split("split", reverse: false),
            ["rsplit"] = Split("rsplit", reverse: true),
            ["splitlines"] = SplitLines(),
            ["join"] = Join(),
            ["partition"] = Partition("partition", reverse: false),
            ["rpartition"] = Partition("rpartition", reverse: true),
            ["center"] = Pad("center", Centre),
            ["ljust"] = Pad("ljust", Left),
            ["rjust"] = Pad("rjust", Right),
            ["zfill"] = ZFill(),
            ["expandtabs"] = ExpandTabs(),
            ["removeprefix"] = RemoveAffix("removeprefix", prefix: true),
            ["removesuffix"] = RemoveAffix("removesuffix", prefix: false),
            ["hex"] = Hex(),
            ["translate"] = Translate(),
        };

    // ---- byte-level helpers -----------------------------------------------------------------

    /// <summary>ASCII-only case mapping: a byte outside `a`-`z`/`A`-`Z` is unchanged.</summary>
    private static byte ToUpper(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    private static byte ToLower(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static bool IsAsciiLetter(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    private static byte[] ToUpper(byte[] value) => Map(value, ToUpper);

    private static byte[] ToLower(byte[] value) => Map(value, ToLower);

    private static byte[] Map(byte[] value, Func<byte, byte> mapping)
    {
        var result = new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
            result[index] = mapping(value[index]);
        return result;
    }

    private static byte[] Capitalize(byte[] value)
    {
        var result = new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
            result[index] = index == 0 ? ToUpper(value[index]) : ToLower(value[index]);
        return result;
    }

    private static byte[] SwapCase(byte[] value) =>
        Map(
            value,
            byteValue => ToUpper(byteValue) == byteValue ? ToLower(byteValue) : ToUpper(byteValue)
        );

    /// <summary>`title`: the first cased byte of each run is upper, the rest lower.</summary>
    private static byte[] Title(byte[] value)
    {
        var result = new byte[value.Length];
        var atWordStart = true;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            var isLetter = IsAsciiLetter(current);
            result[index] = atWordStart ? ToUpper(current) : ToLower(current);
            atWordStart = !isLetter;
        }
        return result;
    }

    /// <summary>
    /// Both searches report an absolute position; the span overload would report one
    /// relative to <paramref name="start"/>, which callers here advance by.
    /// </summary>
    private static int IndexOf(byte[] haystack, byte[] needle, int start, int end)
    {
        if (needle.Length == 0)
            return start <= end ? start : -1;
        if (needle.Length > end - start)
            return -1;
        var found = haystack.AsSpan(start, end - start).IndexOf(needle);
        return found < 0 ? -1 : start + found;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle, int start, int end)
    {
        if (needle.Length == 0)
            return end;
        if (needle.Length > end - start)
            return -1;
        var found = haystack.AsSpan(start, end - start).LastIndexOf(needle);
        return found < 0 ? -1 : start + found;
    }

    // ---- argument coercion ------------------------------------------------------------------

    private static byte[] RequireBytes(string name, PythonValue value) =>
        PythonBufferProtocol.TryGetContent(
            value,
            PythonBufferProtocol.Simple,
            default,
            out var content
        )
            ? content
            : throw Fault(
                $"a bytes-like object is required, not '{ManagedObjectProtocols.GetTypeName(value)}'",
                "TypeError"
            );

    /// <summary>
    /// A search operand: an integer is the single byte it names and must be in range, and a
    /// bytes-like object is itself.
    /// </summary>
    private static byte[] RequireSearchOperand(string name, PythonValue value, TextSpan span)
    {
        if (value is PythonByteSequenceValue bytes)
            return bytes.Value;
        var byteValue = value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => (BigInteger?)null,
        };
        if (byteValue is { } candidate)
        {
            if (candidate < 0 || candidate > 255)
                throw Fault("byte must be in range(0, 256)", "ValueError");
            return [(byte)candidate];
        }

        // Anything else may still hand out a buffer.
        if (
            PythonBufferProtocol.TryGetContent(
                value,
                PythonBufferProtocol.Simple,
                span,
                out var contents
            )
        )
            return contents;

        throw Fault(
            "argument should be integer or bytes-like object, "
                + $"not '{ManagedObjectProtocols.GetTypeName(value)}'",
            "TypeError"
        );
    }

    private static int RequireIndex(string name, PythonValue value, int length)
    {
        BigInteger bound;
        if (value is PythonWholeNumberValue whole)
            bound = whole.Value;
        else if (value is PythonTruthValue truth)
            bound = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(value, default, out bound))
            throw Fault(
                "slice indices must be integers or None or have an __index__ method",
                "TypeError"
            );

        var clamped =
            bound < int.MinValue ? int.MinValue
            : bound > int.MaxValue ? int.MaxValue
            : (int)bound;
        if (clamped < 0)
            clamped += length;
        return Math.Clamp(clamped, 0, length);
    }

    /// <summary>The `(start, end)` a byte search or affix test runs over.</summary>
    private static (int Start, int End) Bounds(
        byte[] value,
        IReadOnlyList<PythonValue> arguments,
        int firstBound
    )
    {
        var length = value.Length;
        var start =
            arguments.Count > firstBound ? RequireIndex("", arguments[firstBound], length) : 0;
        var end =
            arguments.Count > firstBound + 1
                ? RequireIndex("", arguments[firstBound + 1], length)
                : length;
        return (start, end);
    }

    // ---- table construction -----------------------------------------------------------------

    private static PythonProtocolFunctionValue Transform(
        string name,
        Func<byte[], byte[]> action
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 0, 0);
                return Wrap(action(RequireBytes(name, target!)));
            }
        );

    private static PythonProtocolFunctionValue Strip(string name)
    {
        var leading = name is "strip" or "lstrip";
        var trailing = name is "strip" or "rstrip";
        return new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 0, 1);
                var value = RequireBytes(name, target!);
                var cut = arguments.Count == 0 ? null : RequireBytes(name, arguments[0]);
                var start = 0;
                var end = value.Length;
                if (leading)
                {
                    while (
                        start < end
                        && (cut is null ? IsWhitespace(value[start]) : Contains(cut, value[start]))
                    )
                        start++;
                }
                if (trailing)
                {
                    while (
                        end > start
                        && (
                            cut is null
                                ? IsWhitespace(value[end - 1])
                                : Contains(cut, value[end - 1])
                        )
                    )
                        end--;
                }
                return Wrap(value[start..end]);
            }
        );
    }

    /// <summary>CPython's byte whitespace set: space and `\t`-`\r`.</summary>
    private static bool IsWhitespace(byte value) => value == (byte)' ' || value is >= 9 and <= 13;

    private static bool Contains(byte[] value, byte item) => value.AsSpan().IndexOf(item) >= 0;

    private static PythonProtocolFunctionValue Search(
        string name,
        bool rfind,
        bool raiseWhenMissing
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 3);
                var value = RequireBytes(name, target!);
                var needle = RequireSearchOperand(name, arguments[0], default);
                var (start, end) = Bounds(value, arguments, 1);
                var found = rfind
                    ? LastIndexOf(value, needle, start, end)
                    : IndexOf(value, needle, start, end);
                if (found >= 0)
                    return PythonWholeNumberValue.Create(found);
                return raiseWhenMissing
                    ? throw Fault("subsection not found", "ValueError")
                    : PythonWholeNumberValue.Create(-1);
            }
        );

    private static PythonProtocolFunctionValue Count() =>
        new(
            "count",
            (target, arguments) =>
            {
                RequireArguments("count", arguments, 1, 3);
                var value = RequireBytes("count", target!);
                var needle = RequireSearchOperand("count", arguments[0], default);
                var (start, end) = Bounds(value, arguments, 1);
                var count = 0;
                var cursor = start;
                while (cursor <= end)
                {
                    var found = IndexOf(value, needle, cursor, end);
                    if (found < 0)
                        break;
                    count++;
                    cursor = found + Math.Max(1, needle.Length);
                }
                return PythonWholeNumberValue.Create(count);
            }
        );

    private static PythonProtocolFunctionValue Affix(string name, bool start) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 3);
                var value = RequireBytes(name, target!);
                var (from, to) = Bounds(value, arguments, 1);
                var candidates = arguments[0] is PythonTupleValue tuple
                    ? tuple.Elements
                    : [arguments[0]];
                foreach (var candidate in candidates)
                {
                    if (
                        !PythonBufferProtocol.TryGetContent(
                            candidate,
                            PythonBufferProtocol.Simple,
                            default,
                            out var prefix
                        )
                    )
                    {
                        throw Fault(
                            $"{name} first arg must be bytes or a tuple of bytes, "
                                + $"not {ManagedObjectProtocols.GetTypeName(candidate)}",
                            "TypeError"
                        );
                    }
                    var length = prefix.Length;
                    if (length > to - from)
                        continue;
                    var slice = start
                        ? value.AsSpan(from, length)
                        : value.AsSpan(to - length, length);
                    if (slice.SequenceEqual(prefix))
                        return Truth(true);
                }
                return Truth(false);
            }
        );

    private static PythonProtocolFunctionValue Replace() =>
        new(
            "replace",
            (target, arguments) =>
            {
                RequireArguments("replace", arguments, 2, 3);
                var value = RequireBytes("replace", target!);
                var oldValue = RequireBytes("replace", arguments[0]);
                var newValue = RequireBytes("replace", arguments[1]);
                var limit = arguments.Count > 2 ? RequireCount(arguments[2]) : -1;
                return Wrap(ReplaceBytes(value, oldValue, newValue, limit));
            }
        );

    private static byte[] ReplaceBytes(byte[] value, byte[] oldValue, byte[] newValue, int limit)
    {
        var result = new List<byte>(value.Length);
        if (oldValue.Length == 0)
        {
            // An empty pattern inserts between every byte and once more at the end, so an
            // n-byte input takes n+1 insertions unless a smaller limit is given.
            var insertions = limit < 0 ? value.Length + 1 : Math.Min(limit, value.Length + 1);
            for (var index = 0; index < insertions; index++)
            {
                result.AddRange(newValue);
                if (index < value.Length)
                    result.Add(value[index]);
            }
            // The final insertion closes the value, so nothing is left over.
            if (insertions <= value.Length)
                result.AddRange(value[insertions..]);
            return [.. result];
        }

        var cursor = 0;
        while (limit != 0)
        {
            var found = IndexOf(value, oldValue, cursor, value.Length);
            if (found < 0)
                break;
            result.AddRange(value[cursor..found]);
            result.AddRange(newValue);
            cursor = found + oldValue.Length;
            if (limit > 0)
                limit--;
        }
        result.AddRange(value[cursor..]);
        return [.. result];
    }

    private static int RequireCount(PythonValue value) =>
        value is PythonWholeNumberValue whole
            ? whole.Value > int.MaxValue
                ? int.MaxValue
                : whole.Value < 0
                    ? -1
                    : (int)whole.Value
            : value is PythonTruthValue truth
                ? truth.Value
                    ? 1
                    : 0
                : throw Fault(
                    $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be "
                        + "interpreted as an integer",
                    "TypeError"
                );

    private static PythonProtocolFunctionValue Split(string name, bool reverse) =>
        new PythonProtocolFunctionValue(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 0, 2);
                var value = RequireBytes(name, target!);
                var separator =
                    arguments.Count == 0 || arguments[0] is PythonNoneValue
                        ? null
                        : RequireBytes(name, arguments[0]);
                var limit =
                    arguments.Count > 1 && arguments[1] is not PythonNoneValue
                        ? RequireCount(arguments[1])
                        : -1;
                if (separator is { Length: 0 })
                    throw Fault("empty separator", "ValueError");
                var pieces = separator is null
                    ? SplitOnWhitespace(value, limit, reverse)
                    : SplitOnSeparator(value, separator, limit, reverse);
                return new PythonListValue([.. pieces.Select(Wrap)]);
            }
        ).WithSignature(["sep", "maxsplit"], [PythonNoneValue.Instance, null]);

    private static List<byte[]> SplitOnSeparator(
        byte[] value,
        byte[] separator,
        int limit,
        bool reverse
    )
    {
        var pieces = new List<byte[]>();
        if (limit == 0)
        {
            pieces.Add(value);
            return pieces;
        }
        var cursor = reverse ? value.Length : 0;
        while (limit != 0)
        {
            var found = reverse
                ? LastIndexOf(value, separator, 0, cursor)
                : IndexOf(value, separator, cursor, value.Length);
            if (found < 0)
                break;
            pieces.Add(reverse ? value[(found + separator.Length)..cursor] : value[cursor..found]);
            cursor = reverse ? found : found + separator.Length;
            if (limit > 0)
                limit--;
        }
        pieces.Add(reverse ? value[..cursor] : value[cursor..]);
        if (reverse)
            pieces.Reverse();
        return pieces;
    }

    /// <summary>`split(None)`: runs of byte whitespace, with the end limits CPython keeps.</summary>
    private static List<byte[]> SplitOnWhitespace(byte[] value, int limit, bool reverse)
    {
        var pieces = new List<byte[]>();
        if (reverse)
        {
            var end = value.Length;
            while (limit != 0)
            {
                while (end > 0 && IsWhitespace(value[end - 1]))
                    end--;
                if (end == 0)
                    break;
                var start = end;
                while (start > 0 && !IsWhitespace(value[start - 1]))
                    start--;
                pieces.Add(value[start..end]);
                end = start;
                if (limit > 0)
                {
                    limit--;
                    if (limit == 0)
                        break;
                }
            }
            while (end > 0 && IsWhitespace(value[end - 1]))
                end--;
            if (end > 0)
                pieces.Add(value[..end]);
            pieces.Reverse();
            return pieces;
        }

        var cursor = 0;
        while (cursor < value.Length)
        {
            while (cursor < value.Length && IsWhitespace(value[cursor]))
                cursor++;
            if (cursor >= value.Length)
                break;
            if (limit == 0)
            {
                // The remainder keeps its inner whitespace but drops the run that ended
                // the previous piece.
                pieces.Add(value[cursor..]);
                break;
            }
            var start = cursor;
            while (cursor < value.Length && !IsWhitespace(value[cursor]))
                cursor++;
            pieces.Add(value[start..cursor]);
            if (limit > 0)
                limit--;
        }
        return pieces;
    }

    private static PythonProtocolFunctionValue SplitLines() =>
        new PythonProtocolFunctionValue(
            "splitlines",
            (target, arguments) =>
            {
                RequireArguments("splitlines", arguments, 0, 1);
                var value = RequireBytes("splitlines", target!);
                var keepEnds = arguments.Count > 0 && ManagedObjectProtocols.IsTrue(arguments[0]);
                return new PythonListValue([.. SplitLines(value, keepEnds).Select(Wrap)]);
            }
        ).WithSignature(["keepends"], [null]);

    private static List<byte[]> SplitLines(byte[] value, bool keepEnds)
    {
        var lines = new List<byte[]>();
        var start = 0;
        var index = 0;
        while (index < value.Length)
        {
            var current = value[index];
            int width;
            if (current == (byte)'\r')
                width = index + 1 < value.Length && value[index + 1] == (byte)'\n' ? 2 : 1;
            else if (current is (byte)'\n' or (byte)'\v' or (byte)'\f' or 0x1c or 0x1d or 0x1e)
                width = 1;
            else
            {
                index++;
                continue;
            }
            lines.Add(keepEnds ? value[start..(index + width)] : value[start..index]);
            index += width;
            start = index;
        }
        if (start < value.Length)
            lines.Add(value[start..]);
        return lines;
    }

    private static PythonProtocolFunctionValue Join() =>
        new(
            "join",
            (target, arguments) =>
            {
                RequireArguments("join", arguments, 1, 1);
                var separator = RequireBytes("join", target!);
                if (
                    arguments[0]
                    is not PythonListValue
                        and not PythonTupleValue
                        and not PythonSetValue
                )
                {
                    throw Fault($"can only join an iterable", "TypeError");
                }
                var elements =
                    arguments[0] is PythonListValue list ? list.Elements
                    : arguments[0] is PythonTupleValue tuple ? [.. tuple.Elements]
                    : [.. ((PythonSetValue)arguments[0]).Elements];
                var result = new List<byte>();
                for (var index = 0; index < elements.Count; index++)
                {
                    if (index != 0)
                        result.AddRange(separator);
                    if (
                        !PythonBufferProtocol.TryGetContent(
                            elements[index],
                            PythonBufferProtocol.Simple,
                            default,
                            out var element
                        )
                    )
                    {
                        throw Fault(
                            $"sequence item {index}: expected a bytes-like object, "
                                + $"{ManagedObjectProtocols.GetTypeName(elements[index])} found",
                            "TypeError"
                        );
                    }
                    result.AddRange(element);
                }
                return Wrap([.. result]);
            }
        );

    private static PythonProtocolFunctionValue Partition(string name, bool reverse) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 1);
                var value = RequireBytes(name, target!);
                var separator = RequireBytes(name, arguments[0]);
                if (separator.Length == 0)
                    throw Fault("empty separator", "ValueError");
                var found = reverse
                    ? LastIndexOf(value, separator, 0, value.Length)
                    : IndexOf(value, separator, 0, value.Length);
                return found < 0
                    ? new PythonTupleValue(
                        reverse
                            ? [Wrap([]), Wrap([]), Wrap(value)]
                            : [Wrap(value), Wrap([]), Wrap([])]
                    )
                    : new PythonTupleValue([
                        Wrap(value[..found]),
                        Wrap(separator),
                        Wrap(value[(found + separator.Length)..]),
                    ]);
            }
        );

    private static PythonProtocolFunctionValue Pad(
        string name,
        Func<byte[], int, byte[], byte[]> action
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 2);
                var value = RequireBytes(name, target!);
                var width = RequireCount(arguments[0]);
                byte[] fill;
                if (arguments.Count > 1)
                {
                    // The fill is checked even when the width already fits, and unlike
                    // `str` it must be exactly one byte. A non-bytes fill names its type.
                    if (arguments[1] is not PythonByteSequenceValue fillBytes)
                    {
                        throw Fault(
                            $"{name}() argument 2 must be a byte string of length 1, "
                                + $"not {ManagedObjectProtocols.GetTypeName(arguments[1])}",
                            "TypeError"
                        );
                    }
                    fill = fillBytes.Value;
                }
                else
                {
                    fill = [(byte)' '];
                }
                if (fill.Length != 1)
                {
                    throw Fault(
                        $"{name}(): argument 2 must be a byte string of length 1, "
                            + $"not a bytes object of length {fill.Length}",
                        "TypeError"
                    );
                }
                return Wrap(action(value, width, fill));
            }
        );

    private static byte[] Centre(byte[] value, int width, byte[] fill)
    {
        if (value.Length >= width)
            return value;
        // The same left bias `str.center` uses, so the two agree on odd padding.
        var extra = width - value.Length;
        var left = extra / 2 + (extra & width & 1);
        return [.. Fill(fill[0], left), .. value, .. Fill(fill[0], extra - left)];
    }

    private static byte[] Left(byte[] value, int width, byte[] fill) =>
        value.Length >= width ? value : [.. value, .. Fill(fill[0], width - value.Length)];

    private static byte[] Right(byte[] value, int width, byte[] fill) =>
        value.Length >= width ? value : [.. Fill(fill[0], width - value.Length), .. value];

    private static IEnumerable<byte> Fill(byte fill, int count) => Enumerable.Repeat(fill, count);

    private static PythonProtocolFunctionValue ZFill() =>
        new(
            "zfill",
            (target, arguments) =>
            {
                RequireArguments("zfill", arguments, 1, 1);
                var value = RequireBytes("zfill", target!);
                var width = RequireCount(arguments[0]);
                if (value.Length >= width)
                    return Wrap(value);
                var padding = width - value.Length;
                var zeros = Enumerable.Repeat((byte)'0', padding);
                // A leading sign keeps its position: `b'-abc'.zfill(5)` is `b'-0abc'`.
                var signed = value.Length > 0 && value[0] is (byte)'+' or (byte)'-';
                byte[] padded = signed ? [value[0], .. zeros, .. value[1..]] : [.. zeros, .. value];
                return Wrap(padded);
            }
        );

    private static PythonProtocolFunctionValue ExpandTabs() =>
        new PythonProtocolFunctionValue(
            "expandtabs",
            (target, arguments) =>
            {
                RequireArguments("expandtabs", arguments, 0, 1);
                var value = RequireBytes("expandtabs", target!);
                var size = arguments.Count > 0 ? RequireCount(arguments[0]) : 8;
                var result = new List<byte>(value.Length);
                var column = 0;
                foreach (var current in value)
                {
                    if (current == (byte)'\t')
                    {
                        if (size > 0)
                        {
                            var spaces = size - column % size;
                            result.AddRange(Enumerable.Repeat((byte)' ', spaces));
                            column += spaces;
                        }
                        continue;
                    }
                    result.Add(current);
                    column = current is (byte)'\n' or (byte)'\r' ? 0 : column + 1;
                }
                return Wrap([.. result]);
            }
        ).WithSignature(["tabsize"], [null]);

    private static PythonProtocolFunctionValue RemoveAffix(string name, bool prefix) =>
        new(
            name,
            (target, arguments) =>
            {
                RequireArguments(name, arguments, 1, 1);
                var value = RequireBytes(name, target!);
                var affix = RequireBytes(name, arguments[0]);
                if (affix.Length > value.Length)
                    return Wrap(value);
                var matches = prefix
                    ? value.AsSpan(0, affix.Length).SequenceEqual(affix)
                    : value.AsSpan(value.Length - affix.Length).SequenceEqual(affix);
                if (!matches)
                    return Wrap(value);
                return Wrap(prefix ? value[affix.Length..] : value[..^affix.Length]);
            }
        );

    private static PythonProtocolFunctionValue Hex() =>
        new PythonProtocolFunctionValue("hex", InvokeHex, HexWithKeywords);

    /// <summary>`memoryview.hex` is this same method over the bytes a view exposes.</summary>
    internal static PythonTextValue HexOver(
        PythonByteSequenceValue bytes,
        IReadOnlyList<PythonValue> arguments
    ) => InvokeHex(bytes, arguments);

    /// <summary>The same method, with the keywords its separator and grouping are given by.</summary>
    internal static PythonTextValue HexOverWithKeywords(
        PythonByteSequenceValue bytes,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    ) => HexWithKeywords(bytes, positional, names, values);

    private static PythonTextValue InvokeHex(
        PythonValue? target,
        IReadOnlyList<PythonValue> arguments
    )
    {
        // `hex` reports its own arity the way CPython does rather than through the
        // runtime's shared wording.
        if (arguments.Count > 2)
            throw Fault($"hex() takes at most 2 arguments ({arguments.Count} given)", "TypeError");
        var separator = arguments.Count > 0 ? RequireSeparator(arguments[0]) : string.Empty;
        var perSeparator = arguments.Count > 1 ? RequireSeparatorCount(arguments[1]) : 1;
        return Render(target, separator, perSeparator);
    }

    /// <summary>
    /// A separator is one character, given as text or as a byte; `bytes_per_sep` then groups
    /// that many bytes per separator, from the right when positive and from the left when
    /// negative.
    /// </summary>
    private static PythonTextValue Render(PythonValue? target, string separator, int perSeparator)
    {
        var value = RequireBytes("hex", target!);
        return new PythonTextValue(
            RenderHex(value, separator, separator.Length == 0 ? 0 : perSeparator)
        );
    }

    /// <summary>
    /// `hex(sep=…, bytes_per_sep=…)`: the separator may be omitted, so it is bound by hand
    /// rather than through a signature whose default would be indistinguishable from an
    /// explicit `None` — which CPython refuses.
    /// </summary>
    private static PythonTextValue HexWithKeywords(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var slots = new PythonValue?[2];
        for (var index = 0; index < positional.Count && index < slots.Length; index++)
            slots[index] = positional[index];
        for (var index = 0; index < names.Count; index++)
        {
            var slot = names[index] switch
            {
                "sep" => 0,
                "bytes_per_sep" => 1,
                _ => -1,
            };
            if (slot < 0)
                throw Fault(
                    $"hex() got an unexpected keyword argument '{names[index]}'",
                    "TypeError"
                );
            slots[slot] = values[index];
        }
        var separator = slots[0] is null ? string.Empty : RequireSeparator(slots[0]!);
        var perSeparator = slots[1] is null ? 1 : RequireSeparatorCount(slots[1]!);
        return Render(target, separator, perSeparator);
    }

    /// <summary>`bytes_per_sep`, whose sign chooses the end the groups align to.</summary>
    private static int RequireSeparatorCount(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => (int)
                System.Numerics.BigInteger.Clamp(whole.Value, int.MinValue, int.MaxValue),
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted "
                    + "as an integer",
                "TypeError"
            ),
        };

    /// <summary>
    /// One separator character, as a str or a bytes-like object. Anything else reports the
    /// length error CPython raises, since the length is what it inspects first.
    /// </summary>
    private static string RequireSeparator(PythonValue value)
    {
        // A byte is one character in this range, so a single-byte separator is that
        // character; anything that is not text or bytes reports the length error CPython
        // raises, because the length is what it inspects first.
        var text = ManagedObjectProtocols.TryGetByteContent(value, out var contents)
            ? contents.Length == 1
                ? ((char)contents[0]).ToString()
                : string.Empty
            : RequireText(value);
        if (PythonTextTraversal.Count(text, default) != 1)
            throw Fault("sep must be length 1.", "ValueError");
        return text;
    }

    private static string RenderHex(byte[] value, string separator, int bytesPerSeparator)
    {
        if (
            separator.Length == 0
            || bytesPerSeparator == 0
            || Math.Abs(bytesPerSeparator) >= value.Length
        )
            return Convert.ToHexStringLower(value);
        // A positive count aligns its groups to the end, so the first one may be short.
        var perSeparator = Math.Abs(bytesPerSeparator);
        var builder = new System.Text.StringBuilder(value.Length * 3);
        for (var index = 0; index < value.Length; index++)
        {
            if (index != 0)
            {
                // A positive count aligns its groups to the end, so the first group may be
                // short; a negative one groups from the start, and a partial last group
                // simply ends the text.
                var boundary =
                    bytesPerSeparator > 0
                        ? (value.Length - index) % perSeparator == 0
                        : index % perSeparator == 0;
                if (boundary)
                    builder.Append(separator);
            }
            builder.Append(
                value[index].ToString("x2", System.Globalization.CultureInfo.InvariantCulture)
            );
        }
        return builder.ToString();
    }

    private static string RequireText(PythonValue value) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                $"object of type '{ManagedObjectProtocols.GetTypeName(value)}' has no len()",
                "TypeError"
            );

    private static PythonProtocolFunctionValue Translate() =>
        new PythonProtocolFunctionValue(
            "translate",
            (target, arguments) =>
            {
                RequireArguments("translate", arguments, 1, 2);
                var value = RequireBytes("translate", target!);
                if (
                    !PythonBufferProtocol.TryGetContent(
                        arguments[0],
                        PythonBufferProtocol.Simple,
                        default,
                        out var table
                    )
                )
                {
                    throw Fault(
                        "a bytes-like object is required, "
                            + $"not '{ManagedObjectProtocols.GetTypeName(arguments[0])}'",
                        "TypeError"
                    );
                }
                if (table.Length != 256)
                    throw Fault("translation table must be 256 characters long", "ValueError");
                var delete = arguments.Count > 1 ? RequireBytes("translate", arguments[1]) : null;
                var result = new List<byte>(value.Length);
                foreach (var current in value)
                {
                    if (delete is not null && Contains(delete, current))
                        continue;
                    result.Add(table[current]);
                }
                return Wrap([.. result]);
            }
        ).WithSignature(["table"], [null], positionalOnly: 1);

    // ---- type-level methods -----------------------------------------------------------------

    /// <summary>
    /// `bytes.fromhex`: whitespace separates pairs but never splits one, and a pair that is
    /// not two hex digits is reported at the position the pair starts.
    /// </summary>
    internal static PythonBuiltinFunctionValue CreateFromHex() =>
        new(
            "fromhex",
            (arguments, span) =>
            {
                if (arguments.Count != 1)
                {
                    throw Fault(
                        "bytes.fromhex() takes exactly one argument "
                            + $"({arguments.Count} given)",
                        "TypeError"
                    );
                }
                string source;
                if (arguments[0] is PythonTextValue text)
                    source = text.Value;
                else if (
                    PythonBufferProtocol.TryGetContent(
                        arguments[0],
                        PythonBufferProtocol.Simple,
                        span,
                        out var contents
                    )
                )
                    source = Latin1(contents, span);
                else
                    throw Fault(
                        "fromhex() argument must be str or bytes-like, "
                            + $"not {ManagedObjectProtocols.GetTypeName(arguments[0])}",
                        "TypeError"
                    );
                return Wrap(ParseHex(source, span));
            },
            (_, names, _, span) =>
                throw Fault(
                    names.Count == 0
                        ? "bytes.fromhex() takes exactly one argument (0 given)"
                        : "bytes.fromhex() takes no keyword arguments",
                    "TypeError"
                )
        );

    /// <summary>Bytes are read as Latin-1 so every byte maps to one character.</summary>
    private static string Latin1(byte[] value, TextSpan span)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var current in value)
            builder.Append((char)current);
        return builder.ToString();
    }

    /// <summary>
    /// Whitespace separates pairs but is never allowed inside one, and the position reported
    /// is that of the offending character rather than of the pair it belongs to.
    /// </summary>
    private static byte[] ParseHex(string source, TextSpan span)
    {
        var result = new List<byte>(source.Length / 2);
        var index = 0;
        while (index < source.Length)
        {
            while (index < source.Length && IsAsciiWhitespace(source[index]))
                index++;
            if (index >= source.Length)
                break;
            if (!TryHexDigit(source[index], out var high))
                throw NonHex(index);
            index++;
            if (index >= source.Length)
                throw Fault(
                    "fromhex() arg must contain an even number of hexadecimal digits",
                    "ValueError"
                );
            if (!TryHexDigit(source[index], out var low))
                throw NonHex(index);
            index++;
            result.Add((byte)(high << 4 | low));
        }
        return [.. result];
    }

    private static PythonRuntimeException NonHex(int position) =>
        Fault(
            $"non-hexadecimal number found in fromhex() arg at position {position}",
            "ValueError"
        );

    private static bool IsAsciiWhitespace(char value) =>
        value is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';

    private static bool TryHexDigit(char value, out int digit)
    {
        if (value is >= '0' and <= '9')
        {
            digit = value - '0';
            return true;
        }
        if (value is >= 'a' and <= 'f')
        {
            digit = value - 'a' + 10;
            return true;
        }
        if (value is >= 'A' and <= 'F')
        {
            digit = value - 'A' + 10;
            return true;
        }
        digit = 0;
        return false;
    }

    /// <summary>`bytes.maketrans(from, to)`: the identity table with `from` mapped onto `to`.</summary>
    internal static PythonBuiltinFunctionValue CreateMakeTrans() =>
        new(
            "maketrans",
            (arguments, span) =>
            {
                if (arguments.Count != 2)
                {
                    throw Fault(
                        $"maketrans expected 2 arguments, got {arguments.Count}",
                        "TypeError"
                    );
                }
                var from = RequireBytes("maketrans", arguments[0]);
                var to = RequireBytes("maketrans", arguments[1]);
                if (from.Length != to.Length)
                    throw Fault("maketrans arguments must have same length", "ValueError");
                var table = new byte[256];
                for (var index = 0; index < 256; index++)
                    table[index] = (byte)index;
                for (var index = 0; index < from.Length; index++)
                    table[from[index]] = to[index];
                return Wrap(table);
            },
            (_, names, _, span) =>
                throw Fault(
                    names.Count == 0
                        ? "maketrans expected 2 arguments, got 0"
                        : "bytes.maketrans() takes no keyword arguments",
                    "TypeError"
                )
        );

    // ---- shared plumbing --------------------------------------------------------------------

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int minimum,
        int maximum
    )
    {
        if (arguments.Count >= minimum && arguments.Count <= maximum)
            return;
        throw Fault(PythonMethodWording.Arity("bytes", name, arguments.Count), "TypeError");
    }

    private static PythonByteSequenceValue Wrap(byte[] value) =>
        PythonByteSequenceValue.Create(value);

    private static PythonTruthValue Truth(bool value) => PythonTruthValue.FromBoolean(value);

    private static PythonRuntimeException Fault(string message, string pythonType) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, pythonType);
}
