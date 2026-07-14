using System.Collections.ObjectModel;

namespace DotPython.Contracts;

/// <summary>Describes the typed CLR-facing exports of one Python module.</summary>
public sealed class PythonModuleContract
{
    /// <summary>Initializes a module export contract.</summary>
    public PythonModuleContract(
        int formatVersion,
        string moduleName,
        string clrNamespace,
        string clrTypeName,
        PythonModuleStatePolicy statePolicy,
        IEnumerable<PythonFunctionContract> functions
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(formatVersion);

        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clrNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(clrTypeName);
        if (!ContractNameValidator.IsQualifiedName(moduleName))
        {
            throw new ArgumentException(
                "The module name must be a portable qualified identifier.",
                nameof(moduleName)
            );
        }

        if (!ContractNameValidator.IsQualifiedName(clrNamespace))
        {
            throw new ArgumentException(
                "The CLR namespace must be a portable qualified identifier.",
                nameof(clrNamespace)
            );
        }

        if (!ContractNameValidator.IsIdentifier(clrTypeName))
        {
            throw new ArgumentException(
                "The CLR type name must be a portable identifier.",
                nameof(clrTypeName)
            );
        }

        if (!Enum.IsDefined(statePolicy) || statePolicy == PythonModuleStatePolicy.None)
        {
            throw new ArgumentOutOfRangeException(nameof(statePolicy));
        }

        ArgumentNullException.ThrowIfNull(functions);
        var functionList = functions
            .OrderBy(function => function.PythonName, StringComparer.Ordinal)
            .ToList();
        ValidateFunctions(functionList);

        FormatVersion = formatVersion;
        ModuleName = moduleName;
        ClrNamespace = clrNamespace;
        ClrTypeName = clrTypeName;
        StatePolicy = statePolicy;
        Functions = new ReadOnlyCollection<PythonFunctionContract>(functionList);
    }

    /// <summary>Gets the version of the persisted contract schema.</summary>
    public int FormatVersion { get; }

    /// <summary>Gets the Python import name.</summary>
    public string ModuleName { get; }

    /// <summary>Gets the namespace containing the generated CLR facade.</summary>
    public string ClrNamespace { get; }

    /// <summary>Gets the generated CLR facade type name.</summary>
    public string ClrTypeName { get; }

    /// <summary>Gets how mutable module state is scoped.</summary>
    public PythonModuleStatePolicy StatePolicy { get; }

    /// <summary>Gets exported functions in deterministic Python-name order.</summary>
    public IReadOnlyList<PythonFunctionContract> Functions { get; }

    private static void ValidateFunctions(IList<PythonFunctionContract> functions)
    {
        var pythonNames = new HashSet<string>(StringComparer.Ordinal);
        var clrNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            ArgumentNullException.ThrowIfNull(function);
            if (!pythonNames.Add(function.PythonName))
            {
                throw new ArgumentException(
                    $"Duplicate Python export name '{function.PythonName}'.",
                    nameof(functions)
                );
            }

            if (!clrNames.Add(function.ClrName))
            {
                throw new ArgumentException(
                    $"Duplicate CLR export name '{function.ClrName}'.",
                    nameof(functions)
                );
            }
        }
    }
}
