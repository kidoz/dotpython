using DotPython.Language.Text;
using Xunit;

namespace DotPython.ParserTests;

public sealed class SourceTextTests
{
    [Fact]
    public void SourceText_MapsMixedNewLinesToZeroBasedPositions()
    {
        var source = new SourceText("a\r\nβ\n", "sample.py");

        Assert.Equal(3, source.LineCount);
        Assert.Equal(new LinePosition(0, 0), source.GetLinePosition(0));
        Assert.Equal(new LinePosition(1, 0), source.GetLinePosition(3));
        Assert.Equal(new LinePosition(2, 0), source.GetLinePosition(source.Length));
        Assert.Equal("a\r\n", source.GetText(source.GetLineSpan(0)));
        Assert.Equal("sample.py", source.FilePath);
    }

    [Fact]
    public void TextSpan_UsesHalfOpenBounds()
    {
        var span = TextSpan.FromBounds(2, 5);

        Assert.Equal(2, span.Start);
        Assert.Equal(3, span.Length);
        Assert.Equal(5, span.End);
        Assert.True(span.Contains(2));
        Assert.True(span.Contains(4));
        Assert.False(span.Contains(5));
    }
}
