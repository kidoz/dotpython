// CA1308: float() accepts lowercase special tokens; this is input normalization, not a
// comparison key derived from user identity.
#pragma warning disable CA1308

using System.Globalization;
using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Built-in type objects usable as constructors and isinstance class info.</summary>
internal static class PythonBuiltinTypes
{
    internal static readonly PythonBuiltinTypeValue Bool = new("bool", ConstructBool);
    internal static readonly PythonBuiltinTypeValue Dict = new("dict", ConstructDictionary);
    internal static readonly PythonBuiltinTypeValue Float = new("float", ConstructFloat);
    internal static readonly PythonBuiltinTypeValue Int = new("int", ConstructInt);
    internal static readonly PythonBuiltinTypeValue List = new("list", ConstructList);
    internal static readonly PythonBuiltinTypeValue Set = new("set", ConstructSet);
    internal static readonly PythonBuiltinTypeValue Str = new("str", ConstructStr);
    internal static readonly PythonBuiltinTypeValue Tuple = new("tuple", ConstructTuple);

    internal static IEnumerable<PythonBuiltinTypeValue> All =>
        [Bool, Dict, Float, Int, List, Set, Str, Tuple];

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
            "int" => value is PythonWholeNumberValue or PythonTruthValue,
            "float" => value is PythonFloatingPointValue,
            "str" => value is PythonTextValue,
            "list" => value is PythonListValue,
            "tuple" => value is PythonTupleValue,
            "dict" => value is PythonDictionaryValue,
            "set" => value is PythonSetValue,
            _ => false,
        };

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
        RequireArguments("int", arguments, 0, 1, span);
        if (arguments.Count == 0)
        {
            return PythonWholeNumberValue.Create(BigInteger.Zero);
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
            {
                var trimmed = text.Value.Trim();
                if (
                    trimmed.Length != 0
                    && BigInteger.TryParse(
                        trimmed,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var parsed
                    )
                )
                {
                    return PythonWholeNumberValue.Create(parsed);
                }

                throw Fault(
                    $"Invalid literal for int(): {text.ToRepresentationString()}.",
                    "ValueError",
                    span
                );
            }
            default:
                throw Fault(
                    $"int() argument must be a string or a number, "
                        + $"not '{ManagedObjectProtocols.GetTypeName(arguments[0])}'.",
                    "TypeError",
                    span
                );
        }
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

    private static PythonTextValue ConstructStr(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArguments("str", arguments, 0, 1, span);
        return arguments.Count == 0
            ? new PythonTextValue(string.Empty)
            : new PythonTextValue(arguments[0].ToDisplayString());
    }

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

        if (arguments[0] is PythonDictionaryValue source)
        {
            foreach (var item in source.Items)
            {
                ManagedObjectProtocols.SetDictionaryItem(dictionary, item.Key, item.Value, span);
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
