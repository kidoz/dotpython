namespace DotPython.Interop.Contracts;

/// <summary>Defines stable diagnostics produced by static Python contract compilation.</summary>
public static class PythonContractDiagnosticCodes
{
    /// <summary>The stub contains invalid contract syntax.</summary>
    public const string InvalidSyntax = "DPY5001";

    /// <summary>The stub contains a declaration outside the supported export subset.</summary>
    public const string UnsupportedDeclaration = "DPY5002";

    /// <summary>An exported signature is missing a required annotation.</summary>
    public const string MissingAnnotation = "DPY5003";

    /// <summary>An annotation cannot be represented by the initial CLR contract mapping.</summary>
    public const string UnsupportedType = "DPY5004";

    /// <summary>The stub maps multiple declarations to the same public name.</summary>
    public const string DuplicateExport = "DPY5005";

    /// <summary>An imported DTO annotation has no approved CLR mapping.</summary>
    public const string UnresolvedExternalType = "DPY5006";

    /// <summary>A generated public CLR identifier or type is invalid or non-CLS-compliant.</summary>
    public const string InvalidClrSurface = "DPY5007";
}
