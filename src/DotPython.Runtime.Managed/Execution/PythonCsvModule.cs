using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `csv` module: PEP 305's reader and writer over CPython's own state machines, the
/// dialect registry and its three standard dialects, `DictReader`/`DictWriter`, and a
/// port of `Sniffer`'s detection heuristics. The reader consumes any iterable of text
/// lines and follows CPython's quoting, escaping and error rules; the writer answers a
/// single `write` call per row, formats values through `str()`, and implements all six
/// quoting modes. The recorded gaps are the type objects `reader`/`writer` (module
/// callables rather than classes), `StringIO`, and `__class_getitem__` on the helpers.
/// </summary>
internal static class PythonCsvModule
{
    private const string CoreModule = "_csv";
    private const string Module = "csv";
    private const string ErrorCode = "DPY4062";

    private const int QuoteMinimal = 0;
    private const int QuoteAll = 1;
    private const int QuoteNonNumeric = 2;
    private const int QuoteNone = 3;
    private const int QuoteStrings = 4;
    private const int QuoteNotNull = 5;

    private static readonly string[] OptionNames =
    [
        "delimiter",
        "quotechar",
        "escapechar",
        "doublequote",
        "skipinitialspace",
        "lineterminator",
        "quoting",
        "strict",
    ];

    private static readonly Dictionary<string, PythonValue> Dialects = new(StringComparer.Ordinal);

    private static readonly DialectConfig ExcelDefaults = new(
        ",",
        "\"",
        null,
        true,
        false,
        "\r\n",
        QuoteMinimal,
        false
    );

    private static readonly PythonManagedTypeValue ErrorType = CreateErrorType();
    private static readonly PythonManagedTypeValue DialectType = CreateDialectType();
    private static readonly PythonManagedTypeValue ReaderType = CreateReaderType();
    private static readonly PythonManagedTypeValue WriterType = CreateWriterType();
    private static readonly PythonManagedTypeValue DictReaderType = CreateDictReaderType();
    private static readonly PythonManagedTypeValue DictWriterType = CreateDictWriterType();
    private static readonly PythonManagedTypeValue SnifferType = CreateSnifferType();

    private static long _fieldSizeLimit = 131072;

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        // The registry and the field limit are process state in CPython; a fresh
        // module initialization starts from the three standard dialects again.
        Dialects.Clear();
        _fieldSizeLimit = 131072;
        globals.SetValue("QUOTE_MINIMAL", PythonWholeNumberValue.Create(QuoteMinimal));
        globals.SetValue("QUOTE_ALL", PythonWholeNumberValue.Create(QuoteAll));
        globals.SetValue("QUOTE_NONNUMERIC", PythonWholeNumberValue.Create(QuoteNonNumeric));
        globals.SetValue("QUOTE_NONE", PythonWholeNumberValue.Create(QuoteNone));
        globals.SetValue("QUOTE_STRINGS", PythonWholeNumberValue.Create(QuoteStrings));
        globals.SetValue("QUOTE_NOTNULL", PythonWholeNumberValue.Create(QuoteNotNull));
        globals.SetValue("__version__", Text("1.0"));
        globals.SetValue("Error", ErrorType);
        globals.SetValue("Dialect", DialectType);

        var excel = CreateDialectClass("excel", ExcelDefaults);
        var excelTab = CreateDialectClass("excel_tab", ExcelDefaults with { Delimiter = "\t" });
        var unix = CreateDialectClass(
            "unix_dialect",
            ExcelDefaults with
            {
                LineTerminator = "\n",
                Quoting = QuoteAll,
            }
        );
        globals.SetValue("excel", excel);
        globals.SetValue("excel_tab", excelTab);
        globals.SetValue("unix_dialect", unix);
        Dialects["excel"] = excel;
        Dialects["excel-tab"] = excelTab;
        Dialects["unix"] = unix;

        globals.SetValue(
            "register_dialect",
            new PythonBuiltinFunctionValue(
                "register_dialect",
                (arguments, span) => RegisterDialect(arguments, [], [], span),
                (positional, names, values, span) =>
                    RegisterDialect(positional, names, values, span)
            )
        );
        globals.SetValue(
            "unregister_dialect",
            new PythonBuiltinFunctionValue(
                "unregister_dialect",
                (arguments, span) =>
                {
                    var name = RequireDialectName(arguments[0], span);
                    if (!Dialects.Remove(name))
                    {
                        throw CsvError("unknown dialect", span);
                    }
                    return PythonNoneValue.Instance;
                }
            ).WithSignature(["name"], [null])
        );
        globals.SetValue(
            "get_dialect",
            new PythonBuiltinFunctionValue(
                "get_dialect",
                (arguments, span) =>
                {
                    var name = RequireDialectName(arguments[0], span);
                    if (!Dialects.TryGetValue(name, out var dialect))
                    {
                        throw CsvError("unknown dialect", span);
                    }
                    return DialectInstance(ReadDialectObject(dialect, span));
                }
            ).WithSignature(["name"], [null])
        );
        globals.SetValue(
            "list_dialects",
            new PythonBuiltinFunctionValue(
                "list_dialects",
                (_, _) => new PythonListValue([.. Dialects.Keys.Select(Text)])
            )
        );
        globals.SetValue(
            "field_size_limit",
            new PythonBuiltinFunctionValue(
                "field_size_limit",
                (arguments, span) => PythonWholeNumberValue.Create(FieldSizeLimit(arguments, span))
            )
        );
        globals.SetValue(
            "reader",
            new PythonBuiltinFunctionValue(
                "reader",
                (arguments, span) => CreateReader(arguments, [], [], span),
                (positional, names, values, span) => CreateReader(positional, names, values, span)
            )
        );
        globals.SetValue(
            "writer",
            new PythonBuiltinFunctionValue(
                "writer",
                (arguments, span) => CreateWriter(arguments, [], [], span),
                (positional, names, values, span) => CreateWriter(positional, names, values, span)
            )
        );
        globals.SetValue(
            "DictReader",
            new PythonBuiltinFunctionValue(
                "DictReader",
                (arguments, span) => CreateDictReader(arguments, [], [], span),
                (positional, names, values, span) =>
                    CreateDictReader(positional, names, values, span)
            )
        );
        globals.SetValue(
            "DictWriter",
            new PythonBuiltinFunctionValue(
                "DictWriter",
                (arguments, span) => CreateDictWriter(arguments, [], [], span),
                (positional, names, values, span) =>
                    CreateDictWriter(positional, names, values, span)
            )
        );
        globals.SetValue(
            "Sniffer",
            new PythonBuiltinFunctionValue(
                "Sniffer",
                (arguments, span) =>
                {
                    if (arguments.Count != 0)
                    {
                        throw TypeError(
                            $"Sniffer() takes no arguments ({arguments.Count} given)",
                            span
                        );
                    }
                    var sniffer = new PythonManagedObjectValue(SnifferType);
                    sniffer.Attributes["preferred"] = new PythonListValue([
                        Text(","),
                        Text("\t"),
                        Text(";"),
                        Text(" "),
                        Text(":"),
                    ]);
                    return sniffer;
                }
            )
        );
    }

    private static PythonTextValue Text(string value) => new(value);

    private static PythonRuntimeException TypeError(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault(ErrorCode, message, span, "TypeError");

    private static PythonRuntimeException ValueError(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault(ErrorCode, message, span, "ValueError");

    private static PythonRaisedException CsvError(string message, TextSpan span)
    {
        _ = span;
        return new PythonRaisedException(
            new PythonExceptionValue("Error", message)
            {
                ManagedType = ErrorType,
                Arguments = [new PythonTextValue(message)],
            }
        );
    }

    // ---------------------------------------------------------------------------------
    // Exception and type objects
    // ---------------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateErrorType()
    {
        var baseType = PythonBuiltinTypes.GetExceptionType("Exception");
        var type = new PythonManagedTypeValue("Error", exceptionBaseName: "Exception")
        {
            Module = CoreModule,
            LayoutBase = baseType,
        };
        type.Attributes["__module__"] = Text(CoreModule);
        type.Attributes["__qualname__"] = Text("Error");
        type.SetDeclaredBases(new PythonTupleValue([baseType]));
        type.SetResolutionOrder(
            new PythonTupleValue([
                type,
                baseType,
                PythonBuiltinTypes.GetExceptionType("BaseException"),
                PythonBuiltinFunctions.Object,
            ])
        );
        return type;
    }

    private static PythonManagedTypeValue CreateDialectType()
    {
        var type = new PythonManagedTypeValue(
            "Dialect",
            construct: arguments =>
            {
                if (arguments.Count != 0)
                {
                    throw TypeError(
                        $"Dialect.__init__() takes 1 positional argument but {arguments.Count + 1} were given",
                        default
                    );
                }
                throw CsvError("\"delimiter\" must be a unicode character, not NoneType", default);
            }
        )
        {
            Module = CoreModule,
        };
        type.Attributes["__module__"] = Text(CoreModule);
        type.Attributes["__qualname__"] = Text("Dialect");
        foreach (var name in OptionNames)
        {
            type.Attributes[name] = PythonNoneValue.Instance;
        }
        return type;
    }

    private static PythonManagedTypeValue CreateDialectClass(string name, DialectConfig config)
    {
        var type = new PythonManagedTypeValue(name) { Module = Module };
        type.Attributes["__module__"] = Text(Module);
        type.Attributes["__qualname__"] = Text(name);
        SetSpecAttributes(type, config);
        return type;
    }

    private static void SetSpecAttributes(PythonManagedTypeValue type, DialectConfig config)
    {
        type.Attributes["delimiter"] = Text(config.Delimiter);
        type.Attributes["quotechar"] = config.QuoteChar is null
            ? PythonNoneValue.Instance
            : Text(config.QuoteChar);
        type.Attributes["escapechar"] = config.EscapeChar is null
            ? PythonNoneValue.Instance
            : Text(config.EscapeChar);
        type.Attributes["doublequote"] = PythonTruthValue.FromBoolean(config.DoubleQuote);
        type.Attributes["skipinitialspace"] = PythonTruthValue.FromBoolean(config.SkipInitialSpace);
        type.Attributes["lineterminator"] = Text(config.LineTerminator);
        type.Attributes["quoting"] = PythonWholeNumberValue.Create(config.Quoting);
        type.Attributes["strict"] = PythonTruthValue.FromBoolean(config.Strict);
    }

    private static PythonManagedObjectValue DialectInstance(DialectConfig config)
    {
        var instance = new PythonManagedObjectValue(DialectType);
        instance.Attributes["delimiter"] = Text(config.Delimiter);
        instance.Attributes["quotechar"] = config.QuoteChar is null
            ? PythonNoneValue.Instance
            : Text(config.QuoteChar);
        instance.Attributes["escapechar"] = config.EscapeChar is null
            ? PythonNoneValue.Instance
            : Text(config.EscapeChar);
        instance.Attributes["doublequote"] = PythonTruthValue.FromBoolean(config.DoubleQuote);
        instance.Attributes["skipinitialspace"] = PythonTruthValue.FromBoolean(
            config.SkipInitialSpace
        );
        instance.Attributes["lineterminator"] = Text(config.LineTerminator);
        instance.Attributes["quoting"] = PythonWholeNumberValue.Create(config.Quoting);
        instance.Attributes["strict"] = PythonTruthValue.FromBoolean(config.Strict);
        return instance;
    }

    // ---------------------------------------------------------------------------------
    // Dialect configuration
    // ---------------------------------------------------------------------------------

    private sealed record DialectConfig(
        string Delimiter,
        string? QuoteChar,
        string? EscapeChar,
        bool DoubleQuote,
        bool SkipInitialSpace,
        string LineTerminator,
        int Quoting,
        bool Strict
    );

    private static DialectConfig ResolveDialect(
        PythonValue? dialect,
        List<string> optionNames,
        List<PythonValue> optionValues,
        TextSpan span
    )
    {
        var config = ResolveDialectSpec(dialect, span);
        for (var index = 0; index < optionNames.Count; index++)
        {
            config = ApplyOption(config, optionNames[index], optionValues[index], span);
        }
        return Validate(config, span);
    }

    private static DialectConfig ResolveDialectSpec(PythonValue? dialect, TextSpan span)
    {
        if (dialect is null or PythonNoneValue)
        {
            return ExcelDefaults;
        }
        if (dialect is PythonTextValue name)
        {
            return Dialects.TryGetValue(name.Value, out var registered)
                ? ReadDialectObject(registered, span)
                : throw CsvError("unknown dialect", span);
        }
        return ReadDialectObject(dialect, span);
    }

    private static DialectConfig ReadDialectObject(PythonValue spec, TextSpan span)
    {
        var config = ExcelDefaults;
        var delimiter = ReadOption(spec, "delimiter", span);
        if (delimiter is not null)
        {
            config = config with { Delimiter = AsCharacter(delimiter, "delimiter", false, span)! };
        }
        var quotechar = ReadOption(spec, "quotechar", span);
        if (quotechar is not null)
        {
            config = config with { QuoteChar = AsCharacter(quotechar, "quotechar", true, span) };
        }
        var escapechar = ReadOption(spec, "escapechar", span);
        if (escapechar is not null)
        {
            config = config with { EscapeChar = AsCharacter(escapechar, "escapechar", true, span) };
        }
        var doublequote = ReadOption(spec, "doublequote", span);
        if (doublequote is not null)
        {
            config = config with { DoubleQuote = ManagedObjectProtocols.IsTrue(doublequote) };
        }
        var skipinitialspace = ReadOption(spec, "skipinitialspace", span);
        if (skipinitialspace is not null)
        {
            config = config with
            {
                SkipInitialSpace = ManagedObjectProtocols.IsTrue(skipinitialspace),
            };
        }
        var lineterminator = ReadOption(spec, "lineterminator", span);
        if (lineterminator is not null)
        {
            config = config with { LineTerminator = AsLineTerminator(lineterminator, span) };
        }
        var quoting = ReadOption(spec, "quoting", span);
        if (quoting is not null)
        {
            config = config with { Quoting = AsQuoting(quoting, span) };
        }
        var strict = ReadOption(spec, "strict", span);
        if (strict is not null)
        {
            config = config with { Strict = ManagedObjectProtocols.IsTrue(strict) };
        }
        return Validate(config, span);
    }

    private static PythonValue? ReadOption(PythonValue spec, string name, TextSpan span)
    {
        try
        {
            return ManagedObjectProtocols.GetAttribute(spec, name, span);
        }
        catch (PythonRaisedException error) when (error.Value.TypeName == "AttributeError")
        {
            return null;
        }
        catch (PythonRuntimeException error)
            when (error.PythonExceptionTypeName == "AttributeError")
        {
            return null;
        }
    }

    private static DialectConfig ApplyOption(
        DialectConfig config,
        string name,
        PythonValue value,
        TextSpan span
    ) =>
        name switch
        {
            "delimiter" => config with
            {
                Delimiter = AsCharacter(value, "delimiter", false, span)!,
            },
            "quotechar" => config with { QuoteChar = AsCharacter(value, "quotechar", true, span) },
            "escapechar" => config with
            {
                EscapeChar = AsCharacter(value, "escapechar", true, span),
            },
            "doublequote" => config with { DoubleQuote = ManagedObjectProtocols.IsTrue(value) },
            "skipinitialspace" => config with
            {
                SkipInitialSpace = ManagedObjectProtocols.IsTrue(value),
            },
            "lineterminator" => config with { LineTerminator = AsLineTerminator(value, span) },
            "quoting" => config with { Quoting = AsQuoting(value, span) },
            "strict" => config with { Strict = ManagedObjectProtocols.IsTrue(value) },
            _ => throw TypeError(
                $"this function got an unexpected keyword argument '{name}'",
                span
            ),
        };

    private static string? AsCharacter(
        PythonValue value,
        string option,
        bool allowNone,
        TextSpan span
    )
    {
        if (value is PythonNoneValue)
        {
            if (allowNone)
            {
                return null;
            }
            throw TypeError($"\"{option}\" must be a unicode character, not NoneType", span);
        }
        if (value is not PythonTextValue text)
        {
            throw TypeError(
                $"\"{option}\" must be a unicode character"
                    + (allowNone ? " or None" : string.Empty)
                    + $", not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            );
        }
        if (text.Value.Length != 1)
        {
            throw TypeError(
                $"\"{option}\" must be a unicode character"
                    + (allowNone ? " or None" : string.Empty)
                    + $", not a string of length {text.Value.Length}",
                span
            );
        }
        return text.Value;
    }

    private static string AsLineTerminator(PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw TypeError(
                $"\"lineterminator\" must be a string, not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            );

    private static int AsQuoting(PythonValue value, TextSpan span)
    {
        var quoting = value switch
        {
            PythonWholeNumberValue whole => (int)whole.Value,
            PythonTruthValue truth => truth.Value ? QuoteAll : QuoteMinimal,
            _ => throw TypeError(
                $"\"quoting\" must be an integer, not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            ),
        };
        return quoting is >= QuoteMinimal and <= QuoteNotNull
            ? quoting
            : throw TypeError("bad \"quoting\" value", span);
    }

    private static DialectConfig Validate(DialectConfig config, TextSpan span)
    {
        if (config.Delimiter is "\n" or "\r")
        {
            throw ValueError("bad delimiter value", span);
        }
        if (config.QuoteChar is not null && config.QuoteChar == config.Delimiter)
        {
            throw ValueError("bad delimiter or quotechar value", span);
        }
        if (config.EscapeChar is not null && config.EscapeChar == config.Delimiter)
        {
            throw ValueError("bad delimiter or escapechar value", span);
        }
        if (config.EscapeChar is not null && config.EscapeChar == config.QuoteChar)
        {
            throw ValueError("bad escapechar or quotechar value", span);
        }
        if (
            config.Quoting is QuoteAll or QuoteNonNumeric or QuoteStrings or QuoteNotNull
            && config.QuoteChar is null
        )
        {
            throw TypeError("quotechar must be set if quoting enabled", span);
        }
        return config;
    }

    private static string RequireDialectName(PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw TypeError("dialect name must be a string", span);

    private static PythonNoneValue RegisterDialect(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        if (positional.Count == 0 && !names.Contains("name", StringComparer.Ordinal))
        {
            throw TypeError("register_dialect expected at least 1 argument, got 0", span);
        }
        var name =
            positional.Count > 0
                ? RequireDialectName(positional[0], span)
                : RequireDialectName(values[names.ToList().IndexOf("name")], span);
        PythonValue? spec = positional.Count > 1 ? positional[1] : null;
        var overrides = new List<string>();
        var overrideValues = new List<PythonValue>();
        for (var index = 0; index < names.Count; index++)
        {
            switch (names[index])
            {
                case "name":
                    break;
                case "dialect":
                    spec = values[index];
                    break;
                default:
                    overrides.Add(names[index]);
                    overrideValues.Add(values[index]);
                    break;
            }
        }
        Dialects[name] = DialectInstance(ResolveDialect(spec, overrides, overrideValues, span));
        return PythonNoneValue.Instance;
    }

    // ---------------------------------------------------------------------------------
    // Reader
    // ---------------------------------------------------------------------------------

    private sealed class ReaderState(
        PythonIteratorValue source,
        DialectConfig dialect,
        long fieldLimit
    )
    {
        internal PythonIteratorValue Source { get; } = source;
        internal DialectConfig Dialect { get; } = dialect;
        internal long FieldLimit { get; } = fieldLimit;
        internal long LineNumber { get; set; }
        internal string Line { get; set; } = string.Empty;
        internal int Position { get; set; }
        internal bool HasLine { get; set; }
    }

    private sealed record CsvField(string Text, bool Quoted);

    private static PythonManagedTypeValue CreateReaderType()
    {
        var type = new PythonManagedTypeValue("reader") { Module = CoreModule };
        type.Attributes["__module__"] = Text(CoreModule);
        type.Attributes["__qualname__"] = Text("reader");
        type.Attributes["__iter__"] = Method("reader", "__iter__", (self, _) => self!);
        type.Attributes["__next__"] = Method(
            "reader",
            "__next__",
            (self, _) => ReadRow(ReaderOf(self!), default)
        );
        type.Attributes["line_num"] = Property(
            "reader",
            "line_num",
            self => PythonWholeNumberValue.Create(ReaderOf(self).LineNumber)
        );
        type.Attributes["dialect"] = Property(
            "reader",
            "dialect",
            self => DialectInstance(ReaderOf(self).Dialect)
        );
        return type;
    }

    private static ReaderState ReaderOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: ReaderState state }
            ? state
            : throw TypeError("the value is not a csv reader", default);

    private static PythonManagedObjectValue CreateReader(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var (sourceValue, dialectSpec, options) = BindDialectCall(
            positional,
            names,
            values,
            span,
            "reader",
            2
        );
        var dialect = ResolveDialect(
            dialectSpec,
            [.. options.Select(option => option.Name)],
            [.. options.Select(option => option.Value)],
            span
        );
        var source = ManagedObjectProtocols.GetIterator(sourceValue, span);
        return new PythonManagedObjectValue(
            ReaderType,
            new ReaderState(source, dialect, _fieldSizeLimit)
        );
    }

    private static (
        PythonValue First,
        PythonValue? Dialect,
        List<(string Name, PythonValue Value)> Options
    ) BindDialectCall(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span,
        string function,
        int maximum
    )
    {
        if (positional.Count == 0)
        {
            throw TypeError($"{function} expected at least 1 argument, got 0", span);
        }
        if (positional.Count > maximum)
        {
            throw TypeError(
                $"{function} expected at most {maximum} arguments, got {positional.Count}",
                span
            );
        }
        var dialect = positional.Count > 1 ? positional[1] : null;
        var options = new List<(string Name, PythonValue Value)>();
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index] == "dialect")
            {
                if (positional.Count > 1)
                {
                    throw TypeError(
                        "argument for function given by name ('dialect') and position (1)",
                        span
                    );
                }
                dialect = values[index];
                continue;
            }
            if (Array.IndexOf(OptionNames, names[index]) < 0)
            {
                throw TypeError(
                    $"this function got an unexpected keyword argument '{names[index]}'",
                    span
                );
            }
            options.Add((names[index], values[index]));
        }
        return (positional[0], dialect, options);
    }

    private static PythonListValue ReadRow(ReaderState state, TextSpan span)
    {
        var fields = new List<CsvField>();
        var field = new StringBuilder();
        var quoted = false;
        var stateCode = ParserState.StartRecord;

        bool NextLine()
        {
            if (!ManagedObjectProtocols.TryGetNext(state.Source, out var line, span))
            {
                return false;
            }
            if (line is not PythonTextValue text)
            {
                throw CsvError(
                    "iterator should return strings, not "
                        + ManagedObjectProtocols.GetTypeName(line)
                        + " (the file should be opened in text mode)",
                    span
                );
            }
            state.LineNumber++;
            state.Line = text.Value;
            state.Position = 0;
            state.HasLine = true;
            return true;
        }

        void SaveField()
        {
            fields.Add(new CsvField(field.ToString(), quoted));
            field.Clear();
            quoted = false;
        }

        if (!state.HasLine && !NextLine())
        {
            throw new PythonRaisedException(
                ManagedObjectProtocols.CreateStopIteration(PythonNoneValue.Instance)
            );
        }

        while (true)
        {
            if (state.Position >= state.Line.Length)
            {
                if (stateCode == ParserState.EscapedChar)
                {
                    Append(field, '\n', state, span);
                    if (!NextLine())
                    {
                        SaveField();
                        state.HasLine = false;
                        return RowValue(fields, state, span);
                    }
                    stateCode = ParserState.InField;
                    continue;
                }
                if (stateCode is ParserState.InQuotedField or ParserState.EscapeInQuotedField)
                {
                    if (!NextLine())
                    {
                        if (state.Dialect.Strict)
                        {
                            throw CsvError("unexpected end of data", span);
                        }
                        SaveField();
                        state.HasLine = false;
                        return RowValue(fields, state, span);
                    }
                    continue;
                }
                if (stateCode == ParserState.StartRecord && fields.Count == 0 && !state.HasLine)
                {
                    state.HasLine = false;
                    return new PythonListValue([]);
                }
                if (
                    stateCode
                    is ParserState.InField
                        or ParserState.StartField
                        or ParserState.QuoteInQuotedField
                )
                {
                    SaveField();
                }
                state.HasLine = false;
                return RowValue(fields, state, span);
            }

            var c = state.Line[state.Position++];
            switch (stateCode)
            {
                case ParserState.StartRecord:
                    if (c is '\n' or '\r')
                    {
                        stateCode = ParserState.EatCrnl;
                        break;
                    }
                    stateCode = ParserState.StartField;
                    state.Position--;
                    break;
                case ParserState.StartField:
                    if (c is '\n' or '\r')
                    {
                        SaveField();
                        stateCode = ParserState.EatCrnl;
                        break;
                    }
                    if (c == state.Dialect.Delimiter[0])
                    {
                        SaveField();
                        break;
                    }
                    if (
                        state.Dialect.Quoting != QuoteNone
                        && state.Dialect.QuoteChar == c.ToString()
                    )
                    {
                        quoted = true;
                        stateCode = ParserState.InQuotedField;
                        break;
                    }
                    if (state.Dialect.SkipInitialSpace && c == ' ')
                    {
                        break;
                    }
                    if (state.Dialect.EscapeChar == c.ToString())
                    {
                        stateCode = ParserState.EscapedChar;
                        break;
                    }
                    Append(field, c, state, span);
                    stateCode = ParserState.InField;
                    break;
                case ParserState.InField:
                    if (c is '\n' or '\r')
                    {
                        SaveField();
                        stateCode = ParserState.EatCrnl;
                        break;
                    }
                    if (c == state.Dialect.Delimiter[0])
                    {
                        SaveField();
                        stateCode = ParserState.StartField;
                        break;
                    }
                    if (state.Dialect.EscapeChar == c.ToString())
                    {
                        stateCode = ParserState.EscapedChar;
                        break;
                    }
                    Append(field, c, state, span);
                    break;
                case ParserState.EscapedChar:
                    if (c is '\n' or '\r')
                    {
                        Append(field, '\n', state, span);
                        if (!NextLine())
                        {
                            SaveField();
                            state.HasLine = false;
                            return RowValue(fields, state, span);
                        }
                        stateCode = ParserState.InField;
                        break;
                    }
                    Append(field, c, state, span);
                    stateCode = ParserState.InField;
                    break;
                case ParserState.InQuotedField:
                    if (state.Dialect.EscapeChar == c.ToString())
                    {
                        stateCode = ParserState.EscapeInQuotedField;
                        break;
                    }
                    if (state.Dialect.QuoteChar == c.ToString())
                    {
                        stateCode = ParserState.QuoteInQuotedField;
                        break;
                    }
                    Append(field, c, state, span);
                    break;
                case ParserState.EscapeInQuotedField:
                    Append(field, c, state, span);
                    stateCode = ParserState.InQuotedField;
                    break;
                case ParserState.QuoteInQuotedField:
                    if (state.Dialect.QuoteChar == c.ToString())
                    {
                        Append(field, c, state, span);
                        stateCode = state.Dialect.DoubleQuote
                            ? ParserState.InQuotedField
                            : ParserState.InField;
                        break;
                    }
                    if (c == state.Dialect.Delimiter[0])
                    {
                        SaveField();
                        stateCode = ParserState.StartField;
                        break;
                    }
                    if (c is '\n' or '\r')
                    {
                        SaveField();
                        stateCode = ParserState.EatCrnl;
                        break;
                    }
                    if (state.Dialect.Strict)
                    {
                        throw CsvError($"'{state.Dialect.Delimiter}' expected after '\"'", span);
                    }
                    Append(field, c, state, span);
                    stateCode = ParserState.InField;
                    break;
                case ParserState.EatCrnl:
                    if (c is '\n' or '\r')
                    {
                        break;
                    }
                    throw CsvError(
                        "new-line character seen in unquoted field - do you need to open the file with newline=''?",
                        span
                    );
            }
        }
    }

    private enum ParserState
    {
        StartRecord,
        StartField,
        InField,
        EscapedChar,
        InQuotedField,
        EscapeInQuotedField,
        QuoteInQuotedField,
        EatCrnl,
    }

    private static void Append(StringBuilder field, char c, ReaderState state, TextSpan span)
    {
        if (field.Length >= state.FieldLimit)
        {
            throw CsvError($"field larger than field limit ({state.FieldLimit})", span);
        }
        field.Append(c);
    }

    private static PythonListValue RowValue(List<CsvField> fields, ReaderState state, TextSpan span)
    {
        var result = new List<PythonValue>(fields.Count);
        foreach (var item in fields)
        {
            if (
                !item.Quoted
                && item.Text.Length != 0
                && state.Dialect.Quoting is QuoteNonNumeric or QuoteStrings
            )
            {
                result.Add(ParseCsvFloat(item.Text, span));
            }
            else
            {
                result.Add(Text(item.Text));
            }
        }
        return new PythonListValue(result);
    }

    private static PythonFloatingPointValue ParseCsvFloat(string text, TextSpan span)
    {
        var trimmed = text.Trim();
        var normalized = trimmed.StartsWith('+') ? trimmed[1..] : trimmed;
        double parsed;
        if (
            string.Equals(normalized, "inf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "infinity", StringComparison.OrdinalIgnoreCase)
        )
        {
            parsed = double.PositiveInfinity;
        }
        else if (
            string.Equals(normalized, "-inf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "-infinity", StringComparison.OrdinalIgnoreCase)
        )
        {
            parsed = double.NegativeInfinity;
        }
        else if (
            string.Equals(normalized, "nan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "-nan", StringComparison.OrdinalIgnoreCase)
        )
        {
            parsed = double.NaN;
        }
        else
        {
            {
                var cleaned = RemoveNumericUnderscores(trimmed);
                if (
                    cleaned.Length == 0
                    || !double.TryParse(
                        cleaned,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out parsed
                    )
                )
                {
                    throw ValueError(
                        "could not convert string to float: "
                            + new PythonTextValue(text).ToRepresentationString(),
                        span
                    );
                }
            }
        }
        return new PythonFloatingPointValue(parsed);
    }

    private static string RemoveNumericUnderscores(string text)
    {
        if (!text.Contains('_', StringComparison.Ordinal))
        {
            return text;
        }
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (
                text[index] == '_'
                && index > 0
                && index + 1 < text.Length
                && char.IsAsciiDigit(text[index - 1])
                && char.IsAsciiDigit(text[index + 1])
            )
            {
                continue;
            }
            builder.Append(text[index]);
        }
        return builder.ToString();
    }

    // ---------------------------------------------------------------------------------
    // Writer
    // ---------------------------------------------------------------------------------

    private sealed class WriterState(PythonValue write, DialectConfig dialect)
    {
        internal PythonValue Write { get; } = write;
        internal DialectConfig Dialect { get; } = dialect;
    }

    private static PythonManagedTypeValue CreateWriterType()
    {
        var type = new PythonManagedTypeValue("writer") { Module = CoreModule };
        type.Attributes["__module__"] = Text(CoreModule);
        type.Attributes["__qualname__"] = Text("writer");
        type.Attributes["writerow"] = Method(
            "writer",
            "writerow",
            (self, arguments) =>
            {
                if (arguments.Count != 1)
                {
                    throw TypeError(
                        $"writerow() takes exactly one argument ({arguments.Count} given)",
                        default
                    );
                }
                return WriteRow(WriterOf(self!), arguments[0], default);
            }
        );
        type.Attributes["writerows"] = Method(
            "writer",
            "writerows",
            (self, arguments) =>
            {
                if (arguments.Count != 1)
                {
                    throw TypeError(
                        $"writerows() takes exactly one argument ({arguments.Count} given)",
                        default
                    );
                }
                var iterator = RowIterator(arguments[0], default);
                while (ManagedObjectProtocols.TryGetNext(iterator, out var row, default))
                {
                    _ = WriteRow(WriterOf(self!), row, default);
                }
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["dialect"] = Property(
            "writer",
            "dialect",
            self => DialectInstance(WriterOf(self).Dialect)
        );
        return type;
    }

    private static WriterState WriterOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: WriterState state }
            ? state
            : throw TypeError("the value is not a csv writer", default);

    private static PythonManagedObjectValue CreateWriter(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var (target, dialectSpec, options) = BindDialectCall(
            positional,
            names,
            values,
            span,
            "writer",
            2
        );
        PythonValue write;
        try
        {
            write = ManagedObjectProtocols.GetAttribute(target, "write", span);
        }
        catch (PythonRuntimeException error)
            when (error.PythonExceptionTypeName == "AttributeError")
        {
            throw TypeError("argument 1 must have a \"write\" method", span);
        }
        var dialect = ResolveDialect(
            dialectSpec,
            [.. options.Select(option => option.Name)],
            [.. options.Select(option => option.Value)],
            span
        );
        return new PythonManagedObjectValue(WriterType, new WriterState(write, dialect));
    }

    private static PythonIteratorValue RowIterator(PythonValue row, TextSpan span)
    {
        try
        {
            return ManagedObjectProtocols.GetIterator(row, span);
        }
        catch (PythonRuntimeException error) when (error.PythonExceptionTypeName == "TypeError")
        {
            throw CsvError(
                $"iterable expected, not {ManagedObjectProtocols.GetTypeName(row)}",
                span
            );
        }
    }

    private static PythonValue WriteRow(WriterState state, PythonValue row, TextSpan span)
    {
        var dialect = state.Dialect;
        var iterator = RowIterator(row, span);
        var texts = new List<(string Text, bool Quoted)>();
        while (ManagedObjectProtocols.TryGetNext(iterator, out var value, span))
        {
            texts.Add(RenderField(value, dialect, span));
        }
        var builder = new StringBuilder();
        if (texts.Count == 0)
        {
            builder.Append(dialect.LineTerminator);
        }
        else if (
            texts.Count == 1
            && texts[0].Text.Length == 0
            && !texts[0].Quoted
            && dialect.Quoting == QuoteNone
        )
        {
            throw CsvError("single empty field record must be quoted", span);
        }
        else
        {
            for (var index = 0; index < texts.Count; index++)
            {
                var (text, quoted) = texts[index];
                if (texts.Count == 1 && text.Length == 0 && !quoted && dialect.Quoting != QuoteNone)
                {
                    quoted = true;
                }
                if (index != 0)
                {
                    builder.Append(dialect.Delimiter);
                }
                AppendRendered(builder, text, quoted, dialect, span);
            }
            builder.Append(dialect.LineTerminator);
        }
        return UserObjectProtocols.Dispatcher!.Invoke(
            state.Write,
            [new PythonTextValue(builder.ToString())],
            span
        );
    }

    private static (string Text, bool Quoted) RenderField(
        PythonValue value,
        DialectConfig dialect,
        TextSpan span
    )
    {
        var isNone = value is PythonNoneValue;
        string text;
        if (isNone)
        {
            text = string.Empty;
        }
        else
        {
            var converted = ManagedObjectProtocols.Call(PythonBuiltinTypes.Str, [value], span);
            text = converted is PythonTextValue textValue ? textValue.Value : converted.ToString();
        }
        var quoted = dialect.Quoting switch
        {
            QuoteAll => true,
            QuoteNonNumeric => !IsNumeric(value),
            QuoteStrings => value is PythonTextValue,
            QuoteNotNull => !isNone,
            QuoteNone => false,
            _ => NeedsQuotes(text, dialect),
        };
        return (text, quoted);
    }

    private static bool NeedsQuotes(string text, DialectConfig dialect)
    {
        foreach (var c in text)
        {
            if (
                c == dialect.Delimiter[0]
                || (dialect.DoubleQuote && dialect.QuoteChar is { } quote && c == quote[0])
                || c is '\r' or '\n'
            )
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsNumeric(PythonValue value) =>
        value
            is PythonWholeNumberValue
                or PythonFloatingPointValue
                or PythonTruthValue
                or PythonComplexValue;

    private static void AppendRendered(
        StringBuilder builder,
        string text,
        bool quoted,
        DialectConfig dialect,
        TextSpan span
    )
    {
        var quote = dialect.QuoteChar;
        var escape = dialect.EscapeChar;
        if (quoted)
        {
            if (quote is null)
            {
                throw TypeError("quotechar must be set if quoting enabled", span);
            }
            builder.Append(quote);
            foreach (var c in text)
            {
                if (c == quote[0])
                {
                    if (dialect.DoubleQuote)
                    {
                        builder.Append(c).Append(c);
                    }
                    else if (escape is not null)
                    {
                        builder.Append(escape).Append(c);
                    }
                    else
                    {
                        throw CsvError("need to escape, but no escapechar set", span);
                    }
                    continue;
                }
                if (escape is not null && c == escape[0])
                {
                    builder.Append(escape).Append(c);
                    continue;
                }
                builder.Append(c);
            }
            builder.Append(quote);
            return;
        }
        foreach (var c in text)
        {
            var special =
                c == dialect.Delimiter[0]
                || (quote is not null && c == quote[0])
                || c is '\r' or '\n'
                || (escape is not null && c == escape[0]);
            if (special)
            {
                if (escape is null)
                {
                    throw CsvError("need to escape, but no escapechar set", span);
                }
                builder.Append(escape).Append(c);
                continue;
            }
            builder.Append(c);
        }
    }

    private static long FieldSizeLimit(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count == 0)
        {
            return _fieldSizeLimit;
        }
        if (arguments.Count > 1)
        {
            throw TypeError(
                $"field_size_limit() takes at most 1 argument ({arguments.Count} given)",
                span
            );
        }
        if (arguments[0] is not PythonWholeNumberValue whole)
        {
            throw TypeError("limit must be an integer", span);
        }
        var previous = _fieldSizeLimit;
        _fieldSizeLimit = (long)whole.Value;
        return previous;
    }

    // ---------------------------------------------------------------------------------
    // DictReader / DictWriter
    // ---------------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateDictReaderType()
    {
        var type = new PythonManagedTypeValue("DictReader") { Module = Module };
        type.Attributes["__module__"] = Text(Module);
        type.Attributes["__qualname__"] = Text("DictReader");
        type.Attributes["__iter__"] = Method("DictReader", "__iter__", (self, _) => self!);
        type.Attributes["__next__"] = Method(
            "DictReader",
            "__next__",
            (self, _) => NextDictRow(self!, default)
        );
        type.Attributes["fieldnames"] = new PythonPropertyValue(
            new PythonProtocolFunctionValue(
                "fieldnames",
                (_, arguments) =>
                    arguments.Count == 1
                        ? DictReaderFieldNames(arguments[0], default)
                        : throw TypeError("fieldnames takes no arguments", default)
            ),
            new PythonProtocolFunctionValue(
                "fieldnames",
                (_, arguments) =>
                {
                    if (arguments.Count != 2)
                    {
                        throw TypeError("fieldnames must be set to one value", default);
                    }
                    ManagedObjectProtocols.SetAttribute(arguments[0], "_fieldnames", arguments[1]);
                    return PythonNoneValue.Instance;
                }
            ),
            null
        );
        type.Attributes["line_num"] = Property(
            "DictReader",
            "line_num",
            self => PythonWholeNumberValue.Create(ReaderOf(DictReaderReader(self!)).LineNumber)
        );
        return type;
    }

    private static PythonValue DictReaderReader(PythonValue value) =>
        ManagedObjectProtocols.GetAttribute(value, "reader", default);

    private static PythonValue DictReaderFieldNames(PythonValue value, TextSpan span)
    {
        var fieldnames = ManagedObjectProtocols.GetAttribute(value, "_fieldnames", span);
        if (fieldnames is not PythonNoneValue)
        {
            return fieldnames;
        }
        PythonValue row;
        try
        {
            row = ReadRow(ReaderOf(DictReaderReader(value)), span);
        }
        catch (PythonRaisedException error) when (error.Value.TypeName == "StopIteration")
        {
            return PythonNoneValue.Instance;
        }
        ManagedObjectProtocols.SetAttribute(value, "_fieldnames", row, span);
        return row;
    }

    private static PythonDictionaryValue NextDictRow(PythonValue value, TextSpan span)
    {
        var reader = DictReaderReader(value);
        var fieldnames = DictReaderFieldNames(value, span);
        if (fieldnames is PythonNoneValue)
        {
            throw new PythonRaisedException(
                ManagedObjectProtocols.CreateStopIteration(PythonNoneValue.Instance)
            );
        }
        PythonValue row;
        while (true)
        {
            row = ReadRow(ReaderOf(reader), span);
            if (((PythonListValue)row).Elements.Count != 0)
            {
                break;
            }
        }
        var names = (PythonListValue)fieldnames;
        var fields = (PythonListValue)row;
        var result = new PythonDictionaryValue([]);
        var shared = Math.Min(names.Elements.Count, fields.Elements.Count);
        for (var index = 0; index < shared; index++)
        {
            ManagedObjectProtocols.SetDictionaryItem(
                result,
                names.Elements[index],
                fields.Elements[index],
                span
            );
        }
        if (fields.Elements.Count > names.Elements.Count)
        {
            var restKey = ManagedObjectProtocols.GetAttribute(value, "restkey", span);
            ManagedObjectProtocols.SetDictionaryItem(
                result,
                restKey,
                new PythonListValue([.. fields.Elements.Skip(names.Elements.Count)]),
                span
            );
        }
        else if (names.Elements.Count > fields.Elements.Count)
        {
            var restVal = ManagedObjectProtocols.GetAttribute(value, "restval", span);
            for (var index = fields.Elements.Count; index < names.Elements.Count; index++)
            {
                ManagedObjectProtocols.SetDictionaryItem(
                    result,
                    names.Elements[index],
                    restVal,
                    span
                );
            }
        }
        return result;
    }

    private static PythonManagedObjectValue CreateDictReader(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        if (positional.Count == 0)
        {
            throw TypeError(
                "DictReader.__init__() missing 1 required positional argument: 'f'",
                span
            );
        }
        if (positional.Count > 5)
        {
            throw TypeError(
                $"DictReader expected at most 5 arguments, got {positional.Count}",
                span
            );
        }
        PythonValue? fieldnames = positional.Count > 1 ? positional[1] : null;
        PythonValue? restkey = positional.Count > 2 ? positional[2] : null;
        PythonValue? restval = positional.Count > 3 ? positional[3] : null;
        PythonValue? dialect = positional.Count > 4 ? positional[4] : null;
        var readerNames = new List<string>();
        var readerValues = new List<PythonValue>();
        for (var index = 0; index < names.Count; index++)
        {
            switch (names[index])
            {
                case "fieldnames":
                    fieldnames = values[index];
                    break;
                case "restkey":
                    restkey = values[index];
                    break;
                case "restval":
                    restval = values[index];
                    break;
                case "dialect":
                    dialect = values[index];
                    break;
                default:
                    if (Array.IndexOf(OptionNames, names[index]) < 0)
                    {
                        throw TypeError(
                            $"this function got an unexpected keyword argument '{names[index]}'",
                            span
                        );
                    }
                    readerNames.Add(names[index]);
                    readerValues.Add(values[index]);
                    break;
            }
        }
        var readerArguments = new List<PythonValue> { positional[0] };
        if (dialect is not null)
        {
            readerArguments.Add(dialect);
        }
        var reader = CreateReader(readerArguments, readerNames, readerValues, span);
        var instance = new PythonManagedObjectValue(DictReaderType);
        instance.Attributes["reader"] = reader;
        instance.Attributes["restkey"] = restkey ?? PythonNoneValue.Instance;
        instance.Attributes["restval"] = restval ?? PythonNoneValue.Instance;
        instance.Attributes["_fieldnames"] = fieldnames is null
            ? PythonNoneValue.Instance
            : MaterializeList(fieldnames, span);
        return instance;
    }

    private static PythonManagedTypeValue CreateDictWriterType()
    {
        var type = new PythonManagedTypeValue("DictWriter") { Module = Module };
        type.Attributes["__module__"] = Text(Module);
        type.Attributes["__qualname__"] = Text("DictWriter");
        type.Attributes["writeheader"] = Method(
            "DictWriter",
            "writeheader",
            (self, _) =>
            {
                var fieldnames = RequireDictWriterFieldNames(self!, default);
                var header = new PythonDictionaryValue([]);
                foreach (var name in fieldnames.Elements)
                {
                    ManagedObjectProtocols.SetDictionaryItem(header, name, name, default);
                }
                return WriteDictRow(self!, header, default);
            }
        );
        type.Attributes["writerow"] = Method(
            "DictWriter",
            "writerow",
            (self, arguments) =>
            {
                if (arguments.Count != 1)
                {
                    throw TypeError(
                        $"writerow() takes exactly one argument ({arguments.Count} given)",
                        default
                    );
                }
                return WriteDictRow(self!, arguments[0], default);
            }
        );
        type.Attributes["writerows"] = Method(
            "DictWriter",
            "writerows",
            (self, arguments) =>
            {
                if (arguments.Count != 1)
                {
                    throw TypeError(
                        $"writerows() takes exactly one argument ({arguments.Count} given)",
                        default
                    );
                }
                var iterator = ManagedObjectProtocols.GetIterator(arguments[0], default);
                while (ManagedObjectProtocols.TryGetNext(iterator, out var row, default))
                {
                    _ = WriteDictRow(self!, row, default);
                }
                return PythonNoneValue.Instance;
            }
        );
        return type;
    }

    private static PythonManagedObjectValue CreateDictWriter(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        if (positional.Count == 0)
        {
            throw TypeError("DictWriter expected at least 1 argument, got 0", span);
        }
        if (positional.Count > 5)
        {
            throw TypeError(
                $"DictWriter expected at most 5 arguments, got {positional.Count}",
                span
            );
        }
        PythonValue? fieldnames = positional.Count > 1 ? positional[1] : null;
        PythonValue? restval = positional.Count > 2 ? positional[2] : null;
        var extrasaction =
            positional.Count > 3 ? RequireExtrasAction(positional[3], span) : "raise";
        PythonValue? dialect = positional.Count > 4 ? positional[4] : null;
        var writerNames = new List<string>();
        var writerValues = new List<PythonValue>();
        for (var index = 0; index < names.Count; index++)
        {
            switch (names[index])
            {
                case "fieldnames":
                    fieldnames = values[index];
                    break;
                case "restval":
                    restval = values[index];
                    break;
                case "extrasaction":
                    extrasaction = RequireExtrasAction(values[index], span);
                    break;
                case "dialect":
                    dialect = values[index];
                    break;
                default:
                    if (Array.IndexOf(OptionNames, names[index]) < 0)
                    {
                        throw TypeError(
                            $"this function got an unexpected keyword argument '{names[index]}'",
                            span
                        );
                    }
                    writerNames.Add(names[index]);
                    writerValues.Add(values[index]);
                    break;
            }
        }
        if (fieldnames is null)
        {
            throw TypeError(
                "DictWriter.__init__() missing 1 required positional argument: 'fieldnames'",
                span
            );
        }
        var writerArguments = new List<PythonValue> { positional[0] };
        if (dialect is not null)
        {
            writerArguments.Add(dialect);
        }
        var writer = CreateWriter(writerArguments, writerNames, writerValues, span);
        var instance = new PythonManagedObjectValue(DictWriterType);
        instance.Attributes["writer"] = writer;
        instance.Attributes["fieldnames"] =
            fieldnames is PythonNoneValue
                ? PythonNoneValue.Instance
                : MaterializeList(fieldnames, span);
        instance.Attributes["restval"] = restval ?? Text(string.Empty);
        instance.Attributes["extrasaction"] = Text(extrasaction);
        return instance;
    }

    private static string RequireExtrasAction(PythonValue value, TextSpan span)
    {
        if (value is not PythonTextValue text)
        {
            throw ManagedObjectProtocols.Fault(
                ErrorCode,
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object has no attribute 'lower'",
                span,
                "AttributeError"
            );
        }
        if (string.Equals(text.Value, "raise", StringComparison.OrdinalIgnoreCase))
        {
            return "raise";
        }
        if (string.Equals(text.Value, "ignore", StringComparison.OrdinalIgnoreCase))
        {
            return "ignore";
        }
        throw ValueError($"extrasaction ({text.Value}) must be 'raise' or 'ignore'", span);
    }

    private static PythonListValue MaterializeList(PythonValue value, TextSpan span) =>
        new([.. ManagedObjectProtocols.MaterializeValues(value, span)]);

    private static PythonValue WriteDictRow(PythonValue value, PythonValue rowdict, TextSpan span)
    {
        var fieldnames = RequireDictWriterFieldNames(value, span);
        var restval = ManagedObjectProtocols.GetAttribute(value, "restval", span);
        var extrasaction = (PythonTextValue)
            ManagedObjectProtocols.GetAttribute(value, "extrasaction", span);
        var extras = new List<PythonValue>();
        foreach (var key in ManagedObjectProtocols.MaterializeValues(rowdict, span))
        {
            if (!ContainsValue(fieldnames, key))
            {
                extras.Add(key);
            }
        }
        if (extrasaction.Value == "raise" && extras.Count != 0)
        {
            throw ValueError(
                "dict contains fields not in fieldnames: "
                    + string.Join(", ", extras.Select(key => key.ToRepresentationString())),
                span
            );
        }
        var row = new List<PythonValue>(fieldnames.Elements.Count);
        foreach (var name in fieldnames.Elements)
        {
            try
            {
                row.Add(ManagedObjectProtocols.GetItem(rowdict, name, span));
            }
            catch (PythonRaisedException error) when (error.Value.TypeName == "KeyError")
            {
                row.Add(restval);
            }
        }
        var writer = ManagedObjectProtocols.GetAttribute(value, "writer", span);
        return WriteRow(WriterOf(writer), new PythonListValue(row), span);
    }

    private static PythonListValue RequireDictWriterFieldNames(PythonValue value, TextSpan span) =>
        ManagedObjectProtocols.GetAttribute(value, "fieldnames", span) is PythonListValue list
            ? list
            : throw TypeError(
                "unsupported operand type(s) for -: 'dict_keys' and 'NoneType'",
                span
            );

    private static bool ContainsValue(PythonListValue list, PythonValue value) =>
        list.Elements.Any(entry =>
            entry is PythonTextValue left && value is PythonTextValue right
                ? left.Value == right.Value
                : ReferenceEquals(entry, value) || entry.Equals(value)
        );

    // ---------------------------------------------------------------------------------
    // Sniffer
    // ---------------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateSnifferType()
    {
        var type = new PythonManagedTypeValue("Sniffer") { Module = Module };
        type.Attributes["__module__"] = Text(Module);
        type.Attributes["__qualname__"] = Text("Sniffer");
        type.Attributes["sniff"] = Method(
            "Sniffer",
            "sniff",
            (self, arguments) => Sniff(self!, arguments, default),
            (self, positional, names, values) => SniffKeywords(self, positional, names, values)
        );
        type.Attributes["has_header"] = Method(
            "Sniffer",
            "has_header",
            (self, arguments) => HasHeader(self!, arguments, default),
            (self, positional, names, values) => HasHeaderKeywords(self, positional, names, values)
        );
        type.Attributes["_guess_quote_and_delimiter"] = Method(
            "Sniffer",
            "_guess_quote_and_delimiter",
            (_, arguments) => GuessQuoteMethod(arguments, default)
        );
        type.Attributes["_guess_delimiter"] = Method(
            "Sniffer",
            "_guess_delimiter",
            (self, arguments) => GuessDelimiterMethod(self!, arguments, default)
        );
        return type;
    }

    private static PythonManagedTypeValue Sniff(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
        {
            throw TypeError(
                "Sniffer.sniff() missing 1 required positional argument: 'sample'",
                span
            );
        }
        if (arguments.Count > 2)
        {
            throw TypeError(
                "Sniffer.sniff() takes from 2 to 3 positional arguments but "
                    + $"{arguments.Count + 1} were given",
                span
            );
        }
        if (arguments[0] is not PythonTextValue sample)
        {
            throw ManagedObjectProtocols.Fault(
                ErrorCode,
                $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}' object has no attribute 'replace'",
                span,
                "AttributeError"
            );
        }
        var delimiters = DelimitersValue(
            arguments.Count > 1 ? arguments[1] : PythonNoneValue.Instance,
            span
        );
        var data = sample.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var (quotechar, doublequote, delimiter, skipinitialspace) = GuessQuoteAndDelimiter(
            data,
            delimiters
        );
        if (delimiter.Length == 0)
        {
            var guessed = GuessDelimiter(self, data, delimiters);
            delimiter = guessed.Delimiter;
            skipinitialspace = guessed.SkipInitialSpace;
        }
        if (delimiter.Length == 0)
        {
            throw CsvError("Could not determine delimiter", span);
        }
        var type = new PythonManagedTypeValue("dialect") { Module = Module };
        type.Attributes["__module__"] = Text(Module);
        type.Attributes["__qualname__"] = Text("Sniffer.sniff.<locals>.dialect");
        type.Attributes["_name"] = Text("sniffed");
        type.Attributes["lineterminator"] = Text("\r\n");
        type.Attributes["quoting"] = PythonWholeNumberValue.Create(QuoteMinimal);
        type.Attributes["delimiter"] = Text(delimiter);
        type.Attributes["doublequote"] = PythonTruthValue.FromBoolean(doublequote);
        type.Attributes["quotechar"] = Text(quotechar.Length == 0 ? "\"" : quotechar);
        type.Attributes["skipinitialspace"] = PythonTruthValue.FromBoolean(skipinitialspace);
        type.Attributes["escapechar"] = PythonNoneValue.Instance;
        type.Attributes["strict"] = PythonTruthValue.False;
        return type;
    }

    private static PythonManagedTypeValue SniffKeywords(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var arguments = BindSniffArguments(positional, names, values, "sniff");
        return Sniff(self!, arguments, default);
    }

    private static PythonTruthValue HasHeaderKeywords(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var arguments = BindSniffArguments(positional, names, values, "has_header");
        return HasHeader(self!, arguments, default);
    }

    private static List<PythonValue> BindSniffArguments(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        string function
    )
    {
        if (positional.Count > 2)
        {
            throw TypeError(
                $"{function}() takes from 1 to 2 positional arguments but {positional.Count} were given",
                default
            );
        }
        PythonValue? sample = positional.Count > 0 ? positional[0] : null;
        PythonValue? delimiters = positional.Count > 1 ? positional[1] : null;
        for (var index = 0; index < names.Count; index++)
        {
            switch (names[index])
            {
                case "sample":
                    sample = values[index];
                    break;
                case "delimiters":
                    delimiters = values[index];
                    break;
                default:
                    throw TypeError(
                        $"Sniffer.{function}() got an unexpected keyword argument '{names[index]}'",
                        default
                    );
            }
        }
        var arguments = new List<PythonValue>();
        if (sample is not null)
        {
            arguments.Add(sample);
        }
        if (delimiters is not null)
        {
            arguments.Add(delimiters);
        }
        return arguments;
    }

    private static PythonTupleValue GuessQuoteMethod(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count < 2)
        {
            throw TypeError(
                arguments.Count == 0
                    ? "Sniffer._guess_quote_and_delimiter() missing 2 required positional arguments: 'data' and 'delimiters'"
                    : "Sniffer._guess_quote_and_delimiter() missing 1 required positional argument: 'delimiters'",
                span
            );
        }
        if (arguments.Count > 2)
        {
            throw TypeError(
                "Sniffer._guess_quote_and_delimiter() takes 3 positional arguments but "
                    + $"{arguments.Count + 1} were given",
                span
            );
        }
        var data = RequireSniffData(arguments[0], span);
        var (quotechar, doublequote, delimiter, skip) = GuessQuoteAndDelimiter(
            data,
            DelimitersValue(arguments[1], span)
        );
        return new PythonTupleValue([
            Text(quotechar),
            PythonTruthValue.FromBoolean(doublequote),
            delimiter.Length == 0 ? PythonNoneValue.Instance : Text(delimiter),
            delimiter.Length == 0
                ? PythonWholeNumberValue.Create(0)
                : PythonTruthValue.FromBoolean(skip),
        ]);
    }

    private static PythonTupleValue GuessDelimiterMethod(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count < 2)
        {
            throw TypeError(
                arguments.Count == 0
                    ? "Sniffer._guess_delimiter() missing 2 required positional arguments: 'data' and 'delimiters'"
                    : "Sniffer._guess_delimiter() missing 1 required positional argument: 'delimiters'",
                span
            );
        }
        if (arguments.Count > 2)
        {
            throw TypeError(
                "Sniffer._guess_delimiter() takes 3 positional arguments but "
                    + $"{arguments.Count + 1} were given",
                span
            );
        }
        var data = RequireSniffData(arguments[0], span);
        var (delimiter, skip) = GuessDelimiter(self, data, DelimitersValue(arguments[1], span));
        return new PythonTupleValue([
            delimiter.Length == 0 ? PythonNoneValue.Instance : Text(delimiter),
            delimiter.Length == 0
                ? PythonWholeNumberValue.Create(0)
                : PythonTruthValue.FromBoolean(skip),
        ]);
    }

    private static string RequireSniffData(PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw ManagedObjectProtocols.Fault(
                ErrorCode,
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object has no attribute 'replace'",
                span,
                "AttributeError"
            );

    private static string[]? DelimitersValue(PythonValue value, TextSpan span)
    {
        if (value is PythonNoneValue)
        {
            return null;
        }
        if (value is PythonTextValue text)
        {
            return [.. text.Value.Select(c => c.ToString())];
        }
        return [.. ManagedObjectProtocols.MaterializeValues(value, span).Select(ValueText)];
    }

    private static string ValueText(PythonValue value) =>
        value is PythonTextValue text ? text.Value : value.ToString();

    private static (
        string QuoteChar,
        bool DoubleQuote,
        string Delimiter,
        bool SkipInitialSpace
    ) GuessQuoteAndDelimiter(string data, string[]? delimiters)
    {
        const string body = @"(?:(?:\k<quote>){2}|(?!\k<quote>).)*";
        string[] scans =
        [
            @"(?<delim>[^\w\n""'])(?<space> ?)(?<quote>[""'])" + body + @"\k<quote>\k<delim>",
            @"(?:^|\n)(?<quote>[""'])" + body + @"\k<quote>(?<delim>[^\w\n""'])(?<space> ?)",
            @"(?<delim>[^\w\n""'])(?<space> ?)(?<quote>[""'])" + body + @"\k<quote>(?:$|\n)",
            @"(?:^|\n)(?<quote>[""'])" + body + @"\k<quote>(?:$|\n)",
        ];
        foreach (var pattern in scans)
        {
            Regex regex;
            MatchCollection matches;
            try
            {
                regex = new Regex(
                    pattern,
                    RegexOptions.Multiline | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(2)
                );
                matches = regex.Matches(data);
            }
            catch (RegexParseException)
            {
                continue;
            }
            if (matches.Count == 0)
            {
                continue;
            }
            var quotes = new Dictionary<string, int>(StringComparer.Ordinal);
            var foundDelims = new Dictionary<string, int>(StringComparer.Ordinal);
            var spaces = 0;
            var quoteGroup = regex.GroupNumberFromName("quote");
            var delimGroup = regex.GroupNumberFromName("delim");
            var spaceGroup = regex.GroupNumberFromName("space");
            foreach (Match match in matches)
            {
                var quote = match.Groups[quoteGroup].Value;
                if (quote.Length != 0)
                {
                    quotes[quote] = quotes.GetValueOrDefault(quote) + 1;
                }
                if (delimGroup >= 0)
                {
                    var delim = match.Groups[delimGroup].Value;
                    if (
                        delim.Length != 0
                        && (delimiters is null || Array.IndexOf(delimiters, delim) >= 0)
                    )
                    {
                        foundDelims[delim] = foundDelims.GetValueOrDefault(delim) + 1;
                    }
                }
                if (spaceGroup >= 0 && match.Groups[spaceGroup].Value.Length != 0)
                {
                    spaces++;
                }
            }
            var quotechar = quotes.Count == 0 ? string.Empty : MaxKey(quotes);
            if (foundDelims.Count == 0)
            {
                return (quotechar, false, string.Empty, false);
            }
            var delimiter = MaxKey(foundDelims);
            var skipInitialSpace = foundDelims[delimiter] == spaces;
            if (delimiter == "\n")
            {
                delimiter = string.Empty;
                skipInitialSpace = false;
            }
            var doubleQuote = false;
            if (delimiter.Length != 0)
            {
                var delimText = Regex.Escape(delimiter);
                var quoteText = Regex.Escape(quotechar);
                var space = delimiter == " " ? string.Empty : " *";
                var dqPattern =
                    $"(?:(?<={delimText})|^){space}{quoteText}"
                    + $"((?:{quoteText}{quoteText}|[^{quoteText}])*)"
                    + $"{quoteText}(?:{delimText}|$)";
                var doubled = new Regex(dqPattern, RegexOptions.Multiline);
                var pair = quotechar + quotechar;
                foreach (Match match in doubled.Matches(data))
                {
                    if (match.Groups[1].Value.Contains(pair, StringComparison.Ordinal))
                    {
                        doubleQuote = true;
                        break;
                    }
                }
            }
            return (quotechar, doubleQuote, delimiter, skipInitialSpace);
        }
        return (string.Empty, false, string.Empty, false);
    }

    private static string MaxKey(Dictionary<string, int> counts)
    {
        var best = string.Empty;
        var bestCount = -1;
        foreach (var (key, count) in counts)
        {
            if (count > bestCount)
            {
                best = key;
                bestCount = count;
            }
        }
        return best;
    }

    private static (string Delimiter, bool SkipInitialSpace) GuessDelimiter(
        PythonValue self,
        string data,
        string[]? delimiters
    )
    {
        var lines = data.Split('\n').Where(line => line.Length != 0).ToList();
        if (lines.Count == 0)
        {
            return (string.Empty, false);
        }
        var ascii = Enumerable.Range(0, 127).Select(code => ((char)code).ToString()).ToList();
        var chunkLength = Math.Min(10, lines.Count);
        var iteration = 0;
        var charFrequency = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var modes = new Dictionary<string, (int Frequency, int Weight)>(StringComparer.Ordinal);
        var found = new Dictionary<string, (int Frequency, int Weight)>(StringComparer.Ordinal);
        var start = 0;
        var end = chunkLength;
        while (start < lines.Count)
        {
            iteration++;
            foreach (var line in lines.Skip(start).Take(end - start))
            {
                foreach (var character in ascii)
                {
                    if (!charFrequency.TryGetValue(character, out var meta))
                    {
                        meta = [];
                        charFrequency[character] = meta;
                    }
                    var frequency = CountOccurrences(line, character);
                    meta[frequency] = meta.GetValueOrDefault(frequency) + 1;
                }
            }
            foreach (var (character, meta) in charFrequency.ToList())
            {
                var items = meta.ToList();
                if (items.Count == 1 && items[0].Key == 0)
                {
                    continue;
                }
                if (items.Count > 1)
                {
                    var mode = items.MaxBy(item => item.Value);
                    items.Remove(mode);
                    modes[character] = (mode.Key, mode.Value - items.Sum(item => item.Value));
                }
                else
                {
                    modes[character] = (items[0].Key, items[0].Value);
                }
            }
            var total = (double)Math.Min(chunkLength * iteration, lines.Count);
            var consistency = 1.0;
            while (found.Count == 0 && consistency >= 0.9)
            {
                foreach (var (character, mode) in modes)
                {
                    if (
                        mode.Frequency > 0
                        && mode.Weight > 0
                        && mode.Weight / total >= consistency
                        && (delimiters is null || Array.IndexOf(delimiters, character) >= 0)
                    )
                    {
                        found[character] = mode;
                    }
                }
                consistency -= 0.01;
            }
            if (found.Count == 1)
            {
                var delimiter = found.Keys.First();
                return (
                    delimiter,
                    CountOccurrences(lines[0], delimiter)
                        == CountOccurrences(lines[0], delimiter + " ")
                );
            }
            start = end;
            end += chunkLength;
        }
        if (found.Count == 0)
        {
            return (string.Empty, false);
        }
        if (found.Count > 1)
        {
            var preferred = (PythonListValue)
                ManagedObjectProtocols.GetAttribute(self, "preferred", default);
            foreach (var value in preferred.Elements)
            {
                if (value is PythonTextValue text && found.ContainsKey(text.Value))
                {
                    return (
                        text.Value,
                        CountOccurrences(lines[0], text.Value)
                            == CountOccurrences(lines[0], text.Value + " ")
                    );
                }
            }
        }
        var chosen = found
            .OrderBy(entry => entry.Value.Weight)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Last()
            .Key;
        return (
            chosen,
            CountOccurrences(lines[0], chosen) == CountOccurrences(lines[0], chosen + " ")
        );
    }

    private static int CountOccurrences(string line, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = line.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static PythonTruthValue HasHeader(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
        {
            throw TypeError(
                "Sniffer.has_header() missing 1 required positional argument: 'sample'",
                span
            );
        }
        if (arguments.Count > 1)
        {
            throw TypeError(
                "Sniffer.has_header() takes 2 positional arguments but "
                    + $"{arguments.Count + 1} were given",
                span
            );
        }
        if (arguments[0] is not PythonTextValue sample)
        {
            throw ManagedObjectProtocols.Fault(
                ErrorCode,
                $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}' object has no attribute 'replace'",
                span,
                "AttributeError"
            );
        }
        var dialect = Sniff(self, [sample], span);
        var rows = ReadSampleRows(sample.Value, dialect, span);
        if (rows.Count == 0)
        {
            throw new PythonRaisedException(
                ManagedObjectProtocols.CreateStopIteration(PythonNoneValue.Instance)
            );
        }
        var header = rows[0];
        var columns = header.Count;
        var columnTypes = new Dictionary<int, object?>();
        for (var index = 0; index < columns; index++)
        {
            columnTypes[index] = null;
        }
        var checkedRows = 0;
        foreach (var row in rows.Skip(1))
        {
            if (checkedRows > 20)
            {
                break;
            }
            checkedRows++;
            if (row.Count != columns)
            {
                continue;
            }
            foreach (var column in columnTypes.Keys.ToList())
            {
                object thisType = TryParseComplex(row[column]) ? "complex" : row[column].Length;
                if (!Equals(thisType, columnTypes[column]))
                {
                    if (columnTypes[column] is null)
                    {
                        columnTypes[column] = thisType;
                    }
                    else
                    {
                        columnTypes.Remove(column);
                    }
                }
            }
        }
        var votes = 0;
        foreach (var (column, columnType) in columnTypes)
        {
            if (columnType is int length)
            {
                votes += header[column].Length != length ? 1 : -1;
            }
            else if (TryParseComplex(header[column]))
            {
                votes -= 1;
            }
            else
            {
                votes += 1;
            }
        }
        return PythonTruthValue.FromBoolean(votes > 0);
    }

    private static bool TryParseComplex(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static List<List<string>> ReadSampleRows(
        string sample,
        PythonValue dialect,
        TextSpan span
    )
    {
        var lines = new List<PythonValue>();
        foreach (var line in SplitLinesKeepingEnds(sample))
        {
            lines.Add(Text(line));
        }
        var config = ResolveDialectSpec(dialect, span);
        var source = ManagedObjectProtocols.GetIterator(new PythonListValue(lines), span);
        var state = new ReaderState(source, config, _fieldSizeLimit);
        var rows = new List<List<string>>();
        while (true)
        {
            try
            {
                var row = ReadRow(state, span);
                rows.Add([.. ((PythonListValue)row).Elements.Select(ValueText)]);
            }
            catch (PythonRaisedException error) when (error.Value.TypeName == "StopIteration")
            {
                break;
            }
        }
        return rows;
    }

    private static IEnumerable<string> SplitLinesKeepingEnds(string sample)
    {
        var start = 0;
        for (var index = 0; index < sample.Length; index++)
        {
            if (sample[index] == '\n')
            {
                yield return sample[start..(index + 1)];
                start = index + 1;
            }
        }
        if (start < sample.Length)
        {
            yield return sample[start..];
        }
    }

    // ---------------------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------------------

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        ProtocolKeywordInvoker? keywords = null
    ) => PythonUserTypes.Method(owner, name, body, keywords);

    private static PythonPropertyValue Property(
        string owner,
        string name,
        Func<PythonValue, PythonValue> getter
    ) =>
        new(
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) =>
                {
                    if (arguments.Count != 1)
                    {
                        throw TypeError(
                            $"{name}() takes no arguments ({arguments.Count - 1} given)",
                            default
                        );
                    }
                    return getter(arguments[0]);
                }
            ),
            new PythonProtocolFunctionValue(
                name,
                (_, _) =>
                    throw ManagedObjectProtocols.Fault(
                        ErrorCode,
                        "readonly attribute",
                        default,
                        "AttributeError"
                    )
            ),
            null
        );
}
