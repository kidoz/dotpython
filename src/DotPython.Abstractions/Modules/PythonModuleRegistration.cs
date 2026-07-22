namespace DotPython;

/// <summary>Describes a generated typed module client that can be registered with a host.</summary>
/// <typeparam name="TService">The generated module service interface.</typeparam>
public sealed class PythonModuleRegistration<TService>
    where TService : class
{
    private readonly Func<IDotPythonModuleProvider, TService> _createClient;

    /// <summary>Initializes a typed module registration.</summary>
    public PythonModuleRegistration(
        PythonModuleDefinition definition,
        Func<IDotPythonModuleProvider, TService> createClient
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(createClient);
        Definition = definition;
        _createClient = createClient;
    }

    /// <summary>Gets the immutable compiled module definition.</summary>
    public PythonModuleDefinition Definition { get; }

    /// <summary>Gets the Python import name.</summary>
    public string ModuleName => Definition.Contract.ModuleName;

    /// <summary>Gets how mutable module state is scoped.</summary>
    public Contracts.PythonModuleStatePolicy StatePolicy => Definition.Contract.StatePolicy;

    /// <summary>Creates a typed client backed by a host-owned module provider.</summary>
    public TService CreateClient(IDotPythonModuleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _createClient(provider)
            ?? throw new InvalidOperationException(
                $"The typed client factory for module '{ModuleName}' returned null."
            );
    }
}
