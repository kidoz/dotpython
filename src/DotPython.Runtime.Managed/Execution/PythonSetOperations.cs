using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Set operations reuse stored hashes when consuming another exact set.</summary>
internal static class PythonSetOperations
{
    internal enum Operation
    {
        Union,
        Intersection,
        Difference,
        SymmetricDifference,
    }

    internal static PythonSetValue Create(PythonValue source, TextSpan span = default)
    {
        if (source is PythonSetValue set)
            return set.Copy(frozen: false, span: span);
        var result = new PythonSetValue([]);
        Update(result, source, span);
        return result;
    }

    internal static void Update(PythonSetValue target, PythonValue source, TextSpan span = default)
    {
        if (ReferenceEquals(target, source))
            return;
        if (source is PythonSetValue set)
        {
            if (target.Entries.Count == 0)
            {
                target.ReplaceEntries(set, span);
                return;
            }
            for (var index = 0; index < set.Entries.Count; index++)
            {
                CheckWork(span);
                var entry = set.Entries[index];
                ManagedObjectProtocols.AddToSetKnownHash(target, entry.Value, entry.Hash, span);
            }
            return;
        }
        if (source is PythonDictionaryValue dictionary)
        {
            for (var index = 0; index < dictionary.Items.Count; index++)
            {
                CheckWork(span);
                var entry = dictionary.Items[index];
                ManagedObjectProtocols.AddToSetKnownHash(target, entry.Key, entry.KeyHash, span);
            }
            return;
        }
        var iterator = ManagedObjectProtocols.GetIterator(source, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var value, span))
        {
            CheckWork(span);
            ManagedObjectProtocols.AddToSet(target, value, span);
        }
    }

    internal static PythonSetValue Combine(
        PythonSetValue left,
        PythonValue right,
        Operation operation,
        TextSpan span = default
    )
    {
        switch (operation)
        {
            case Operation.Union:
            {
                var result = left.Copy(span: span);
                if (!ReferenceEquals(left, right))
                    Update(result, right, span);
                return result;
            }
            case Operation.Intersection:
                return Intersect(left, right, span);
            case Operation.Difference:
            {
                var rightCount = right switch
                {
                    PythonSetValue set => set.Entries.Count,
                    PythonDictionaryValue dictionary => dictionary.Items.Count,
                    _ => -1,
                };
                if (rightCount >= 0 && (left.Entries.Count >> 2) <= rightCount)
                {
                    var result = new PythonSetValue([]) { IsFrozen = left.IsFrozen };
                    for (var index = 0; index < left.Entries.Count; index++)
                    {
                        CheckWork(span);
                        var entry = left.Entries[index];
                        var found = right is PythonSetValue set
                            ? ManagedObjectProtocols.FindSetEntry(
                                set,
                                entry.Value,
                                entry.Hash,
                                span
                            ) >= 0
                            : ManagedObjectProtocols.TryFindDictionaryItem(
                                (PythonDictionaryValue)right,
                                entry.Value,
                                entry.Hash,
                                out _
                            );
                        if (!found)
                            ManagedObjectProtocols.AddToSetKnownHash(
                                result,
                                entry.Value,
                                entry.Hash,
                                span
                            );
                    }
                    return result;
                }
                var copy = left.Copy(span: span);
                DifferenceUpdate(copy, right, span);
                return copy;
            }
            default:
            {
                var result = new PythonSetValue([]) { IsFrozen = left.IsFrozen };
                Update(result, right, span);
                SymmetricDifferenceUpdate(result, left, span);
                return result;
            }
        }
    }

    internal static PythonSetValue Intersect(
        PythonSetValue left,
        PythonValue right,
        TextSpan span = default
    )
    {
        if (ReferenceEquals(left, right))
            return left.Copy(span: span);
        var result = new PythonSetValue([]) { IsFrozen = left.IsFrozen };
        if (right is PythonSetValue set)
        {
            var source = set.Entries.Count <= left.Entries.Count ? set : left;
            var target = ReferenceEquals(source, set) ? left : set;
            for (var index = 0; index < source.Entries.Count; index++)
            {
                CheckWork(span);
                var entry = source.Entries[index];
                if (ManagedObjectProtocols.FindSetEntry(target, entry.Value, entry.Hash, span) >= 0)
                    ManagedObjectProtocols.AddToSetKnownHash(result, entry.Value, entry.Hash, span);
            }
            return result;
        }
        var iterator = ManagedObjectProtocols.GetIterator(right, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var value, span))
        {
            CheckWork(span);
            var hash = ManagedObjectProtocols.ComputePythonHash(value, span);
            if (ManagedObjectProtocols.FindSetEntry(left, value, hash, span) >= 0)
            {
                ManagedObjectProtocols.AddToSetKnownHash(result, value, hash, span);
                if (result.Entries.Count >= left.Entries.Count)
                    break;
            }
        }
        return result;
    }

    internal static void DifferenceUpdate(
        PythonSetValue target,
        PythonValue source,
        TextSpan span = default
    )
    {
        if (ReferenceEquals(target, source))
        {
            target.ClearEntries();
            return;
        }
        if (source is PythonSetValue set)
        {
            for (var index = 0; index < set.Entries.Count; index++)
            {
                CheckWork(span);
                var entry = set.Entries[index];
                var found = ManagedObjectProtocols.FindSetEntry(
                    target,
                    entry.Value,
                    entry.Hash,
                    span
                );
                if (found >= 0)
                    target.RemoveEntry(found);
            }
            return;
        }
        var iterator = ManagedObjectProtocols.GetIterator(source, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var value, span))
        {
            CheckWork(span);
            var hash = ManagedObjectProtocols.ComputePythonHash(value, span);
            var found = ManagedObjectProtocols.FindSetEntry(target, value, hash, span);
            if (found >= 0)
                target.RemoveEntry(found);
        }
    }

    internal static void SymmetricDifferenceUpdate(
        PythonSetValue target,
        PythonValue source,
        TextSpan span = default
    )
    {
        if (ReferenceEquals(target, source))
        {
            target.ClearEntries();
            return;
        }
        if (source is PythonDictionaryValue dictionary)
        {
            for (var index = 0; index < dictionary.Items.Count; index++)
            {
                CheckWork(span);
                var entry = dictionary.Items[index];
                var found = ManagedObjectProtocols.FindSetEntry(
                    target,
                    entry.Key,
                    entry.KeyHash,
                    span
                );
                if (found >= 0)
                    target.RemoveEntry(found);
                else
                    ManagedObjectProtocols.AddToSetKnownHash(
                        target,
                        entry.Key,
                        entry.KeyHash,
                        span
                    );
            }
            return;
        }
        var set = source as PythonSetValue ?? Create(source, span);
        for (var index = 0; index < set.Entries.Count; index++)
        {
            CheckWork(span);
            var entry = set.Entries[index];
            var found = ManagedObjectProtocols.FindSetEntry(target, entry.Value, entry.Hash, span);
            if (found >= 0)
                target.RemoveEntry(found);
            else
                ManagedObjectProtocols.AddToSetKnownHash(target, entry.Value, entry.Hash, span);
        }
    }

    internal static PythonTruthValue CompareOrdered(
        PythonSetValue left,
        PythonSetValue right,
        PythonRichComparison comparison,
        TextSpan span = default
    ) =>
        PythonTruthValue.FromBoolean(
            comparison switch
            {
                PythonRichComparison.LessThan => left.Entries.Count < right.Entries.Count
                    && IsSubset(left, right, span),
                PythonRichComparison.LessThanOrEqual => IsSubset(left, right, span),
                PythonRichComparison.GreaterThan => left.Entries.Count > right.Entries.Count
                    && IsSubset(right, left, span),
                PythonRichComparison.GreaterThanOrEqual => IsSubset(right, left, span),
                _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
            }
        );

    internal static bool IsSubset(PythonSetValue left, PythonValue right, TextSpan span = default)
    {
        if (right is not PythonSetValue set)
            return Intersect(left, right, span).Entries.Count == left.Entries.Count;
        if (left.Entries.Count > set.Entries.Count)
            return false;
        for (var index = 0; index < left.Entries.Count; index++)
        {
            CheckWork(span);
            var entry = left.Entries[index];
            if (ManagedObjectProtocols.FindSetEntry(set, entry.Value, entry.Hash, span) < 0)
                return false;
        }
        return true;
    }

    internal static bool IsSuperset(PythonSetValue left, PythonValue right, TextSpan span = default)
    {
        if (right is PythonSetValue set)
            return IsSubset(set, left, span);
        var iterator = ManagedObjectProtocols.GetIterator(right, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var value, span))
        {
            CheckWork(span);
            var hash = ManagedObjectProtocols.ComputePythonHash(value, span);
            if (ManagedObjectProtocols.FindSetEntry(left, value, hash, span) < 0)
                return false;
        }
        return true;
    }

    internal static bool IsDisjoint(PythonSetValue left, PythonValue right, TextSpan span = default)
    {
        if (ReferenceEquals(left, right))
            return left.Entries.Count == 0;
        if (right is PythonSetValue set)
        {
            var source = set.Entries.Count <= left.Entries.Count ? set : left;
            var target = ReferenceEquals(source, set) ? left : set;
            for (var index = 0; index < source.Entries.Count; index++)
            {
                CheckWork(span);
                var entry = source.Entries[index];
                if (ManagedObjectProtocols.FindSetEntry(target, entry.Value, entry.Hash, span) >= 0)
                    return false;
            }
            return true;
        }
        var iterator = ManagedObjectProtocols.GetIterator(right, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var value, span))
        {
            CheckWork(span);
            var hash = ManagedObjectProtocols.ComputePythonHash(value, span);
            if (ManagedObjectProtocols.FindSetEntry(left, value, hash, span) >= 0)
                return false;
        }
        return true;
    }

    private static void CheckWork(TextSpan span) =>
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
}
