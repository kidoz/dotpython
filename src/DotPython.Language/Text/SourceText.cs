namespace DotPython.Language.Text;

public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public SourceText(string text, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        Content = text;
        FilePath = filePath;
        _lineStarts = FindLineStarts(text);
    }

    public string Content { get; }

    public string? FilePath { get; }

    public int Length => Content.Length;

    public int LineCount => _lineStarts.Length;

    public char this[int index] => Content[index];

    public string GetText(TextSpan span)
    {
        ValidateSpan(span);
        return Content.Substring(span.Start, span.Length);
    }

    public LinePosition GetLinePosition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Length);

        var line = Array.BinarySearch(_lineStarts, position);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return new LinePosition(line, position - _lineStarts[line]);
    }

    public TextSpan GetLineSpan(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);

        var start = _lineStarts[line];
        var end = line + 1 < LineCount ? _lineStarts[line + 1] : Length;
        return TextSpan.FromBounds(start, end);
    }

    private static int[] FindLineStarts(string text)
    {
        var starts = new List<int> { 0 };

        for (var position = 0; position < text.Length; position++)
        {
            switch (text[position])
            {
                case '\r' when position + 1 < text.Length && text[position + 1] == '\n':
                    position++;
                    starts.Add(position + 1);
                    break;
                case '\r':
                case '\n':
                    starts.Add(position + 1);
                    break;
            }
        }

        return [.. starts];
    }

    private void ValidateSpan(TextSpan span)
    {
        if (span.End > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(span));
        }
    }
}
