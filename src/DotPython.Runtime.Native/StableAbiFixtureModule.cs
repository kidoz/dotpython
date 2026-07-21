using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotPython.Runtime.Native;

internal sealed class StableAbiFixtureModule : IDisposable
{
    private const int MaximumAnyverVersions = 4096;
    private readonly nint _bridgeLibrary;
    private readonly nint _fixtureLibrary;
    private readonly nint _module;
    private readonly HashSet<string> _allowedMethods;
    private readonly ModuleCallLong _callLong;
    private readonly ModuleDestroy _destroy;
    private readonly ErrorText _errorType;
    private readonly ErrorText _errorMessage;
    private readonly ActiveObjectCount _activeObjectCount;
    private readonly long _initializedObjectCount;
    private readonly long _activeObjectBaseline;
    private readonly int _expectedCleanupCount;
    private readonly bool _releaseLibraries;
    private readonly CleanupCount? _cleanupCount;
    private readonly AnyverCompare? _anyverCompare;
    private readonly AnyverSortVersions? _anyverSortVersions;
    private readonly AnyverVersionToJson? _anyverVersionToJson;
    private readonly object _gate = new();
    private int _disposed;

    private StableAbiFixtureModule(
        nint bridgeLibrary,
        nint fixtureLibrary,
        nint module,
        StableAbiSymbolManifest manifest,
        bool multiPhase,
        long readyValue,
        ModuleCallLong callLong,
        ModuleDestroy destroy,
        ErrorText errorType,
        ErrorText errorMessage,
        ActiveObjectCount activeObjectCount,
        long initializedObjectCount,
        long activeObjectBaseline,
        int expectedCleanupCount,
        bool releaseLibraries,
        CleanupCount? cleanupCount,
        AnyverCompare? anyverCompare,
        AnyverSortVersions? anyverSortVersions,
        AnyverVersionToJson? anyverVersionToJson
    )
    {
        _bridgeLibrary = bridgeLibrary;
        _fixtureLibrary = fixtureLibrary;
        _module = module;
        _allowedMethods = manifest.AllowedMethods.ToHashSet(StringComparer.Ordinal);
        _callLong = callLong;
        _destroy = destroy;
        _errorType = errorType;
        _errorMessage = errorMessage;
        _activeObjectCount = activeObjectCount;
        _initializedObjectCount = initializedObjectCount;
        _activeObjectBaseline = activeObjectBaseline;
        _expectedCleanupCount = expectedCleanupCount;
        _releaseLibraries = releaseLibraries;
        _cleanupCount = cleanupCount;
        _anyverCompare = anyverCompare;
        _anyverSortVersions = anyverSortVersions;
        _anyverVersionToJson = anyverVersionToJson;
        ManifestVersion = manifest.ManifestVersion;
        ModuleName = manifest.ModuleName;
        ArtifactSha256 = manifest.ArtifactSha256;
        NativeEntrySha256 = manifest.NativeEntrySha256;
        MultiPhase = multiPhase;
        ReadyValue = readyValue;
    }

    internal string ManifestVersion { get; }

    internal string ModuleName { get; }

    internal string? ArtifactSha256 { get; }

    internal string? NativeEntrySha256 { get; }

    internal bool MultiPhase { get; }

    internal long ReadyValue { get; }

    internal int CleanupCountAfterDispose { get; private set; }

    internal long InvokeLong(string method, long? argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        lock (_gate)
        {
            EnsureUsable(method);
            var status = _callLong(
                _module,
                method,
                argument.HasValue ? 1 : 0,
                argument.GetValueOrDefault(),
                out var result
            );
            if (status != 0)
            {
                throw InvocationFailure();
            }

            return result;
        }
    }

    internal long CompareAnyver(string left, string right, string ecosystem)
    {
        RequireAnyverText(left, "left version");
        RequireAnyverText(right, "right version");
        RequireAnyverText(ecosystem, "ecosystem");
        lock (_gate)
        {
            EnsureAnyver();
            if (_anyverCompare!(_module, left, right, ecosystem, out var result) != 0)
            {
                throw InvocationFailure();
            }

            return result;
        }
    }

    internal IReadOnlyList<string> SortAnyver(IReadOnlyList<string> versions, string ecosystem)
    {
        if (versions is null)
        {
            throw InvalidArguments("The Anyver version collection is required.");
        }

        RequireAnyverText(ecosystem, "ecosystem");
        if (versions.Count > MaximumAnyverVersions)
        {
            throw InvalidArguments(
                $"At most {MaximumAnyverVersions} versions may be sorted per call."
            );
        }

        lock (_gate)
        {
            EnsureAnyver();
            var strings = new nint[versions.Count];
            nint pointers = 0;
            try
            {
                for (var index = 0; index < versions.Count; index++)
                {
                    RequireAnyverText(versions[index], $"version at index {index}");
                    strings[index] = Marshal.StringToCoTaskMemUTF8(versions[index]);
                }

                pointers = Marshal.AllocHGlobal(checked(strings.Length * IntPtr.Size));
                Marshal.Copy(strings, 0, pointers, strings.Length);
                if (
                    _anyverSortVersions!(_module, pointers, versions.Count, ecosystem, out var json)
                    != 0
                )
                {
                    throw InvocationFailure();
                }

                return JsonSerializer.Deserialize(
                        ReadUtf8(json),
                        StableAbiResultJsonContext.Default.StringArray
                    ) ?? throw InvalidResult("Anyver sort returned JSON null.");
            }
            catch (JsonException exception)
            {
                throw InvalidResult("Anyver sort returned invalid JSON.", exception);
            }
            finally
            {
                if (pointers != 0)
                {
                    Marshal.FreeHGlobal(pointers);
                }

                foreach (var value in strings)
                {
                    Marshal.FreeCoTaskMem(value);
                }
            }
        }
    }

    internal StableAbiAnyverVersionInfo DescribeAnyverVersion(string version, string ecosystem)
    {
        RequireAnyverText(version, "version");
        RequireAnyverText(ecosystem, "ecosystem");
        lock (_gate)
        {
            EnsureAnyver();
            if (_anyverVersionToJson!(_module, version, ecosystem, out var json) != 0)
            {
                throw InvocationFailure();
            }

            try
            {
                return JsonSerializer.Deserialize(
                        ReadUtf8(json),
                        StableAbiResultJsonContext.Default.StableAbiAnyverVersionInfo
                    ) ?? throw InvalidResult("Anyver Version returned JSON null.");
            }
            catch (JsonException exception)
            {
                throw InvalidResult("Anyver Version returned invalid JSON.", exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _destroy(_module);
            CleanupCountAfterDispose = _cleanupCount?.Invoke() ?? 0;
            var activeObjects = _activeObjectCount();
            if (_releaseLibraries)
            {
                NativeLibrary.Free(_fixtureLibrary);
                NativeLibraryGlobalLoader.Free(_bridgeLibrary);
            }

            if (
                (
                    _cleanupCount is not null
                    && (
                        CleanupCountAfterDispose != _expectedCleanupCount
                        || activeObjects != _activeObjectBaseline
                    )
                ) || (_cleanupCount is null && activeObjects > _initializedObjectCount)
            )
            {
                throw Failure(
                    "DPY8006",
                    StableAbiLoadPhase.Cleanup,
                    $"Native module cleanup count was {CleanupCountAfterDispose} and active object count was {activeObjects}."
                );
            }
        }
    }

    internal static StableAbiFixtureModule Initialize(
        nint bridgeLibrary,
        nint fixtureLibrary,
        StableAbiSymbolManifest manifest,
        string fixturePath,
        string fixtureHash,
        bool releaseLibraries
    )
    {
        var bridgeVersion = GetDelegate<BridgeVersion>(bridgeLibrary, "dp_abi3_bridge_version");
        if (bridgeVersion() != manifest.BridgeAbiVersion)
        {
            throw Failure(
                "DPY8003",
                StableAbiLoadPhase.SymbolResolution,
                "The native bridge ABI version does not match the symbol manifest.",
                fixturePath,
                fixtureHash
            );
        }

        var initialize = GetDelegate<ModuleInitialize>(bridgeLibrary, "dp_abi3_module_initialize");
        var getInt = GetDelegate<ModuleGetInt>(bridgeLibrary, "dp_abi3_module_get_int");
        var callLong = GetDelegate<ModuleCallLong>(bridgeLibrary, "dp_abi3_module_call_long");
        var destroy = GetDelegate<ModuleDestroy>(bridgeLibrary, "dp_abi3_module_destroy");
        var errorType = GetDelegate<ErrorText>(bridgeLibrary, "dp_abi3_error_type");
        var errorMessage = GetDelegate<ErrorText>(bridgeLibrary, "dp_abi3_error_message");
        var activeObjectCount = GetDelegate<ActiveObjectCount>(
            bridgeLibrary,
            "dp_abi3_active_object_count"
        );
        var moduleInitializer = GetDelegate<ModuleInitializer>(
            fixtureLibrary,
            manifest.InitializationSymbol
        );
        var activeObjectBaseline = activeObjectCount();
        var initializationResult = moduleInitializer();
        if (
            initialize(initializationResult, out var module, out var multiPhase) != 0
            || module == 0
        )
        {
            throw Failure(
                "DPY8005",
                StableAbiLoadPhase.ModuleInitialization,
                $"{ReadUtf8(errorType())}: {ReadUtf8(errorMessage())}",
                fixturePath,
                fixtureHash
            );
        }

        long readyValue = 0;
        CleanupCount? cleanupCount = null;
        AnyverCompare? anyverCompare = null;
        AnyverSortVersions? anyverSortVersions = null;
        AnyverVersionToJson? anyverVersionToJson = null;
        var expectedCleanupCount = 0;
        if (manifest.IsAnyver)
        {
            anyverCompare = GetDelegate<AnyverCompare>(bridgeLibrary, "dp_abi3_anyver_compare");
            anyverSortVersions = GetDelegate<AnyverSortVersions>(
                bridgeLibrary,
                "dp_abi3_anyver_sort_versions"
            );
            anyverVersionToJson = GetDelegate<AnyverVersionToJson>(
                bridgeLibrary,
                "dp_abi3_anyver_version_to_json"
            );
        }
        else
        {
            cleanupCount = GetDelegate<CleanupCount>(
                fixtureLibrary,
                "dotpython_fixture_cleanup_count"
            );
            expectedCleanupCount = checked(cleanupCount() + 1);
            if (getInt(module, "fixture_ready", out readyValue) != 0)
            {
                destroy(module);
                throw Failure(
                    "DPY8005",
                    StableAbiLoadPhase.ModuleInitialization,
                    $"{ReadUtf8(errorType())}: {ReadUtf8(errorMessage())}",
                    fixturePath,
                    fixtureHash
                );
            }
        }

        return new StableAbiFixtureModule(
            bridgeLibrary,
            fixtureLibrary,
            module,
            manifest,
            multiPhase != 0,
            readyValue,
            callLong,
            destroy,
            errorType,
            errorMessage,
            activeObjectCount,
            activeObjectCount(),
            activeObjectBaseline,
            expectedCleanupCount,
            releaseLibraries,
            cleanupCount,
            anyverCompare,
            anyverSortVersions,
            anyverVersionToJson
        );
    }

    private void EnsureUsable(string method)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_allowedMethods.Contains(method))
        {
            throw Failure(
                "DPY8003",
                StableAbiLoadPhase.Invocation,
                $"Native module method '{method}' is not allowlisted."
            );
        }
    }

    private void EnsureAnyver()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_anyverCompare is null || _anyverSortVersions is null || _anyverVersionToJson is null)
        {
            throw Failure(
                "DPY8003",
                StableAbiLoadPhase.Invocation,
                "The loaded native module is not the pinned Anyver module."
            );
        }
    }

    private StableAbiLoadException InvocationFailure() =>
        Failure(
            "DPY8005",
            StableAbiLoadPhase.Invocation,
            $"{ReadUtf8(_errorType())}: {ReadUtf8(_errorMessage())}"
        );

    private static void RequireAnyverText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidArguments($"The Anyver {name} cannot be empty.");
        }
    }

    private static StableAbiLoadException InvalidArguments(string message) =>
        Failure("DPY8005", StableAbiLoadPhase.Invocation, message);

    private static T GetDelegate<T>(nint library, string symbol)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));

    private static string ReadUtf8(nint value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;

    private static StableAbiLoadException InvalidResult(string message, Exception? inner = null) =>
        Failure("DPY8005", StableAbiLoadPhase.Invocation, message, inner: inner);

    private static StableAbiLoadException Failure(
        string code,
        StableAbiLoadPhase phase,
        string message,
        string? artifactPath = null,
        string? artifactHash = null,
        Exception? inner = null
    ) => new(code, phase, message, artifactPath, artifactHash, missingSymbol: null, inner);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BridgeVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ModuleInitializer();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ModuleInitialize(
        nint initializationResult,
        out nint module,
        out int multiPhase
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ModuleGetInt(
        nint module,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out long value
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ModuleCallLong(
        nint module,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string method,
        int hasArgument,
        long argument,
        out long result
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AnyverCompare(
        nint module,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string left,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string right,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ecosystem,
        out long result
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AnyverSortVersions(
        nint module,
        nint versions,
        long versionCount,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ecosystem,
        out nint resultJson
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AnyverVersionToJson(
        nint module,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string version,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ecosystem,
        out nint resultJson
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ModuleDestroy(nint module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ErrorText();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ActiveObjectCount();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CleanupCount();
}

internal sealed record StableAbiAnyverVersionInfo(
    string Raw,
    string Ecosystem,
    long Epoch,
    long Major,
    long Minor,
    long Patch,
    string Build,
    [property: JsonPropertyName("is_prerelease")] bool IsPrerelease,
    [property: JsonPropertyName("is_postrelease")] bool IsPostrelease
);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(StableAbiAnyverVersionInfo))]
internal sealed partial class StableAbiResultJsonContext : JsonSerializerContext;
