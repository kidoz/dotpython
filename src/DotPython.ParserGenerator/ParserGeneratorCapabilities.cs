namespace DotPython.ParserGenerator;

/// <summary>Describes the grammar features required by the DotPython parser generator.</summary>
public static class ParserGeneratorCapabilities
{
    /// <summary>Gets whether parsing expression grammar generation is the selected strategy.</summary>
    public static bool SupportsParsingExpressionGrammars => true;

    /// <summary>Gets the version of the executable Python grammar subset.</summary>
    public const int ExecutableSubsetVersion = 2;
}
