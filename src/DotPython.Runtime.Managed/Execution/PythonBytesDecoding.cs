using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Decodes the runtime's qualified byte codecs without CLR fallback grouping.
/// Error boundaries and BOM behavior follow CPython 3.14.7 unicodeobject.c.
/// </summary>
internal static class PythonBytesDecoding
{
    private const int MaximumTextLength = 10_000_000;

    internal static string Decode(
        byte[] bytes,
        int codePage,
        bool detectByteOrderMark,
        string errors,
        TextSpan span
    )
    {
        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
        return new Decoder(bytes, codePage, detectByteOrderMark, errors, span).Decode();
    }

    private sealed class Decoder(
        byte[] bytes,
        int codePage,
        bool detectByteOrderMark,
        string errors,
        TextSpan span
    )
    {
        private readonly StringBuilder _text = new(Math.Min(bytes.Length, MaximumTextLength));
        private int _position;
        private int _nextWork;
        private string _encoding = string.Empty;
        private bool _bigEndian;

        internal string Decode()
        {
            _encoding = codePage switch
            {
                65001 => "utf-8",
                20127 => "ascii",
                28591 => "iso8859-1",
                1200 => detectByteOrderMark && !BitConverter.IsLittleEndian
                    ? "utf-16-be"
                    : "utf-16-le",
                1201 => "utf-16-be",
                _ => throw new ArgumentOutOfRangeException(nameof(codePage)),
            };
            _bigEndian =
                codePage == 1201
                || codePage == 1200 && detectByteOrderMark && !BitConverter.IsLittleEndian;
            if (codePage == 1200 && detectByteOrderMark && bytes.Length >= 2)
            {
                if (bytes[0] == 0xff && bytes[1] == 0xfe)
                {
                    _bigEndian = false;
                    _position = 2;
                }
                else if (bytes[0] == 0xfe && bytes[1] == 0xff)
                {
                    _bigEndian = true;
                    _position = 2;
                }
                _encoding = _bigEndian ? "utf-16-be" : "utf-16-le";
            }

            while (_position < bytes.Length)
            {
                CheckProgress();
                switch (codePage)
                {
                    case 65001:
                        DecodeUtf8();
                        break;
                    case 1200:
                    case 1201:
                        DecodeUtf16();
                        break;
                    default:
                        if (codePage == 20127 && bytes[_position] >= 128)
                            HandleError(_position + 1, "ordinal not in range(128)");
                        else
                            _text.Append((char)bytes[_position++]);
                        break;
                }
                CheckLength();
            }
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            return _text.ToString();
        }

        private void CheckLength()
        {
            if (_text.Length > MaximumTextLength)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "The decoded text exceeds the supported size.",
                    span,
                    "OverflowError"
                );
        }

        private void CheckProgress()
        {
            if (_position < _nextWork)
                return;
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            // A scalar or malformed prefix consumes at most four bytes. Leave
            // room for it so checkpoints are never more than 256 bytes apart.
            _nextWork = _position > int.MaxValue - 252 ? int.MaxValue : _position + 252;
        }

        private void DecodeUtf8()
        {
            var first = bytes[_position];
            if (first < 128)
            {
                _text.Append((char)first);
                ++_position;
                return;
            }
            var length = first switch
            {
                >= 0xc2 and <= 0xdf => 2,
                >= 0xe0 and <= 0xef => 3,
                >= 0xf0 and <= 0xf4 => 4,
                _ => 0,
            };
            if (length == 0)
            {
                HandleError(_position + 1, "invalid start byte");
                return;
            }

            var scalar = first & (0x7f >> length);
            for (var offset = 1; offset < length; ++offset)
            {
                if (offset >= bytes.Length - _position)
                {
                    HandleError(bytes.Length, "unexpected end of data");
                    return;
                }
                var next = bytes[_position + offset];
                if (
                    next is < 0x80 or > 0xbf
                    || offset == 1
                        && (
                            first == 0xe0 && next < 0xa0
                            || first == 0xed && next >= 0xa0
                            || first == 0xf0 && next < 0x90
                            || first == 0xf4 && next >= 0x90
                        )
                )
                {
                    HandleError(_position + offset, "invalid continuation byte");
                    return;
                }
                scalar = (scalar << 6) | (next & 0x3f);
            }
            _text.Append(char.ConvertFromUtf32(scalar));
            _position += length;
        }

        private void DecodeUtf16()
        {
            if (bytes.Length - _position < 2)
            {
                HandleError(bytes.Length, "truncated data");
                return;
            }
            var first = ReadUtf16(_position);
            if (first is < 0xd800 or > 0xdfff)
            {
                _text.Append((char)first);
                _position += 2;
                return;
            }
            if (first >= 0xdc00)
            {
                HandleError(_position + 2, "illegal encoding");
                return;
            }
            if (bytes.Length - _position < 4)
            {
                HandleError(bytes.Length, "unexpected end of data");
                return;
            }
            var second = ReadUtf16(_position + 2);
            if (second is < 0xdc00 or > 0xdfff)
            {
                HandleError(_position + 2, "illegal UTF-16 surrogate");
                return;
            }
            _text.Append((char)first);
            _text.Append((char)second);
            _position += 4;
        }

        private int ReadUtf16(int index) =>
            _bigEndian
                ? (bytes[index] << 8) | bytes[index + 1]
                : bytes[index] | (bytes[index + 1] << 8);

        private void HandleError(int end, string reason)
        {
            switch (errors)
            {
                case "ignore":
                    break;
                case "replace":
                    _text.Append('\ufffd');
                    break;
                case "backslashreplace":
                    for (var index = _position; index < end; ++index)
                    {
                        _text.Append("\\x");
                        _text.Append(
                            bytes[index]
                                .ToString("x2", System.Globalization.CultureInfo.InvariantCulture)
                        );
                        CheckLength();
                    }
                    break;
                case "strict":
                    // CPython snapshots the entire input for decoder-generated
                    // errors. A directly called exception constructor retains it.
                    var source = new byte[bytes.Length];
                    for (var offset = 0; offset < bytes.Length; offset += 256)
                    {
                        UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                        bytes
                            .AsSpan(offset, Math.Min(256, bytes.Length - offset))
                            .CopyTo(source.AsSpan(offset));
                    }
                    var exception = new PythonExceptionValue("UnicodeDecodeError", string.Empty);
                    PythonUnicodeErrors.Initialize(
                        exception,
                        [
                            new PythonTextValue(_encoding),
                            PythonByteSequenceValue.Create(source),
                            PythonWholeNumberValue.Create(_position),
                            PythonWholeNumberValue.Create(end),
                            new PythonTextValue(reason),
                        ],
                        span
                    );
                    throw new PythonRuntimeException(
                        "DPY4003",
                        PythonUnicodeErrors.Format(exception, span),
                        span,
                        "UnicodeDecodeError"
                    )
                    {
                        ExceptionValue = exception,
                    };
                case "xmlcharrefreplace":
                case "namereplace":
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        "don't know how to handle UnicodeDecodeError in error callback",
                        span,
                        "TypeError"
                    );
                default:
                    // CPython looks handlers up only when malformed input actually
                    // needs one. Valid data accepts even an unregistered name.
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"unknown error handler name '{errors}'",
                        span,
                        "LookupError"
                    );
            }
            _position = end;
        }
    }
}
