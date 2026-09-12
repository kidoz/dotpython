namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonGlobalNamespace
{
    private readonly Dictionary<string, PythonGlobalSlot> _slots = new(StringComparer.Ordinal);
    private int _indexedVersion;
    private long _keysVersion;
    private bool _hasNonStringKeys;

    internal PythonGlobalNamespace(PythonDictionaryValue? dictionary = null)
    {
        Dictionary = dictionary ?? new PythonDictionaryValue([]);
        _indexedVersion = -1;
    }

    internal PythonDictionaryValue Dictionary { get; }

    internal long KeysVersion
    {
        get
        {
            SynchronizeSlots();
            // A non-string key may change its equality behavior without a dictionary
            // mutation. Neither successful global loads nor builtin fallbacks can cache it.
            if (_hasNonStringKeys)
            {
                _keysVersion = unchecked(_keysVersion + 1);
            }

            return _keysVersion;
        }
    }

    internal IEnumerable<(string Name, PythonValue Value)> Entries =>
        Dictionary
            .Items.Where(item => item.Key is PythonTextValue)
            .Select(item => (((PythonTextValue)item.Key).Value, item.Value));

    internal void SetValue(string name, PythonValue value)
    {
        if (TryGetSlot(name, out var slot))
        {
            slot.Value = value;
            return;
        }

        SynchronizeSlots();
        var item = new PythonDictionaryItemValue(new PythonTextValue(name), value);
        Dictionary.AddItem(item);
        _slots.Add(name, new PythonGlobalSlot(item));
        _indexedVersion = Dictionary.SizeVersion;
        _keysVersion = unchecked(_keysVersion + 1);
    }

    internal bool TryGetSlot(string name, out PythonGlobalSlot slot)
    {
        SynchronizeSlots();
        if (!_hasNonStringKeys)
        {
            return _slots.TryGetValue(name, out slot!);
        }

        if (
            ManagedObjectProtocols.TryFindDictionaryItem(
                Dictionary,
                new PythonTextValue(name),
                out var item
            )
        )
        {
            slot = new PythonGlobalSlot(item);
            return true;
        }

        slot = null!;
        return false;
    }

    internal bool Remove(string name)
    {
        if (!TryGetSlot(name, out var slot))
        {
            return false;
        }

        Dictionary.RemoveItem(slot.Item);
        if (_hasNonStringKeys)
        {
            SynchronizeSlots();
        }
        else
        {
            _slots.Remove(name);
            _indexedVersion = Dictionary.SizeVersion;
            _keysVersion = unchecked(_keysVersion + 1);
        }

        return true;
    }

    internal bool TryGetValue(string name, out PythonValue value)
    {
        if (TryGetSlot(name, out var slot))
        {
            value = slot.Value;
            return true;
        }

        value = null!;
        return false;
    }

    internal bool RemoveValue(string name, PythonValue expectedValue) =>
        TryGetValue(name, out var value) && ReferenceEquals(value, expectedValue) && Remove(name);

    private void SynchronizeSlots()
    {
        if (_indexedVersion == Dictionary.SizeVersion)
        {
            return;
        }

        // Dictionary writes update the same item observed by cached global loads.
        // Structural mutations invalidate those caches through KeysVersion.
        _slots.Clear();
        _hasNonStringKeys = false;
        foreach (var item in Dictionary.Items)
        {
            if (item.Key is PythonTextValue name)
            {
                _slots.Add(name.Value, new PythonGlobalSlot(item));
            }
            else
            {
                _hasNonStringKeys = true;
            }
        }

        _indexedVersion = Dictionary.SizeVersion;
        _keysVersion = unchecked(_keysVersion + 1);
    }
}

internal sealed class PythonGlobalSlot(PythonDictionaryItemValue item)
{
    internal PythonDictionaryItemValue Item { get; } = item;

    internal PythonValue Value
    {
        get => Item.Value;
        set => Item.Value = value;
    }
}
