using System.Collections.ObjectModel;
using DotPython.Contracts;

namespace DotPython.Interop.Contracts;

/// <summary>Configures static conversion of one <c>.pyi</c> module into a CLR export contract.</summary>
public sealed class PythonStubContractOptions
{
    private IReadOnlyList<PythonExternalTypeMapping> _externalTypeMappings = [];

    /// <summary>Gets or sets the Python import name represented by the stub.</summary>
    public required string ModuleName { get; init; }

    /// <summary>Gets or sets the namespace for the generated CLR facade.</summary>
    public required string ClrNamespace { get; init; }

    /// <summary>Gets or sets the generated CLR facade type name.</summary>
    public required string ClrTypeName { get; init; }

    /// <summary>Gets or sets how mutable module state is scoped.</summary>
    public PythonModuleStatePolicy StatePolicy { get; init; } = PythonModuleStatePolicy.PerRuntime;

    /// <summary>Gets or sets approved DTO and host-contract mappings.</summary>
    public IReadOnlyList<PythonExternalTypeMapping> ExternalTypeMappings
    {
        get => _externalTypeMappings;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _externalTypeMappings = new ReadOnlyCollection<PythonExternalTypeMapping>([.. value]);
        }
    }
}
