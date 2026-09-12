namespace DotPython.Runtime.Managed.Execution;

/// <summary>Live insertion-ordered items and the compact-dictionary entry positions seen by cursors.</summary>
internal sealed record PythonDictionaryValue : PythonValue
{
    private readonly List<PythonDictionaryItemValue> _items = [];
    private readonly List<PythonDictionaryItemValue?> _entries = [];
    private long _usable;
    private bool _stringKeys = true;

    internal PythonDictionaryValue(List<PythonDictionaryItemValue> items)
    {
        foreach (var item in items)
            AddItem(item);
    }

    internal IReadOnlyList<PythonDictionaryItemValue> Items => _items;
    internal int EntryCount => _entries.Count;
    internal int SizeVersion { get; private set; }

    internal PythonDictionaryItemValue? GetEntry(int position) => _entries[position];

    private void EnsureKeyKind(PythonValue key)
    {
        if (_stringKeys && key is not PythonTextValue)
        {
            Compact();
            _stringKeys = false;
        }
    }

    internal void AddItem(PythonDictionaryItemValue item)
    {
        EnsureKeyKind(item.Key);
        if (_usable == 0)
            Compact();
        _entries.Add(item);
        _items.Add(item);
        _usable--;
        SizeVersion++;
    }

    internal void RemoveItem(PythonDictionaryItemValue item, bool trimTail = false)
    {
        var position = _entries.IndexOf(item);
        if (position < 0)
            return;
        _entries[position] = null;
        _items.Remove(item);
        // popitem reduces the entry high-water mark but does not restore insertion capacity.
        if (trimTail)
            _entries.RemoveRange(position, _entries.Count - position);
        SizeVersion++;
    }

    internal void ClearItems()
    {
        _entries.Clear();
        _items.Clear();
        _usable = 0;
        _stringKeys = true;
        SizeVersion++;
    }

    private void Compact()
    {
        // CPython 3.14 combined tables grow to a power of two >= used*3;
        // only two thirds of the slots admit entries. Deleted slots consume capacity
        // until compaction, even when the active dictionary size has been restored.
        long size = 8;
        while (size < (long)_items.Count * 3)
            size *= 2;
        _entries.Clear();
        _entries.AddRange(_items);
        _usable = size * 2 / 3 - _items.Count;
    }

    internal override string ToDisplayString()
    {
        if (!PythonRepresentationGuard.TryEnter(this))
            return "{...}";
        try
        {
            return $"{{{string.Join(", ", _items.Select(item => $"{item.Key.ToRepresentationString()}: {item.Value.ToRepresentationString()}"))}}}";
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }
}
