namespace DotPython;

/// <summary>Identifies the native binary contract implemented by an execution provider.</summary>
public enum PythonNativeExtensionAbi
{
    /// <summary>No native binary contract was specified.</summary>
    None = 0,

    /// <summary>The CPython Stable ABI used by <c>abi3</c> extension modules.</summary>
    CpythonStableAbi = 1,

    /// <summary>The implementation-neutral HPy Universal ABI.</summary>
    HpyUniversal = 2,

    /// <summary>A version- and build-specific CPython ABI.</summary>
    CpythonVersionSpecific = 3,
}

/// <summary>Describes the evidence level behind a native-extension capability.</summary>
public enum PythonNativeExtensionSupportLevel
{
    /// <summary>No support level was specified.</summary>
    None = 0,

    /// <summary>The capability is available only for controlled experiments.</summary>
    Experimental = 1,

    /// <summary>The capability is qualified only for its reported package artifacts.</summary>
    Qualified = 2,
}

/// <summary>Identifies the process boundary at which native extension code executes.</summary>
public enum PythonNativeExtensionExecutionBoundary
{
    /// <summary>No execution boundary was specified.</summary>
    None = 0,

    /// <summary>Native code executes in the application process.</summary>
    InProcess = 1,

    /// <summary>Native code executes in a separately managed worker process.</summary>
    WorkerProcess = 2,
}

/// <summary>Describes the trust policy required to execute a native extension.</summary>
public enum PythonNativeExtensionTrustPolicy
{
    /// <summary>No trust policy was specified.</summary>
    None = 0,

    /// <summary>Only application-trusted native artifacts may use the capability.</summary>
    TrustedOnly = 1,

    /// <summary>The capability requires worker isolation and its configured operating-system policy.</summary>
    WorkerIsolationRequired = 2,
}
