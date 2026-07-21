using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotPython.Runtime.Native;

internal sealed class StableAbiModule : IDisposable
{
    private const int MaximumGenericObjects = 8192;
    private readonly nint _bridgeLibrary;
    private readonly nint _moduleLibrary;
    private readonly nint _module;
    private readonly ModuleDestroy _destroy;
    private readonly ErrorText _errorType;
    private readonly ErrorText _errorMessage;
    private readonly ActiveObjectCount _activeObjectCount;
    private readonly long _initializedObjectCount;
    private readonly long _activeObjectBaseline;
    private readonly int _expectedCleanupCount;
    private readonly bool _releaseLibraries;
    private readonly CleanupCount? _cleanupCount;
    private readonly GenericBridgeApi _generic;
    private readonly List<nint> _genericObjects = [];
    private readonly object _gate = new();
    private int _disposed;

    private StableAbiModule(
        nint bridgeLibrary,
        nint moduleLibrary,
        nint module,
        StableAbiSymbolManifest manifest,
        bool multiPhase,
        ModuleDestroy destroy,
        ErrorText errorType,
        ErrorText errorMessage,
        ActiveObjectCount activeObjectCount,
        long initializedObjectCount,
        long activeObjectBaseline,
        int expectedCleanupCount,
        bool releaseLibraries,
        CleanupCount? cleanupCount,
        GenericBridgeApi generic
    )
    {
        _bridgeLibrary = bridgeLibrary;
        _moduleLibrary = moduleLibrary;
        _module = module;
        _destroy = destroy;
        _errorType = errorType;
        _errorMessage = errorMessage;
        _activeObjectCount = activeObjectCount;
        _initializedObjectCount = initializedObjectCount;
        _activeObjectBaseline = activeObjectBaseline;
        _expectedCleanupCount = expectedCleanupCount;
        _releaseLibraries = releaseLibraries;
        _cleanupCount = cleanupCount;
        _generic = generic;
        ManifestVersion = manifest.ManifestVersion;
        ModuleName = manifest.ModuleName;
        ArtifactSha256 = manifest.ArtifactSha256;
        NativeEntrySha256 = manifest.NativeEntrySha256;
        MultiPhase = multiPhase;
    }

    internal string ManifestVersion { get; }

    internal string ModuleName { get; }

    internal string? ArtifactSha256 { get; }

    internal string? NativeEntrySha256 { get; }

    internal bool MultiPhase { get; }

    internal int CleanupCountAfterDispose { get; private set; }

    internal IReadOnlyList<string> GetAttributeNames()
    {
        lock (_gate)
        {
            EnsureActive();
            if (_generic.ModuleAttributeNames(_module, out var json) != 0)
            {
                throw InvocationFailure();
            }

            try
            {
                return JsonSerializer.Deserialize(
                        ReadUtf8(json),
                        StableAbiResultJsonContext.Default.StringArray
                    )
                    ?? throw InvalidResult("Native module attribute discovery returned JSON null.");
            }
            catch (JsonException exception)
            {
                throw InvalidResult(
                    "Native module attribute discovery returned invalid JSON.",
                    exception
                );
            }
        }
    }

    internal StableAbiObject GetAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            EnsureActive();
            if (_generic.ObjectGetAttribute(_module, name, out var result) != 0 || result == 0)
            {
                throw InvocationFailure();
            }

            return Track(result);
        }
    }

    internal StableAbiObject CreateText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            EnsureActive();
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var pointer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                if (_generic.ObjectFromUtf8(pointer, bytes.Length, out var result) != 0)
                {
                    throw InvocationFailure();
                }

                return Track(result);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    internal StableAbiObject CreateInt64(long value)
    {
        lock (_gate)
        {
            EnsureActive();
            if (_generic.ObjectFromInt64(value, out var result) != 0)
            {
                throw InvocationFailure();
            }

            return Track(result);
        }
    }

    internal StableAbiObject CreateBoolean(bool value)
    {
        lock (_gate)
        {
            EnsureActive();
            if (_generic.ObjectFromBool(value ? 1 : 0, out var result) != 0)
            {
                throw InvocationFailure();
            }

            return Track(result);
        }
    }

    internal StableAbiObject CreateNone()
    {
        lock (_gate)
        {
            EnsureActive();
            if (_generic.ObjectFromNone(out var result) != 0)
            {
                throw InvocationFailure();
            }

            return Track(result);
        }
    }

    internal StableAbiObject CreateSequence(
        StableAbiObjectKind kind,
        IReadOnlyList<StableAbiObject> items
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        if (kind is not (StableAbiObjectKind.List or StableAbiObjectKind.Tuple))
        {
            throw InvalidArguments("A generic native sequence must be a list or tuple.");
        }

        lock (_gate)
        {
            EnsureActive();
            var handles = ValidateHandles(items);
            return Track(
                InvokeObjectArray(
                    handles,
                    (nint pointer, long count, out nint result) =>
                        _generic.ObjectSequence((int)kind, pointer, count, out result)
                )
            );
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

            for (var index = _genericObjects.Count - 1; index >= 0; index--)
            {
                _generic.ObjectRelease(_genericObjects[index]);
            }

            _genericObjects.Clear();
            _destroy(_module);
            CleanupCountAfterDispose = _cleanupCount?.Invoke() ?? 0;
            var activeObjects = _activeObjectCount();
            if (_releaseLibraries)
            {
                NativeLibrary.Free(_moduleLibrary);
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

    internal static StableAbiModule Initialize(
        nint bridgeLibrary,
        nint moduleLibrary,
        StableAbiSymbolManifest manifest,
        string modulePath,
        string moduleHash,
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
                modulePath,
                moduleHash
            );
        }

        var initialize = GetDelegate<ModuleInitialize>(bridgeLibrary, "dp_abi3_module_initialize");
        var destroy = GetDelegate<ModuleDestroy>(bridgeLibrary, "dp_abi3_module_destroy");
        var errorType = GetDelegate<ErrorText>(bridgeLibrary, "dp_abi3_error_type");
        var errorMessage = GetDelegate<ErrorText>(bridgeLibrary, "dp_abi3_error_message");
        var activeObjectCount = GetDelegate<ActiveObjectCount>(
            bridgeLibrary,
            "dp_abi3_active_object_count"
        );
        var generic = GenericBridgeApi.Load(bridgeLibrary);
        var moduleInitializer = GetDelegate<ModuleInitializer>(
            moduleLibrary,
            manifest.InitializationSymbol
        );
        var activeObjectBaseline = activeObjectCount();
        var initializationResult = moduleInitializer();
        if (
            initialize(initializationResult, out var module, out var multiPhase) != 0
            || module == 0
        )
        {
            var errorTypeText = ReadUtf8(errorType());
            var errorMessageText = ReadUtf8(errorMessage());
            destroy(0);
            throw Failure(
                "DPY8005",
                StableAbiLoadPhase.ModuleInitialization,
                $"{errorTypeText}: {errorMessageText}",
                modulePath,
                moduleHash
            );
        }

        CleanupCount? cleanupCount = null;
        var expectedCleanupCount = 0;
        if (manifest.IsConformanceFixture)
        {
            cleanupCount = GetDelegate<CleanupCount>(
                moduleLibrary,
                $"{manifest.ModuleName}_cleanup_count"
            );
            expectedCleanupCount = checked(cleanupCount() + 1);
        }

        return new StableAbiModule(
            bridgeLibrary,
            moduleLibrary,
            module,
            manifest,
            multiPhase != 0,
            destroy,
            errorType,
            errorMessage,
            activeObjectCount,
            activeObjectCount(),
            activeObjectBaseline,
            expectedCleanupCount,
            releaseLibraries,
            cleanupCount,
            generic
        );
    }

    internal StableAbiObjectKind GetObjectKind(StableAbiObject value)
    {
        lock (_gate)
        {
            var handle = ValidateHandle(value);
            if (_generic.ObjectKind(handle, out var kind) != 0)
            {
                throw InvocationFailure();
            }

            return Enum.IsDefined((StableAbiObjectKind)kind)
                ? (StableAbiObjectKind)kind
                : throw InvalidResult($"Native object returned unknown kind {kind}.");
        }
    }

    internal StableAbiObject GetObjectAttribute(StableAbiObject value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            var handle = ValidateHandle(value);
            if (_generic.ObjectGetAttribute(handle, name, out var result) != 0 || result == 0)
            {
                throw InvocationFailure();
            }

            return Track(result);
        }
    }

    internal StableAbiObject CallObject(
        StableAbiObject callable,
        IReadOnlyList<StableAbiObject> arguments
    )
    {
        ArgumentNullException.ThrowIfNull(arguments);
        lock (_gate)
        {
            var callableHandle = ValidateHandle(callable);
            var handles = ValidateHandles(arguments);
            return Track(
                InvokeObjectArray(
                    handles,
                    (nint pointer, long count, out nint result) =>
                        _generic.ObjectCall(callableHandle, pointer, count, out result)
                )
            );
        }
    }

    internal long GetObjectInt64(StableAbiObject value)
    {
        lock (_gate)
        {
            if (_generic.ObjectAsInt64(ValidateHandle(value), out var result) != 0)
            {
                throw InvocationFailure();
            }

            return result;
        }
    }

    internal bool GetObjectBoolean(StableAbiObject value)
    {
        lock (_gate)
        {
            if (_generic.ObjectAsBool(ValidateHandle(value), out var result) != 0)
            {
                throw InvocationFailure();
            }

            return result != 0;
        }
    }

    internal string GetObjectText(StableAbiObject value)
    {
        lock (_gate)
        {
            if (_generic.ObjectAsUtf8(ValidateHandle(value), out var result, out var length) != 0)
            {
                throw InvocationFailure();
            }

            return ReadUtf8(result, length);
        }
    }

    internal string GetObjectDisplay(StableAbiObject value)
    {
        lock (_gate)
        {
            if (_generic.ObjectString(ValidateHandle(value), out var result, out var length) != 0)
            {
                throw InvocationFailure();
            }

            return ReadUtf8(result, length);
        }
    }

    internal long GetObjectSize(StableAbiObject value)
    {
        lock (_gate)
        {
            if (_generic.ObjectSize(ValidateHandle(value), out var result) != 0)
            {
                throw InvocationFailure();
            }

            return result;
        }
    }

    internal StableAbiObject GetObjectItem(StableAbiObject value, StableAbiObject key)
    {
        lock (_gate)
        {
            if (
                _generic.ObjectGetItem(ValidateHandle(value), ValidateHandle(key), out var result)
                    != 0
                || result == 0
            )
            {
                throw InvocationFailure();
            }

            return Track(result);
        }
    }

    internal void ReleaseObject(StableAbiObject value)
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            var handle = value.Detach(this);
            if (handle == 0)
            {
                return;
            }

            var index = _genericObjects.LastIndexOf(handle);
            if (index < 0)
            {
                throw new InvalidOperationException("The generic native object is not owned.");
            }

            _genericObjects.RemoveAt(index);
            _generic.ObjectRelease(handle);
        }
    }

    private StableAbiObject Track(nint handle)
    {
        if (handle == 0)
        {
            throw InvalidResult("The generic native bridge returned a null object.");
        }

        if (_genericObjects.Count >= MaximumGenericObjects)
        {
            _generic.ObjectRelease(handle);
            throw InvalidArguments(
                $"A native module session cannot own more than {MaximumGenericObjects} generic objects."
            );
        }

        _genericObjects.Add(handle);
        return new StableAbiObject(this, handle);
    }

    private nint ValidateHandle(StableAbiObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureActive();
        return value.GetHandle(this);
    }

    private nint[] ValidateHandles(IReadOnlyList<StableAbiObject> values)
    {
        if (values.Count > 4096)
        {
            throw InvalidArguments("A generic native operation accepts at most 4096 objects.");
        }

        var handles = new nint[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            handles[index] = ValidateHandle(values[index]);
        }

        return handles;
    }

    private nint InvokeObjectArray(nint[] handles, ObjectArrayOperation operation)
    {
        nint pointer = 0;
        try
        {
            if (handles.Length != 0)
            {
                pointer = Marshal.AllocHGlobal(checked(handles.Length * IntPtr.Size));
                Marshal.Copy(handles, 0, pointer, handles.Length);
            }

            if (operation(pointer, handles.Length, out var result) != 0 || result == 0)
            {
                throw InvocationFailure();
            }

            return result;
        }
        finally
        {
            if (pointer != 0)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private StableAbiLoadException InvocationFailure() =>
        Failure(
            "DPY8005",
            StableAbiLoadPhase.Invocation,
            $"{ReadUtf8(_errorType())}: {ReadUtf8(_errorMessage())}"
        );

    private static StableAbiLoadException InvalidArguments(string message) =>
        Failure("DPY8005", StableAbiLoadPhase.Invocation, message);

    private static T GetDelegate<T>(nint library, string symbol)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));

    private static string ReadUtf8(nint value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;

    private static string ReadUtf8(nint value, long length)
    {
        if (value == 0 || length < 0 || length > int.MaxValue)
        {
            throw InvalidResult("The native bridge returned invalid UTF-8 storage.");
        }

        return Marshal.PtrToStringUTF8(value, checked((int)length));
    }

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

    private delegate int ObjectArrayOperation(nint values, long count, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ModuleAttributeNames(nint module, out nint resultJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectGetAttribute(
        nint value,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out nint result
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectCall(nint callable, nint arguments, long count, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectFromUtf8(nint value, long length, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectFromInt64(long value, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectFromBool(int value, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectFromNone(out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectSequence(int kind, nint items, long count, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectKind(nint value, out int kind);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectAsInt64(nint value, out long result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectAsBool(nint value, out int result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectAsUtf8(nint value, out nint result, out long length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectString(nint value, out nint result, out long length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectSize(nint value, out long result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObjectGetItem(nint value, nint key, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjectRelease(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ModuleDestroy(nint module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ErrorText();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ActiveObjectCount();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CleanupCount();

    private sealed record GenericBridgeApi(
        ModuleAttributeNames ModuleAttributeNames,
        ObjectGetAttribute ObjectGetAttribute,
        ObjectCall ObjectCall,
        ObjectFromUtf8 ObjectFromUtf8,
        ObjectFromInt64 ObjectFromInt64,
        ObjectFromBool ObjectFromBool,
        ObjectFromNone ObjectFromNone,
        ObjectSequence ObjectSequence,
        ObjectKind ObjectKind,
        ObjectAsInt64 ObjectAsInt64,
        ObjectAsBool ObjectAsBool,
        ObjectAsUtf8 ObjectAsUtf8,
        ObjectString ObjectString,
        ObjectSize ObjectSize,
        ObjectGetItem ObjectGetItem,
        ObjectRelease ObjectRelease
    )
    {
        internal static GenericBridgeApi Load(nint library) =>
            new(
                GetDelegate<ModuleAttributeNames>(library, "dp_abi3_module_attribute_names"),
                GetDelegate<ObjectGetAttribute>(library, "dp_abi3_object_get_attr"),
                GetDelegate<ObjectCall>(library, "dp_abi3_object_call"),
                GetDelegate<ObjectFromUtf8>(library, "dp_abi3_object_from_utf8"),
                GetDelegate<ObjectFromInt64>(library, "dp_abi3_object_from_int64"),
                GetDelegate<ObjectFromBool>(library, "dp_abi3_object_from_bool"),
                GetDelegate<ObjectFromNone>(library, "dp_abi3_object_from_none"),
                GetDelegate<ObjectSequence>(library, "dp_abi3_object_sequence"),
                GetDelegate<ObjectKind>(library, "dp_abi3_object_kind_of"),
                GetDelegate<ObjectAsInt64>(library, "dp_abi3_object_as_int64"),
                GetDelegate<ObjectAsBool>(library, "dp_abi3_object_as_bool"),
                GetDelegate<ObjectAsUtf8>(library, "dp_abi3_object_as_utf8"),
                GetDelegate<ObjectString>(library, "dp_abi3_object_string"),
                GetDelegate<ObjectSize>(library, "dp_abi3_object_size"),
                GetDelegate<ObjectGetItem>(library, "dp_abi3_object_get_item"),
                GetDelegate<ObjectRelease>(library, "dp_abi3_object_release")
            );
    }
}

internal enum StableAbiObjectKind
{
    Invalid = 0,
    None = 1,
    Boolean = 2,
    Integer = 3,
    Text = 4,
    Bytes = 5,
    List = 6,
    Tuple = 7,
    Dictionary = 8,
    Module = 9,
    Callable = 10,
    Type = 11,
    Instance = 12,
}

internal sealed class StableAbiObject : IDisposable
{
    private StableAbiModule? _owner;
    private nint _handle;

    internal StableAbiObject(StableAbiModule owner, nint handle)
    {
        _owner = owner;
        _handle = handle;
    }

    internal StableAbiObjectKind Kind => RequireOwner().GetObjectKind(this);

    internal StableAbiModule Owner => RequireOwner();

    internal StableAbiObject GetAttribute(string name) =>
        RequireOwner().GetObjectAttribute(this, name);

    internal StableAbiObject Call(IReadOnlyList<StableAbiObject> arguments) =>
        RequireOwner().CallObject(this, arguments);

    internal long AsInt64() => RequireOwner().GetObjectInt64(this);

    internal bool AsBoolean() => RequireOwner().GetObjectBoolean(this);

    internal string AsText() => RequireOwner().GetObjectText(this);

    internal string ToDisplayString() => RequireOwner().GetObjectDisplay(this);

    internal long GetSize() => RequireOwner().GetObjectSize(this);

    internal StableAbiObject GetItem(StableAbiObject key) =>
        RequireOwner().GetObjectItem(this, key);

    public void Dispose()
    {
        var owner = _owner;
        owner?.ReleaseObject(this);
    }

    internal nint Detach(StableAbiModule owner)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            return 0;
        }

        _owner = null;
        return Interlocked.Exchange(ref _handle, 0);
    }

    internal nint GetHandle(StableAbiModule owner)
    {
        ObjectDisposedException.ThrowIf(!ReferenceEquals(_owner, owner) || _handle == 0, this);
        return _handle;
    }

    private StableAbiModule RequireOwner() =>
        _owner ?? throw new ObjectDisposedException(nameof(StableAbiObject));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(string[]))]
internal sealed partial class StableAbiResultJsonContext : JsonSerializerContext;
