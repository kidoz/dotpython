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
    IReadOnlyList<string> AllowedMethods,
    string? ArtifactFileName,
    string? ArtifactSha256,
    string? NativeEntry,
    string? NativeEntrySha256,
    string? SourceRevision
)
{
    internal const string ExpectedManifestVersion = "dotpython-abi3-fixture-v1";
    internal const string AnyverManifestVersion = "dotpython-abi3-anyver-1.1.0-v1";
    internal const string ExpectedProviderId = "dotpython-managed-abi3";
    internal const int ExpectedBridgeAbiVersion = 2;

    internal bool IsAnyver =>
        string.Equals(ManifestVersion, AnyverManifestVersion, StringComparison.Ordinal);

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

    private static readonly string[] AnyverStableAbiSymbols =
    [
        "PyBaseObject_Type",
        "PyBytes_AsString",
        "PyBytes_Size",
        "PyCMethod_New",
        "PyDict_GetItemWithError",
        "PyDict_New",
        "PyDict_Next",
        "PyDict_SetItem",
        "PyDict_Size",
        "PyDict_Type",
        "PyErr_Fetch",
        "PyErr_GivenExceptionMatches",
        "PyErr_NewExceptionWithDoc",
        "PyErr_NormalizeException",
        "PyErr_Occurred",
        "PyErr_PrintEx",
        "PyErr_Restore",
        "PyErr_SetObject",
        "PyErr_SetString",
        "PyErr_WriteUnraisable",
        "PyEval_RestoreThread",
        "PyEval_SaveThread",
        "PyExc_AttributeError",
        "PyExc_BaseException",
        "PyExc_IndexError",
        "PyExc_RuntimeError",
        "PyExc_SystemError",
        "PyExc_TypeError",
        "PyExc_ValueError",
        "PyException_SetCause",
        "PyException_SetTraceback",
        "PyGILState_Ensure",
        "PyGILState_Release",
        "PyImport_Import",
        "PyIter_Next",
        "PyList_Append",
        "PyList_GetItem",
        "PyList_New",
        "PyList_SetItem",
        "PyList_Size",
        "PyList_Type",
        "PyLong_AsLong",
        "PyLong_FromLong",
        "PyLong_FromSsize_t",
        "PyLong_FromUnsignedLongLong",
        "PyModuleDef_Init",
        "PyModule_GetNameObject",
        "PyModule_Type",
        "PyObject_Call",
        "PyObject_CallNoArgs",
        "PyObject_DelItem",
        "PyObject_GC_UnTrack",
        "PyObject_GenericGetDict",
        "PyObject_GenericSetDict",
        "PyObject_GetAttr",
        "PyObject_GetItem",
        "PyObject_GetIter",
        "PyObject_Repr",
        "PyObject_SetAttr",
        "PyObject_SetAttrString",
        "PyObject_SetItem",
        "PyObject_Size",
        "PyObject_Str",
        "PySequence_Check",
        "PyTraceBack_Print",
        "PyTuple_GetItem",
        "PyTuple_New",
        "PyTuple_SetItem",
        "PyTuple_Size",
        "PyTuple_Type",
        "PyType_FromSpec",
        "PyType_GetFlags",
        "PyType_GetName",
        "PyType_GetQualName",
        "PyType_GetSlot",
        "PyType_IsSubtype",
        "PyType_Type",
        "PyUnicode_AsEncodedString",
        "PyUnicode_AsUTF8AndSize",
        "PyUnicode_FromStringAndSize",
        "PyUnicode_InternInPlace",
        "PyUnicode_Type",
        "Py_IsInitialized",
        "Py_NewRef",
        "_Py_DecRef",
        "_Py_FalseStruct",
        "_Py_IncRef",
        "_Py_NoneStruct",
        "_Py_NotImplementedStruct",
        "_Py_TrueStruct",
    ];

    private static readonly string[] BridgeOnlyExports =
    [
        "dp_abi3_active_object_count",
        "dp_abi3_anyver_compare",
        "dp_abi3_anyver_sort_versions",
        "dp_abi3_anyver_version_to_json",
        "dp_abi3_bridge_version",
        "dp_abi3_error_message",
        "dp_abi3_error_type",
        "dp_abi3_module_call_long",
        "dp_abi3_module_destroy",
        "dp_abi3_module_get_int",
        "dp_abi3_module_initialize",
    ];

    private static readonly string[] FixtureMethods = ["fail", "increment"];

    private static readonly string[] AnyverMethods =
    [
        "Version",
        "batch_compare",
        "build_purl",
        "build_vers",
        "bump_major",
        "bump_minor",
        "bump_patch",
        "bump_prerelease",
        "compare",
        "compare_semver_strict",
        "eq",
        "ge",
        "gt",
        "gte",
        "latest_stable",
        "le",
        "lt",
        "lte",
        "max_version",
        "min_version",
        "ne",
        "next_stable",
        "osv_affected",
        "parse_purl",
        "purl_ecosystem",
        "range_contains",
        "ranges_intersect",
        "satisfies",
        "sort_versions",
        "stable_versions",
        "vers_contains",
        "version",
    ];

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
            || !string.Equals(ProviderId, ExpectedProviderId, StringComparison.Ordinal)
            || !string.Equals(AbiFamily, "abi3", StringComparison.Ordinal)
            || !string.Equals(MinimumAbiVersion, "3.11", StringComparison.Ordinal)
            || BridgeAbiVersion != ExpectedBridgeAbiVersion
        )
        {
            throw Invalid("The Stable-ABI symbol manifest identity is unsupported.");
        }

        RequireExactSet(
            RequiredBridgeExports,
            AnyverStableAbiSymbols.Append("PyModule_AddIntConstant").Concat(BridgeOnlyExports),
            "bridge exports"
        );
        if (IsAnyver)
        {
            ValidateAnyver();
        }
        else if (string.Equals(ManifestVersion, ExpectedManifestVersion, StringComparison.Ordinal))
        {
            ValidateFixture();
        }
        else
        {
            throw Invalid("The Stable-ABI symbol manifest version is unsupported.");
        }
    }

    private void ValidateFixture()
    {
        if (
            !string.Equals(ModuleName, "dotpython_fixture", StringComparison.Ordinal)
            || !string.Equals(
                InitializationSymbol,
                "PyInit_dotpython_fixture",
                StringComparison.Ordinal
            )
            || ArtifactFileName is not null
            || ArtifactSha256 is not null
            || NativeEntry is not null
            || NativeEntrySha256 is not null
            || SourceRevision is not null
        )
        {
            throw Invalid("The Stable-ABI fixture manifest identity is unsupported.");
        }

        RequireExact(AllowedStableAbiSymbols, StableAbiSymbols, "Stable-ABI imports");
        RequireExact(RequiredFixtureExports, FixtureExports, "fixture exports");
        RequireExact(AllowedMethods, FixtureMethods, "fixture methods");
    }

    private void ValidateAnyver()
    {
        if (
            !string.Equals(ModuleName, "anyver._anyver", StringComparison.Ordinal)
            || !string.Equals(InitializationSymbol, "PyInit__anyver", StringComparison.Ordinal)
            || !string.Equals(
                ArtifactFileName,
                "anyver-1.1.0-cp311-abi3-macosx_11_0_arm64.whl",
                StringComparison.Ordinal
            )
            || !string.Equals(
                ArtifactSha256,
                "0f2fa90663b0203d3086c313d6384a6d74177e1f52508abf613cb17439edc4f9",
                StringComparison.Ordinal
            )
            || !string.Equals(NativeEntry, "anyver/_anyver.abi3.so", StringComparison.Ordinal)
            || !string.Equals(
                NativeEntrySha256,
                "d635b4b37c6db5688d49ecb1b924fc6c3bfe7f51b630d5ca153ab6ab474b2827",
                StringComparison.Ordinal
            )
            || !string.Equals(
                SourceRevision,
                "3dc892e3eb9d1a4baf7a315a6ce4a41b3893337e",
                StringComparison.Ordinal
            )
        )
        {
            throw Invalid("The Anyver Stable-ABI manifest identity is unsupported.");
        }

        RequireExact(AllowedStableAbiSymbols, AnyverStableAbiSymbols, "Anyver Stable-ABI imports");
        RequireExact(RequiredFixtureExports, ["PyInit__anyver"], "Anyver exports");
        RequireExact(AllowedMethods, AnyverMethods, "Anyver methods");
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

    private static void RequireExactSet(
        IReadOnlyList<string> actual,
        IEnumerable<string> expected,
        string name
    )
    {
        ArgumentNullException.ThrowIfNull(actual);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        if (actual.Count != expectedSet.Count || !actual.All(expectedSet.Contains))
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
