using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Strict encoder validation with Python character offsets and error ranges.</summary>
internal static class PythonTextEncoding
{
    private const int MaximumBytes = 10_000_000;

    internal static byte[] EncodeStrict(PythonTextValue source, Encoding codec, TextSpan span)
    {
        var text = source.Value;
        var codePage = codec.CodePage;
        var preamble = codec.GetPreamble();
        var encoding = codePage switch
        {
            20127 => "ascii",
            28591 => "latin-1",
            65001 => "utf-8",
            1200 => preamble.Length == 0 ? "utf-16-le" : "utf-16",
            1201 => "utf-16-be",
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };
        var position = 0;
        var offset = 0;
        var byteCount = preamble.Length;
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
        while (offset < text.Length)
        {
            if ((position & 127) == 0)
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            var character = ReadCharacter(text, offset, out var width);
            if (IsInvalid(character, codePage))
            {
                var end = position + 1;
                var next = offset + width;
                // UTF-16 reports the first surrogate; UTF-8 and the ordinal
                // encoders report the complete consecutive invalid run.
                if (codePage is not (1200 or 1201))
                {
                    while (next < text.Length)
                    {
                        if (((end - position) & 127) == 0)
                            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                        var following = ReadCharacter(text, next, out var nextWidth);
                        if (!IsInvalid(following, codePage))
                            break;
                        next += nextWidth;
                        ++end;
                    }
                }
                var reason = codePage switch
                {
                    20127 => "ordinal not in range(128)",
                    28591 => "ordinal not in range(256)",
                    _ => "surrogates not allowed",
                };
                var exception = new PythonExceptionValue("UnicodeEncodeError", string.Empty);
                PythonUnicodeErrors.Initialize(
                    exception,
                    [
                        new PythonTextValue(encoding),
                        source,
                        PythonWholeNumberValue.Create(position),
                        PythonWholeNumberValue.Create(end),
                        new PythonTextValue(reason),
                    ],
                    span
                );
                throw new PythonRuntimeException(
                    "DPY4003",
                    PythonUnicodeErrors.Format(exception, span),
                    span,
                    "UnicodeEncodeError"
                )
                {
                    ExceptionValue = exception,
                };
            }
            byteCount += codePage switch
            {
                20127 or 28591 => 1,
                1200 or 1201 => width * 2,
                _ => character < 0x80 ? 1
                : character < 0x800 ? 2
                : character < 0x10000 ? 3
                : 4,
            };
            if (byteCount > MaximumBytes)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "encoded bytes exceed the managed materialization limit",
                    span,
                    "OverflowError"
                );
            offset += width;
            ++position;
        }
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
        var result = new byte[byteCount];
        preamble.CopyTo(result, 0);
        codec.GetBytes(text.AsSpan(), result.AsSpan(preamble.Length));
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
        return result;
    }

    private static bool IsInvalid(int character, int codePage) =>
        codePage switch
        {
            20127 => character >= 128,
            28591 => character >= 256,
            _ => character is >= 0xd800 and <= 0xdfff,
        };

    private static int ReadCharacter(string text, int offset, out int width)
    {
        if (
            char.IsHighSurrogate(text[offset])
            && offset + 1 < text.Length
            && char.IsLowSurrogate(text[offset + 1])
        )
        {
            width = 2;
            return char.ConvertToUtf32(text[offset], text[offset + 1]);
        }
        width = 1;
        return text[offset];
    }
}
