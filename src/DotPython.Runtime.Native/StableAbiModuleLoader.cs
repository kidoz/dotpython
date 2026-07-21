using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DotPython.Runtime.Native;

internal static class StableAbiModuleLoader
{
    private const long MaximumArtifactBytes = 16 * 1024 * 1024;

    internal static StableAbiModule Load(StableAbiModuleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateFile(configuration.BridgePath, nameof(configuration.BridgePath));
        ValidateFile(configuration.ModulePath, nameof(configuration.ModulePath));
        ValidateFile(configuration.ManifestPath, nameof(configuration.ManifestPath));
        var bridgeHash = ValidateHash(configuration.BridgePath, configuration.BridgeSha256);
        var moduleHash = ValidateHash(configuration.ModulePath, configuration.ModuleSha256);
        _ = ValidateHash(configuration.ManifestPath, configuration.ManifestSha256);
        NativeBinaryIdentity
            .Read(configuration.BridgePath)
            .ValidateCurrentPlatform(configuration.BridgePath);
        NativeBinaryIdentity
            .Read(configuration.ModulePath)
            .ValidateCurrentPlatform(configuration.ModulePath);
        var manifest = StableAbiSymbolManifest.Load(configuration.ManifestPath);
        if (
            manifest.NativeEntrySha256 is not null
            && !string.Equals(manifest.NativeEntrySha256, moduleHash, StringComparison.Ordinal)
        )
        {
            throw new StableAbiLoadException(
                "DPY8001",
                StableAbiLoadPhase.Policy,
                "The configured native entry does not match the pinned manifest SHA-256.",
                configuration.ModulePath,
                moduleHash,
                missingSymbol: null
            );
        }

        if (manifest.ProcessPinned)
        {
            var cached = StableAbiProcessLibraryCache.Load(
                configuration.BridgePath,
                bridgeHash,
                configuration.ModulePath,
                moduleHash
            );
            ValidateExports(
                cached.Bridge,
                manifest.RequiredBridgeExports,
                configuration.BridgePath,
                bridgeHash
            );
            ValidateExports(
                cached.Module,
                manifest.RequiredModuleExports,
                configuration.ModulePath,
                moduleHash
            );
            try
            {
                return StableAbiModule.Initialize(
                    cached.Bridge,
                    cached.Module,
                    manifest,
                    configuration.ModulePath,
                    moduleHash,
                    releaseLibraries: false
                );
            }
            catch (StableAbiLoadException exception)
                when (exception.Phase == StableAbiLoadPhase.ModuleInitialization)
            {
                throw new StableAbiLoadException(
                    "DPY8004",
                    StableAbiLoadPhase.ModuleInitialization,
                    $"Pinned Stable-ABI initialization left process-lifetime native caches in an unknown state: {exception.Message}",
                    configuration.ModulePath,
                    moduleHash,
                    missingSymbol: null,
                    exception
                );
            }
        }

        nint bridge = 0;
        nint module = 0;
        try
        {
            bridge = LoadBridgeLibrary(
                configuration.BridgePath,
                bridgeHash,
                StableAbiLoadPhase.BridgeLoad
            );
            ValidateExports(
                bridge,
                manifest.RequiredBridgeExports,
                configuration.BridgePath,
                bridgeHash
            );
            module = LoadLibrary(
                configuration.ModulePath,
                moduleHash,
                StableAbiLoadPhase.ModuleLoad
            );
            ValidateExports(
                module,
                manifest.RequiredModuleExports,
                configuration.ModulePath,
                moduleHash
            );
            return StableAbiModule.Initialize(
                bridge,
                module,
                manifest,
                configuration.ModulePath,
                moduleHash,
                releaseLibraries: true
            );
        }
        catch
        {
            if (module != 0)
            {
                NativeLibrary.Free(module);
            }

            if (bridge != 0)
            {
                NativeLibraryGlobalLoader.Free(bridge);
            }

            throw;
        }
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void ValidateFile(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new StableAbiLoadException(
                "DPY8004",
                StableAbiLoadPhase.Policy,
                $"Native artifact path '{path}' is not an existing absolute file.",
                path,
                artifactSha256: null,
                missingSymbol: null
            );
        }

        var info = new FileInfo(path);
        if (
            info.Length <= 0
            || info.Length > MaximumArtifactBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new StableAbiLoadException(
                "DPY8004",
                StableAbiLoadPhase.Policy,
                $"Native artifact '{path}' violates size or link policy.",
                path,
                artifactSha256: null,
                missingSymbol: null
            );
        }
    }

    private static string ValidateHash(string path, string expected)
    {
        if (
            expected.Length != 64
            || expected.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')
            )
        )
        {
            throw new ArgumentException("A lowercase SHA-256 value is required.", nameof(expected));
        }

        var actual = ComputeSha256(path);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new StableAbiLoadException(
                "DPY8001",
                StableAbiLoadPhase.Policy,
                $"Native artifact '{Path.GetFileName(path)}' did not match its approved SHA-256.",
                path,
                actual,
                missingSymbol: null
            );
        }

        return actual;
    }

    internal static nint LoadLibrary(string path, string hash, StableAbiLoadPhase phase)
    {
        try
        {
            return NativeLibrary.Load(path);
        }
        catch (Exception exception)
            when (exception is DllNotFoundException or BadImageFormatException)
        {
            throw new StableAbiLoadException(
                "DPY8004",
                phase,
                $"Native artifact '{Path.GetFileName(path)}' could not be loaded: {exception.Message}",
                path,
                hash,
                missingSymbol: null,
                exception
            );
        }
    }

    internal static nint LoadBridgeLibrary(string path, string hash, StableAbiLoadPhase phase)
    {
        try
        {
            return NativeLibraryGlobalLoader.Load(path);
        }
        catch (Exception exception)
            when (exception is DllNotFoundException or BadImageFormatException)
        {
            throw new StableAbiLoadException(
                "DPY8004",
                phase,
                $"Native artifact '{Path.GetFileName(path)}' could not be loaded globally: {exception.Message}",
                path,
                hash,
                missingSymbol: null,
                exception
            );
        }
    }

    private static void ValidateExports(
        nint library,
        IReadOnlyList<string> symbols,
        string path,
        string hash
    )
    {
        foreach (var symbol in symbols)
        {
            if (!NativeLibrary.TryGetExport(library, symbol, out _))
            {
                throw new StableAbiLoadException(
                    "DPY8003",
                    StableAbiLoadPhase.SymbolResolution,
                    $"Native artifact '{Path.GetFileName(path)}' is missing allowlisted symbol '{symbol}'.",
                    path,
                    hash,
                    symbol
                );
            }
        }
    }
}
