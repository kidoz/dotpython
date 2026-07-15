using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotPython.Hosting.Packaging;

/// <summary>Serializes wheel-inspection reports for diagnostics and automation.</summary>
public static class PythonWheelInspectionJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes an inspection report as deterministic UTF-8-compatible JSON.</summary>
    /// <param name="inspection">The report to serialize.</param>
    /// <returns>The serialized report.</returns>
    public static string Serialize(PythonWheelInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        return JsonSerializer.Serialize(inspection, SerializerOptions);
    }
}
