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
    string CapabilityId,
    string LibraryLifetime,
    string ModuleName,
    string InitializationSymbol,
    IReadOnlyList<string> AllowedStableAbiSymbols,
    IReadOnlyList<string> RequiredModuleExports,
    IReadOnlyList<string> RequiredBridgeExports,
    IReadOnlyList<string> AllowedMethods,
    string? ArtifactFileName,
    string? ArtifactSha256,
    string? NativeEntry,
    string? NativeEntrySha256,
    string? PackageInitializer,
    string? PackageInitializerSha256,
    string? PackageMetadata,
    string? PackageMetadataSha256,
    string? SourceRevision
)
{
    internal const string ExpectedManifestVersion = "dotpython-abi3-fixture-v4";
    internal const string ExpectedSecondaryManifestVersion = "dotpython-abi3-fixture-secondary-v2";
    internal const string ExpectedProviderId = "dotpython-managed-abi3";
    internal const int ExpectedBridgeAbiVersion = 6;

    internal bool IsConformanceFixture =>
        string.Equals(ManifestVersion, ExpectedManifestVersion, StringComparison.Ordinal)
        || string.Equals(
            ManifestVersion,
            ExpectedSecondaryManifestVersion,
            StringComparison.Ordinal
        );

    internal bool ProcessPinned =>
        string.Equals(LibraryLifetime, "process", StringComparison.Ordinal);

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

    private static readonly string[] ConformanceModuleExports =
    [
        "PyInit_dotpython_fixture",
        "dotpython_fixture_cleanup_count",
    ];

    private static readonly string[] SecondaryConformanceModuleExports =
    [
        "PyInit_dotpython_fixture_secondary",
        "dotpython_fixture_secondary_cleanup_count",
    ];

    private static readonly string[] BridgeStableAbiSymbols =
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
        "PyObject_RichCompare",
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

    private static readonly string[] BridgeControlExports =
    [
        "dp_abi3_active_object_count",
        "dp_abi3_bridge_version",
        "dp_abi3_error_message",
        "dp_abi3_error_type",
        "dp_abi3_module_destroy",
        "dp_abi3_module_initialize",
        "dp_abi3_module_attribute_names",
        "dp_abi3_object_as_bool",
        "dp_abi3_object_as_int64",
        "dp_abi3_object_as_utf8",
        "dp_abi3_object_call",
        "dp_abi3_object_call_kw",
        "dp_abi3_object_from_bool",
        "dp_abi3_object_from_int64",
        "dp_abi3_object_from_none",
        "dp_abi3_object_from_utf8",
        "dp_abi3_object_get_attr",
        "dp_abi3_object_get_item",
        "dp_abi3_object_kind_of",
        "dp_abi3_object_release",
        "dp_abi3_object_repr",
        "dp_abi3_object_rich_compare",
        "dp_abi3_object_sequence",
        "dp_abi3_object_size",
        "dp_abi3_object_string",
    ];

    private static readonly string[] ConformanceMethods = ["fail", "increment"];
    private static readonly string[] SecondaryConformanceMethods = ["double", "fail"];

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
            SchemaVersion != 3
            || !string.Equals(ProviderId, ExpectedProviderId, StringComparison.Ordinal)
            || !string.Equals(AbiFamily, "abi3", StringComparison.Ordinal)
            || !string.Equals(MinimumAbiVersion, "3.11", StringComparison.Ordinal)
            || BridgeAbiVersion != ExpectedBridgeAbiVersion
            || !IsToken(ManifestVersion)
            || !IsToken(CapabilityId)
            || LibraryLifetime is not ("module" or "process")
            || !IsQualifiedModuleName(ModuleName)
            || string.IsNullOrWhiteSpace(InitializationSymbol)
        )
        {
            throw Invalid("The Stable-ABI symbol manifest identity is unsupported.");
        }

        RequireExactSet(
            RequiredBridgeExports,
            BridgeStableAbiSymbols.Append("PyModule_AddIntConstant").Concat(BridgeControlExports),
            "bridge exports"
        );
        ValidateSymbolList(AllowedStableAbiSymbols, "Stable-ABI imports", maximumCount: 512);
        ValidateSymbolList(RequiredModuleExports, "native module exports", maximumCount: 32);
        ValidateSymbolList(AllowedMethods, "module methods", maximumCount: 512);
        if (
            !AllowedStableAbiSymbols.All(symbol =>
                string.Equals(symbol, "PyModule_AddIntConstant", StringComparison.Ordinal)
                || BridgeStableAbiSymbols.Contains(symbol, StringComparer.Ordinal)
            )
        )
        {
            throw Invalid("The manifest imports symbols outside the versioned bridge capability.");
        }

        var moduleLeaf = ModuleName.Split('.').Last();
        if (!string.Equals(InitializationSymbol, $"PyInit_{moduleLeaf}", StringComparison.Ordinal))
        {
            throw Invalid("The native initializer does not match the qualified module name.");
        }

        if (IsConformanceFixture)
        {
            ValidateFixture();
        }
        else
        {
            ValidateQualifiedArtifact();
        }
    }

    private void ValidateFixture()
    {
        var secondary = string.Equals(
            ManifestVersion,
            ExpectedSecondaryManifestVersion,
            StringComparison.Ordinal
        );
        var expectedModuleName = secondary ? "dotpython_fixture_secondary" : "dotpython_fixture";
        var expectedCapability = secondary
            ? "managed-stable-abi-fixture-secondary-v2"
            : "managed-stable-abi-fixture-v4";
        var expectedInitializer = secondary
            ? "PyInit_dotpython_fixture_secondary"
            : "PyInit_dotpython_fixture";
        if (
            !string.Equals(ModuleName, expectedModuleName, StringComparison.Ordinal)
            || !string.Equals(CapabilityId, expectedCapability, StringComparison.Ordinal)
            || !string.Equals(LibraryLifetime, "module", StringComparison.Ordinal)
            || !string.Equals(InitializationSymbol, expectedInitializer, StringComparison.Ordinal)
            || ArtifactFileName is not null
            || ArtifactSha256 is not null
            || NativeEntry is not null
            || NativeEntrySha256 is not null
            || PackageInitializer is not null
            || PackageInitializerSha256 is not null
            || PackageMetadata is not null
            || PackageMetadataSha256 is not null
            || SourceRevision is not null
        )
        {
            throw Invalid("The Stable-ABI fixture manifest identity is unsupported.");
        }

        RequireExact(AllowedStableAbiSymbols, StableAbiSymbols, "Stable-ABI imports");
        RequireExact(
            RequiredModuleExports,
            secondary ? SecondaryConformanceModuleExports : ConformanceModuleExports,
            "conformance module exports"
        );
        RequireExact(
            AllowedMethods,
            secondary ? SecondaryConformanceMethods : ConformanceMethods,
            "conformance module methods"
        );
    }

    private void ValidateQualifiedArtifact()
    {
        if (
            !ProcessPinned
            || string.IsNullOrWhiteSpace(ArtifactFileName)
            || ArtifactFileName.IndexOfAny(['/', '\\']) >= 0
            || Path.GetFileName(ArtifactFileName) != ArtifactFileName
            || !ArtifactFileName.EndsWith(".whl", StringComparison.Ordinal)
            || !IsSha256(ArtifactSha256)
            || !IsSafeRelativePath(NativeEntry)
            || !IsSha256(NativeEntrySha256)
            || !IsSafeRelativePath(PackageInitializer)
            || !IsSha256(PackageInitializerSha256)
            || !IsSafeRelativePath(PackageMetadata)
            || !IsSha256(PackageMetadataSha256)
            || string.IsNullOrWhiteSpace(SourceRevision)
            || SourceRevision.Length > 128
            || AllowedStableAbiSymbols.Count == 0
            || AllowedMethods.Count == 0
        )
        {
            throw Invalid("The qualified Stable-ABI artifact manifest is incomplete or unsafe.");
        }

        RequireExact(RequiredModuleExports, [InitializationSymbol], "native module exports");
    }

    private static void ValidateSymbolList(
        IReadOnlyList<string> values,
        string name,
        int maximumCount
    )
    {
        ArgumentNullException.ThrowIfNull(values);
        if (
            values.Count > maximumCount
            || values.Any(value =>
                string.IsNullOrWhiteSpace(value)
                || value.Length > 256
                || value.Any(character =>
                    character
                        is not ('_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                )
            )
            || values.Count != values.Distinct(StringComparer.Ordinal).Count()
        )
        {
            throw Invalid($"The allowlisted {name} are invalid or exceed their bound.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 128 }
        && value.All(character =>
            character
                is '-'
                    or '.'
                    or '_'
                    or >= 'a'
                    and <= 'z'
                    or >= 'A'
                    and <= 'Z'
                    or >= '0'
                    and <= '9'
        );

    private static bool IsQualifiedModuleName(string? value) =>
        value is { Length: > 0 and <= 256 }
        && value
            .Split('.')
            .All(segment =>
                segment.Length > 0
                && segment[0] is '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                && segment.All(character =>
                    character is '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                )
            );

    private static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value))
        {
            return false;
        }

        var segments = value.Split(['/', '\\']);
        return segments.Length > 1
            && segments.All(segment =>
                segment.Length > 0
                && segment is not "." and not ".."
                && !segment.Contains(':', StringComparison.Ordinal)
                && !segment.Contains('\0', StringComparison.Ordinal)
            );
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
