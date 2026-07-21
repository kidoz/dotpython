using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DotPython.Runtime.Native;

internal static class StableAbiFixtureLoader
{
    private const long MaximumArtifactBytes = 16 * 1024 * 1024;

    internal static StableAbiFixtureModule Load(StableAbiFixtureConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateFile(configuration.BridgePath, nameof(configuration.BridgePath));
        ValidateFile(configuration.FixturePath, nameof(configuration.FixturePath));
        ValidateFile(configuration.ManifestPath, nameof(configuration.ManifestPath));
        var bridgeHash = ValidateHash(configuration.BridgePath, configuration.BridgeSha256);
        var fixtureHash = ValidateHash(configuration.FixturePath, configuration.FixtureSha256);
        _ = ValidateHash(configuration.ManifestPath, configuration.ManifestSha256);
        NativeBinaryIdentity
            .Read(configuration.BridgePath)
            .ValidateCurrentPlatform(configuration.BridgePath);
        NativeBinaryIdentity
            .Read(configuration.FixturePath)
            .ValidateCurrentPlatform(configuration.FixturePath);
        var manifest = StableAbiSymbolManifest.Load(configuration.ManifestPath);
        if (
            manifest.NativeEntrySha256 is not null
            && !string.Equals(manifest.NativeEntrySha256, fixtureHash, StringComparison.Ordinal)
        )
        {
            throw new StableAbiLoadException(
                "DPY8001",
                StableAbiLoadPhase.Policy,
                "The configured native entry does not match the pinned manifest SHA-256.",
                configuration.FixturePath,
                fixtureHash,
                missingSymbol: null
            );
        }

        if (manifest.ProcessPinned)
        {
            var cached = StableAbiProcessLibraryCache.Load(
                configuration.BridgePath,
                bridgeHash,
                configuration.FixturePath,
                fixtureHash
            );
            ValidateExports(
                cached.Bridge,
                manifest.RequiredBridgeExports,
                configuration.BridgePath,
                bridgeHash
            );
            ValidateExports(
                cached.Fixture,
                manifest.RequiredFixtureExports,
                configuration.FixturePath,
                fixtureHash
            );
            try
            {
                return StableAbiFixtureModule.Initialize(
                    cached.Bridge,
                    cached.Fixture,
                    manifest,
                    configuration.FixturePath,
                    fixtureHash,
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
                    configuration.FixturePath,
                    fixtureHash,
                    missingSymbol: null,
                    exception
                );
            }
        }

        nint bridge = 0;
        nint fixture = 0;
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
            fixture = LoadLibrary(
                configuration.FixturePath,
                fixtureHash,
                StableAbiLoadPhase.FixtureLoad
            );
            ValidateExports(
                fixture,
                manifest.RequiredFixtureExports,
                configuration.FixturePath,
                fixtureHash
            );
            return StableAbiFixtureModule.Initialize(
                bridge,
                fixture,
                manifest,
                configuration.FixturePath,
                fixtureHash,
                releaseLibraries: true
            );
        }
        catch
        {
            if (fixture != 0)
            {
                NativeLibrary.Free(fixture);
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
