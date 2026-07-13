namespace DotPython.StdLib;

/// <summary>Describes the support state of a standard-library module.</summary>
public enum StandardLibrarySupportStatus
{
    /// <summary>The module is not supported.</summary>
    Unsupported,

    /// <summary>The module implements only a documented subset.</summary>
    Partial,

    /// <summary>The module is available for experimental use.</summary>
    Experimental,

    /// <summary>The module satisfies its published compatibility tests.</summary>
    Supported,
}
