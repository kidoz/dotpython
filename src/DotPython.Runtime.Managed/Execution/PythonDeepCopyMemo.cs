using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `copy.deepcopy` memo: for each original, the copy already produced for it.
/// </summary>
/// <remarks>
/// The authoritative map uses reference identity, which keeps values that Python
/// cannot hash (lists, dicts, sets) memoizable the way CPython's `id()`-keyed memo
/// does, and keeps lookups O(1). A `dict` view keyed by the same identity tokens
/// `id()` returns is handed to user `__deepcopy__` hooks, so a hook can register a
/// placeholder before recursing exactly as CPython documents. Entries a hook writes
/// through that view are adopted on the next miss.
/// </remarks>
internal sealed class PythonDeepCopyMemo
{
    private readonly Dictionary<PythonValue, PythonValue> _entries = new(
        ReferenceEqualityComparer.Instance
    );
    private int _writtenVersion;

    internal PythonDeepCopyMemo(PythonDictionaryValue dictionary)
    {
        Dictionary = dictionary;
        _writtenVersion = dictionary.SizeVersion;
    }

    /// <summary>Creates an empty memo backed by a fresh dictionary.</summary>
    internal static PythonDeepCopyMemo Create() => new(new PythonDictionaryValue([]));

    /// <summary>The memo as the `dict` handed to user `__deepcopy__` hooks.</summary>
    internal PythonDictionaryValue Dictionary { get; }

    internal bool TryGetValue(PythonValue value, out PythonValue copy)
    {
        if (_entries.TryGetValue(value, out copy!))
        {
            return true;
        }

        return TryAdoptHookEntry(value, out copy!);
    }

    internal void Set(PythonValue value, PythonValue copy, TextSpan span)
    {
        _entries[value] = copy;
        if (IdentityToken(value) is { } token)
        {
            ManagedObjectProtocols.SetDictionaryItem(Dictionary, token, copy, span);
            _writtenVersion = Dictionary.SizeVersion;
        }
    }

    /// <summary>
    /// Picks up an entry a hook wrote into the memo dict itself. Only consulted on a
    /// miss, and only when the dictionary changed since this memo last wrote to it,
    /// so the common path stays a single identity-map lookup.
    /// </summary>
    private bool TryAdoptHookEntry(PythonValue value, out PythonValue copy)
    {
        copy = null!;
        if (_writtenVersion == Dictionary.SizeVersion || IdentityToken(value) is not { } token)
        {
            return false;
        }

        _writtenVersion = Dictionary.SizeVersion;
        foreach (var item in Dictionary.Items)
        {
            if (!ReferenceEquals(item.Key, token))
            {
                continue;
            }

            _entries[value] = item.Value;
            copy = item.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The identity token for a value, or null when no interpreter is running on this
    /// thread. Without a dispatcher the hook-visible dictionary is left empty and the
    /// memo still works for the interpreter's own recursion.
    /// </summary>
    private static PythonValue? IdentityToken(PythonValue value) =>
        UserObjectProtocols.Dispatcher?.GetIdentity(value);
}
