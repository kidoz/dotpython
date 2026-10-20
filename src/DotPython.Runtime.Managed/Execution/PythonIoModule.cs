using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `io` module's in-memory streams: `StringIO` and `BytesIO` over growable buffers,
/// with the read/write/seek/tell protocol, truncation, iteration, the context manager,
/// and CPython's newline handling for `StringIO` (writing with the configured newline,
/// universal-newline reading when `newline` is `""` or `None`). The file-backed part of
/// `io` - `TextIOWrapper`, `FileIO`, the buffered streams and `open` - remains the
/// recorded gap; `open` stays the runtime's read-only builtin.
/// </summary>
internal static class PythonIoModule
{
    private const int DefaultBufferSize = 131072;

    private static readonly PythonManagedTypeValue StringIoType = CreateStreamType(
        "StringIO",
        strings: true
    );
    private static readonly PythonManagedTypeValue BytesIoType = CreateStreamType(
        "BytesIO",
        strings: false
    );
    private static readonly PythonManagedTypeValue UnsupportedOperationType =
        CreateUnsupportedOperationType();

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        var roots = UserObjectProtocols.Dispatcher?.SearchRoots ?? [];
        globals.SetValue(
            "FileIO",
            new PythonBuiltinFunctionValue(
                "FileIO",
                (arguments, span) => NewFileIo(arguments, [], [], roots, span),
                (positional, names, values, span) =>
                    NewFileIo(positional, names, values, roots, span)
            )
        );
        globals.SetValue(
            "BufferedReader",
            new PythonBuiltinFunctionValue(
                "BufferedReader",
                (arguments, span) => NewBufferedReader(arguments, [], [], span),
                (positional, names, values, span) =>
                    NewBufferedReader(positional, names, values, span)
            )
        );
        globals.SetValue(
            "TextIOWrapper",
            new PythonBuiltinFunctionValue(
                "TextIOWrapper",
                (arguments, span) => NewTextIo(arguments, [], [], span),
                (positional, names, values, span) => NewTextIo(positional, names, values, span)
            )
        );
        globals.SetValue("open", PythonStandardModules.CreateOpenBuiltin(roots));
        globals.SetValue("SEEK_SET", PythonWholeNumberValue.Create(0));
        globals.SetValue("SEEK_CUR", PythonWholeNumberValue.Create(1));
        globals.SetValue("SEEK_END", PythonWholeNumberValue.Create(2));
        globals.SetValue("DEFAULT_BUFFER_SIZE", PythonWholeNumberValue.Create(DefaultBufferSize));
        globals.SetValue("UnsupportedOperation", UnsupportedOperationType);
        globals.SetValue(
            "StringIO",
            new PythonBuiltinFunctionValue(
                "StringIO",
                (arguments, span) => NewStringIo(arguments, [], [], span),
                (positional, names, values, span) => NewStringIo(positional, names, values, span)
            )
        );
        globals.SetValue(
            "BytesIO",
            new PythonBuiltinFunctionValue(
                "BytesIO",
                (arguments, span) => NewBytesIo(arguments, [], [], span),
                (positional, names, values, span) => NewBytesIo(positional, names, values, span)
            )
        );
    }

    private static PythonTextValue Text(string value) => new(value);

    private static PythonRuntimeException Fault(string type, string message) =>
        ManagedObjectProtocols.Fault("DPY4063", message, default, type);

    private static PythonRuntimeException ValueError(string message) =>
        Fault("ValueError", message);

    internal static PythonRaisedException UnsupportedOperation(string name) => Unsupported(name);

    private static PythonRaisedException Unsupported(string name) =>
        new(
            new PythonExceptionValue("UnsupportedOperation", name)
            {
                ManagedType = UnsupportedOperationType,
                Arguments = [Text(name)],
            }
        );

    private static PythonManagedTypeValue CreateUnsupportedOperationType()
    {
        var osError = PythonBuiltinTypes.GetExceptionType("OSError");
        var valueError = PythonBuiltinTypes.GetExceptionType("ValueError");
        var type = new PythonManagedTypeValue("UnsupportedOperation", exceptionBaseName: "OSError")
        {
            Module = "_io",
            LayoutBase = osError,
        };
        type.Attributes["__module__"] = Text("_io");
        type.Attributes["__qualname__"] = Text("UnsupportedOperation");
        type.SetDeclaredBases(new PythonTupleValue([osError, valueError]));
        type.SetResolutionOrder(
            new PythonTupleValue([
                type,
                osError,
                valueError,
                PythonBuiltinTypes.GetExceptionType("Exception"),
                PythonBuiltinTypes.GetExceptionType("BaseException"),
                PythonBuiltinFunctions.Object,
            ])
        );
        return type;
    }

    // -----------------------------------------------------------------------------
    // Stream state
    // -----------------------------------------------------------------------------

    private sealed class StringIoState(string? newline)
    {
        internal List<char> Buffer { get; } = [];
        internal string? Newline { get; } = newline;
        internal long Position { get; set; }
        internal bool Closed { get; set; }
    }

    private sealed class BytesIoState
    {
        internal List<byte> Buffer { get; } = [];
        internal long Position { get; set; }
        internal bool Closed { get; set; }
    }

    private static StringIoState StringIoOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: StringIoState state }
            ? state
            : throw Fault("TypeError", "the value is not a StringIO");

    private static BytesIoState BytesIoOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: BytesIoState state }
            ? state
            : throw Fault("TypeError", "the value is not a BytesIO");

    private static PythonManagedObjectValue NewStringIo(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var (initial, newlineValue) = BindConstructor(
            "StringIO",
            positional,
            names,
            values,
            ["initial_value", "newline"],
            span
        );
        string? newline = "\n";
        if (newlineValue is PythonNoneValue)
        {
            newline = null;
        }
        else if (newlineValue is not null)
        {
            if (newlineValue is not PythonTextValue newlineText)
            {
                throw Fault(
                    "TypeError",
                    $"newline must be str or None, not {ManagedObjectProtocols.GetTypeName(newlineValue)}"
                );
            }
            if (newlineText.Value is not ("" or "\n" or "\r" or "\r\n"))
            {
                throw ValueError($"illegal newline value: {newlineText.ToRepresentationString()}");
            }
            newline = newlineText.Value;
        }
        string? initialText = null;
        if (initial is not null and not PythonNoneValue)
        {
            if (initial is not PythonTextValue text)
            {
                throw Fault(
                    "TypeError",
                    $"initial_value must be str or None, not {ManagedObjectProtocols.GetTypeName(initial)}"
                );
            }
            initialText = text.Value;
        }
        var state = new StringIoState(newline);
        if (initialText is { Length: > 0 })
        {
            state.Buffer.AddRange(initialText);
        }
        return new PythonManagedObjectValue(StringIoType, state);
    }

    private static PythonManagedObjectValue NewBytesIo(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var (initial, _) = BindConstructor(
            "BytesIO",
            positional,
            names,
            values,
            ["initial_bytes"],
            span
        );
        var state = new BytesIoState();
        if (initial is not null and not PythonNoneValue)
        {
            if (BytesOf(initial) is not { } bytes)
            {
                throw Fault(
                    "TypeError",
                    $"a bytes-like object is required, not '{ManagedObjectProtocols.GetTypeName(initial)}'"
                );
            }
            state.Buffer.AddRange(bytes);
        }
        return new PythonManagedObjectValue(BytesIoType, state);
    }

    private static PythonFileValue NewFileIo(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        IReadOnlyList<string> roots,
        TextSpan span
    )
    {
        var slots = PythonKeywordArguments.Bind(
            "FileIO",
            ["file", "mode", "closefd", "opener"],
            0,
            positional,
            names,
            values,
            span,
            typeStyleErrors: true
        );
        if (slots[0] is null)
        {
            throw Fault("TypeError", "FileIO() missing required argument 'file' (pos 1)");
        }
        if (!PythonPathlib.TryConvertPathArgument(slots[0]!, span, out var path))
        {
            throw Fault(
                "NotImplementedError",
                "FileIO() supports path names only in this runtime slice "
                    + "(file descriptors are out of scope)."
            );
        }
        var mode = "r";
        if (slots[1] is { } modeValue)
        {
            if (modeValue is not PythonTextValue modeText)
            {
                throw Fault(
                    "TypeError",
                    "FileIO() argument 'mode' must be str, not "
                        + ManagedObjectProtocols.GetTypeName(modeValue)
                );
            }
            mode = modeText.Value;
        }
        if (mode is not ("r" or "rb"))
        {
            foreach (var character in mode)
            {
                if (!"rwxab+".Contains(character, StringComparison.Ordinal))
                {
                    throw Fault("ValueError", $"invalid mode: '{mode}'");
                }
            }
            if (mode.IndexOfAny(['w', 'x', 'a', '+']) >= 0)
            {
                throw Fault(
                    "PermissionError",
                    $"FileIO() mode '{mode}' requires a filesystem write capability that "
                        + "this runtime slice does not provide."
                );
            }
            throw Fault("ValueError", $"invalid mode: '{mode}'");
        }
        if (slots[2] is { } closeFdValue && !ManagedObjectProtocols.IsTrue(closeFdValue))
        {
            throw Fault("ValueError", "Cannot use closefd=False with file name");
        }
        if (slots[3] is { } opener && opener is not PythonNoneValue)
        {
            throw Fault(
                "NotImplementedError",
                "FileIO() with an opener is not supported in this runtime slice."
            );
        }
        return PythonStandardModules.OpenReadable(
            roots,
            path,
            "rb",
            binary: true,
            kind: "FileIO",
            encoding: null,
            errors: null,
            newline: null,
            span
        );
    }

    private static PythonFileValue NewBufferedReader(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = PythonKeywordArguments.Bind(
            "BufferedReader",
            ["raw"],
            0,
            positional,
            names,
            values,
            span
        );
        if (slots[0] is null)
        {
            throw Fault("TypeError", "BufferedReader() missing required argument 'raw' (pos 1)");
        }
        var (bytes, position, name, mode) = BufferSource(slots[0]!, span);
        return new PythonFileValue(name, mode, string.Empty)
        {
            IsBinary = true,
            BinaryContent = bytes,
            Position = position,
            Kind = "BufferedReader",
        };
    }

    private static PythonFileValue NewTextIo(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var slots = PythonKeywordArguments.Bind(
            "TextIOWrapper",
            ["buffer", "encoding", "errors", "newline"],
            0,
            positional,
            names,
            values,
            span
        );
        if (slots[0] is null)
        {
            throw Fault("TypeError", "TextIOWrapper() missing required argument 'buffer' (pos 1)");
        }
        var (bytes, position, name, mode) = BufferSource(slots[0]!, span);
        var encoding = OptionalName(slots[1], "encoding", span);
        var errors = OptionalName(slots[2], "errors", span);
        var newline = TextIoNewline(slots[3], span);
        var decodeName = encoding ?? "utf-8";
        _ = PythonTextCodecs.Resolve(decodeName, span);
        var payload = position <= 0 ? bytes : bytes[position..];
        var text = PythonTextCodecs.Decode(payload, decodeName, errors ?? "strict", span);
        return new PythonFileValue(name, mode, text)
        {
            Newline = newline,
            EncodingName = encoding ?? "UTF-8",
            Errors = errors ?? "strict",
            Kind = "TextIOWrapper",
            HasMode = false,
        };
    }

    /// <summary>The bytes, position, name and mode behind a binary buffer object.</summary>
    private static (byte[] Bytes, int Position, string Name, string Mode) BufferSource(
        PythonValue value,
        TextSpan span
    ) =>
        value switch
        {
            PythonFileValue { IsBinary: true } file => (
                file.BinaryContent,
                file.Position,
                file.Name,
                file.Mode
            ),
            PythonManagedObjectValue { Payload: BytesIoState state } => (
                [.. state.Buffer],
                (int)Math.Min(state.Position, state.Buffer.Count),
                string.Empty,
                string.Empty
            ),
            _ => throw Fault(
                "AttributeError",
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object has no attribute "
                    + "'readable'"
            ),
        };

    private static string? OptionalName(PythonValue? value, string name, TextSpan span)
    {
        if (value is null or PythonNoneValue)
        {
            return null;
        }
        if (value is not PythonTextValue text)
        {
            throw Fault(
                "TypeError",
                $"TextIOWrapper() argument '{name}' must be str or None, not "
                    + ManagedObjectProtocols.GetTypeName(value)
            );
        }
        return text.Value;
    }

    private static string? TextIoNewline(PythonValue? value, TextSpan span)
    {
        if (value is null or PythonNoneValue)
        {
            return null;
        }
        if (value is not PythonTextValue text)
        {
            throw Fault(
                "TypeError",
                "TextIOWrapper() argument 'newline' must be str or None, not "
                    + ManagedObjectProtocols.GetTypeName(value)
            );
        }
        if (text.Value is not ("" or "\n" or "\r" or "\r\n"))
        {
            throw ValueError($"illegal newline value: {text.Value}");
        }
        return text.Value;
    }

    private static (PythonValue? Initial, PythonValue? Option) BindConstructor(
        string name,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        string[] parameters,
        TextSpan span
    )
    {
        if (positional.Count > parameters.Length)
        {
            throw Fault(
                "TypeError",
                $"{name}() takes at most {parameters.Length} arguments "
                    + $"({positional.Count} given)"
            );
        }
        var slots = new PythonValue?[parameters.Length];
        for (var index = 0; index < positional.Count; index++)
        {
            slots[index] = positional[index];
        }
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(parameters, names[index]);
            if (slot < 0)
            {
                throw Fault(
                    "TypeError",
                    $"{name}() got an unexpected keyword argument '{names[index]}'"
                );
            }
            if (slots[slot] is not null)
            {
                throw Fault(
                    "TypeError",
                    $"{name}() got multiple values for argument '{names[index]}'"
                );
            }
            slots[slot] = values[index];
        }
        _ = span;
        return (slots[0], slots.Length > 1 ? slots[1] : null);
    }

    private static byte[]? BytesOf(PythonValue value) =>
        value switch
        {
            PythonByteSequenceValue bytes => bytes.Value,
            PythonByteArrayValue array => array.Value,
            PythonMemoryViewValue view => view.Bytes,
            _ => null,
        };

    // -----------------------------------------------------------------------------
    // Stream types
    // -----------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateStreamType(string name, bool strings)
    {
        var type = new PythonManagedTypeValue(name) { Module = "_io" };
        type.Attributes["__module__"] = Text("_io");
        type.Attributes["__qualname__"] = Text(name);
        type.Attributes["write"] = Method(
            name,
            "write",
            strings
                ? (self, arguments) => StringWrite(StringIoOf(self!), arguments, default)
                : (self, arguments) => BytesWrite(BytesIoOf(self!), arguments, default)
        );
        type.Attributes["writelines"] = Method(
            name,
            "writelines",
            strings
                ? (self, arguments) => StringWriteLines(StringIoOf(self!), arguments, default)
                : (self, arguments) => BytesWriteLines(BytesIoOf(self!), arguments, default)
        );
        type.Attributes["read"] = Method(
            name,
            "read",
            strings
                ? (self, arguments) => Text(StringRead(StringIoOf(self!), Size(arguments)))
                : (self, arguments) => Bytes(BytesRead(BytesIoOf(self!), Size(arguments)))
        );
        type.Attributes["readline"] = Method(
            name,
            "readline",
            strings
                ? (self, arguments) => Text(StringReadLine(StringIoOf(self!), Size(arguments)))
                : (self, arguments) => Bytes(BytesReadLine(BytesIoOf(self!), Size(arguments)))
        );
        type.Attributes["readlines"] = Method(
            name,
            "readlines",
            strings
                ? (self, arguments) => StringReadLines(StringIoOf(self!), arguments, default)
                : (self, arguments) => BytesReadLines(BytesIoOf(self!), arguments, default)
        );
        type.Attributes["getvalue"] = Method(
            name,
            "getvalue",
            strings
                ? (self, _) =>
                {
                    var state = StringIoOf(self!);
                    CheckStringOpen(state);
                    return Text(new string([.. state.Buffer]));
                }
                : (self, _) =>
                {
                    var state = BytesIoOf(self!);
                    CheckBytesOpen(state);
                    return Bytes([.. state.Buffer]);
                }
        );
        type.Attributes["seek"] = Method(
            name,
            "seek",
            strings
                ? (self, arguments) => Integer(StringSeek(StringIoOf(self!), arguments))
                : (self, arguments) => Integer(BytesSeek(BytesIoOf(self!), arguments))
        );
        type.Attributes["tell"] = Method(
            name,
            "tell",
            strings
                ? (self, _) =>
                {
                    var state = StringIoOf(self!);
                    CheckStringOpen(state);
                    return Integer(state.Position);
                }
                : (self, _) =>
                {
                    var state = BytesIoOf(self!);
                    CheckBytesOpen(state);
                    return Integer(state.Position);
                }
        );
        type.Attributes["truncate"] = Method(
            name,
            "truncate",
            strings
                ? (self, arguments) => Integer(StringTruncate(StringIoOf(self!), arguments))
                : (self, arguments) => Integer(BytesTruncate(BytesIoOf(self!), arguments))
        );
        type.Attributes["close"] = Method(
            name,
            "close",
            (self, _) =>
            {
                if (strings)
                {
                    StringIoOf(self!).Closed = true;
                }
                else
                {
                    BytesIoOf(self!).Closed = true;
                }
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["flush"] = Method(
            name,
            "flush",
            (self, _) =>
            {
                if (strings)
                {
                    CheckStringOpen(StringIoOf(self!));
                }
                else
                {
                    CheckBytesOpen(BytesIoOf(self!));
                }
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["fileno"] = Method(name, "fileno", (_, _) => throw Unsupported("fileno"));
        type.Attributes["readable"] = Method(name, "readable", (_, _) => PythonTruthValue.True);
        type.Attributes["writable"] = Method(name, "writable", (_, _) => PythonTruthValue.True);
        type.Attributes["seekable"] = Method(name, "seekable", (_, _) => PythonTruthValue.True);
        type.Attributes["closed"] = new PythonPropertyValue(
            new PythonProtocolFunctionValue(
                "closed",
                (_, arguments) =>
                    PythonTruthValue.FromBoolean(
                        strings ? StringIoOf(arguments[0]).Closed : BytesIoOf(arguments[0]).Closed
                    )
            ),
            null,
            null
        );
        type.Attributes["__enter__"] = Method(
            name,
            "__enter__",
            (self, _) =>
            {
                if (strings)
                {
                    var state = StringIoOf(self!);
                    if (state.Closed)
                    {
                        throw ValueError("I/O operation on closed file.");
                    }
                }
                else if (BytesIoOf(self!).Closed)
                {
                    throw ValueError("I/O operation on closed file.");
                }
                return self!;
            }
        );
        type.Attributes["__exit__"] = Method(
            name,
            "__exit__",
            (self, _) =>
            {
                if (strings)
                {
                    StringIoOf(self!).Closed = true;
                }
                else
                {
                    BytesIoOf(self!).Closed = true;
                }
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["__iter__"] = Method(name, "__iter__", (self, _) => self!);
        type.Attributes["__next__"] = Method(
            name,
            "__next__",
            strings
                ? (self, _) =>
                {
                    var line = StringReadLine(StringIoOf(self!), -1);
                    return line.Length == 0
                        ? throw new PythonRaisedException(
                            ManagedObjectProtocols.CreateStopIteration(PythonNoneValue.Instance)
                        )
                        : Text(line);
                }
                : (self, _) =>
                {
                    var line = BytesReadLine(BytesIoOf(self!), -1);
                    return line.Length == 0
                        ? throw new PythonRaisedException(
                            ManagedObjectProtocols.CreateStopIteration(PythonNoneValue.Instance)
                        )
                        : Bytes(line);
                }
        );
        return type;
    }

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body
    ) => PythonUserTypes.Method(owner, name, body);

    private static PythonWholeNumberValue Integer(long value) =>
        PythonWholeNumberValue.Create(value);

    private static PythonByteSequenceValue Bytes(byte[] value) => new(value);

    private static PythonByteSequenceValue Bytes(IEnumerable<byte> value) => new([.. value]);

    private static void CheckStringOpen(StringIoState state)
    {
        if (state.Closed)
        {
            throw ValueError("I/O operation on closed file");
        }
    }

    private static void CheckBytesOpen(BytesIoState state)
    {
        if (state.Closed)
        {
            throw ValueError("I/O operation on closed file.");
        }
    }

    private static long Size(IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count == 0 || arguments[0] is PythonNoneValue)
        {
            return -1;
        }
        return arguments[0] switch
        {
            PythonWholeNumberValue whole => (long)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault(
                "TypeError",
                $"argument should be integer or None, not '{ManagedObjectProtocols.GetTypeName(arguments[0])}'"
            ),
        };
    }

    private static PythonWholeNumberValue StringWrite(
        StringIoState state,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        CheckStringOpen(state);
        if (arguments.Count != 1 || arguments[0] is not PythonTextValue text)
        {
            var value = arguments.Count == 0 ? PythonNoneValue.Instance : arguments[0];
            throw Fault(
                "TypeError",
                $"string argument expected, got '{ManagedObjectProtocols.GetTypeName(value)}'"
            );
        }
        var written = text.Value;
        var translated = state.Newline switch
        {
            "\r" => written.Replace("\n", "\r", StringComparison.Ordinal),
            "\r\n" => written.Replace("\n", "\r\n", StringComparison.Ordinal),
            _ => written,
        };
        WriteChars(state, translated);
        _ = span;
        return Integer(text.Value.Length);
    }

    private static void WriteChars(StringIoState state, string value)
    {
        if (state.Position > state.Buffer.Count)
        {
            state.Buffer.AddRange(
                Enumerable.Repeat('\0', (int)(state.Position - state.Buffer.Count))
            );
        }
        var position = (int)state.Position;
        for (var index = 0; index < value.Length; index++)
        {
            if (position < state.Buffer.Count)
            {
                state.Buffer[position++] = value[index];
            }
            else
            {
                state.Buffer.Add(value[index]);
                position++;
            }
        }
        state.Position = position;
    }

    private static PythonNoneValue StringWriteLines(
        StringIoState state,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        CheckStringOpen(state);
        if (arguments.Count != 1)
        {
            throw Fault("TypeError", "writelines() takes exactly one argument");
        }
        foreach (var line in ManagedObjectProtocols.MaterializeValues(arguments[0], span))
        {
            StringWrite(state, [line], span);
        }
        return PythonNoneValue.Instance;
    }

    private static string StringRead(StringIoState state, long size)
    {
        CheckStringOpen(state);
        var rawStart = (int)Math.Min(state.Position, state.Buffer.Count);
        var raw = new string([.. state.Buffer.Skip(rawStart)]);
        var (text, consumed) = TranslateRead(state.Newline, raw, size);
        state.Position = rawStart + consumed;
        return text;
    }

    /// <summary>Translates reading the raw text, taking at most <paramref name="size"/>
    /// characters; consumed is the raw length those characters came from.</summary>
    internal static (string Text, int Consumed) TranslateRead(
        string? newline,
        string raw,
        long size
    )
    {
        if (newline is not null)
        {
            var take = size < 0 ? raw.Length : (int)Math.Min(size, raw.Length);
            return (raw[..take], take);
        }
        var output = new System.Text.StringBuilder(raw.Length);
        var index = 0;
        while (index < raw.Length && (size < 0 || output.Length < size))
        {
            if (raw[index] == '\r')
            {
                var width = index + 1 < raw.Length && raw[index + 1] == '\n' ? 2 : 1;
                output.Append('\n');
                index += width;
                continue;
            }
            output.Append(raw[index++]);
        }
        return (output.ToString(), index);
    }

    private static string StringReadLine(StringIoState state, long size)
    {
        CheckStringOpen(state);
        var rawStart = (int)Math.Min(state.Position, state.Buffer.Count);
        var raw = new string([.. state.Buffer.Skip(rawStart)]);
        var boundary = LineBoundary(state.Newline, raw, size);
        var (text, consumed) = TranslateRead(state.Newline, raw[..boundary], size);
        state.Position = rawStart + consumed;
        return text;
    }

    internal static int LineBoundary(string? newline, string raw, long size)
    {
        var limit = size < 0 ? raw.Length : (int)Math.Min(size, raw.Length);
        if (newline == "\n")
        {
            var index = raw.IndexOf('\n', 0, limit);
            return index < 0 ? limit : index + 1;
        }
        if (newline is "\r" or "\r\n")
        {
            var index = raw.IndexOf(newline, 0, limit, StringComparison.Ordinal);
            return index < 0 ? limit : index + newline.Length;
        }
        for (var index = 0; index < limit; index++)
        {
            if (raw[index] == '\r')
            {
                return index + 1 < raw.Length && raw[index + 1] == '\n' ? index + 2 : index + 1;
            }
            if (raw[index] == '\n')
            {
                return index + 1;
            }
        }
        return limit;
    }

    private static PythonListValue StringReadLines(
        StringIoState state,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        _ = span;
        var hint = arguments.Count > 0 ? Size(arguments) : -1;
        var lines = new List<PythonValue>();
        while (true)
        {
            var line = StringReadLine(state, -1);
            if (line.Length == 0)
            {
                break;
            }
            lines.Add(Text(line));
            if (hint > 0 && lines.Count >= hint)
            {
                break;
            }
        }
        return new PythonListValue(lines);
    }

    private static long StringSeek(StringIoState state, IReadOnlyList<PythonValue> arguments)
    {
        CheckStringOpen(state);
        var offset = arguments.Count > 0 ? RequireLong(arguments[0]) : 0;
        var whence = arguments.Count > 1 ? RequireLong(arguments[1]) : 0;
        if (whence == 1 && offset != 0)
        {
            throw Fault("OSError", "Can't do nonzero cur-relative seeks");
        }
        if (whence == 2 && offset != 0)
        {
            throw Fault("OSError", "Can't do nonzero cur-relative seeks");
        }
        long target = whence switch
        {
            0 => offset,
            1 => state.Position + offset,
            2 => state.Buffer.Count + offset,
            _ => throw ValueError($"Invalid whence ({whence}, should be 0, 1 or 2)"),
        };
        if (target < 0)
        {
            throw ValueError($"Negative seek position {target}");
        }
        state.Position = target;
        return target;
    }

    private static long BytesSeek(BytesIoState state, IReadOnlyList<PythonValue> arguments)
    {
        CheckBytesOpen(state);
        var offset = arguments.Count > 0 ? RequireLong(arguments[0]) : 0;
        var whence = arguments.Count > 1 ? RequireLong(arguments[1]) : 0;
        long target = whence switch
        {
            0 => offset,
            1 => state.Position + offset,
            2 => state.Buffer.Count + offset,
            _ => throw ValueError($"Invalid whence ({whence}, should be 0, 1 or 2)"),
        };
        if (target < 0)
        {
            throw ValueError($"negative seek value {target}");
        }
        state.Position = target;
        return target;
    }

    private static long RequireLong(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => (long)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw Fault(
                "TypeError",
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer"
            ),
        };

    private static long StringTruncate(StringIoState state, IReadOnlyList<PythonValue> arguments)
    {
        CheckStringOpen(state);
        var size = arguments.Count > 0 ? RequireLong(arguments[0]) : state.Position;
        if (size < state.Buffer.Count)
        {
            state.Buffer.RemoveRange((int)size, state.Buffer.Count - (int)size);
        }
        return size;
    }

    private static long BytesTruncate(BytesIoState state, IReadOnlyList<PythonValue> arguments)
    {
        CheckBytesOpen(state);
        var size = arguments.Count > 0 ? RequireLong(arguments[0]) : state.Position;
        if (size < state.Buffer.Count)
        {
            state.Buffer.RemoveRange((int)size, state.Buffer.Count - (int)size);
        }
        return size;
    }

    private static PythonWholeNumberValue BytesWrite(
        BytesIoState state,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        CheckBytesOpen(state);
        if (arguments.Count != 1 || BytesOf(arguments[0]) is not { } bytes)
        {
            var value = arguments.Count == 0 ? PythonNoneValue.Instance : arguments[0];
            throw Fault(
                "TypeError",
                $"a bytes-like object is required, not '{ManagedObjectProtocols.GetTypeName(value)}'"
            );
        }
        if (state.Position > state.Buffer.Count)
        {
            state.Buffer.AddRange(
                Enumerable.Repeat((byte)0, (int)(state.Position - state.Buffer.Count))
            );
        }
        var position = (int)state.Position;
        foreach (var value in bytes)
        {
            if (position < state.Buffer.Count)
            {
                state.Buffer[position++] = value;
            }
            else
            {
                state.Buffer.Add(value);
                position++;
            }
        }
        state.Position = position;
        _ = span;
        return Integer(bytes.Length);
    }

    private static PythonNoneValue BytesWriteLines(
        BytesIoState state,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        CheckBytesOpen(state);
        if (arguments.Count != 1)
        {
            throw Fault("TypeError", "writelines() takes exactly one argument");
        }
        foreach (var line in ManagedObjectProtocols.MaterializeValues(arguments[0], span))
        {
            BytesWrite(state, [line], span);
        }
        return PythonNoneValue.Instance;
    }

    private static byte[] BytesRead(BytesIoState state, long size)
    {
        CheckBytesOpen(state);
        var start = (int)Math.Min(state.Position, state.Buffer.Count);
        var available = state.Buffer.Count - start;
        var take = size < 0 ? available : (int)Math.Min(size, available);
        state.Position = start + take;
        return [.. state.Buffer.Skip(start).Take(take)];
    }

    private static byte[] BytesReadLine(BytesIoState state, long size)
    {
        CheckBytesOpen(state);
        var start = (int)Math.Min(state.Position, state.Buffer.Count);
        var limit =
            size < 0 ? state.Buffer.Count - start : (int)Math.Min(size, state.Buffer.Count - start);
        var end = start;
        while (end < start + limit)
        {
            end++;
            if (state.Buffer[end - 1] == (byte)'\n')
            {
                break;
            }
        }
        state.Position = end;
        return [.. state.Buffer.Skip(start).Take(end - start)];
    }

    private static PythonListValue BytesReadLines(
        BytesIoState state,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        _ = span;
        var hint = arguments.Count > 0 ? Size(arguments) : -1;
        var lines = new List<PythonValue>();
        while (true)
        {
            var line = BytesReadLine(state, -1);
            if (line.Length == 0)
            {
                break;
            }
            lines.Add(Bytes(line));
            if (hint > 0 && lines.Count >= hint)
            {
                break;
            }
        }
        return new PythonListValue(lines);
    }
}
