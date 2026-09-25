using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Registrations owned by an engine's module registry, shared by its executions.</summary>
internal sealed class PythonCodecErrorRegistry
{
    private readonly Dictionary<string, PythonValue> _builtins = PythonCodecs.CreateHandlers();
    private readonly Dictionary<string, PythonValue> _registered = new(StringComparer.Ordinal);

    internal void Register(string name, PythonValue handler) => _registered[name] = handler;

    internal PythonValue Lookup(string name, TextSpan span) =>
        _registered.TryGetValue(name, out var handler) || _builtins.TryGetValue(name, out handler)
            ? handler
            : throw PythonCodecs.UnknownHandler(name, span);

    internal bool IsBuiltin(string name, PythonValue handler) =>
        _builtins.TryGetValue(name, out var builtin) && ReferenceEquals(builtin, handler);
}
