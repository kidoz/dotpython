namespace DotPython;

/// <summary>Activates compiled DotPython modules for generated CLR facades.</summary>
public interface IDotPythonModuleRuntime : IAsyncDisposable
{
    /// <summary>Loads a compiled module and returns an explicitly owned module handle.</summary>
    ValueTask<IDotPythonModule> LoadModuleAsync(
        PythonModuleDefinition definition,
        CancellationToken cancellationToken = default
    );
}
