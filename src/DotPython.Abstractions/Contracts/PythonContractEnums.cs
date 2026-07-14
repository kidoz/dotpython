namespace DotPython.Contracts;

/// <summary>Describes how a Python module's mutable state is scoped.</summary>
public enum PythonModuleStatePolicy
{
    /// <summary>No state policy was specified.</summary>
    None = 0,

    /// <summary>Each DotPython runtime owns one module instance.</summary>
    PerRuntime = 1,

    /// <summary>Each DotPython session owns a separate module instance.</summary>
    PerSession = 2,
}

/// <summary>Describes whether an exported Python function returns an awaitable.</summary>
public enum PythonCallShape
{
    /// <summary>No call shape was specified.</summary>
    None = 0,

    /// <summary>The Python function completes synchronously.</summary>
    Synchronous = 1,

    /// <summary>The Python function is declared with <c>async def</c>.</summary>
    Asynchronous = 2,
}

/// <summary>Describes how a Python function parameter can be supplied.</summary>
public enum PythonParameterKind
{
    /// <summary>No parameter kind was specified.</summary>
    None = 0,

    /// <summary>The parameter can be supplied positionally or by keyword.</summary>
    PositionalOrKeyword = 1,
}
