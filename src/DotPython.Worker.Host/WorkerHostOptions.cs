using System.Globalization;
using DotPython.Protocol;
using DotPython.Runtime.Native;

namespace DotPython.Worker.Host;

internal sealed record WorkerHostOptions(
    WorkerIdentity Identity,
    WorkerProtocolLimits Limits,
    IReadOnlyList<string> PackageRoots,
    IReadOnlyList<StableAbiModuleCatalogEntry> StableAbiModules,
    bool TestFaultInjection,
    WorkerProtocolVersion ProtocolVersion
)
{
    internal static WorkerHostOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var testFaultInjection = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var name = arguments[index];
            if (name == "--test-fault-injection")
            {
                testFaultInjection = true;
                continue;
            }

            if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Count)
            {
                throw new ArgumentException($"Invalid worker argument '{name}'.");
            }

            if (!values.TryGetValue(name, out var entries))
            {
                entries = [];
                values.Add(name, entries);
            }

            entries.Add(arguments[++index]);
        }

        var packageRoots = values.TryGetValue("--package-root", out var roots) ? roots : [];
        foreach (var root in packageRoots)
        {
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            {
                throw new ArgumentException($"Worker package root '{root}' is invalid.");
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException($"Worker package root '{root}' cannot be a link.");
            }
        }

        var features = new List<string> { "managed-execution", "cancellation", "sessions" };
        if (testFaultInjection)
        {
            features.Add("test-fault-injection");
        }

        IReadOnlyList<StableAbiModuleCatalogEntry> stableAbiModules = [];
        var hasNativeModule = values.ContainsKey("--abi3-module");
        if (hasNativeModule)
        {
            var bridgePath = Required(values, "--abi3-bridge");
            var bridgeSha256 = Required(values, "--abi3-bridge-sha256");
            var modulePaths = RequiredMany(values, "--abi3-module");
            var manifestPaths = RequiredMany(values, "--abi3-manifest");
            var moduleHashes = RequiredMany(values, "--abi3-module-sha256");
            var manifestHashes = RequiredMany(values, "--abi3-manifest-sha256");
            if (
                modulePaths.Count > 64
                || manifestPaths.Count != modulePaths.Count
                || moduleHashes.Count != modulePaths.Count
                || manifestHashes.Count != modulePaths.Count
            )
            {
                throw new ArgumentException(
                    "The Stable-ABI module catalog is misaligned or exceeds its 64-module bound."
                );
            }

            var entries = new List<StableAbiModuleCatalogEntry>(modulePaths.Count);
            for (var index = 0; index < modulePaths.Count; index++)
            {
                var configuration = new StableAbiModuleConfiguration(
                    bridgePath,
                    modulePaths[index],
                    manifestPaths[index],
                    bridgeSha256,
                    moduleHashes[index],
                    manifestHashes[index]
                );
                var manifest = StableAbiSymbolManifest.Load(configuration.ManifestPath);
                entries.Add(new StableAbiModuleCatalogEntry(configuration, manifest));
            }

            stableAbiModules = StableAbiModuleCatalog.ValidateAndFreeze(entries);
            features.AddRange(
                stableAbiModules.Select(entry => entry.Manifest.CapabilityId).Distinct()
            );
        }
        else if (values.Keys.Any(key => key.StartsWith("--abi3-", StringComparison.Ordinal)))
        {
            throw new ArgumentException("The Stable-ABI module configuration is incomplete.");
        }

        var protocolMajor = values.TryGetValue("--protocol-major", out var majorValues)
            ? ParsePositive(majorValues.Single(), "--protocol-major")
            : WorkerProtocolVersion.Current.Major;
        return new WorkerHostOptions(
            new WorkerIdentity(
                Required(values, "--provider-id"),
                Required(values, "--provider-version"),
                Required(values, "--runtime-id"),
                Required(values, "--runtime-version"),
                Required(values, "--architecture"),
                Required(values, "--environment-hash"),
                Guid.Parse(Required(values, "--worker-id")),
                ParsePositiveLong(Required(values, "--generation"), "--generation"),
                features
            ),
            new WorkerProtocolLimits(
                ParsePositive(Required(values, "--max-message-bytes"), "--max-message-bytes"),
                ParsePositive(Required(values, "--max-output-bytes"), "--max-output-bytes"),
                ParsePositive(Required(values, "--max-concurrency"), "--max-concurrency"),
                ParsePositive(Required(values, "--max-sessions"), "--max-sessions")
            ),
            packageRoots,
            stableAbiModules,
            testFaultInjection,
            new WorkerProtocolVersion(protocolMajor, WorkerProtocolVersion.Current.Minor)
        );
    }

    private static List<string> RequiredMany(
        Dictionary<string, List<string>> values,
        string name
    )
    {
        if (!values.TryGetValue(name, out var entries) || entries.Count == 0)
        {
            throw new ArgumentException($"Worker argument '{name}' must occur at least once.");
        }

        return entries;
    }

    private static string Required(Dictionary<string, List<string>> values, string name)
    {
        if (!values.TryGetValue(name, out var entries) || entries.Count != 1)
        {
            throw new ArgumentException($"Worker argument '{name}' must occur exactly once.");
        }

        return entries[0];
    }

    private static int ParsePositive(string value, string name)
    {
        if (
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result <= 0
        )
        {
            throw new ArgumentException($"Worker argument '{name}' must be a positive integer.");
        }

        return result;
    }

    private static long ParsePositiveLong(string value, string name)
    {
        if (
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result <= 0
        )
        {
            throw new ArgumentException($"Worker argument '{name}' must be a positive integer.");
        }

        return result;
    }
}
