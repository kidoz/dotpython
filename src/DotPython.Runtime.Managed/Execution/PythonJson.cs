// Encoder and decoder semantics follow CPython 3.14.7 Lib/json/{__init__,encoder,decoder}.py:
// https://github.com/python/cpython/blob/v3.14.7/Lib/json/decoder.py
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>json</c> module: <c>dumps</c>, <c>loads</c>, and <c>JSONDecodeError</c>.
/// The module is pure JSON text handling; file objects (<c>dump</c>/<c>load</c>),
/// custom encoders/decoders (<c>cls=</c>), and the hook parameters
/// (<c>default</c> excepted on the encode side) are outside this slice and are
/// rejected rather than approximated.
/// </summary>
internal static class PythonJson
{
    private const string ErrorCode = "DPY4039";
    private const string SliceCode = "DPY4037";

    /// <summary>
    /// The nesting depth accepted before the recursive walk reports RecursionError.
    /// The walk is native, so it is bounded independently of the frame limit.
    /// </summary>
    private const int MaximumNesting = 1000;

    private const string DecodeErrorModule = "json.decoder";

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        var decodeError = CreateDecodeErrorType();
        globals.SetValue("JSONDecodeError", decodeError);
        globals.SetValue("dumps", CreateDumps());
        globals.SetValue("loads", CreateLoads(decodeError));
    }

    // ---------------------------------------------------------------------------------
    // JSONDecodeError
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// <c>json.decoder.JSONDecodeError</c>, the ValueError subclass the decoder raises.
    /// A native module initializer runs without a class-body frame, so the equivalent of
    /// the class statement is built directly: the base, the installed resolution order,
    /// the <c>__module__</c>/<c>__qualname__</c> entries class creation would place in
    /// the namespace, and the <c>__init__</c> that computes <c>lineno</c>/<c>colno</c>.
    /// </summary>
    private static PythonManagedTypeValue CreateDecodeErrorType()
    {
        var exceptionBase = PythonBuiltinTypes.GetExceptionType("ValueError");
        var type = new PythonManagedTypeValue("JSONDecodeError", exceptionBaseName: "ValueError")
        {
            Module = DecodeErrorModule,
            LayoutBase = exceptionBase,
        };
        type.Attributes["__module__"] = new PythonTextValue(DecodeErrorModule);
        type.Attributes["__qualname__"] = new PythonTextValue("JSONDecodeError");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Subclass of ValueError with the following additional properties:\n"
                + "\n"
                + "msg: The unformatted error message\n"
                + "doc: The JSON document being parsed\n"
                + "pos: The start index of doc where parsing failed\n"
                + "lineno: The line corresponding to pos\n"
                + "colno: The column corresponding to pos"
        );
        type.Attributes["__init__"] = new PythonProtocolFunctionValue(
            "__init__",
            (receiver, arguments) => InitializeDecodeError(receiver, arguments, [], [], default),
            (receiver, arguments, names, values) =>
                InitializeDecodeError(receiver, arguments, names, values, default)
        );
        type.SetDeclaredBases(new PythonTupleValue([exceptionBase]));
        type.SetResolutionOrder(
            new PythonTupleValue([
                type,
                exceptionBase,
                PythonBuiltinTypes.GetExceptionType("Exception"),
                PythonBuiltinTypes.GetExceptionType("BaseException"),
                PythonBuiltinFunctions.Object,
            ])
        );
        return type;
    }

    private static PythonNoneValue InitializeDecodeError(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (receiver is null)
        {
            if (positional.Count == 0)
                throw Fault(
                    "descriptor '__init__' of 'JSONDecodeError' object needs an argument",
                    span
                );
            receiver = positional[0];
            positional = [.. positional.Skip(1)];
        }
        if (receiver is not PythonExceptionValue exception)
            throw Fault(
                $"descriptor '__init__' requires a 'JSONDecodeError' object but received a '{ManagedObjectProtocols.GetTypeName(receiver)}'",
                span
            );

        var slots = new PythonValue?[3];
        string[] parameters = ["msg", "doc", "pos"];
        if (positional.Count > 3)
            throw Fault(
                $"JSONDecodeError.__init__() takes 4 positional arguments but {positional.Count + 1} were given",
                span
            );
        for (var index = 0; index < positional.Count; index++)
            slots[index] = positional[index];
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0)
                throw Fault(
                    $"JSONDecodeError.__init__() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            if (slots[slot] is not null)
                throw Fault(
                    $"JSONDecodeError.__init__() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            slots[slot] = keywordValues[index];
        }
        var missing = new List<string>();
        for (var index = 0; index < slots.Length; index++)
            if (slots[index] is null)
                missing.Add($"'{parameters[index]}'");
        if (missing.Count > 0)
            throw Fault(
                missing.Count == 1
                    ? $"JSONDecodeError.__init__() missing 1 required positional argument: {missing[0]}"
                    : $"JSONDecodeError.__init__() missing {missing.Count} required positional arguments: {JoinNames(missing)}",
                span
            );

        var message = slots[0]!;
        if (slots[1] is not PythonTextValue document)
            // CPython's __init__ calls doc.count(...) first, so a non-str doc is
            // reported as the missing attribute rather than a type error.
            throw Fault(
                $"'{ManagedObjectProtocols.GetTypeName(slots[1]!)}' object has no attribute 'count'",
                span,
                "AttributeError"
            );
        var position = ReadDecoderIndex(slots[2]!, span);

        // CPython's errmsg formatting accepts any message object; only doc and pos
        // are used arithmetically.
        var text = document.Value;
        var line = CountNewlines(text, 0, position) + 1;
        var column = position - LastNewline(text, 0, position);
        var formatted =
            $"{message.ToDisplayString()}: line {line} column {column} (char {position})";
        ManagedObjectProtocols.ApplyBaseExceptionInit(exception, [new PythonTextValue(formatted)]);
        exception.Attributes["msg"] = message;
        exception.Attributes["doc"] = document;
        exception.Attributes["pos"] = PythonWholeNumberValue.Create(position);
        exception.Attributes["lineno"] = PythonWholeNumberValue.Create(line);
        exception.Attributes["colno"] = PythonWholeNumberValue.Create(column);
        return PythonNoneValue.Instance;
    }

    private static PythonRaisedException DecodeFault(
        PythonManagedTypeValue decodeError,
        string message,
        string document,
        int position
    )
    {
        var line = CountNewlines(document, 0, position) + 1;
        var column = position - LastNewline(document, 0, position);
        var formatted = $"{message}: line {line} column {column} (char {position})";
        var exception = new PythonExceptionValue("JSONDecodeError", formatted)
        {
            ManagedType = decodeError,
            Arguments = [new PythonTextValue(formatted)],
        };
        exception.Attributes["msg"] = new PythonTextValue(message);
        exception.Attributes["doc"] = new PythonTextValue(document);
        exception.Attributes["pos"] = PythonWholeNumberValue.Create(position);
        exception.Attributes["lineno"] = PythonWholeNumberValue.Create(line);
        exception.Attributes["colno"] = PythonWholeNumberValue.Create(column);
        return new PythonRaisedException(exception);
    }

    /// <summary>CPython's argument list: `'a'`, `'a' and 'b'`, `'a', 'b', and 'c'`.</summary>
    private static string JoinNames(List<string> names) =>
        names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + $", and {names[^1]}",
        };

    private static int CountNewlines(string text, int start, int end)
    {
        var count = 0;
        for (var index = start; index < end && index < text.Length; ++index)
            if (text[index] == '\n')
                ++count;
        return count;
    }

    private static int LastNewline(string text, int start, int end)
    {
        for (var index = end - 1; index >= start; --index)
            if (index < text.Length && text[index] == '\n')
                return index;
        return -1;
    }

    private static int ReadDecoderIndex(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonWholeNumberValue whole
                when whole.Value >= int.MinValue && whole.Value <= int.MaxValue => (int)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonWholeNumberValue => throw Fault("position out of range", span, "OverflowError"),
            _ => throw Fault(
                $"unsupported operand type(s) for -: 'int' and '{ManagedObjectProtocols.GetTypeName(value)}'",
                span
            ),
        };

    // ---------------------------------------------------------------------------------
    // dumps
    // ---------------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateDumps() =>
        new(
            "dumps",
            (positional, span) => Dump(positional, [], [], span),
            (positional, names, values, span) => Dump(positional, names, values, span)
        );

    private static PythonTextValue Dump(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        string[] parameters =
        [
            "obj",
            "skipkeys",
            "ensure_ascii",
            "check_circular",
            "allow_nan",
            "cls",
            "indent",
            "separators",
            "default",
            "sort_keys",
        ];
        if (positional.Count > 1)
            throw Fault(
                $"dumps() takes 1 positional argument but {positional.Count} were given",
                span
            );
        var slots = new PythonValue?[parameters.Length];
        if (positional.Count == 1)
            slots[0] = positional[0];
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0)
                // CPython forwards unrecognized keywords to JSONEncoder.__init__.
                throw Fault(
                    $"JSONEncoder.__init__() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            if (slots[slot] is not null)
                throw Fault(
                    $"dumps() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            slots[slot] = keywordValues[index];
        }
        if (slots[0] is null)
            throw Fault("dumps() missing 1 required positional argument: 'obj'", span);
        if (slots[5] is not null and not PythonNoneValue)
            throw Fault(
                "json.dumps() does not support cls in this runtime slice.",
                span,
                "TypeError",
                SliceCode
            );

        var indent = ReadIndent(slots[6], span);
        var (itemSeparator, keySeparator) = ReadSeparators(slots[7], indent is not null, span);
        var options = new EncoderOptions
        {
            SkipKeys = IsTrue(slots[1]),
            EnsureAscii = slots[2] is null || IsTrue(slots[2]),
            CheckCircular = slots[3] is null || IsTrue(slots[3]),
            AllowNan = slots[4] is null || IsTrue(slots[4]),
            Indent = indent,
            ItemSeparator = itemSeparator,
            KeySeparator = keySeparator,
            Default = slots[8] is not PythonNoneValue ? slots[8] : null,
            SortKeys = IsTrue(slots[9]),
        };
        return new PythonTextValue(new Encoder(options, span).Encode(slots[0]!));
    }

    private static string? ReadIndent(PythonValue? value, TextSpan span) =>
        value switch
        {
            null or PythonNoneValue => null,
            PythonTextValue text => text.Value,
            PythonTruthValue truth => truth.Value ? " " : string.Empty,
            PythonWholeNumberValue whole => whole.Value > 0
                ? new string(' ', (int)whole.Value)
                : "",
            _ => throw Fault(
                $"can't multiply sequence by non-int of type '{ManagedObjectProtocols.GetTypeName(value)}'",
                span
            ),
        };

    private static (string Item, string Key) ReadSeparators(
        PythonValue? value,
        bool indented,
        TextSpan span
    )
    {
        if (value is null or PythonNoneValue)
            // CPython's default: the item separator loses its space once the
            // encoder inserts newlines of its own.
            return indented ? (",", ": ") : (", ", ": ");
        var elements = value switch
        {
            PythonTupleValue tuple => (IReadOnlyList<PythonValue>)tuple.Elements,
            PythonListValue list => list.Elements,
            _ => throw Fault(
                $"cannot unpack non-iterable {ManagedObjectProtocols.GetTypeName(value)} object",
                span
            ),
        };
        if (elements.Count < 2)
            throw Fault(
                $"not enough values to unpack (expected 2, got {elements.Count})",
                span,
                "ValueError"
            );
        if (elements.Count > 2)
            throw Fault(
                $"too many values to unpack (expected 2, got {elements.Count})",
                span,
                "ValueError"
            );
        // The key separator is validated first, matching c_make_encoder's argument order.
        var key = RequireSeparator(elements[1], 5, span);
        var item = RequireSeparator(elements[0], 6, span);
        return (item, key);
    }

    private static string RequireSeparator(PythonValue value, int position, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                $"make_encoder() argument {position} must be str, not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            );

    private static bool IsTrue(PythonValue? value) =>
        value is not null && ManagedObjectProtocols.IsTrue(value);

    private sealed class EncoderOptions
    {
        internal bool SkipKeys { get; init; }

        internal bool EnsureAscii { get; init; } = true;

        internal bool CheckCircular { get; init; } = true;

        internal bool AllowNan { get; init; } = true;

        internal bool SortKeys { get; init; }

        internal string? Indent { get; init; }

        internal string ItemSeparator { get; init; } = ", ";

        internal string KeySeparator { get; init; } = ": ";

        internal PythonValue? Default { get; init; }
    }

    // ---------------------------------------------------------------------------------
    // loads
    // ---------------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateLoads(PythonManagedTypeValue decodeError) =>
        new(
            "loads",
            (positional, span) => Load(positional, [], [], decodeError, span),
            (positional, names, values, span) => Load(positional, names, values, decodeError, span)
        );

    private static PythonValue Load(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        PythonManagedTypeValue decodeError,
        TextSpan span
    )
    {
        string[] parameters =
        [
            "s",
            "cls",
            "object_hook",
            "parse_float",
            "parse_int",
            "parse_constant",
            "object_pairs_hook",
            "strict",
        ];
        if (positional.Count > 1)
            throw Fault(
                $"loads() takes 1 positional argument but {positional.Count} were given",
                span
            );
        var slots = new PythonValue?[parameters.Length];
        if (positional.Count == 1)
            slots[0] = positional[0];
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0)
                throw Fault(
                    $"JSONDecoder.__init__() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            if (slots[slot] is not null)
                throw Fault(
                    $"loads() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            slots[slot] = keywordValues[index];
        }
        if (slots[0] is null)
            throw Fault("loads() missing 1 required positional argument: 's'", span);
        for (var index = 1; index < parameters.Length - 1; index++)
            if (slots[index] is not null and not PythonNoneValue)
                throw Fault(
                    $"json.loads() does not support {parameters[index]} in this runtime slice.",
                    span,
                    "TypeError",
                    SliceCode
                );

        var text = ReadDocument(slots[0]!, span);
        if (text.StartsWith('﻿'))
            throw DecodeFault(
                decodeError,
                "Unexpected UTF-8 BOM (decode using utf-8-sig)",
                text,
                0
            );
        return new Decoder(text, decodeError, slots[7] is null || IsTrue(slots[7]), span).Decode();
    }

    private static string ReadDocument(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonTextValue text => text.Value,
            PythonByteSequenceValue bytes => DecodeUtf8(bytes.Value, span),
            PythonByteArrayValue mutable => DecodeUtf8(mutable.Value, span),
            _ => throw Fault(
                $"the JSON object must be str, bytes or bytearray, not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            ),
        };

    private static string DecodeUtf8(byte[] bytes, TextSpan span)
    {
        // CPython runs detect_encoding first, so a UTF-16/UTF-32 BOM selects a
        // different codec. Those codecs are outside this slice.
        if (
            bytes.Length >= 2
            && (
                bytes[0] == 0xff && bytes[1] == 0xfe
                || bytes[0] == 0xfe && bytes[1] == 0xff
                || bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0
                || bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0xfe
            )
        )
            throw Fault(
                "json.loads() only decodes UTF-8 byte input in this runtime slice.",
                span,
                "TypeError",
                SliceCode
            );
        var start =
            bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return PythonBytesDecoding.Decode(
            start == 0 ? bytes : bytes[start..],
            65001,
            false,
            "strict",
            span
        );
    }

    // ---------------------------------------------------------------------------------
    // Encoding
    // ---------------------------------------------------------------------------------

    private sealed class Encoder(EncoderOptions options, TextSpan span)
    {
        private readonly StringBuilder _builder = new();
        private readonly HashSet<PythonValue> _markers = new(ReferenceEqualityComparer.Instance);
        private int _items;
        private int _nesting;

        internal string Encode(PythonValue value)
        {
            Append(value, 0);
            return _builder.ToString();
        }

        private void Append(PythonValue value, int level)
        {
            if ((++_items & 1023) == 0)
                CheckWork();

            switch (value)
            {
                case PythonTextValue text:
                    AppendText(text.Value);
                    return;
                case PythonNoneValue:
                    _builder.Append("null");
                    return;
                case PythonTruthValue truth:
                    _builder.Append(truth.Value ? "true" : "false");
                    return;
                case PythonWholeNumberValue whole:
                    _builder.Append(whole.Value.ToString(CultureInfo.InvariantCulture));
                    return;
                case PythonFloatingPointValue floating:
                    _builder.Append(FloatText(floating.Value));
                    return;
                case PythonListValue list:
                    AppendArray([.. list.Elements], level, value);
                    return;
                case PythonTupleValue tuple:
                    AppendArray(tuple.Elements, level, value);
                    return;
                case PythonDictionaryValue dictionary:
                    AppendObject(dictionary, level);
                    return;
                default:
                    AppendDefault(value, level);
                    return;
            }
        }

        private void AppendDefault(PythonValue value, int level)
        {
            if (options.Default is null)
                throw Fault(
                    $"Object of type {ManagedObjectProtocols.GetTypeName(value)} is not JSON serializable",
                    span
                );
            var marker = options.CheckCircular && Enter(value);
            EnterNesting();
            try
            {
                var replacement = Invoke(options.Default, value);
                Append(replacement, level);
            }
            finally
            {
                ExitNesting();
                if (marker)
                    _markers.Remove(value);
            }
        }

        private void AppendArray(PythonValue[] elements, int level, PythonValue identity)
        {
            if (elements.Length == 0)
            {
                _builder.Append("[]");
                return;
            }
            var marker = options.CheckCircular && Enter(identity);
            EnterNesting();
            try
            {
                var inner = level + 1;
                var separator = options.Indent is { } indent
                    ? options.ItemSeparator + "\n" + Repeat(indent, inner)
                    : options.ItemSeparator;
                _builder.Append('[');
                if (options.Indent is not null)
                    _builder.Append('\n').Append(Repeat(options.Indent, inner));
                for (var index = 0; index < elements.Length; index++)
                {
                    if (index != 0)
                        _builder.Append(separator);
                    Append(elements[index], inner);
                }
                if (options.Indent is not null)
                    _builder.Append('\n').Append(Repeat(options.Indent, level));
                _builder.Append(']');
            }
            finally
            {
                ExitNesting();
                if (marker)
                    _markers.Remove(identity);
            }
        }

        private void AppendObject(PythonDictionaryValue dictionary, int level)
        {
            if (dictionary.Items.Count == 0)
            {
                _builder.Append("{}");
                return;
            }
            var marker = options.CheckCircular && Enter(dictionary);
            EnterNesting();
            try
            {
                var inner = level + 1;
                var newline = options.Indent is { } indent ? "\n" + Repeat(indent, inner) : null;
                var separator = options.Indent is not null
                    ? options.ItemSeparator + newline
                    : options.ItemSeparator;
                _builder.Append('{');
                var first = true;
                foreach (var item in Sorted(dictionary))
                {
                    var key = KeyText(item.Key);
                    if (key is null)
                        continue;
                    if (first)
                    {
                        first = false;
                        if (newline is not null)
                            _builder.Append(newline);
                    }
                    else
                        _builder.Append(separator);
                    AppendText(key);
                    _builder.Append(options.KeySeparator);
                    Append(item.Value, inner);
                }
                if (!first && newline is not null)
                    _builder.Append('\n').Append(Repeat(options.Indent!, level));
                _builder.Append('}');
            }
            finally
            {
                ExitNesting();
                if (marker)
                    _markers.Remove(dictionary);
            }
        }

        private void EnterNesting()
        {
            if (++_nesting > MaximumNesting)
                throw Fault("maximum recursion depth exceeded", span, "RecursionError");
        }

        private void ExitNesting() => _nesting--;

        /// <summary>`sorted(dct.items())`: the keys order the items, and equal keys keep
        /// their insertion order.</summary>
        private List<PythonDictionaryItemValue> Sorted(PythonDictionaryValue dictionary)
        {
            var items = new List<PythonDictionaryItemValue>(dictionary.Items);
            if (!options.SortKeys)
                return items;
            for (var index = 1; index < items.Count; index++)
            {
                var current = items[index];
                var position = index;
                while (position > 0 && Less(current.Key, items[position - 1].Key))
                {
                    items[position] = items[position - 1];
                    position--;
                }
                items[position] = current;
            }
            return items;
        }

        private bool Less(PythonValue left, PythonValue right)
        {
            // Dict keys are usually plain scalars, where CPython reports the operator
            // and both operand types rather than the runtime's generic diagnostic.
            if (IsUnorderedPair(left, right))
                throw Fault(
                    $"'<' not supported between instances of '{ManagedObjectProtocols.GetTypeName(left)}' and '{ManagedObjectProtocols.GetTypeName(right)}'",
                    span
                );
            return ManagedObjectProtocols.IsTrue(
                ManagedObjectProtocols.RichCompareValue(
                    left,
                    right,
                    PythonRichComparison.LessThan,
                    span
                )
            );
        }

        /// <summary>
        /// True when CPython's ordering would reject the pair outright: values of two
        /// comparison families never order against each other, and dicts, None and
        /// complex numbers do not order at all. Values whose ordering is decided by a
        /// user slot are left to the runtime's own comparison.
        /// </summary>
        private static bool IsUnorderedPair(PythonValue left, PythonValue right)
        {
            var leftFamily = ComparisonFamily(left);
            var rightFamily = ComparisonFamily(right);
            if (leftFamily is null || rightFamily is null)
                return false;
            if (leftFamily != rightFamily)
                return true;
            return leftFamily is "dict" or "NoneType" or "complex";
        }

        private static string? ComparisonFamily(PythonValue value) =>
            value switch
            {
                PythonWholeNumberValue or PythonFloatingPointValue or PythonTruthValue => "number",
                PythonTextValue => "str",
                PythonByteSequenceValue => "bytes",
                PythonByteArrayValue => "bytearray",
                PythonListValue => "list",
                PythonTupleValue => "tuple",
                PythonSetValue set => set.IsFrozen ? "frozenset" : "set",
                PythonDictionaryValue => "dict",
                PythonNoneValue => "NoneType",
                PythonComplexValue => "complex",
                _ => null,
            };

        /// <summary>The JSON object key for a dictionary key, or null when it is skipped.</summary>
        private string? KeyText(PythonValue key)
        {
            switch (key)
            {
                case PythonTextValue text:
                    return text.Value;
                case PythonFloatingPointValue floating:
                    return FloatText(floating.Value);
                case PythonTruthValue truth:
                    return truth.Value ? "true" : "false";
                case PythonNoneValue:
                    return "null";
                case PythonWholeNumberValue whole:
                    return whole.Value.ToString(CultureInfo.InvariantCulture);
                default:
                    if (options.SkipKeys)
                        return null;
                    throw Fault(
                        $"keys must be str, int, float, bool or None, not {ManagedObjectProtocols.GetTypeName(key)}",
                        span
                    );
            }
        }

        private string FloatText(double value)
        {
            if (double.IsNaN(value))
                return Finite("NaN", "nan");
            if (double.IsPositiveInfinity(value))
                return Finite("Infinity", "inf");
            if (double.IsNegativeInfinity(value))
                return Finite("-Infinity", "-inf");
            // The encoder emits float.__repr__ for the rest.
            return new PythonFloatingPointValue(value).ToDisplayString();
        }

        private string Finite(string text, string representation)
        {
            if (options.AllowNan)
                return text;
            throw Fault(
                $"Out of range float values are not JSON compliant: {representation}",
                span,
                "ValueError"
            );
        }

        private void AppendText(string text)
        {
            _builder.Append('"');
            foreach (var character in text)
            {
                switch (character)
                {
                    case '"':
                        _builder.Append("\\\"");
                        continue;
                    case '\\':
                        _builder.Append("\\\\");
                        continue;
                    case '\b':
                        _builder.Append("\\b");
                        continue;
                    case '\f':
                        _builder.Append("\\f");
                        continue;
                    case '\n':
                        _builder.Append("\\n");
                        continue;
                    case '\r':
                        _builder.Append("\\r");
                        continue;
                    case '\t':
                        _builder.Append("\\t");
                        continue;
                }
                if (character < 0x20)
                {
                    _builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    continue;
                }
                if (!options.EnsureAscii || character is >= ' ' and <= '~')
                {
                    _builder.Append(character);
                    continue;
                }
                // The ASCII form escapes one UTF-16 code unit at a time, so an
                // astral character becomes its surrogate pair's two escapes.
                _builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
            }
            _builder.Append('"');
        }

        private bool Enter(PythonValue value)
        {
            if (!_markers.Add(value))
                throw Fault("Circular reference detected", span, "ValueError");
            return true;
        }

        private PythonValue Invoke(PythonValue callable, PythonValue argument)
        {
            var dispatcher = UserObjectProtocols.Dispatcher;
            if (dispatcher is null)
                throw Fault(
                    $"'{ManagedObjectProtocols.GetTypeName(callable)}' object is not callable",
                    span
                );
            return dispatcher.Invoke(callable, [argument], span);
        }

        private static string Repeat(string value, int count) =>
            count <= 0 || value.Length == 0
                ? string.Empty
                : string.Concat(Enumerable.Repeat(value, count));

        private void CheckWork() => UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
    }

    // ---------------------------------------------------------------------------------
    // Decoding
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The JSON scanner and object/array parsers of `json.decoder`, including its
    /// error positions: every diagnostic carries the index CPython reports.
    /// </summary>
    private sealed class Decoder(
        string text,
        PythonManagedTypeValue decodeError,
        bool strict,
        TextSpan span
    )
    {
        private int _items;
        private int _nesting;

        /// <summary>The index after the string last read by <see cref="ScanString"/>.</summary>
        private int _scanned;

        internal PythonValue Decode()
        {
            var start = SkipWhitespace(0);
            if (!TryScan(start, out var value, out var end))
                throw Error("Expecting value", start);
            end = SkipWhitespace(end);
            if (end != text.Length)
                throw Error("Extra data", end);
            return value;
        }

        private bool TryScan(int index, out PythonValue value, out int end)
        {
            value = PythonNoneValue.Instance;
            end = index;
            if ((++_items & 1023) == 0)
                CheckWork();
            if (index >= text.Length)
                return false;
            switch (text[index])
            {
                case '"':
                    value = new PythonTextValue(ScanString(index + 1));
                    end = _scanned;
                    return true;
                case '{':
                    EnterNesting();
                    try
                    {
                        value = ScanObject(index + 1, out end);
                    }
                    finally
                    {
                        ExitNesting();
                    }
                    return true;
                case '[':
                    EnterNesting();
                    try
                    {
                        value = ScanArray(index + 1, out end);
                    }
                    finally
                    {
                        ExitNesting();
                    }
                    return true;
                case 'n' when Matches(index, "null"):
                    end = index + 4;
                    return true;
                case 't' when Matches(index, "true"):
                    value = PythonTruthValue.True;
                    end = index + 4;
                    return true;
                case 'f' when Matches(index, "false"):
                    value = PythonTruthValue.False;
                    end = index + 5;
                    return true;
                case 'N' when Matches(index, "NaN"):
                    value = new PythonFloatingPointValue(double.NaN);
                    end = index + 3;
                    return true;
                case 'I' when Matches(index, "Infinity"):
                    value = new PythonFloatingPointValue(double.PositiveInfinity);
                    end = index + 8;
                    return true;
                case '-' when Matches(index, "-Infinity"):
                    value = new PythonFloatingPointValue(double.NegativeInfinity);
                    end = index + 9;
                    return true;
            }
            if (TryScanNumber(index, out value, out end))
                return true;
            end = index;
            return false;
        }

        private bool Matches(int index, string token) =>
            index + token.Length <= text.Length
            && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

        /// <summary>NUMBER_RE: <c>(-?(?:0|[1-9]\d*))(\.\d+)?([eE][-+]?\d+)?</c>.</summary>
        private bool TryScanNumber(int index, out PythonValue value, out int end)
        {
            value = PythonNoneValue.Instance;
            end = index;
            var position = index;
            var negative = position < text.Length && text[position] == '-';
            if (negative)
                position++;
            var digits = position;
            // Either a single zero, or a nonzero digit followed by any digits; a
            // leading zero therefore ends the token after one character.
            if (position < text.Length && text[position] == '0')
                position++;
            else
            {
                if (position < text.Length && text[position] is >= '1' and <= '9')
                    position++;
                while (position < text.Length && char.IsAsciiDigit(text[position]))
                    position++;
            }
            if (position == digits)
                return false;
            var isFloat = false;
            if (position < text.Length && text[position] == '.')
            {
                var fraction = position + 1;
                while (fraction < text.Length && char.IsAsciiDigit(text[fraction]))
                    fraction++;
                if (fraction > position + 1)
                {
                    position = fraction;
                    isFloat = true;
                }
            }
            if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
            {
                var exponent = position + 1;
                if (exponent < text.Length && (text[exponent] == '+' || text[exponent] == '-'))
                    exponent++;
                var exponentDigits = exponent;
                while (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
                    exponent++;
                if (exponent > exponentDigits)
                {
                    position = exponent;
                    isFloat = true;
                }
            }
            var token = text[index..position];
            if (isFloat)
            {
                // float(text): an out-of-range magnitude becomes an infinity and an
                // underflow becomes a zero, exactly as double parsing reports them.
                if (
                    !double.TryParse(
                        token,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var floating
                    )
                )
                    throw Error("Expecting value", index);
                value = new PythonFloatingPointValue(floating);
            }
            else
                value = PythonWholeNumberValue.Create(
                    BigInteger.Parse(token, CultureInfo.InvariantCulture)
                );
            end = position;
            return true;
        }

        /// <summary>py_scanstring: the text after the opening quote, with the end index
        /// delivered through <see cref="_scanned"/>.</summary>
        private string ScanString(int end)
        {
            var begin = end - 1;
            var builder = new StringBuilder();
            while (true)
            {
                var terminator = end;
                while (
                    terminator < text.Length
                    && text[terminator] != '"'
                    && text[terminator] != '\\'
                    && text[terminator] >= 0x20
                )
                    terminator++;
                if (terminator >= text.Length)
                    throw Error("Unterminated string starting at", begin);
                if (terminator != end)
                    builder.Append(text, end, terminator - end);
                end = terminator + 1;
                var stop = text[terminator];
                if (stop == '"')
                    break;
                if (stop != '\\')
                {
                    // Only the strict scanner rejects a literal control character.
                    if (strict)
                        throw Error("Invalid control character at", end - 1);
                    builder.Append(stop);
                    continue;
                }
                if (end >= text.Length)
                    throw Error("Unterminated string starting at", begin);
                var escape = text[end];
                if (escape != 'u')
                {
                    var decoded = Escape(escape);
                    if (decoded < 0)
                        throw Error("Invalid \\escape", end - 1);
                    builder.Append((char)decoded);
                    end++;
                    continue;
                }
                var code = ScanHexQuad(end);
                end += 5;
                if (
                    code is >= 0xd800 and <= 0xdbff
                    && end + 2 <= text.Length
                    && text[end] == '\\'
                    && text[end + 1] == 'u'
                )
                {
                    var low = ScanHexQuad(end + 1);
                    if (low is >= 0xdc00 and <= 0xdfff)
                    {
                        code = 0x10000 + ((code - 0xd800) << 10 | (low - 0xdc00));
                        end += 6;
                    }
                }
                builder.Append(
                    code > 0xffff ? char.ConvertFromUtf32(code) : ((char)code).ToString()
                );
            }
            _scanned = end;
            return builder.ToString();
        }

        private int ScanHexQuad(int position)
        {
            if (position + 5 > text.Length)
                throw Error("Invalid \\uXXXX escape", position);
            var value = 0;
            for (var index = position + 1; index < position + 5; index++)
            {
                var digit = HexDigit(text[index]);
                if (digit < 0)
                    throw Error("Invalid \\uXXXX escape", position);
                value = value << 4 | digit;
            }
            return value;
        }

        private static int HexDigit(char character) =>
            character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };

        private static int Escape(char character) =>
            character switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => -1,
            };

        private PythonDictionaryValue ScanObject(int index, out int end)
        {
            var pairs = new List<PythonDictionaryItemValue>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var at = index;
            if (at >= text.Length || text[at] != '"')
            {
                if (at < text.Length && IsWhitespace(text[at]))
                {
                    at = SkipWhitespace(at);
                }
                if (at < text.Length && text[at] == '}')
                {
                    end = at + 1;
                    return new PythonDictionaryValue([]);
                }
                if (at >= text.Length || text[at] != '"')
                    throw Error("Expecting property name enclosed in double quotes", at);
            }
            at++;
            while (true)
            {
                var key = ScanString(at);
                at = _scanned;
                if (at >= text.Length || text[at] != ':')
                {
                    at = SkipWhitespace(at);
                    if (at >= text.Length || text[at] != ':')
                        throw Error("Expecting ':' delimiter", at);
                }
                at++;
                if (at < text.Length && IsWhitespace(text[at]))
                {
                    at++;
                    if (at < text.Length && IsWhitespace(text[at]))
                        at = SkipWhitespace(at + 1);
                }
                if (!TryScan(at, out var item, out at))
                    throw Error("Expecting value", at);

                var value = new PythonTextValue(key);
                if (positions.TryGetValue(key, out var existing))
                    pairs[existing].Value = item;
                else
                {
                    positions[key] = pairs.Count;
                    pairs.Add(new PythonDictionaryItemValue(value, item));
                }

                char next;
                if (at >= text.Length)
                    next = '\0';
                else
                {
                    next = text[at];
                    if (IsWhitespace(next))
                    {
                        at = SkipWhitespace(at + 1);
                        next = at < text.Length ? text[at] : '\0';
                    }
                }
                at++;
                if (next == '}')
                    break;
                if (next != ',')
                    throw Error("Expecting ',' delimiter", at - 1);
                var comma = at - 1;
                at = SkipWhitespace(at);
                next = at < text.Length ? text[at] : '\0';
                at++;
                if (next != '"')
                {
                    if (next == '}')
                        throw Error("Illegal trailing comma before end of object", comma);
                    throw Error("Expecting property name enclosed in double quotes", at - 1);
                }
            }
            end = at;
            return new PythonDictionaryValue(pairs);
        }

        private PythonListValue ScanArray(int index, out int end)
        {
            var values = new List<PythonValue>();
            var at = index;
            if (at < text.Length && IsWhitespace(text[at]))
            {
                at = SkipWhitespace(at + 1);
            }
            if (at < text.Length && text[at] == ']')
            {
                end = at + 1;
                return new PythonListValue([]);
            }
            while (true)
            {
                if (!TryScan(at, out var item, out at))
                    throw Error("Expecting value", at);
                values.Add(item);
                var next = at < text.Length ? text[at] : '\0';
                if (IsWhitespace(next))
                {
                    at = SkipWhitespace(at + 1);
                    next = at < text.Length ? text[at] : '\0';
                }
                at++;
                if (next == ']')
                    break;
                if (next != ',')
                    throw Error("Expecting ',' delimiter", at - 1);
                var comma = at - 1;
                at = SkipWhitespace(at);
                if (at < text.Length && text[at] == ']')
                    throw Error("Illegal trailing comma before end of array", comma);
            }
            end = at;
            return new PythonListValue(values);
        }

        private static bool IsWhitespace(char character) =>
            character is ' ' or '\t' or '\n' or '\r';

        private int SkipWhitespace(int index)
        {
            while (index < text.Length && IsWhitespace(text[index]))
                index++;
            return index;
        }

        private void CheckWork() => UserObjectProtocols.Dispatcher?.CheckIterationWork(span);

        private void EnterNesting()
        {
            if (++_nesting > MaximumNesting)
                throw Fault("maximum recursion depth exceeded", span, "RecursionError");
        }

        private void ExitNesting() => _nesting--;

        private PythonRaisedException Error(string message, int position) =>
            DecodeFault(decodeError, message, text, position);
    }

    private static PythonRuntimeException Fault(
        string message,
        TextSpan span,
        string type = "TypeError",
        string code = ErrorCode
    ) => ManagedObjectProtocols.Fault(code, message, span, type);
}
