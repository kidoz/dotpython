using System.Text;

namespace DotPython.Worker.Host;

internal sealed class BoundedTextWriter(int maxBytes) : TextWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly StringBuilder _builder = new();
    private int _byteCount;

    public override Encoding Encoding => Utf8;

    public override void Write(char value)
    {
        Span<char> text = stackalloc char[1];
        text[0] = value;
        Append(text);
    }

    public override void Write(string? value)
    {
        if (value is not null)
        {
            Append(value.AsSpan());
        }
    }

    public override string ToString() => _builder.ToString();

    private void Append(ReadOnlySpan<char> value)
    {
        var bytes = Utf8.GetByteCount(value);
        if (bytes > maxBytes - _byteCount)
        {
            throw new WorkerOutputLimitException();
        }

        _builder.Append(value);
        _byteCount += bytes;
    }
}

internal sealed class WorkerOutputLimitException : Exception
{
    public WorkerOutputLimitException()
        : base("Worker output exceeded the configured limit.") { }

    public WorkerOutputLimitException(string message)
        : base(message) { }

    public WorkerOutputLimitException(string message, Exception innerException)
        : base(message, innerException) { }
}
