using System.Runtime.CompilerServices;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Weak direct-subclass links, independent of sessions and custom MROs.</summary>
internal static class PythonTypeHierarchy
{
    private sealed class Children
    {
        internal List<WeakReference<PythonManagedTypeValue>> Entries { get; } = [];
    }

    private static readonly ConditionalWeakTable<PythonValue, Children> Subclasses = new();

    internal static void Register(PythonManagedTypeValue type, PythonTupleValue bases)
    {
        foreach (var parent in bases.Elements)
        {
            var children = Subclasses.GetValue(parent, static _ => new());
            lock (children)
            {
                children.Entries.RemoveAll(entry => !entry.TryGetTarget(out _));
                if (
                    !children.Entries.Any(entry =>
                        entry.TryGetTarget(out var target) && ReferenceEquals(target, type)
                    )
                )
                    children.Entries.Add(new(type));
            }
        }
    }

    internal static void ReplaceBases(
        PythonManagedTypeValue type,
        PythonTupleValue oldBases,
        PythonTupleValue newBases
    )
    {
        foreach (var parent in oldBases.Elements)
        {
            if (!Subclasses.TryGetValue(parent, out var children))
                continue;
            lock (children)
                children.Entries.RemoveAll(entry =>
                    !entry.TryGetTarget(out var target) || ReferenceEquals(target, type)
                );
        }
        Register(type, newBases);
    }

    internal static List<PythonManagedTypeValue> Snapshot(PythonManagedTypeValue type)
    {
        var result = new List<PythonManagedTypeValue>();
        if (!Subclasses.TryGetValue(type, out var children))
            return result;
        lock (children)
        {
            children.Entries.RemoveAll(entry => !entry.TryGetTarget(out _));
            foreach (var entry in children.Entries)
                if (entry.TryGetTarget(out var child))
                    result.Add(child);
        }
        return result;
    }
}
