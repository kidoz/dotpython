using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Traverses UTF-16 without replacing unpaired Python surrogate codepoints.</summary>
internal static class PythonTextTraversal
{
    internal readonly record struct Character(int Value)
    {
        public override string ToString() =>
            Value <= char.MaxValue ? ((char)Value).ToString() : char.ConvertFromUtf32(Value);

        internal UnicodeCategory Category =>
            Value is >= 0xd800 and <= 0xdfff
                ? UnicodeCategory.Surrogate
                : Rune.GetUnicodeCategory(new Rune(Value));
    }

    internal static int Width(string text, int offset) =>
        char.IsHighSurrogate(text[offset])
        && offset + 1 < text.Length
        && char.IsLowSurrogate(text[offset + 1])
            ? 2
            : 1;

    internal static int PreviousOffset(string text, int end) =>
        end >= 2 && char.IsLowSurrogate(text[end - 1]) && char.IsHighSurrogate(text[end - 2])
            ? end - 2
            : end - 1;

    internal static IEnumerable<Character> Enumerate(string text, TextSpan span = default)
    {
        var count = 0;
        for (var offset = 0; offset < text.Length; )
        {
            CheckWork(count++, span);
            var width = Width(text, offset);
            yield return new Character(
                width == 2 ? char.ConvertToUtf32(text[offset], text[offset + 1]) : text[offset]
            );
            offset += width;
        }
    }

    internal static int Count(string text, TextSpan span = default) =>
        Enumerate(text, span).Count();

    internal static PythonTextValue GetItem(PythonTextValue text, PythonValue index, TextSpan span)
    {
        if (index is PythonSliceValue slice)
            return Slice(text, slice, span);
        BigInteger position;
        if (index is PythonWholeNumberValue whole)
            position = whole.Value;
        else if (index is PythonTruthValue truth)
            position = truth.Value ? 1 : 0;
        else if (!UserObjectProtocols.TryConvertToIndex(index, span, out position))
            throw ManagedObjectProtocols.Fault(
                "DPY4011",
                $"string indices must be integers, not '{ManagedObjectProtocols.GetTypeName(index)}'",
                span,
                "TypeError"
            );
        var maximum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MaxValue) : int.MaxValue;
        if (position < -maximum - 1 || position > maximum)
            throw ManagedObjectProtocols.Fault(
                "DPY4012",
                $"cannot fit '{ManagedObjectProtocols.GetTypeName(index)}' into an index-sized integer",
                span,
                "IndexError"
            );
        var count = Count(text.Value, span);
        if (position < 0)
            position += count;
        if (position < 0 || position >= count)
            throw ManagedObjectProtocols.Fault(
                "DPY4012",
                "string index out of range",
                span,
                "IndexError"
            );
        var offset = 0;
        for (var current = 0; current < (int)position; current++)
        {
            CheckWork(current, span);
            offset += Width(text.Value, offset);
        }
        return new PythonTextValue(text.Value.Substring(offset, Width(text.Value, offset)));
    }

    private static PythonTextValue Slice(
        PythonTextValue text,
        PythonSliceValue slice,
        TextSpan span
    )
    {
        var bounds = ManagedObjectProtocols.UnpackSlice(slice, span);
        var count = Count(text.Value, span);
        var (start, stop, step) = ManagedObjectProtocols.AdjustSliceIndices(bounds, count);
        if (start == 0 && stop == count && step == 1)
            return text;
        var builder = new StringBuilder();
        long next = start;
        if (step > 0)
        {
            var offset = 0;
            for (var current = 0; current < stop && next < stop; current++)
            {
                CheckWork(current, span);
                var width = Width(text.Value, offset);
                if (current == next)
                {
                    builder.Append(text.Value, offset, width);
                    next += step;
                }
                offset += width;
            }
        }
        else
        {
            var end = text.Value.Length;
            for (var current = count - 1; current > stop && next > stop; current--)
            {
                CheckWork(count - 1 - current, span);
                var offset = PreviousOffset(text.Value, end);
                if (current == next)
                {
                    builder.Append(text.Value, offset, end - offset);
                    next += step;
                }
                end = offset;
            }
        }
        return new PythonTextValue(builder.ToString());
    }

    private static void CheckWork(int count, TextSpan span)
    {
        if ((count & 255) == 0)
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
    }
}
