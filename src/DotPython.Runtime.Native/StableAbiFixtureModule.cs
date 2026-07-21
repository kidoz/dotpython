using System.Runtime.InteropServices;

namespace DotPython.Runtime.Native;

internal sealed class StableAbiFixtureModule : IDisposable
{
    private readonly nint _bridgeLibrary;
    private readonly nint _fixtureLibrary;
    private readonly nint _module;
    private readonly HashSet<string> _allowedMethods;
    private readonly ModuleCallLong _callLong;
    private readonly ModuleDestroy _destroy;
    private readonly ErrorText _errorType;
    private readonly ErrorText _errorMessage;
    private readonly ActiveObjectCount _activeObjectCount;
    private readonly CleanupCount _cleanupCount;
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
        CleanupCount cleanupCount
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
        _cleanupCount = cleanupCount;
        ManifestVersion = manifest.ManifestVersion;
        ModuleName = manifest.ModuleName;
        MultiPhase = multiPhase;
        ReadyValue = readyValue;
    }

    internal string ManifestVersion { get; }

    internal string ModuleName { get; }

    internal bool MultiPhase { get; }

    internal long ReadyValue { get; }

    internal int CleanupCountAfterDispose { get; private set; }

    internal long InvokeLong(string method, long? argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_allowedMethods.Contains(method))
            {
                throw Failure(
                    "DPY8003",
                    StableAbiLoadPhase.Invocation,
                    $"Native fixture method '{method}' is not allowlisted."
                );
            }

            var status = _callLong(
                _module,
                method,
                argument.HasValue ? 1 : 0,
                argument.GetValueOrDefault(),
                out var result
            );
            if (status != 0)
            {
                throw Failure(
                    "DPY8005",
                    StableAbiLoadPhase.Invocation,
                    $"{ReadUtf8(_errorType())}: {ReadUtf8(_errorMessage())}"
                );
            }

            return result;
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
            CleanupCountAfterDispose = _cleanupCount();
            var activeObjects = _activeObjectCount();
            NativeLibrary.Free(_fixtureLibrary);
            NativeLibrary.Free(_bridgeLibrary);
            if (CleanupCountAfterDispose != 1 || activeObjects != 0)
            {
                throw Failure(
                    "DPY8006",
                    StableAbiLoadPhase.Cleanup,
                    $"Native fixture cleanup count was {CleanupCountAfterDispose} and active object count was {activeObjects}."
                );
            }
        }
    }

    internal static StableAbiFixtureModule Initialize(
        nint bridgeLibrary,
        nint fixtureLibrary,
        StableAbiSymbolManifest manifest,
        string fixturePath,
        string fixtureHash
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
        var cleanupCount = GetDelegate<CleanupCount>(
            fixtureLibrary,
            "dotpython_fixture_cleanup_count"
        );
        var moduleInitializer = GetDelegate<ModuleInitializer>(
            fixtureLibrary,
            manifest.InitializationSymbol
        );
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

        if (getInt(module, "fixture_ready", out var readyValue) != 0)
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
            cleanupCount
        );
    }

    private static T GetDelegate<T>(nint library, string symbol)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));

    private static string ReadUtf8(nint value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;

    private static StableAbiLoadException Failure(
        string code,
        StableAbiLoadPhase phase,
        string message,
        string? artifactPath = null,
        string? artifactHash = null
    ) => new(code, phase, message, artifactPath, artifactHash, missingSymbol: null);

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
    private delegate void ModuleDestroy(nint module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ErrorText();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ActiveObjectCount();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CleanupCount();
}
