using DotPython.Language;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.ParserTests;

public sealed class LanguageFrontEndTests
{
    [Fact]
    public void CurrentLanguageVersion_TargetsPython314()
    {
        Assert.Equal(new Version(3, 14), PythonLanguageVersion.Current);
    }

    [Fact]
    public void SupportedArtifactVersions_ContainCurrentAndRejectOthers()
    {
        Assert.Equal(
            PythonLanguageVersion.Current,
            Assert.Single(PythonLanguageVersion.SupportedArtifactVersions)
        );
        Assert.False(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 15)));
        Assert.False(PythonLanguageVersion.IsSupportedArtifactVersion(new Version(3, 15, 0)));
        // The single-current-target 3.14 release does not accept newer language stamps.
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
