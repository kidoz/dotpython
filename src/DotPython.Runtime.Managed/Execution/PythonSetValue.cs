using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed record PythonSetEntry(PythonValue Value, BigInteger Hash);

/// <summary>Owns live set elements together with the hash captured at insertion.</summary>
internal sealed record PythonSetValue : PythonValue
{
    private readonly List<PythonValue> _elements = [];
    private readonly List<PythonSetEntry> _entries = [];

    internal PythonSetValue(IEnumerable<PythonValue> elements)
    {
        foreach (var value in elements)
            ManagedObjectProtocols.AddToSet(this, value, default);
    }

    internal IReadOnlyList<PythonValue> Elements => _elements;
    internal IReadOnlyList<PythonSetEntry> Entries => _entries;
    internal bool IsFrozen { get; init; }
    internal int MutationVersion { get; private set; }
    internal BigInteger? CachedFrozenHash { get; set; }

    internal void AddEntry(PythonValue value, BigInteger hash)
    {
        _entries.Add(new PythonSetEntry(value, hash));
        _elements.Add(value);
        Changed();
    }

    internal void RemoveEntry(int index)
    {
        _entries.RemoveAt(index);
        _elements.RemoveAt(index);
        Changed();
    }

    internal void ClearEntries()
    {
        _entries.Clear();
        _elements.Clear();
        Changed();
    }

    internal PythonSetValue Copy(bool? frozen = null, TextSpan span = default)
    {
        var copy = new PythonSetValue([]) { IsFrozen = frozen ?? IsFrozen };
        copy.ReplaceEntries(this, span);
        return copy;
    }

    internal void ReplaceEntries(PythonSetValue source, TextSpan span = default)
    {
        if (ReferenceEquals(this, source))
            return;
        List<PythonSetEntry> entries = [];
        List<PythonValue> elements = [];
        foreach (var entry in source._entries)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            entries.Add(entry);
            elements.Add(entry.Value);
        }
        _entries.Clear();
        _entries.AddRange(entries);
        _elements.Clear();
        _elements.AddRange(elements);
        Changed();
    }

    private void Changed()
    {
        MutationVersion++;
        CachedFrozenHash = null;
    }

    internal override string ToDisplayString()
    {
        if (Elements.Count == 0)
        {
            return IsFrozen ? "frozenset()" : "set()";
        }

        if (!PythonRepresentationGuard.TryEnter(this))
        {
            return IsFrozen ? "frozenset({...})" : "{...}";
        }

        try
        {
            var elements =
                "{"
                + string.Join(", ", Elements.Select(element => element.ToRepresentationString()))
                + "}";
            return IsFrozen ? $"frozenset({elements})" : elements;
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }
}
