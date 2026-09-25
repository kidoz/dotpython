// Codec recovery semantics adapted from CPython 3.14.7 Objects/unicodeobject.c
// and Objects/stringlib/codecs.h:
// https://github.com/python/cpython/tree/v3.14.7/Objects
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

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
        // User callbacks must run once. A bounded builder replaces the previous
        // count/write replay, which is only valid for side-effect-free handlers.
        return new Encoder(source, codec, errors, span).Run();
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
        private readonly List<byte> _output = [];
        private readonly PythonCodecCallback _callback = new(errors, span);
        private int? _sourceCount;

        internal byte[] Run()
        {
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
                {
                    if (NeedsRegistry(character) && !_callback.IsBuiltin())
                    {
                        (position, offset) = HandleCallback(position, offset, width);
                        // The loop's increment belongs to the ordinary scan only.
                        position--;
                        continue;
                    }
                    HandleError(character, position, offset, width);
                }
                offset += width;
            }
            CheckWork();
            return _output.ToArray();
        }

        private bool NeedsRegistry(int character) =>
            (
                _codePage is 1200 or 1201
                || errors switch
                {
                    "ignore" or "replace" or "backslashreplace" or "xmlcharrefreplace" => false,
                    "strict" => _codePage == 65001,
                    "surrogatepass" => _codePage != 65001,
                    "surrogateescape" => character is < 0xdc80 or > 0xdcff,
                    _ => true,
                }
            );

        private (int Position, int Offset) HandleCallback(int position, int offset, int width)
        {
            var end = ErrorEnd(position, offset, width);
            var error = _callback.Error(EncodingName, source, position, end, Reason);
            var (replacement, next) = _callback.Invoke(error, decode: false);
            var newPosition = _callback.NormalizePosition(
                next,
                _sourceCount ??= PythonTextTraversal.Count(source.Value, span)
            );
            if (replacement is PythonByteSequenceValue raw)
            {
                if (_codePage is 1200 or 1201 && (raw.Value.Length & 1) != 0)
                    throw new PythonRaisedException(
                        _callback.Error(EncodingName, source, position, end, Reason)
                    );
                for (var i = 0; i < raw.Value.Length; i++)
                {
                    if ((i & 255) == 0)
                        CheckWork();
                    WriteByte(raw.Value[i]);
                }
            }
            else
            {
                // CPython's represented UTF encoders require ASCII callback text;
                // arbitrary encoded replacements can instead be returned as bytes.
                foreach (
                    var ch in PythonTextTraversal.Enumerate(
                        ((PythonTextValue)replacement).Value,
                        span
                    )
                )
                {
                    if (ch.Value >= (_codePage == 28591 ? 256 : 128))
                        throw new PythonRaisedException(
                            _callback.Error(EncodingName, source, position, end, Reason)
                        );
                    WriteCharacter(ch.Value);
                }
            }
            while (position < newPosition)
            {
                if ((position & 127) == 0)
                    CheckWork();
                offset += PythonTextTraversal.Width(source.Value, offset);
                position++;
            }
            while (position > newPosition)
            {
                if ((position & 127) == 0)
                    CheckWork();
                offset = PythonTextTraversal.PreviousOffset(source.Value, offset);
                position--;
            }
            return (position, offset);
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

        private int ErrorEnd(int position, int offset, int width)
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
            return end;
        }

        private string EncodingName =>
            _codePage switch
            {
                20127 => "ascii",
                28591 => "latin-1",
                65001 => "utf-8",
                1200 => _preamble.Length == 0 ? "utf-16-le" : "utf-16",
                1201 => "utf-16-be",
                _ => throw new InvalidOperationException("Unsupported encoding code page."),
            };

        private string Reason =>
            _codePage switch
            {
                20127 => "ordinal not in range(128)",
                28591 => "ordinal not in range(256)",
                _ => "surrogates not allowed",
            };

        private void ThrowEncodingError(int position, int offset, int width) =>
            throw new PythonRaisedException(
                _callback.Error(
                    EncodingName,
                    source,
                    position,
                    ErrorEnd(position, offset, width),
                    Reason
                )
            );

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
            if (_output.Count == MaximumBytes)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "encoded bytes exceed the managed materialization limit",
                    span,
                    "OverflowError"
                );
            _output.Add((byte)value);
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
