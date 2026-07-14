namespace DotPython;

/// <summary>Identifies the public operation phase that produced a DotPython failure.</summary>
public enum DotPythonFailurePhase
{
    /// <summary>No operation phase was identified.</summary>
    None = 0,

    /// <summary>A compiled module could not be activated.</summary>
    ModuleLoad = 1,

    /// <summary>A contract-defined function could not be invoked.</summary>
    Invocation = 2,

    /// <summary>A value could not cross the CLR/Python boundary.</summary>
    Conversion = 3,
}
