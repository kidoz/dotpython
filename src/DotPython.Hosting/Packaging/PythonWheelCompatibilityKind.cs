namespace DotPython.Hosting.Packaging;

/// <summary>Identifies the native compatibility contract advertised by a wheel.</summary>
public enum PythonWheelCompatibilityKind
{
    /// <summary>The artifact is not a structurally valid wheel.</summary>
    Invalid,

    /// <summary>The wheel is platform-independent and contains no native binaries.</summary>
    PurePython,

    /// <summary>The wheel targets the CPython Stable ABI (<c>abi3</c>).</summary>
    CpythonStableAbi,

    /// <summary>The wheel targets a version-specific CPython ABI.</summary>
    CpythonVersionSpecific,

    /// <summary>The wheel contains an HPy Universal ABI extension.</summary>
    HpyUniversal,

    /// <summary>The wheel contains or advertises an unrecognized native binary contract.</summary>
    UnknownBinary,
}
