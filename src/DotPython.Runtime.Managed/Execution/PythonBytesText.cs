using System.Globalization;
using System.Text;

namespace DotPython.Runtime.Managed.Execution;

internal static class PythonBytesText
{
    private static readonly string[] Parameters = ["encoding", "errors"];
    internal static readonly PythonProtocolFunctionValue DecodeMethod = new(
        "decode",
        (target, positional) => Decode((PythonByteSequenceValue)target!, positional, [], []),
        (target, positional, names, values) =>
            Decode((PythonByteSequenceValue)target!, positional, names, values)
    );

    private static PythonTextValue Decode(
        PythonByteSequenceValue bytes,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        if (positional.Count + names.Count > 2)
            throw Error(
                $"decode() takes at most 2 {(positional.Count == 0 ? "keyword arguments" : "arguments")} ({positional.Count + names.Count} given)",
                "TypeError"
            );
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(Parameters, names[index]);
            if (slot >= 0 && slot < positional.Count)
                throw Error(
                    $"argument for decode() given by name ('{names[index]}') and position ({slot + 1})",
                    "TypeError"
                );
        }
        var slots = PythonKeywordArguments.Bind(
            "decode",
            Parameters,
            0,
            positional,
            names,
            values,
            UserObjectProtocols.Dispatcher?.CurrentSpan ?? default
        );
        var encoding = CodecName(slots[0], "encoding", "utf-8");
        var errors = CodecName(slots[1], "errors", "strict");
        return new PythonTextValue(
            PythonTextCodecs.Decode(
                bytes.Value,
                encoding,
                errors,
                UserObjectProtocols.Dispatcher?.CurrentSpan ?? default
            )
        );
    }

    private static string CodecName(PythonValue? value, string name, string fallback)
    {
        if (value is null)
            return fallback;
        if (value is not PythonTextValue text)
            throw Error(
                $"decode() argument '{name}' must be str, not {(value is PythonNoneValue ? "None" : ManagedObjectProtocols.GetTypeName(value))}",
                "TypeError"
            );
        if (text.Value.Contains('\0', StringComparison.Ordinal))
            throw Error("embedded null character", "ValueError");
        return text.Value;
    }

    internal static string Represent(byte[] bytes)
    {
        long length = 3;
        var singleQuotes = 0;
        var doubleQuotes = false;
        for (var index = 0; index < bytes.Length; index++)
        {
            CheckWork(index);
            var value = bytes[index];
            if (value == '\'')
                singleQuotes++;
            if (value == '"')
                doubleQuotes = true;
            length += value switch
            {
                (byte)'\\' or (byte)'\t' or (byte)'\n' or (byte)'\r' => 2,
                < 32 or >= 127 => 4,
                _ => 1,
            };
        }
        var quote = singleQuotes > 0 && !doubleQuotes ? '"' : '\'';
        if (quote == '\'')
            length += singleQuotes;
        if (length > 10_000_000)
            throw Error("The bytes representation exceeds the supported size.", "OverflowError");
        var builder = new StringBuilder((int)length);
        builder.Append('b').Append(quote);
        for (var index = 0; index < bytes.Length; index++)
        {
            CheckWork(index);
            var value = bytes[index];
            if (value == quote || value == '\\')
                builder.Append('\\').Append((char)value);
            else
                switch (value)
                {
                    case (byte)'\t':
                        builder.Append("\\t");
                        break;
                    case (byte)'\n':
                        builder.Append("\\n");
                        break;
                    case (byte)'\r':
                        builder.Append("\\r");
                        break;
                    case < 32 or >= 127:
                        builder.Append(CultureInfo.InvariantCulture, $"\\x{value:x2}");
                        break;
                    default:
                        builder.Append((char)value);
                        break;
                }
        }
        return builder.Append(quote).ToString();
    }

    private static void CheckWork(int index)
    {
        if ((index & 255) == 0)
            UserObjectProtocols.Dispatcher?.CheckIterationWork(
                UserObjectProtocols.Dispatcher.CurrentSpan
            );
    }

    private static PythonRuntimeException Error(string message, string type) =>
        ManagedObjectProtocols.Fault(
            "DPY4003",
            message,
            UserObjectProtocols.Dispatcher?.CurrentSpan ?? default,
            type
        );
}
