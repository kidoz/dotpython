using DotPython.Contracts;

namespace DotPython;

/// <summary>Represents one runtime-owned instance of a compiled DotPython module.</summary>
public interface IDotPythonModule : IAsyncDisposable
{
    /// <summary>Gets the static export contract implemented by this module.</summary>
    PythonModuleContract Contract { get; }

    /// <summary>Invokes a contract-defined function that returns no CLR value.</summary>
    ValueTask InvokeAsync(
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken = default
    );

    /// <summary>Invokes a contract-defined function and converts its result to <typeparamref name="TResult"/>.</summary>
    ValueTask<TResult> InvokeAsync<TResult>(
        PythonFunctionInvocation invocation,
        CancellationToken cancellationToken = default
    );
}
