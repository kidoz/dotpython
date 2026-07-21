using System.Collections.ObjectModel;
using DotPython.Language.Ast;
using DotPython.Language.Text;

namespace DotPython.Compiler.Binding;

public sealed class PythonBoundScope
{
    private readonly IList<string> _cellVariableNames;
    private readonly Dictionary<string, int> _cellVariableIndexes;
    private readonly HashSet<string> _cellVariableNameSet;
    private readonly IList<string> _freeVariableNames;
    private readonly Dictionary<string, int> _freeVariableIndexes;
    private readonly HashSet<string> _freeVariableNameSet;
    private readonly HashSet<string> _localNameSet;
    private readonly Dictionary<string, int> _localNameIndexes;

    internal PythonBoundScope(
        PythonScopeKind kind,
        string name,
        PythonFunctionDefinitionStatement? definition,
        IList<string> parameters,
        IList<string> localNames,
        IList<string> referencedNames,
        IList<string> cellVariableNames,
        IList<string> freeVariableNames,
        IList<PythonBoundScope> children,
        IReadOnlyDictionary<string, TextSpan> declaredGlobalNames,
        IReadOnlyDictionary<string, TextSpan> declaredNonlocalNames
    )
    {
        Kind = kind;
        Name = name;
        Definition = definition;
        DeclaredGlobalNames = declaredGlobalNames;
        DeclaredNonlocalNames = declaredNonlocalNames;
        Parameters = new ReadOnlyCollection<string>(parameters);
        LocalNames = new ReadOnlyCollection<string>(localNames);
        ReferencedNames = new ReadOnlyCollection<string>(referencedNames);
        CellVariableNames = new ReadOnlyCollection<string>(cellVariableNames);
        FreeVariableNames = new ReadOnlyCollection<string>(freeVariableNames);
        _cellVariableNames = cellVariableNames;
        _freeVariableNames = freeVariableNames;
        Children = new ReadOnlyCollection<PythonBoundScope>(children);
        _localNameSet = new HashSet<string>(localNames, StringComparer.Ordinal);
        _localNameIndexes = localNames
            .Select((localName, index) => (localName, index))
            .ToDictionary(item => item.localName, item => item.index, StringComparer.Ordinal);
        _cellVariableNameSet = new HashSet<string>(cellVariableNames, StringComparer.Ordinal);
        _cellVariableIndexes = cellVariableNames
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        _freeVariableNameSet = new HashSet<string>(freeVariableNames, StringComparer.Ordinal);
        _freeVariableIndexes = freeVariableNames
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
    }

    public PythonScopeKind Kind { get; }

    public string Name { get; }

    public IReadOnlyList<string> Parameters { get; }

    public IReadOnlyList<string> LocalNames { get; }

    public IReadOnlyList<string> ReferencedNames { get; }

    public IReadOnlyList<string> CellVariableNames { get; }

    public IReadOnlyList<string> FreeVariableNames { get; }

    public IReadOnlyList<PythonBoundScope> Children { get; }

    internal PythonFunctionDefinitionStatement? Definition { get; }

    internal IReadOnlyDictionary<string, TextSpan> DeclaredGlobalNames { get; }

    internal IReadOnlyDictionary<string, TextSpan> DeclaredNonlocalNames { get; }

    internal bool IsDeclaredGlobal(string name) => DeclaredGlobalNames.ContainsKey(name);

    internal bool IsLocal(string name) => _localNameSet.Contains(name);

    internal int GetLocalIndex(string name) => _localNameIndexes[name];

    internal bool IsCellVariable(string name) => _cellVariableNameSet.Contains(name);

    internal bool IsFreeVariable(string name) => _freeVariableNameSet.Contains(name);

    internal int GetCellVariableIndex(string name) => _cellVariableIndexes[name];

    internal int GetFreeVariableIndex(string name) => _freeVariableIndexes[name];

    internal void AddCellVariable(string name)
    {
        if (_cellVariableNameSet.Add(name))
        {
            _cellVariableIndexes.Add(name, _cellVariableNames.Count);
            _cellVariableNames.Add(name);
        }
    }

    internal void AddFreeVariable(string name)
    {
        if (_freeVariableNameSet.Add(name))
        {
            _freeVariableIndexes.Add(name, _freeVariableNames.Count);
            _freeVariableNames.Add(name);
        }
    }

    internal void OrderCellVariablesByLocalDeclaration()
    {
        if (_cellVariableNames.Count < 2)
        {
            return;
        }

        var orderedNames = LocalNames.Where(_cellVariableNameSet.Contains).ToArray();
        _cellVariableNames.Clear();
        _cellVariableIndexes.Clear();
        for (var index = 0; index < orderedNames.Length; index++)
        {
            _cellVariableNames.Add(orderedNames[index]);
            _cellVariableIndexes.Add(orderedNames[index], index);
        }
    }
}
