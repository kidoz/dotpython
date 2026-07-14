using System.Text;
using System.Text.Json;
using DotPython.Language;

namespace DotPython.Compiler.Artifacts;

public static class DotPythonModuleManifestJson
{
    public static string Serialize(DotPythonModuleManifest manifest) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(manifest));

    public static DotPythonModuleManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Deserialize(Encoding.UTF8.GetBytes(json));
    }

    internal static byte[] SerializeToUtf8Bytes(DotPythonModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteString("moduleName", manifest.ModuleName);
            writer.WriteString("languageVersion", manifest.LanguageVersion);
            writer.WriteNumber("bytecodeFormatVersion", manifest.BytecodeFormatVersion);
            writer.WriteStartArray("exports");
            foreach (var export in manifest.Exports)
            {
                writer.WriteStartObject();
                writer.WriteString("pythonName", export.PythonName);
                writer.WriteString("contractName", export.ContractName);
                writer.WriteString("kind", GetKindName(export.Kind));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    internal static DotPythonModuleManifest Deserialize(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The module manifest root must be an object.");
            }

            var formatVersion = ReadInt32(root, "formatVersion");
            if (formatVersion != DotPythonModuleArtifactFormat.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Module manifest format {formatVersion} is not supported."
                );
            }

            var moduleName = ReadString(root, "moduleName");
            var languageVersion = ReadString(root, "languageVersion");
            var expectedLanguageVersion = PythonLanguageVersion.Current.ToString(2);
            if (!string.Equals(languageVersion, expectedLanguageVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Python language version '{languageVersion}' is not supported."
                );
            }

            var bytecodeFormatVersion = ReadInt32(root, "bytecodeFormatVersion");
            if (bytecodeFormatVersion != DotPythonBytecodeFormat.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Bytecode format {bytecodeFormatVersion} is not supported."
                );
            }

            if (
                !root.TryGetProperty("exports", out var exportsElement)
                || exportsElement.ValueKind != JsonValueKind.Array
            )
            {
                throw new InvalidDataException("The module manifest exports must be an array.");
            }

            var exports = new List<DotPythonModuleExport>();
            foreach (var exportElement in exportsElement.EnumerateArray())
            {
                if (exportElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Every module export must be an object.");
                }

                exports.Add(
                    new DotPythonModuleExport(
                        ReadString(exportElement, "pythonName"),
                        ReadString(exportElement, "contractName"),
                        ReadKind(ReadString(exportElement, "kind"))
                    )
                );
            }

            return new DotPythonModuleManifest(
                formatVersion,
                moduleName,
                languageVersion,
                bytecodeFormatVersion,
                exports
            );
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The module manifest JSON is invalid.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The module manifest is invalid.", exception);
        }
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var property)
            || !property.TryGetInt32(out var value)
        )
        {
            throw new InvalidDataException(
                $"The module manifest property '{propertyName}' must be an integer."
            );
        }

        return value;
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
                $"The module manifest property '{propertyName}' must be a non-empty string."
            );
        }

        return property.GetString()!;
    }

    private static string GetKindName(DotPythonExportKind kind) =>
        kind switch
        {
            DotPythonExportKind.Function => "function",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static DotPythonExportKind ReadKind(string value) =>
        value switch
        {
            "function" => DotPythonExportKind.Function,
            _ => throw new InvalidDataException($"Module export kind '{value}' is not supported."),
        };
}
