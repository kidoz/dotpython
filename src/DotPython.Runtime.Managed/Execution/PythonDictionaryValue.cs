using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Live insertion-ordered items and the compact-dictionary entry positions seen by cursors.</summary>
internal sealed record PythonDictionaryValue : PythonValue
{
    private readonly List<PythonDictionaryItemValue> _items = [];
    private readonly List<PythonDictionaryItemValue?> _entries = [];
    private long _usable;
    private long _tableSize = 1;
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

    private void EnsureKeyKind(PythonValue key, TextSpan span)
    {
        if (_stringKeys && key is not PythonTextValue)
        {
            Compact(span);
            _stringKeys = false;
        }
    }

    internal void AddItem(PythonDictionaryItemValue item, TextSpan span = default)
    {
        EnsureKeyKind(item.Key, span);
        if (_usable == 0)
            Compact(span);
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
        _tableSize = 1;
        _stringKeys = true;
        SizeVersion++;
    }

    internal static PythonDictionaryValue CreatePresized(
        int itemCount,
        bool stringKeys,
        TextSpan span
    )
    {
        var dictionary = new PythonDictionaryValue([]);
        // CPython's literal factory keeps tiny dictionaries fresh and caps its
        // speculative allocation at 2**17 table slots, regardless of pair count.
        if (itemCount > 5)
        {
            dictionary.Resize(Math.Min(1L << 17, ((long)itemCount * 3 + 1) / 2), span);
            dictionary._stringKeys = stringKeys;
        }
        return dictionary;
    }

    internal static PythonDictionaryValue CreateFromSetStorage(int count, TextSpan span)
    {
        var dictionary = new PythonDictionaryValue([]);
        dictionary.Resize(((long)count * 3 + 1) / 2, span);
        dictionary._stringKeys = false;
        return dictionary;
    }

    internal PythonDictionaryValue CreateFromKeysStorage(TextSpan span)
    {
        var dictionary = new PythonDictionaryValue([]);
        // Exact-dictionary fromkeys compacts and reserves from the live count;
        // unlike a literal it retains the source key kind even when empty.
        dictionary.Resize(((long)_items.Count * 3 + 1) / 2, span);
        dictionary._stringKeys = _stringKeys;
        return dictionary;
    }

    internal PythonDictionaryValue ShallowCopy(TextSpan span = default)
    {
        var copy = new PythonDictionaryValue([]);
        if (_items.Count == 0)
            return copy;
        // Combined-table copies retain holes while at least two thirds of the
        // entry high-water mark is live (integer truncation is observable).
        if (_items.Count >= (long)_entries.Count * 2 / 3)
            copy.CloneStorage(this, span);
        else
            ManagedObjectProtocols.MergeDictionary(copy, this, span);
        return copy;
    }

    internal bool TryCloneForMerge(PythonDictionaryValue source, TextSpan span)
    {
        if (
            _items.Count != 0
            || source._items.Count != source._entries.Count
            || (source._tableSize != 8 && source._tableSize / 2 * 2 / 3 >= source._items.Count)
        )
            return false;
        CloneStorage(source, span);
        return true;
    }

    private void CloneStorage(PythonDictionaryValue source, TextSpan span)
    {
        // Build before publishing so host interruption cannot leave partial storage.
        List<PythonDictionaryItemValue?> entries = [];
        List<PythonDictionaryItemValue> items = [];
        foreach (var item in source._entries)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            var clone = item is null
                ? null
                : new PythonDictionaryItemValue(item.Key, item.Value, item.KeyHash);
            entries.Add(clone);
            if (clone is not null)
                items.Add(clone);
        }
        _entries.Clear();
        _entries.AddRange(entries);
        _items.Clear();
        _items.AddRange(items);
        _tableSize = source._tableSize;
        _usable = source._usable;
        _stringKeys = source._stringKeys;
        SizeVersion++;
    }

    internal void PrepareMerge(PythonDictionaryValue source, TextSpan span)
    {
        // CPython compares total table capacity, not remaining insertion room,
        // and reserves for all source keys even if some will only replace values.
        if (_tableSize * 2 / 3 < source._items.Count)
        {
            Resize((((long)_items.Count + source._items.Count) * 3 + 1) / 2, span);
            _stringKeys &= source._stringKeys;
        }
    }

    private void Compact(TextSpan span) => Resize((long)_items.Count * 3, span);

    private void Resize(long minimumSize, TextSpan span)
    {
        long size = 8;
        while (size < minimumSize)
            size *= 2;
        List<PythonDictionaryItemValue?> entries = [];
        foreach (var item in _items)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            entries.Add(item);
        }
        _entries.Clear();
        _entries.AddRange(entries);
        _tableSize = size;
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
