// CA1308: float() accepts lowercase special tokens; this is input normalization, not a
// comparison key derived from user identity.
#pragma warning disable CA1308

using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Built-in type objects usable as constructors and isinstance class info.</summary>
internal static class PythonBuiltinTypes
{
    internal static readonly PythonBuiltinTypeValue Bool = new("bool", ConstructBool);
    internal static readonly PythonBuiltinTypeValue Bytes = new(
        "bytes",
        ConstructBytes,
        PythonKeywordArguments.Adapt(
            "bytes",
            ["source", "encoding", "errors"],
            [null, null, null],
            ConstructBytes
        )
    );
    internal static readonly PythonBuiltinTypeValue Dict = new(
        "dict",
        ConstructDictionary,
        ConstructDictionaryWithKeywords
    );
    internal static readonly PythonBuiltinTypeValue Float = new("float", ConstructFloat);
    internal static readonly PythonBuiltinTypeValue Int = new(
        "int",
        ConstructInt,
        PythonKeywordArguments.Adapt(
            "int",
            ["x", "base"],
            [null, null],
            ConstructInt,
            positionalOnly: 1
        )
    );
    internal static readonly PythonBuiltinTypeValue List = new("list", ConstructList);
    internal static readonly PythonBuiltinTypeValue Set = new("set", ConstructSet);
    internal static readonly PythonBuiltinTypeValue Frozenset = new(
        "frozenset",
        ConstructFrozenset
    );
    internal static readonly PythonBuiltinTypeValue Str = new(
        "str",
        ConstructStr,
        PythonKeywordArguments.Adapt(
            "str",
            ["object", "encoding", "errors"],
            [new PythonTextValue(string.Empty), null, null],
            ConstructStr
        )
    );
    internal static readonly PythonBuiltinTypeValue Tuple = new("tuple", ConstructTuple);

    internal static IEnumerable<PythonBuiltinTypeValue> All =>
        [Bool, Bytes, Dict, Float, Frozenset, Int, List, Set, Str, Tuple];

    internal static PythonBuiltinTypeValue CreateOpaque(string name) =>
        new(
            name,
            (_, span) =>
                throw ManagedObjectProtocols.Fault(
                    "DPY4009",
                    $"The type '{name}' is not constructible in this runtime slice.",
                    span,
                    "TypeError"
                )
        );

    internal static bool IsInstance(PythonValue value, PythonBuiltinTypeValue type) =>
        type.Name switch
        {
            "bool" => value is PythonTruthValue,
            "bytes" => value is PythonByteSequenceValue,
            "int" => value is PythonWholeNumberValue or PythonTruthValue,
            "float" => value is PythonFloatingPointValue,
            "str" => value is PythonTextValue,
            "list" => value is PythonListValue,
            "tuple" => value is PythonTupleValue,
            "dict" => value is PythonDictionaryValue,
            "set" => value is PythonSetValue { IsFrozen: false },
            "frozenset" => value is PythonSetValue { IsFrozen: true },
            "complex" => value is PythonComplexValue,
            "Template" => value is PythonTemplateValue,
            "Interpolation" => value is PythonInterpolationValue,
            "object" => true,
            _ => false,
        };

    private static PythonByteSequenceValue ConstructBytes(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("bytes", arguments, 0, 3, span);
        if (arguments.Count == 0)
        {
            return new PythonByteSequenceValue([]);
        }

        if (arguments.Count >= 2)
        {
            if (arguments[0] is not PythonTextValue source)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "encoding without a string argument",
                    span,
                    "TypeError"
                );
            }

            if (arguments[1] is not PythonTextValue encoding)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"bytes() argument 'encoding' must be str, not {ManagedObjectProtocols.GetTypeName(arguments[1])}",
                    span,
                    "TypeError"
                );
            }

            var errors =
                arguments.Count == 3 && arguments[2] is PythonTextValue errorsText
                    ? errorsText.Value
                    : "strict";
            return new PythonByteSequenceValue(
                PythonTextCodecs.Encode(source.Value, encoding.Value, errors, span)
            );
        }

        switch (arguments[0])
        {
            case PythonByteSequenceValue bytes:
                return new PythonByteSequenceValue((byte[])bytes.Value.Clone());
            case PythonWholeNumberValue { Value.Sign: >= 0 } size when size.Value <= 4096:
                return new PythonByteSequenceValue(new byte[(int)size.Value]);
            case PythonTextValue:
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "string argument without an encoding",
                    span,
                    "TypeError"
                );
            default:
            {
                var values = ManagedObjectProtocols.MaterializeValues(arguments[0], span);
                var buffer = new byte[values.Count];
                for (var index = 0; index < values.Count; index++)
                {
                    if (
                        values[index] is not PythonWholeNumberValue item
                        || item.Value.Sign < 0
                        || item.Value > byte.MaxValue
                    )
                    {
                        throw ManagedObjectProtocols.Fault(
                            "DPY4003",
                            "bytes must be in range(0, 256)",
                            span,
                            "ValueError"
                        );
                    }

                    buffer[index] = (byte)item.Value;
                }

                return new PythonByteSequenceValue(buffer);
            }
        }
    }

    private static PythonTruthValue ConstructBool(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("bool", arguments, 0, 1, span);
        return arguments.Count == 0
            ? PythonTruthValue.False
            : PythonTruthValue.FromBoolean(ManagedObjectProtocols.IsTrue(arguments[0]));
    }

    private static PythonWholeNumberValue ConstructInt(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("int", arguments, 0, 2, span);
        if (arguments.Count == 0)
        {
            return PythonWholeNumberValue.Create(BigInteger.Zero);
        }

        if (arguments.Count == 2)
        {
            return ParseIntegerWithBase(arguments[0], arguments[1], span);
        }

        if (UserObjectProtocols.TryConvertToInt(arguments[0], span, out var userInteger))
        {
            return PythonWholeNumberValue.Create(userInteger);
        }

        switch (arguments[0])
        {
            case PythonWholeNumberValue wholeNumber:
                return wholeNumber;
            case PythonTruthValue truth:
                return PythonWholeNumberValue.Create(
                    truth.Value ? BigInteger.One : BigInteger.Zero
                );
            case PythonFloatingPointValue floatingPoint:
                if (double.IsNaN(floatingPoint.Value))
                {
                    throw Fault("Cannot convert float NaN to integer.", "ValueError", span);
                }

                if (double.IsInfinity(floatingPoint.Value))
                {
                    throw Fault("Cannot convert float infinity to integer.", "OverflowError", span);
                }

                return PythonWholeNumberValue.Create(
                    new BigInteger(Math.Truncate(floatingPoint.Value))
                );
            case PythonTextValue text:
                return ParseInteger(text, 10, span);
            case PythonByteSequenceValue bytes:
                return ParseInteger(
                    new PythonTextValue(Encoding.ASCII.GetString(bytes.Value)),
                    10,
                    span
                );
            default:
                throw Fault(
                    $"int() argument must be a string, a bytes-like object or a real number, "
                        + $"not '{ManagedObjectProtocols.GetTypeName(arguments[0])}'",
                    "TypeError",
                    span
                );
        }
    }

    private static PythonWholeNumberValue ParseIntegerWithBase(
        PythonValue value,
        PythonValue baseValue,
        TextSpan span
    )
    {
        var radix = (int)
            BigInteger.Min(
                BigInteger.Max(PythonBuiltinFunctions.RequireIndex(baseValue, span), -1),
                37
            );
        if (radix != 0 && radix is < 2 or > 36)
        {
            throw Fault("int() base must be >= 2 and <= 36, or 0", "ValueError", span);
        }

        return value switch
        {
            PythonTextValue text => ParseInteger(text, radix, span),
            PythonByteSequenceValue bytes => ParseInteger(
                new PythonTextValue(Encoding.ASCII.GetString(bytes.Value)),
                radix,
                span
            ),
            _ => throw Fault(
                "int() can't convert non-string with explicit base",
                "TypeError",
                span
            ),
        };
    }

    /// <summary>
    /// CPython's integer literal grammar for `int(text, base)`: optional sign, an optional
    /// prefix matching the base (or any prefix for base 0), digits with single underscores.
    /// </summary>
    private static PythonWholeNumberValue ParseInteger(
        PythonTextValue text,
        int radix,
        TextSpan span
    )
    {
        var invalid = Fault(
            $"invalid literal for int() with base {radix}: {text.ToRepresentationString()}",
            "ValueError",
            span
        );
        var body = text.Value.Trim();
        var negative = false;
        if (body.Length != 0 && body[0] is '+' or '-')
        {
            negative = body[0] == '-';
            body = body[1..];
        }

        var effectiveRadix = radix;
        if (body.Length >= 2 && body[0] == '0')
        {
            var prefixRadix = char.ToLowerInvariant(body[1]) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                _ => 0,
            };
            if (prefixRadix != 0 && (radix == 0 || radix == prefixRadix))
            {
                effectiveRadix = prefixRadix;
                body = body[2..];
                if (body.StartsWith('_'))
                {
                    body = body[1..];
                }
            }
        }

        if (effectiveRadix == 0)
        {
            // Base 0 without a prefix only accepts decimal literals without leading zeros.
            effectiveRadix = 10;
            if (body.Length > 1 && body[0] == '0' && body.TrimStart('0', '_').Length != 0)
            {
                throw invalid;
            }
        }

        if (
            body.Length == 0
            || body.StartsWith('_')
            || body.EndsWith('_')
            || body.Contains("__", StringComparison.Ordinal)
        )
        {
            throw invalid;
        }

        var result = BigInteger.Zero;
        foreach (var character in body)
        {
            if (character == '_')
            {
                continue;
            }

            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'z' => character - 'a' + 10,
                >= 'A' and <= 'Z' => character - 'A' + 10,
                _ => int.MaxValue,
            };
            if (digit >= effectiveRadix)
            {
                throw invalid;
            }

            result = result * effectiveRadix + digit;
        }

        return PythonWholeNumberValue.Create(negative ? -result : result);
    }

    private static PythonFloatingPointValue ConstructFloat(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("float", arguments, 0, 1, span);
        if (arguments.Count == 0)
        {
            return new PythonFloatingPointValue(0);
        }

        if (UserObjectProtocols.TryConvertToFloat(arguments[0], span, out var userFloat))
        {
            return new PythonFloatingPointValue(userFloat);
        }

        switch (arguments[0])
        {
            case PythonFloatingPointValue floatingPoint:
                return floatingPoint;
            case PythonWholeNumberValue wholeNumber:
                return new PythonFloatingPointValue((double)wholeNumber.Value);
            case PythonTruthValue truth:
                return new PythonFloatingPointValue(truth.Value ? 1 : 0);
            case PythonTextValue text:
            {
                var trimmed = text.Value.Trim();
                var normalized = trimmed.ToLowerInvariant().TrimStart('+');
                var parsedSpecial = normalized switch
                {
                    "inf" or "infinity" => double.PositiveInfinity,
                    "-inf" or "-infinity" => double.NegativeInfinity,
                    "nan" or "-nan" => double.NaN,
                    _ => (double?)null,
                };
                if (parsedSpecial is { } special)
                {
                    return new PythonFloatingPointValue(special);
                }

                if (
                    trimmed.Length != 0
                    && double.TryParse(
                        trimmed,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsed
                    )
                )
                {
                    return new PythonFloatingPointValue(parsed);
                }

                throw Fault(
                    $"Could not convert string to float: {text.ToRepresentationString()}.",
                    "ValueError",
                    span
                );
            }
            default:
                throw Fault(
                    $"float() argument must be a string or a number, "
                        + $"not '{ManagedObjectProtocols.GetTypeName(arguments[0])}'.",
                    "TypeError",
                    span
                );
        }
    }

    private static PythonSetValue ConstructSet(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArguments("set", arguments, 0, 1, span);
        return arguments.Count == 0
            ? new PythonSetValue([])
            : ManagedObjectProtocols.CreateSet(
                ManagedObjectProtocols.MaterializeValues(arguments[0], span),
                span
            );
    }

    private static PythonSetValue ConstructFrozenset(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("frozenset", arguments, 0, 1, span);
        var deduplicated =
            arguments.Count == 0
                ? new PythonSetValue([])
                : ManagedObjectProtocols.CreateSet(
                    ManagedObjectProtocols.MaterializeValues(arguments[0], span),
                    span
                );
        return new PythonSetValue(deduplicated.Elements) { IsFrozen = true };
    }

    private static PythonTextValue ConstructStr(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArguments("str", arguments, 0, 3, span);
        if (arguments.Count == 0)
        {
            return new PythonTextValue(string.Empty);
        }

        if (arguments.Count == 1)
        {
            return new PythonTextValue(arguments[0].ToDisplayString());
        }

        if (arguments[0] is not PythonByteSequenceValue bytes)
        {
            throw Fault(
                $"decoding to str: need a bytes-like object, {ManagedObjectProtocols.GetTypeName(arguments[0])} found",
                "TypeError",
                span
            );
        }

        var errors =
            arguments.Count == 3 ? RequireCodecText("errors", arguments[2], span) : "strict";
        return new PythonTextValue(
            PythonTextCodecs.Decode(
                bytes.Value,
                RequireCodecText("encoding", arguments[1], span),
                errors,
                span
            )
        );
    }

    private static string RequireCodecText(string name, PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                $"str() argument '{name}' must be str, not {ManagedObjectProtocols.GetTypeName(value)}",
                "TypeError",
                span
            );

    private static PythonListValue ConstructList(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("list", arguments, 0, 1, span);
        return arguments.Count == 0
            ? new PythonListValue([])
            : new PythonListValue(ManagedObjectProtocols.MaterializeValues(arguments[0], span));
    }

    private static PythonTupleValue ConstructTuple(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("tuple", arguments, 0, 1, span);
        return arguments.Count == 0
            ? new PythonTupleValue([])
            : new PythonTupleValue([
                .. ManagedObjectProtocols.MaterializeValues(arguments[0], span),
            ]);
    }

    private static PythonDictionaryValue ConstructDictionary(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArguments("dict", arguments, 0, 1, span);
        var dictionary = new PythonDictionaryValue([]);
        if (arguments.Count == 0)
        {
            return dictionary;
        }

        var sourceValue = PythonMappingProxies.Unwrap(arguments[0]);
        if (sourceValue is PythonDictionaryValue source)
        {
            foreach (var item in source.Items)
            {
                dictionary.Items.Add(
                    new PythonDictionaryItemValue(item.Key, item.Value, item.KeyHash)
                );
            }
            return dictionary;
        }
        if (arguments[0] is PythonMappingProxyValue)
        {
            var keysMethod = ManagedObjectProtocols.GetAttribute(sourceValue, "keys", span);
            var keys = UserObjectProtocols.Dispatcher is { } dispatcher
                ? dispatcher.Invoke(keysMethod, [], span)
                : ManagedObjectProtocols.Call(keysMethod, [], span);
            foreach (var key in ManagedObjectProtocols.MaterializeValues(keys, span))
            {
                ManagedObjectProtocols.SetDictionaryItem(
                    dictionary,
                    key,
                    ManagedObjectProtocols.GetItem(sourceValue, key, span),
                    span
                );
            }
            return dictionary;
        }

        foreach (var pair in ManagedObjectProtocols.MaterializeValues(arguments[0], span))
        {
            var elements = ManagedObjectProtocols.MaterializeValues(pair, span);
            if (elements.Count != 2)
            {
                throw Fault(
                    $"The dictionary update sequence element has length {elements.Count}; "
                        + "2 is required.",
                    "ValueError",
                    span
                );
            }

            ManagedObjectProtocols.SetDictionaryItem(dictionary, elements[0], elements[1], span);
        }

        return dictionary;
    }

    private static PythonDictionaryValue ConstructDictionaryWithKeywords(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var dictionary = ConstructDictionary(positional, span);
        for (var index = 0; index < keywordNames.Count; index++)
        {
            ManagedObjectProtocols.SetDictionaryItem(
                dictionary,
                new PythonTextValue(keywordNames[index]),
                keywordValues[index],
                span
            );
        }

        return dictionary;
    }

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int minimum,
        int maximum,
        TextSpan span
    )
    {
        if (arguments.Count < minimum || arguments.Count > maximum)
        {
            throw Fault(
                $"{name}() expected at most {maximum} argument(s), "
                    + $"but received {arguments.Count}.",
                "TypeError",
                span
            );
        }
    }

    private static PythonRuntimeException Fault(string message, string pythonType, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4009", message, span, pythonType);
}
