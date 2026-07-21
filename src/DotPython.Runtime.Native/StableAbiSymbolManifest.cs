using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotPython.Runtime.Native;

internal sealed record StableAbiSymbolManifest(
    int SchemaVersion,
    string ManifestVersion,
    string ProviderId,
    string AbiFamily,
    string MinimumAbiVersion,
    int BridgeAbiVersion,
    string ModuleName,
    string InitializationSymbol,
    IReadOnlyList<string> AllowedStableAbiSymbols,
    IReadOnlyList<string> RequiredFixtureExports,
    IReadOnlyList<string> RequiredBridgeExports,
    IReadOnlyList<string> AllowedMethods
)
{
    internal const string ExpectedManifestVersion = "dotpython-abi3-fixture-v1";
    internal const string ExpectedProviderId = "dotpython-managed-abi3";
    internal const int ExpectedBridgeAbiVersion = 1;

    private static readonly string[] StableAbiSymbols =
    [
        "PyErr_Occurred",
        "PyErr_SetString",
        "PyExc_ValueError",
        "PyLong_AsLong",
        "PyLong_FromLong",
        "PyModuleDef_Init",
        "PyModule_AddIntConstant",
    ];

    private static readonly string[] FixtureExports =
    [
        "PyInit_dotpython_fixture",
        "dotpython_fixture_cleanup_count",
    ];

    private static readonly string[] BridgeExports =
    [
        "PyErr_Occurred",
        "PyErr_SetString",
        "PyExc_ValueError",
        "PyLong_AsLong",
        "PyLong_FromLong",
        "PyModuleDef_Init",
        "PyModule_AddIntConstant",
        "dp_abi3_active_object_count",
        "dp_abi3_bridge_version",
        "dp_abi3_error_message",
        "dp_abi3_error_type",
        "dp_abi3_module_call_long",
        "dp_abi3_module_destroy",
        "dp_abi3_module_get_int",
        "dp_abi3_module_initialize",
    ];

    private static readonly string[] Methods = ["fail", "increment"];

    internal static StableAbiSymbolManifest Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        StableAbiSymbolManifest manifest;
        try
        {
            manifest =
                JsonSerializer.Deserialize(
                    bytes,
                    StableAbiManifestJsonContext.Default.StableAbiSymbolManifest
                ) ?? throw Invalid("The Stable-ABI symbol manifest contained JSON null.");
        }
        catch (JsonException exception)
        {
            throw Invalid("The Stable-ABI symbol manifest is not valid JSON.", exception);
        }

        manifest.Validate();
        return manifest;
    }

    private void Validate()
    {
        if (
            SchemaVersion != 1
            || !string.Equals(ManifestVersion, ExpectedManifestVersion, StringComparison.Ordinal)
            || !string.Equals(ProviderId, ExpectedProviderId, StringComparison.Ordinal)
            || !string.Equals(AbiFamily, "abi3", StringComparison.Ordinal)
            || !string.Equals(MinimumAbiVersion, "3.11", StringComparison.Ordinal)
            || BridgeAbiVersion != ExpectedBridgeAbiVersion
            || !string.Equals(ModuleName, "dotpython_fixture", StringComparison.Ordinal)
            || !string.Equals(
                InitializationSymbol,
                "PyInit_dotpython_fixture",
                StringComparison.Ordinal
            )
        )
        {
            throw Invalid("The Stable-ABI symbol manifest identity is unsupported.");
        }

        RequireExact(AllowedStableAbiSymbols, StableAbiSymbols, "Stable-ABI imports");
        RequireExact(RequiredFixtureExports, FixtureExports, "fixture exports");
        RequireExact(RequiredBridgeExports, BridgeExports, "bridge exports");
        RequireExact(AllowedMethods, Methods, "fixture methods");
    }

    private static void RequireExact(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected,
        string name
    )
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw Invalid($"The allowlisted {name} do not match the versioned bridge contract.");
        }
    }

    private static StableAbiLoadException Invalid(string message, Exception? inner = null) =>
        new(
            "DPY8003",
            StableAbiLoadPhase.Manifest,
            message,
            artifactPath: null,
            artifactSha256: null,
            missingSymbol: null,
            inner
        );
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
)]
[JsonSerializable(typeof(StableAbiSymbolManifest))]
internal sealed partial class StableAbiManifestJsonContext : JsonSerializerContext;
