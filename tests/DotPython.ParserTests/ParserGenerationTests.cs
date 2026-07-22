using DotPython.ParserGenerator;
using DotPython.ParserGenerator.Generation;
using Xunit;

namespace DotPython.ParserTests;

public sealed class ParserGenerationTests
{
    [Fact]
    public void Generate_MatchesCheckedInParserAndIsDeterministic()
    {
        var repositoryRoot = FindRepositoryRoot();
        var grammarPath = Path.Combine(
            repositoryRoot,
            "src",
            "DotPython.ParserGenerator",
            "Grammar",
            "python314-subset.gram"
        );
        var generatedPath = Path.Combine(
            repositoryRoot,
            "src",
            "DotPython.ParserGenerator",
            "Generated",
            "PythonGrammar.g.cs"
        );
        var grammar = File.ReadAllText(grammarPath);

        var first = PythonParserSourceGenerator.Generate(grammar);
        var second = PythonParserSourceGenerator.Generate(grammar);
        var windowsLineEndings = PythonParserSourceGenerator.Generate(
            grammar.ReplaceLineEndings("\r\n")
        );

        Assert.Equal(first, second);
        Assert.Equal(first, windowsLineEndings);
        Assert.Equal(File.ReadAllText(generatedPath), first);
        Assert.Equal(63, ParserGeneratorCapabilities.GeneratedGrammarRuleCount);
        Assert.Equal(64, ParserGeneratorCapabilities.GeneratedGrammarFingerprint.Length);
        Assert.Contains(
            ParserGeneratorCapabilities.GeneratedGrammarFingerprint,
            first,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("statement: missing\nfile: statement ENDMARKER")]
    [InlineData("file: NAME\nfile: NUMBER")]
    [InlineData("statement: NAME")]
    [InlineData("file NAME")]
    public void Generate_RejectsInvalidGrammar(string grammar)
    {
        Assert.Throws<InvalidDataException>(() => PythonParserSourceGenerator.Generate(grammar));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotPython.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The DotPython repository root could not be located.");
    }
}
