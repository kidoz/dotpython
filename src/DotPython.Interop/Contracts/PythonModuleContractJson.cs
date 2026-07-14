using System.Text;
using System.Text.Json;
using DotPython.Contracts;

namespace DotPython.Interop.Contracts;

/// <summary>Serializes the versioned intermediate module contract in canonical JSON order.</summary>
public static class PythonModuleContractJson
{
    private const int MaximumContractLength = 4 * 1024 * 1024;
    private const int MaximumFunctionCount = 10_000;
    private const int MaximumParameterCount = 1_000;
    private const int MaximumTypeArgumentCount = 16;
    private const int MaximumTypeDepth = 32;

    /// <summary>Serializes a contract to deterministic compact UTF-8 JSON.</summary>
    public static string Serialize(PythonModuleContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.FormatVersion != DotPythonContractFormat.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Module contract format {contract.FormatVersion} is not supported."
            );
        }

        if (contract.Functions.Count > MaximumFunctionCount)
        {
            throw new InvalidOperationException("The module contract has too many functions.");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", contract.FormatVersion);
            writer.WriteString("moduleName", contract.ModuleName);
            writer.WriteString("clrNamespace", contract.ClrNamespace);
            writer.WriteString("clrTypeName", contract.ClrTypeName);
            writer.WriteString("statePolicy", GetStatePolicyName(contract.StatePolicy));
            writer.WriteStartArray("functions");
            foreach (var function in contract.Functions)
            {
                WriteFunction(writer, function);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (buffer.Length > MaximumContractLength)
        {
            throw new InvalidOperationException("The module contract is too large.");
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Deserializes and validates a versioned intermediate module contract.</summary>
    public static PythonModuleContract Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumContractLength)
        {
            throw new InvalidDataException("The module contract is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = MaximumTypeDepth + 16 }
            );
            var root = RequireObject(document.RootElement, "module contract");
            var formatVersion = ReadInt32(root, "formatVersion");
            if (formatVersion != DotPythonContractFormat.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Module contract format {formatVersion} is not supported."
                );
            }

            var functionsElement = ReadArray(root, "functions");
            if (functionsElement.GetArrayLength() > MaximumFunctionCount)
            {
                throw new InvalidDataException("The module contract has too many functions.");
            }

            var functions = new List<PythonFunctionContract>();
            foreach (var element in functionsElement.EnumerateArray())
            {
                functions.Add(ReadFunction(element));
            }

            return new PythonModuleContract(
                formatVersion,
                ReadString(root, "moduleName"),
                ReadString(root, "clrNamespace"),
                ReadString(root, "clrTypeName"),
                ReadStatePolicy(ReadString(root, "statePolicy")),
                functions
            );
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The module contract JSON is invalid.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The module contract is invalid.", exception);
        }
    }

    private static void WriteFunction(Utf8JsonWriter writer, PythonFunctionContract function)
    {
        if (function.Parameters.Count > MaximumParameterCount)
        {
            throw new InvalidOperationException("A module function has too many parameters.");
        }

        writer.WriteStartObject();
        writer.WriteString("pythonName", function.PythonName);
        writer.WriteString("clrName", function.ClrName);
        writer.WriteString("callShape", GetCallShapeName(function.CallShape));
        writer.WriteStartArray("parameters");
        foreach (var parameter in function.Parameters)
        {
            writer.WriteStartObject();
            writer.WriteString("pythonName", parameter.PythonName);
            writer.WriteString("clrName", parameter.ClrName);
            writer.WriteString("kind", GetParameterKindName(parameter.Kind));
            writer.WriteBoolean("hasDefault", parameter.HasDefault);
            writer.WritePropertyName("type");
            WriteType(writer, parameter.Type, 0);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("returnType");
        WriteType(writer, function.ReturnType, 0);
        writer.WriteEndObject();
    }

    private static void WriteType(Utf8JsonWriter writer, PythonTypeContract type, int depth)
    {
        if (depth > MaximumTypeDepth)
        {
            throw new InvalidOperationException("The module contract type nesting is too deep.");
        }

        if (type.TypeArguments.Count > MaximumTypeArgumentCount)
        {
            throw new InvalidOperationException("A module contract type has too many arguments.");
        }

        writer.WriteStartObject();
        writer.WriteString("pythonName", type.PythonName);
        writer.WriteString("clrTypeName", type.ClrTypeName);
        writer.WriteBoolean("nullable", type.IsNullable);
        writer.WriteBoolean("valueType", type.IsValueType);
        writer.WriteBoolean("clsCompliant", type.IsClsCompliant);
        writer.WriteStartArray("typeArguments");
        foreach (var argument in type.TypeArguments)
        {
            WriteType(writer, argument, depth + 1);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static PythonFunctionContract ReadFunction(JsonElement element)
    {
        var function = RequireObject(element, "function");
        var parametersElement = ReadArray(function, "parameters");
        if (parametersElement.GetArrayLength() > MaximumParameterCount)
        {
            throw new InvalidDataException("A module function has too many parameters.");
        }

        var parameters = new List<PythonParameterContract>();
        foreach (var parameterElement in parametersElement.EnumerateArray())
        {
            var parameter = RequireObject(parameterElement, "parameter");
            parameters.Add(
                new PythonParameterContract(
                    ReadString(parameter, "pythonName"),
                    ReadString(parameter, "clrName"),
                    ReadParameterKind(ReadString(parameter, "kind")),
                    ReadBoolean(parameter, "hasDefault"),
                    ReadType(ReadObject(parameter, "type"), 0)
                )
            );
        }

        return new PythonFunctionContract(
            ReadString(function, "pythonName"),
            ReadString(function, "clrName"),
            ReadCallShape(ReadString(function, "callShape")),
            parameters,
            ReadType(ReadObject(function, "returnType"), 0)
        );
    }

    private static PythonTypeContract ReadType(JsonElement element, int depth)
    {
        if (depth > MaximumTypeDepth)
        {
            throw new InvalidDataException("The module contract type nesting is too deep.");
        }

        var type = RequireObject(element, "type");
        var argumentsElement = ReadArray(type, "typeArguments");
        if (argumentsElement.GetArrayLength() > MaximumTypeArgumentCount)
        {
            throw new InvalidDataException("A module contract type has too many arguments.");
        }

        var arguments = new List<PythonTypeContract>();
        foreach (var argument in argumentsElement.EnumerateArray())
        {
            arguments.Add(ReadType(argument, depth + 1));
        }

        return new PythonTypeContract(
            ReadString(type, "pythonName"),
            ReadString(type, "clrTypeName"),
            ReadBoolean(type, "nullable"),
            ReadBoolean(type, "valueType"),
            ReadBoolean(type, "clsCompliant"),
            arguments
        );
    }

    private static JsonElement RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The module contract {description} must be an object.");
        }

        return element;
    }

    private static JsonElement ReadObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException(
                $"The module contract property '{propertyName}' is required."
            );
        }

        return RequireObject(property, propertyName);
    }

    private static JsonElement ReadArray(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array
        )
        {
            throw new InvalidDataException(
                $"The module contract property '{propertyName}' must be an array."
            );
        }

        return property;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var property)
            || !property.TryGetInt32(out var value)
        )
        {
            throw new InvalidDataException(
                $"The module contract property '{propertyName}' must be an integer."
            );
        }

        return value;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException(
                $"The module contract property '{propertyName}' must be a Boolean."
            );
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"The module contract property '{propertyName}' must be a Boolean."
            ),
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString())
        )
        {
            throw new InvalidDataException(
                $"The module contract property '{propertyName}' must be a non-empty string."
            );
        }

        return property.GetString()!;
    }

    private static string GetStatePolicyName(PythonModuleStatePolicy value) =>
        value switch
        {
            PythonModuleStatePolicy.PerRuntime => "perRuntime",
            PythonModuleStatePolicy.PerSession => "perSession",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static PythonModuleStatePolicy ReadStatePolicy(string value) =>
        value switch
        {
            "perRuntime" => PythonModuleStatePolicy.PerRuntime,
            "perSession" => PythonModuleStatePolicy.PerSession,
            _ => throw new InvalidDataException($"Module state policy '{value}' is unsupported."),
        };

    private static string GetCallShapeName(PythonCallShape value) =>
        value switch
        {
            PythonCallShape.Synchronous => "synchronous",
            PythonCallShape.Asynchronous => "asynchronous",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static PythonCallShape ReadCallShape(string value) =>
        value switch
        {
            "synchronous" => PythonCallShape.Synchronous,
            "asynchronous" => PythonCallShape.Asynchronous,
            _ => throw new InvalidDataException($"Python call shape '{value}' is unsupported."),
        };

    private static string GetParameterKindName(PythonParameterKind value) =>
        value switch
        {
            PythonParameterKind.PositionalOrKeyword => "positionalOrKeyword",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static PythonParameterKind ReadParameterKind(string value) =>
        value switch
        {
            "positionalOrKeyword" => PythonParameterKind.PositionalOrKeyword,
            _ => throw new InvalidDataException($"Python parameter kind '{value}' is unsupported."),
        };
}
