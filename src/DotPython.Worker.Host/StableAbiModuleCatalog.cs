using System.Collections.ObjectModel;
using DotPython.Runtime.Native;

namespace DotPython.Worker.Host;

internal sealed record StableAbiModuleCatalogEntry(
    StableAbiModuleConfiguration Configuration,
    StableAbiSymbolManifest Manifest
);

internal static class StableAbiModuleCatalog
{
    internal static IReadOnlyList<StableAbiModuleCatalogEntry> ValidateAndFreeze(
        IReadOnlyList<StableAbiModuleCatalogEntry> entries
    )
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count is 0 or > 64)
        {
            throw new ArgumentException(
                "A Stable-ABI module catalog must contain between 1 and 64 entries.",
                nameof(entries)
            );
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var moduleNames = new HashSet<string>(StringComparer.Ordinal);
        var modulePaths = new HashSet<string>(pathComparer);
        var manifestPaths = new HashSet<string>(pathComparer);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (
                !moduleNames.Add(entry.Manifest.ModuleName)
                || !modulePaths.Add(Path.GetFullPath(entry.Configuration.ModulePath))
                || !manifestPaths.Add(Path.GetFullPath(entry.Configuration.ManifestPath))
            )
            {
                throw new ArgumentException(
                    "Stable-ABI module names, native paths, and manifest paths must be unique.",
                    nameof(entries)
                );
            }
        }

        return new ReadOnlyCollection<StableAbiModuleCatalogEntry>(entries.ToArray());
    }
}
