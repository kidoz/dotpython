namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonGlobalNamespace
{
    private readonly Dictionary<string, PythonGlobalSlot> _slots = new(StringComparer.Ordinal);

    internal long KeysVersion { get; private set; }

    internal void SetValue(string name, PythonValue value)
    {
        if (_slots.TryGetValue(name, out var slot))
        {
            slot.Value = value;
            return;
        }

        _slots.Add(name, new PythonGlobalSlot(value));
        KeysVersion = unchecked(KeysVersion + 1);
    }

    internal bool TryGetSlot(string name, out PythonGlobalSlot slot) =>
        _slots.TryGetValue(name, out slot!);

    internal bool TryGetValue(string name, out PythonValue value)
    {
        if (_slots.TryGetValue(name, out var slot))
        {
            value = slot.Value;
            return true;
        }

        value = null!;
        return false;
    }

    internal bool RemoveValue(string name, PythonValue expectedValue)
    {
        if (!_slots.TryGetValue(name, out var slot) || !ReferenceEquals(slot.Value, expectedValue))
        {
            return false;
        }

        _slots.Remove(name);
        KeysVersion = unchecked(KeysVersion + 1);
        return true;
    }
}

internal sealed class PythonGlobalSlot(PythonValue value)
{
    internal PythonValue Value { get; set; } = value;
}
