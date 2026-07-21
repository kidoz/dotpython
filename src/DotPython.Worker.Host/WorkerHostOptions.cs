using System.Globalization;
using DotPython.Protocol;
using DotPython.Runtime.Native;

namespace DotPython.Worker.Host;

internal sealed record WorkerHostOptions(
    WorkerIdentity Identity,
    WorkerProtocolLimits Limits,
    IReadOnlyList<string> PackageRoots,
    StableAbiFixtureConfiguration? StableAbiFixture,
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

        StableAbiFixtureConfiguration? stableAbiFixture = null;
        var hasNativeFixture = values.ContainsKey("--abi3-fixture");
        if (hasNativeFixture)
        {
            stableAbiFixture = new StableAbiFixtureConfiguration(
                Required(values, "--abi3-bridge"),
                Required(values, "--abi3-fixture"),
                Required(values, "--abi3-manifest"),
                Required(values, "--abi3-bridge-sha256"),
                Required(values, "--abi3-fixture-sha256"),
                Required(values, "--abi3-manifest-sha256")
            );
            var manifest = StableAbiSymbolManifest.Load(stableAbiFixture.ManifestPath);
            features.Add(
                manifest.IsAnyver
                    ? "managed-stable-abi-anyver-1.1.0"
                    : "managed-stable-abi-fixture-v1"
            );
        }
        else if (values.Keys.Any(key => key.StartsWith("--abi3-", StringComparison.Ordinal)))
        {
            throw new ArgumentException("The Stable-ABI fixture configuration is incomplete.");
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
            stableAbiFixture,
            testFaultInjection,
            new WorkerProtocolVersion(protocolMajor, WorkerProtocolVersion.Current.Minor)
        );
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
