// The sorting algorithm is adapted from CPython 3.14.7 Objects/listobject.c:
// https://github.com/python/cpython/blob/v3.14.7/Objects/listobject.c
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release
// This adaptation uses managed paired arrays, Python protocol dispatch, and
// exception-safe restoration of temporary runs instead of reference-counted slices.

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Stable powersort with CPython's run detection, insertion and galloping order.
/// The caller owns key creation, reverse handling, and list storage detachment.
/// </summary>
internal static class PythonStableSort
{
    internal static void Sort(PythonValue[] values, PythonValue[] keys, TextSpan span)
    {
        if (values.Length != keys.Length)
            throw new ArgumentException("Sort keys and values must have the same length.");
        if (values.Length > 1)
            new SortState(values, keys, span).Sort();
    }

    private readonly record struct Slice(PythonValue[] Keys, PythonValue[] Values)
    {
        internal void CopyTo(int source, Slice target, int destination, int length)
        {
            Array.Copy(Keys, source, target.Keys, destination, length);
            if (!ReferenceEquals(Keys, Values))
                Array.Copy(Values, source, target.Values, destination, length);
        }

        internal void CopyOneTo(int source, Slice target, int destination)
        {
            target.Keys[destination] = Keys[source];
            if (!ReferenceEquals(Keys, Values))
                target.Values[destination] = Values[source];
        }
    }

    private readonly record struct Run(int Start, int Length, int Power = 0);

    private sealed class SortState(PythonValue[] values, PythonValue[] keys, TextSpan span)
    {
        private const int MinimumGallop = 7;
        private readonly Slice _items = new(keys, values);
        private readonly List<Run> _pending = [];
        private Slice _temporary;
        private int _minimumGallop = MinimumGallop;
        private PythonManagedTypeValue? _managedKeyType;
        private bool _tupleKeys;

        internal void Sort()
        {
            SelectComparison();
            var minRun = MinimumRun(keys.Length);
            var start = 0;
            while (start < keys.Length)
            {
                var remaining = keys.Length - start;
                var length = CountRun(start, remaining);
                if (length < minRun)
                {
                    var forced = Math.Min(remaining, minRun);
                    BinarySort(start, forced, length);
                    length = forced;
                }

                if (_pending.Count > 0)
                {
                    var last = _pending[^1];
                    var power = Power(last.Start, last.Length, length, keys.Length);
                    while (_pending.Count > 1 && _pending[^2].Power > power)
                        MergeAt(_pending.Count - 2);
                    _pending[^1] = _pending[^1] with { Power = power };
                }
                _pending.Add(new Run(start, length));
                start += length;
            }

            while (_pending.Count > 1)
            {
                var index = _pending.Count - 2;
                if (index > 0 && _pending[index - 1].Length < _pending[index + 1].Length)
                    --index;
                MergeAt(index);
            }
        }

        private void SelectComparison()
        {
            var first = keys[0];
            var tuples = first is PythonTupleValue { Elements.Length: > 0 };
            if (tuples)
                first = ((PythonTupleValue)first).Elements[0];
            if (first is not PythonManagedObjectValue firstManaged)
                return;
            for (var index = 0; index < keys.Length; ++index)
            {
                if ((index & 255) == 0)
                    CheckWork();
                var key = keys[index];
                if (tuples)
                {
                    if (key is not PythonTupleValue { Elements.Length: > 0 } tuple)
                        return;
                    key = tuple.Elements[0];
                }
                if (
                    key is not PythonManagedObjectValue managed
                    || !ReferenceEquals(managed.Type, firstManaged.Type)
                )
                    return;
            }
            _managedKeyType = firstManaged.Type;
            _tupleKeys = tuples;
        }

        private bool Less(PythonValue left, PythonValue right)
        {
            CheckWork();
            bool result;
            if (_tupleKeys)
            {
                var leftItems = ((PythonTupleValue)left).Elements;
                var rightItems = ((PythonTupleValue)right).Elements;
                var index = 0;
                for (; index < leftItems.Length && index < rightItems.Length; ++index)
                {
                    CheckWork();
                    if (
                        !ReferenceEquals(leftItems[index], rightItems[index])
                        && !ManagedObjectProtocols.IsTrue(
                            ManagedObjectProtocols.RichCompareValue(
                                leftItems[index],
                                rightItems[index],
                                PythonRichComparison.Equal,
                                span
                            )
                        )
                    )
                        break;
                }
                result =
                    index >= leftItems.Length || index >= rightItems.Length
                        ? leftItems.Length < rightItems.Length
                    : index == 0 ? LessHomogeneous(leftItems[index], rightItems[index])
                    : LessGeneric(leftItems[index], rightItems[index]);
            }
            else
            {
                result = LessHomogeneous(left, right);
            }
            CheckWork();
            return result;
        }

        private bool LessHomogeneous(PythonValue left, PythonValue right)
        {
            // CPython's homogeneous rich-comparison path probes the forward slot
            // before its generic NotImplemented fallback. This extra probe is
            // observable when user __lt__ declines the comparison.
            if (
                left is PythonManagedObjectValue managed
                && ReferenceEquals(managed.Type, _managedKeyType)
            )
            {
                var result = UserObjectProtocols.InvokeSortRichCompare(managed, right, span);
                if (result is not PythonNotImplementedValue)
                    return ManagedObjectProtocols.IsTrue(result);
            }
            return LessGeneric(left, right);
        }

        private bool LessGeneric(PythonValue left, PythonValue right) =>
            ManagedObjectProtocols.IsTrue(
                ManagedObjectProtocols.RichCompareValue(
                    left,
                    right,
                    PythonRichComparison.LessThan,
                    span
                )
            );

        private void CheckWork() => UserObjectProtocols.Dispatcher?.CheckIterationWork(span);

        private void Reverse(int start, int length)
        {
            var end = start + length - 1;
            for (var index = start; index < end; ++index, --end)
            {
                if ((index & 255) == 0)
                    CheckWork();
                (_items.Keys[index], _items.Keys[end]) = (_items.Keys[end], _items.Keys[index]);
                if (!ReferenceEquals(keys, values))
                    (_items.Values[index], _items.Values[end]) = (
                        _items.Values[end],
                        _items.Values[index]
                    );
            }
        }

        private int CountRun(int start, int remaining)
        {
            var count = 1;
            for (; count < remaining; ++count)
            {
                if (Less(keys[start + count], keys[start + count - 1]))
                    break;
            }
            if (count == remaining)
                return count;
            if (count > 1)
            {
                if (Less(keys[start], keys[start + count - 1]))
                    return count;
                Reverse(start, count);
            }
            ++count;

            var equal = 0;
            for (; count < remaining; ++count)
            {
                if (Less(keys[start + count], keys[start + count - 1]))
                {
                    ReverseEqualTail();
                }
                else
                {
                    if (Less(keys[start + count - 1], keys[start + count]))
                        break;
                    ++equal;
                }
            }
            ReverseEqualTail();
            Reverse(start, count);

            for (; count < remaining; ++count)
            {
                if (Less(keys[start + count], keys[start + count - 1]))
                    break;
            }
            return count;

            void ReverseEqualTail()
            {
                if (equal == 0)
                    return;
                ++equal;
                Reverse(start + count - equal, equal);
                equal = 0;
            }
        }

        private void BinarySort(int start, int length, int sorted)
        {
            for (var index = Math.Max(sorted, 1); index < length; ++index)
            {
                var pivotKey = keys[start + index];
                var pivotValue = values[start + index];
                var left = 0;
                var right = index;
                do
                {
                    var middle = (left + right) >> 1;
                    if (Less(pivotKey, keys[start + middle]))
                        right = middle;
                    else
                        left = middle + 1;
                } while (left < right);
                // No callback or cancellation between moving entries and restoring
                // the pivot: every exceptional exit must retain a permutation.
                _items.CopyTo(start + left, _items, start + left + 1, index - left);
                keys[start + left] = pivotKey;
                values[start + left] = pivotValue;
            }
        }

        private static int MinimumRun(int length)
        {
            var remainder = 0;
            while (length >= 64)
            {
                remainder |= length & 1;
                length >>= 1;
            }
            return length + remainder;
        }

        private static int Power(int start, int firstLength, int secondLength, int total)
        {
            var power = 0;
            long first = 2L * start + firstLength;
            long second = first + firstLength + secondLength;
            while (true)
            {
                ++power;
                if (first >= total)
                {
                    first -= total;
                    second -= total;
                }
                else if (second >= total)
                {
                    return power;
                }
                first <<= 1;
                second <<= 1;
            }
        }

        private int GallopLeft(
            PythonValue key,
            PythonValue[] array,
            int start,
            int length,
            int hint
        )
        {
            var last = 0;
            var offset = 1;
            if (Less(array[start + hint], key))
            {
                var maximum = length - hint;
                while (offset < maximum)
                {
                    if (!Less(array[start + hint + offset], key))
                        break;
                    last = offset;
                    offset = (int)Math.Min(2L * offset + 1, maximum);
                }
                last += hint;
                offset += hint;
            }
            else
            {
                var maximum = hint + 1;
                while (offset < maximum)
                {
                    if (Less(array[start + hint - offset], key))
                        break;
                    last = offset;
                    offset = (int)Math.Min(2L * offset + 1, maximum);
                }
                var previous = last;
                last = hint - offset;
                offset = hint - previous;
            }
            ++last;
            while (last < offset)
            {
                var middle = last + ((offset - last) >> 1);
                if (Less(array[start + middle], key))
                    last = middle + 1;
                else
                    offset = middle;
            }
            return offset;
        }

        private int GallopRight(
            PythonValue key,
            PythonValue[] array,
            int start,
            int length,
            int hint
        )
        {
            var last = 0;
            var offset = 1;
            if (Less(key, array[start + hint]))
            {
                var maximum = hint + 1;
                while (offset < maximum)
                {
                    if (!Less(key, array[start + hint - offset]))
                        break;
                    last = offset;
                    offset = (int)Math.Min(2L * offset + 1, maximum);
                }
                var previous = last;
                last = hint - offset;
                offset = hint - previous;
            }
            else
            {
                var maximum = length - hint;
                while (offset < maximum)
                {
                    if (Less(key, array[start + hint + offset]))
                        break;
                    last = offset;
                    offset = (int)Math.Min(2L * offset + 1, maximum);
                }
                last += hint;
                offset += hint;
            }
            ++last;
            while (last < offset)
            {
                var middle = last + ((offset - last) >> 1);
                if (Less(key, array[start + middle]))
                    offset = middle;
                else
                    last = middle + 1;
            }
            return offset;
        }

        private void MergeAt(int index)
        {
            var first = _pending[index];
            var second = _pending[index + 1];
            var firstStart = first.Start;
            var firstLength = first.Length;
            var secondStart = second.Start;
            var secondLength = second.Length;
            _pending[index] = first with { Length = firstLength + secondLength };
            _pending.RemoveAt(index + 1);

            var skipped = GallopRight(keys[secondStart], keys, firstStart, firstLength, 0);
            firstStart += skipped;
            firstLength -= skipped;
            if (firstLength == 0)
                return;
            secondLength = GallopLeft(
                keys[firstStart + firstLength - 1],
                keys,
                secondStart,
                secondLength,
                secondLength - 1
            );
            if (secondLength == 0)
                return;
            if (firstLength <= secondLength)
                MergeLow(firstStart, firstLength, secondStart, secondLength);
            else
                MergeHigh(firstStart, firstLength, secondStart, secondLength);
        }

        private Slice Temporary(int length)
        {
            if (_temporary.Keys is null || _temporary.Keys.Length < length)
            {
                var temporaryKeys = new PythonValue[length];
                _temporary = new Slice(
                    temporaryKeys,
                    ReferenceEquals(keys, values) ? temporaryKeys : new PythonValue[length]
                );
            }
            return _temporary;
        }

        private void MergeLow(int firstStart, int firstLength, int secondStart, int secondLength)
        {
            var temporary = Temporary(firstLength);
            _items.CopyTo(firstStart, temporary, 0, firstLength);
            var destination = firstStart;
            var first = 0;
            var second = secondStart;
            try
            {
                _items.CopyOneTo(second++, _items, destination++);
                if (--secondLength == 0)
                    return;
                if (firstLength == 1)
                {
                    CopySecond();
                    return;
                }
                var minimumGallop = _minimumGallop;
                while (true)
                {
                    var firstCount = 0;
                    var secondCount = 0;
                    while (true)
                    {
                        if (Less(keys[second], temporary.Keys[first]))
                        {
                            _items.CopyOneTo(second++, _items, destination++);
                            ++secondCount;
                            firstCount = 0;
                            if (--secondLength == 0)
                                return;
                            if (secondCount >= minimumGallop)
                                break;
                        }
                        else
                        {
                            temporary.CopyOneTo(first++, _items, destination++);
                            ++firstCount;
                            secondCount = 0;
                            if (--firstLength == 1)
                            {
                                CopySecond();
                                return;
                            }
                            if (firstCount >= minimumGallop)
                                break;
                        }
                    }
                    ++minimumGallop;
                    do
                    {
                        minimumGallop -= minimumGallop > 1 ? 1 : 0;
                        _minimumGallop = minimumGallop;
                        firstCount = GallopRight(
                            keys[second],
                            temporary.Keys,
                            first,
                            firstLength,
                            0
                        );
                        if (firstCount != 0)
                        {
                            temporary.CopyTo(first, _items, destination, firstCount);
                            destination += firstCount;
                            first += firstCount;
                            firstLength -= firstCount;
                            if (firstLength == 1)
                            {
                                CopySecond();
                                return;
                            }
                            if (firstLength == 0)
                                return;
                        }
                        _items.CopyOneTo(second++, _items, destination++);
                        if (--secondLength == 0)
                            return;
                        secondCount = GallopLeft(
                            temporary.Keys[first],
                            keys,
                            second,
                            secondLength,
                            0
                        );
                        if (secondCount != 0)
                        {
                            _items.CopyTo(second, _items, destination, secondCount);
                            destination += secondCount;
                            second += secondCount;
                            secondLength -= secondCount;
                            if (secondLength == 0)
                                return;
                        }
                        temporary.CopyOneTo(first++, _items, destination++);
                        if (--firstLength == 1)
                        {
                            CopySecond();
                            return;
                        }
                    } while (firstCount >= MinimumGallop || secondCount >= MinimumGallop);
                    _minimumGallop = ++minimumGallop;
                }
            }
            finally
            {
                // On comparison failure, put every unconsumed temporary item back.
                // The remaining second run is already immediately after this gap.
                if (firstLength != 0)
                    temporary.CopyTo(first, _items, destination, firstLength);
            }

            void CopySecond()
            {
                _items.CopyTo(second, _items, destination, secondLength);
                temporary.CopyOneTo(first, _items, destination + secondLength);
                firstLength = 0;
            }
        }

        private void MergeHigh(int firstStart, int firstLength, int secondStart, int secondLength)
        {
            var temporary = Temporary(secondLength);
            _items.CopyTo(secondStart, temporary, 0, secondLength);
            var destination = secondStart + secondLength - 1;
            var first = firstStart + firstLength - 1;
            var second = secondLength - 1;
            try
            {
                _items.CopyOneTo(first--, _items, destination--);
                if (--firstLength == 0)
                    return;
                if (secondLength == 1)
                {
                    CopyFirst();
                    return;
                }
                var minimumGallop = _minimumGallop;
                while (true)
                {
                    var firstCount = 0;
                    var secondCount = 0;
                    while (true)
                    {
                        if (Less(temporary.Keys[second], keys[first]))
                        {
                            _items.CopyOneTo(first--, _items, destination--);
                            ++firstCount;
                            secondCount = 0;
                            if (--firstLength == 0)
                                return;
                            if (firstCount >= minimumGallop)
                                break;
                        }
                        else
                        {
                            temporary.CopyOneTo(second--, _items, destination--);
                            ++secondCount;
                            firstCount = 0;
                            if (--secondLength == 1)
                            {
                                CopyFirst();
                                return;
                            }
                            if (secondCount >= minimumGallop)
                                break;
                        }
                    }
                    ++minimumGallop;
                    do
                    {
                        minimumGallop -= minimumGallop > 1 ? 1 : 0;
                        _minimumGallop = minimumGallop;
                        firstCount =
                            firstLength
                            - GallopRight(
                                temporary.Keys[second],
                                keys,
                                firstStart,
                                firstLength,
                                firstLength - 1
                            );
                        if (firstCount != 0)
                        {
                            destination -= firstCount;
                            first -= firstCount;
                            _items.CopyTo(first + 1, _items, destination + 1, firstCount);
                            firstLength -= firstCount;
                            if (firstLength == 0)
                                return;
                        }
                        temporary.CopyOneTo(second--, _items, destination--);
                        if (--secondLength == 1)
                        {
                            CopyFirst();
                            return;
                        }
                        secondCount =
                            secondLength
                            - GallopLeft(
                                keys[first],
                                temporary.Keys,
                                0,
                                secondLength,
                                secondLength - 1
                            );
                        if (secondCount != 0)
                        {
                            destination -= secondCount;
                            second -= secondCount;
                            temporary.CopyTo(second + 1, _items, destination + 1, secondCount);
                            secondLength -= secondCount;
                            if (secondLength == 1)
                            {
                                CopyFirst();
                                return;
                            }
                            if (secondLength == 0)
                                return;
                        }
                        _items.CopyOneTo(first--, _items, destination--);
                        if (--firstLength == 0)
                            return;
                    } while (firstCount >= MinimumGallop || secondCount >= MinimumGallop);
                    _minimumGallop = ++minimumGallop;
                }
            }
            finally
            {
                if (secondLength != 0)
                    temporary.CopyTo(0, _items, destination - secondLength + 1, secondLength);
            }

            void CopyFirst()
            {
                _items.CopyTo(
                    first - firstLength + 1,
                    _items,
                    destination - firstLength + 1,
                    firstLength
                );
                temporary.CopyOneTo(second, _items, destination - firstLength);
                secondLength = 0;
            }
        }
    }
}
