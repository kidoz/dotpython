using System.Collections.ObjectModel;
using DotPython.Language.Ast;

namespace DotPython.Compiler.Binding;

public sealed class PythonBoundScope
{
    private readonly HashSet<string> _localNameSet;
    private readonly Dictionary<string, int> _localNameIndexes;

    internal PythonBoundScope(
        PythonScopeKind kind,
        string name,
        PythonFunctionDefinitionStatement? definition,
        IList<string> parameters,
        IList<string> localNames,
        IList<string> referencedNames,
        IList<PythonBoundScope> children
    )
    {
        Kind = kind;
        Name = name;
        Definition = definition;
        Parameters = new ReadOnlyCollection<string>(parameters);
        LocalNames = new ReadOnlyCollection<string>(localNames);
        ReferencedNames = new ReadOnlyCollection<string>(referencedNames);
        Children = new ReadOnlyCollection<PythonBoundScope>(children);
        _localNameSet = new HashSet<string>(localNames, StringComparer.Ordinal);
        _localNameIndexes = localNames
            .Select((localName, index) => (localName, index))
            .ToDictionary(item => item.localName, item => item.index, StringComparer.Ordinal);
    }

    public PythonScopeKind Kind { get; }

    public string Name { get; }

    public IReadOnlyList<string> Parameters { get; }

    public IReadOnlyList<string> LocalNames { get; }

    public IReadOnlyList<string> ReferencedNames { get; }

    public IReadOnlyList<PythonBoundScope> Children { get; }

    internal PythonFunctionDefinitionStatement? Definition { get; }

    internal bool IsLocal(string name) => _localNameSet.Contains(name);

    internal int GetLocalIndex(string name) => _localNameIndexes[name];
}
