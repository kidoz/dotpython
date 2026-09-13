using System.Globalization;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Bounded text encoding with Python character offsets and error handlers.</summary>
internal static class PythonTextEncoding
{
    private const int MaximumBytes = 10_000_000;

    internal static byte[] Encode(
        PythonTextValue source,
        Encoding codec,
        string errors,
        TextSpan span
    )
    {
        var encoder = new Encoder(source, codec, errors, span);
        // Count before allocating. The second pass follows exactly the same
        // replacement and encoding path, checking current work in both passes.
        var length = encoder.Run(null);
        var result = new byte[length];
        encoder.Run(result);
        return result;
    }

    private sealed class Encoder(
        PythonTextValue source,
        Encoding codec,
        string errors,
        TextSpan span
    )
    {
        private readonly int _codePage = codec.CodePage;
        private readonly byte[] _preamble = codec.GetPreamble();
        private byte[]? _output;
        private int _length;

        internal int Run(byte[]? output)
        {
            _output = output;
            _length = 0;
            CheckWork();
            foreach (var value in _preamble)
                WriteByte(value);
            var text = source.Value;
            var position = 0;
            for (var offset = 0; offset < text.Length; ++position)
            {
                if ((position & 127) == 0)
                    CheckWork();
                var character = ReadCharacter(text, offset, out var width);
                if (!IsInvalid(character))
                    WriteCharacter(character);
                else
                    HandleError(character, position, offset, width);
                offset += width;
            }
            CheckWork();
            return _length;
        }

        private void HandleError(int character, int position, int offset, int width)
        {
            switch (errors)
            {
                case "ignore":
                    return;
                case "replace":
                    WriteCharacter('?');
                    return;
                case "backslashreplace":
                    var escaped =
                        character <= 0xff ? $"\\x{character:x2}"
                        : character <= 0xffff ? $"\\u{character:x4}"
                        : $"\\U{character:x8}";
                    WriteReplacement(escaped);
                    return;
                case "namereplace":
                    // CPython's codec name lookup includes its private alias
                    // and named-sequence entries; unicodedata.name does not.
                    var name = PythonUnicodeNames.GetName(
                        character,
                        includeAliasesAndSequences: true
                    );
                    if (name is null)
                        goto case "backslashreplace";
                    WriteReplacement("\\N{" + name + "}");
                    return;
                case "xmlcharrefreplace":
                    WriteReplacement("&#" + character.ToString(CultureInfo.InvariantCulture) + ";");
                    return;
                case "surrogatepass" when _codePage is 65001 or 1200 or 1201:
                    WriteCharacter(character);
                    return;
                case "surrogateescape"
                    when _codePage is not (1200 or 1201) && character is >= 0xdc80 and <= 0xdcff:
                    WriteByte(character - 0xdc00);
                    return;
                case "strict":
                case "surrogatepass":
                case "surrogateescape":
                    ThrowEncodingError(position, offset, width);
                    return;
                default:
                    // Handler lookup is deferred until unencodable input needs it.
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"unknown error handler name '{errors}'",
                        span,
                        "LookupError"
                    );
            }
        }

        private void ThrowEncodingError(int position, int offset, int width)
        {
            var end = position + 1;
            // UTF-16 reports the first surrogate. Other represented codecs
            // include the remainder of the consecutive unencodable run.
            if (_codePage is not (1200 or 1201))
            {
                var next = offset + width;
                while (next < source.Value.Length)
                {
                    if (((end - position) & 127) == 0)
                        CheckWork();
                    var following = ReadCharacter(source.Value, next, out var nextWidth);
                    if (!IsInvalid(following))
                        break;
                    next += nextWidth;
                    ++end;
                }
            }
            var encoding = _codePage switch
            {
                20127 => "ascii",
                28591 => "latin-1",
                65001 => "utf-8",
                1200 => _preamble.Length == 0 ? "utf-16-le" : "utf-16",
                1201 => "utf-16-be",
                _ => throw new InvalidOperationException("Unsupported encoding code page."),
            };
            var reason = _codePage switch
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

        private void WriteReplacement(string replacement)
        {
            // Replacement text belongs to the target codec, including UTF-16
            // byte order. The stream preamble has already been emitted once.
            foreach (var character in replacement)
                WriteCharacter(character);
        }

        private void WriteCharacter(int character)
        {
            if (_codePage is 20127 or 28591)
                WriteByte(character);
            else if (_codePage is 1200 or 1201)
            {
                if (character > 0xffff)
                {
                    var supplementary = character - 0x10000;
                    WriteCodeUnit(0xd800 | (supplementary >> 10));
                    WriteCodeUnit(0xdc00 | (supplementary & 0x3ff));
                }
                else
                    WriteCodeUnit(character);
            }
            else if (character < 0x80)
                WriteByte(character);
            else if (character < 0x800)
            {
                WriteByte(0xc0 | (character >> 6));
                WriteByte(0x80 | (character & 0x3f));
            }
            else if (character < 0x10000)
            {
                WriteByte(0xe0 | (character >> 12));
                WriteByte(0x80 | ((character >> 6) & 0x3f));
                WriteByte(0x80 | (character & 0x3f));
            }
            else
            {
                WriteByte(0xf0 | (character >> 18));
                WriteByte(0x80 | ((character >> 12) & 0x3f));
                WriteByte(0x80 | ((character >> 6) & 0x3f));
                WriteByte(0x80 | (character & 0x3f));
            }
        }

        private void WriteCodeUnit(int character)
        {
            if (_codePage == 1201)
            {
                WriteByte(character >> 8);
                WriteByte(character & 0xff);
            }
            else
            {
                WriteByte(character & 0xff);
                WriteByte(character >> 8);
            }
        }

        private void WriteByte(int value)
        {
            if (_length == MaximumBytes)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "encoded bytes exceed the managed materialization limit",
                    span,
                    "OverflowError"
                );
            if (_output is not null)
                _output[_length] = (byte)value;
            ++_length;
        }

        private bool IsInvalid(int character) =>
            _codePage switch
            {
                20127 => character >= 128,
                28591 => character >= 256,
                _ => character is >= 0xd800 and <= 0xdfff,
            };

        private void CheckWork() => UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
    }

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
