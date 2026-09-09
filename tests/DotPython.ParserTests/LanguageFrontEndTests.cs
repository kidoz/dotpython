using DotPython.Language;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.ParserTests;

public sealed class LanguageFrontEndTests
{
    [Fact]
    public void CurrentLanguageVersion_TargetsPython315()
    {
        Assert.Equal(new Version(3, 15), PythonLanguageVersion.Current);
    }

    [Fact]
    public void SupportedArtifactVersions_ContainCurrentAndRejectOthers()
    {
        Assert.Contains(
            PythonLanguageVersion.Current,
            PythonLanguageVersion.SupportedArtifactVersions
        );
        Assert.True(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 15)));
        Assert.True(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 15, 0)));
        // ADR-015 rule 2: the previous version stays accepted for one release line.
        Assert.True(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 14)));
        Assert.True(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 14, 6)));
        Assert.False(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 13)));
        Assert.False(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 16)));
    }

    [Fact]
    public void ParserGenerator_UsesParsingExpressionGrammarStrategy()
    {
        Assert.True(ParserGeneratorCapabilities.SupportsParsingExpressionGrammars);
    }
}
