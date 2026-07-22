namespace DotPython;

/// <summary>
/// Provides invocation access to registered modules without transferring ownership of module handles.
/// </summary>
/// <remarks>
/// A provider owns each activated module until the provider is asynchronously disposed. Module
/// activation is single-flight per definition, and an activation failure remains sticky for that
/// provider lifetime. Canceling one caller stops that caller's wait without canceling activation
/// that may still be required by other callers.
/// </remarks>
public interface IDotPythonModuleProvider : IAsyncDisposable
{
    /// <summary>Loads and validates a module without invoking one of its exports.</summary>
    ValueTask WarmUpAsync(
        PythonModuleDefinition definition,
        CancellationToken cancellationToken = default
    );

    /// <summary>Invokes a function that does not return a value.</summary>
    ValueTask InvokeAsync(
        PythonModuleDefinition definition,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken = default
    );

    /// <summary>Invokes a function and converts its result to <typeparamref name="TResult"/>.</summary>
    ValueTask<TResult> InvokeAsync<TResult>(
        PythonModuleDefinition definition,
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken = default
    );
}
