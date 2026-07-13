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
    public void ParserGenerator_UsesParsingExpressionGrammarStrategy()
    {
        Assert.True(ParserGeneratorCapabilities.SupportsParsingExpressionGrammars);
    }
}
