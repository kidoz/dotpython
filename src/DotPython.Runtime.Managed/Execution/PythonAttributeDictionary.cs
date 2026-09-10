using System.Collections;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>String attribute access backed by the Python-visible namespace dictionary.</summary>
internal sealed class PythonAttributeDictionary : IEnumerable<KeyValuePair<string, PythonValue>>
{
    private readonly PythonGlobalNamespace _namespace;

    internal PythonAttributeDictionary(PythonDictionaryValue? dictionary = null)
    {
        _namespace = new PythonGlobalNamespace(dictionary);
    }

    internal PythonDictionaryValue Dictionary => _namespace.Dictionary;

    internal PythonValue this[string name]
    {
        get =>
            _namespace.TryGetValue(name, out var value)
                ? value
                : throw new KeyNotFoundException(name);
        set => _namespace.SetValue(name, value);
    }

    internal bool TryGetValue(string name, out PythonValue value) =>
        _namespace.TryGetValue(name, out value);

    internal bool Remove(string name) => _namespace.Remove(name);

    public IEnumerator<KeyValuePair<string, PythonValue>> GetEnumerator() =>
        _namespace
            .Entries.Select(item => new KeyValuePair<string, PythonValue>(item.Name, item.Value))
            .GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
