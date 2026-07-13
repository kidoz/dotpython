namespace DotPython.Language.Text;

public readonly record struct LinePosition
{
    public LinePosition(int line, int character)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfNegative(character);

        Line = line;
        Character = character;
    }

    public int Line { get; }

    public int Character { get; }
}
