namespace DotPython.ParserGenerator;

using DotPython.ParserGenerator.Generation;

/// <summary>Describes the grammar features required by the DotPython parser generator.</summary>
public static class ParserGeneratorCapabilities
{
    /// <summary>Gets whether parsing expression grammar generation is the selected strategy.</summary>
    public static bool SupportsParsingExpressionGrammars => true;

    /// <summary>Gets the version of the executable Python grammar subset.</summary>
    public const int ExecutableSubsetVersion = 18;

    /// <summary>Gets the SHA-256 fingerprint of the pinned grammar input.</summary>
    public static string GeneratedGrammarFingerprint => GeneratedPythonGrammar.SourceSha256;

    /// <summary>Gets the number of rules compiled into the executable grammar.</summary>
    public static int GeneratedGrammarRuleCount => GeneratedPythonGrammar.RuleCount;
}
