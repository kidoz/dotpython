using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace DotPython.Worker;

public sealed record WorkerProcessOptions
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public required string WorkingDirectory { get; init; }

    public string ProviderId { get; init; } = "dotpython-managed-abi3";

    public string ProviderVersion { get; init; } = "0.1.0";

    public string RuntimeId { get; init; } = "dotpython-managed";

    public string RuntimeVersion { get; init; } = "3.14";

    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();

    public required string EnvironmentHash { get; init; }

    public IReadOnlyList<string> RequiredFeatures { get; init; } = ["managed-execution"];

    public IReadOnlyList<string> PackageRoots { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public WorkerResourcePolicy Policy { get; init; } = new();

    public IReadOnlyList<WorkerStableAbiModuleOptions> StableAbiModules { get; init; } =
        Array.Empty<WorkerStableAbiModuleOptions>();

    public bool EnableTestFaultInjection { get; init; }

    internal void Validate()
    {
        RequireAbsoluteFile(FileName, nameof(FileName));
        RequireAbsoluteDirectory(WorkingDirectory, nameof(WorkingDirectory));
        RequireText(ProviderId, nameof(ProviderId));
        RequireText(ProviderVersion, nameof(ProviderVersion));
        RequireText(RuntimeId, nameof(RuntimeId));
        RequireText(RuntimeVersion, nameof(RuntimeVersion));
        RequireText(Architecture, nameof(Architecture));
        RequireText(EnvironmentHash, nameof(EnvironmentHash));
        ArgumentNullException.ThrowIfNull(Arguments);
        ArgumentNullException.ThrowIfNull(RequiredFeatures);
        ArgumentNullException.ThrowIfNull(PackageRoots);
        ArgumentNullException.ThrowIfNull(EnvironmentVariables);
        ArgumentNullException.ThrowIfNull(Policy);
        Policy.Validate();
        ArgumentNullException.ThrowIfNull(StableAbiModules);
        if (StableAbiModules.Count > 64)
        {
            throw new ArgumentException(
                "A worker can qualify at most 64 Stable-ABI modules.",
                nameof(StableAbiModules)
            );
        }

        foreach (var module in StableAbiModules)
        {
            ArgumentNullException.ThrowIfNull(module);
            module.Validate();
        }

        ValidateStableAbiCatalog();

        foreach (var feature in RequiredFeatures)
        {
            RequireText(feature, nameof(RequiredFeatures));
        }

        foreach (var packageRoot in PackageRoots)
        {
            RequireAbsoluteDirectory(packageRoot, nameof(PackageRoots));
            if ((File.GetAttributes(packageRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "Package roots cannot be links or reparse points.",
                    nameof(PackageRoots)
                );
            }
        }

        foreach (var variable in EnvironmentVariables)
        {
            RequireText(variable.Key, nameof(EnvironmentVariables));
            ArgumentNullException.ThrowIfNull(variable.Value);
            if (variable.Key.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Environment variable names cannot contain '='.",
                    nameof(EnvironmentVariables)
                );
            }
        }
    }

    private void ValidateStableAbiCatalog()
    {
        if (StableAbiModules.Count == 0)
        {
            return;
        }

        var first = StableAbiModules[0];
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var modulePaths = new HashSet<string>(pathComparer);
        var manifestPaths = new HashSet<string>(pathComparer);
        foreach (var module in StableAbiModules)
        {
            if (
                !pathComparer.Equals(
                    Path.GetFullPath(module.BridgePath),
                    Path.GetFullPath(first.BridgePath)
                )
                || !string.Equals(module.BridgeSha256, first.BridgeSha256, StringComparison.Ordinal)
            )
            {
                throw new ArgumentException(
                    "Every Stable-ABI module in a worker must use the same pinned bridge identity.",
                    nameof(StableAbiModules)
                );
            }

            if (
                !modulePaths.Add(Path.GetFullPath(module.ModulePath))
                || !manifestPaths.Add(Path.GetFullPath(module.ManifestPath))
            )
            {
                throw new ArgumentException(
                    "Stable-ABI module and manifest paths must be unique within a worker catalog.",
                    nameof(StableAbiModules)
                );
            }
        }
    }

    private static void RequireAbsoluteFile(string path, string parameterName)
    {
        RequireText(path, parameterName);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new ArgumentException(
                "An existing absolute file path is required.",
                parameterName
            );
        }
    }

    private static void RequireAbsoluteDirectory(string path, string parameterName)
    {
        RequireText(path, parameterName);
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new ArgumentException(
                "An existing absolute directory path is required.",
                parameterName
            );
        }
    }

    private static void RequireText(string value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
}
