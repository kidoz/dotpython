using DotPython.Language.Ast;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonModuleRegistry
{
    private const int MaxImportDepth = 100;
    private const int MaxModuleCount = 10_000;
    private const int MaxModuleNameLength = 512;
    private const int MaxModuleSourceLength = 8 * 1024 * 1024;
    private const int MaxTotalSourceLength = 64 * 1024 * 1024;

    private readonly Func<string, SourceText, TextSpan, PreparedPythonCode> _compile;
    private readonly Dictionary<string, LoadedPythonModule> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceText> _sources;
    private int _initializingCount;

    internal PythonModuleRegistry(
        IReadOnlyDictionary<string, SourceText>? sources,
        Func<string, SourceText, TextSpan, PreparedPythonCode> compile
    )
    {
        ArgumentNullException.ThrowIfNull(compile);
        _compile = compile;
        _sources = new Dictionary<string, SourceText>(StringComparer.Ordinal);
        if (sources is null)
        {
            return;
        }

        if (sources.Count > MaxModuleCount)
        {
            throw new ArgumentException(
                $"The managed module catalog exceeds the {MaxModuleCount} module limit.",
                nameof(sources)
            );
        }

        var totalSourceLength = 0L;
        foreach (var (name, source) in sources)
        {
            if (_sources.Count >= MaxModuleCount)
            {
                throw new ArgumentException(
                    $"The managed module catalog exceeds the {MaxModuleCount} module limit.",
                    nameof(sources)
                );
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(source);
            if (name.Length > MaxModuleNameLength)
            {
                throw new ArgumentException(
                    $"Managed module name '{name[..Math.Min(name.Length, 80)]}…' exceeds the "
                        + $"{MaxModuleNameLength} character limit.",
                    nameof(sources)
                );
            }

            if (!IsDottedModuleName(name))
            {
                throw new ArgumentException(
                    $"Managed module name '{name}' must be one or more dot-separated Python identifiers.",
                    nameof(sources)
                );
            }

            if (source.Length > MaxModuleSourceLength)
            {
                throw new ArgumentException(
                    $"Managed module '{name}' exceeds the {MaxModuleSourceLength} character source limit.",
                    nameof(sources)
                );
            }

            totalSourceLength += source.Length;
            if (totalSourceLength > MaxTotalSourceLength)
            {
                throw new ArgumentException(
                    $"The managed module catalog exceeds the {MaxTotalSourceLength} character source limit.",
                    nameof(sources)
                );
            }

            _sources.Add(name, source);
        }

        foreach (var name in _sources.Keys)
        {
            var separator = name.AsSpan().LastIndexOf('.');
            if (separator < 0)
            {
                continue;
            }

            var parentName = name[..separator];
            if (!_sources.ContainsKey(parentName))
            {
                throw new ArgumentException(
                    $"Managed package '{parentName}' must be registered before submodule '{name}'.",
                    nameof(sources)
                );
            }

            _packages.Add(parentName);
        }
    }

    internal bool ContainsAbsolute(string name) => _sources.ContainsKey(name);

    internal PythonModuleImport Resolve(string name, string currentPackage, TextSpan span) =>
        ResolveAbsolute(NormalizeName(name, currentPackage, span), span);

    internal PythonModuleImport ResolveAbsolute(string name, TextSpan span)
    {
        if (_loaded.TryGetValue(name, out var loaded))
        {
            var separator = name.AsSpan().LastIndexOf('.');
            if (
                separator >= 0
                && _loaded.TryGetValue(name[..separator], out var loadedParent)
                && !ReferenceEquals(loaded.Parent, loadedParent.Module)
            )
            {
                loadedParent.Module.Globals.SetValue(GetLeafName(name), loaded.Module);
                loaded.Parent = loadedParent.Module;
            }

            return new PythonModuleImport(loaded.Module, null);
        }

        if (!_sources.TryGetValue(name, out var source))
        {
            throw new PythonRuntimeException(
                "DPY4020",
                $"No managed module named '{name}' is registered.",
                span
            );
        }

        if (_initializingCount >= MaxImportDepth)
        {
            throw new PythonRuntimeException(
                "DPY4024",
                $"Managed module initialization exceeded the {MaxImportDepth} import depth limit.",
                span
            );
        }

        var parent = GetLoadedParent(name, span);
        var code = _compile(name, source, span);
        var globals = new PythonGlobalNamespace();
        globals.SetValue("__name__", new PythonTextValue(name));
        globals.SetValue("__package__", new PythonTextValue(GetPackageName(name)));
        var module = new PythonModuleValue(name, globals);
        _loaded.Add(name, new LoadedPythonModule(module, parent));
        _initializingCount++;
        return new PythonModuleImport(module, code);
    }

    internal void Complete(PythonModuleValue module)
    {
        if (
            _loaded.TryGetValue(module.Name, out var loaded)
            && ReferenceEquals(loaded.Module, module)
            && !loaded.IsInitialized
        )
        {
            loaded.IsInitialized = true;
            _initializingCount--;
            loaded.Parent?.Globals.SetValue(GetLeafName(module.Name), module);
        }
    }

    internal void Fail(PythonModuleValue module)
    {
        if (
            _loaded.TryGetValue(module.Name, out var loaded)
            && ReferenceEquals(loaded.Module, module)
            && !loaded.IsInitialized
        )
        {
            _loaded.Remove(module.Name);
            _initializingCount--;
            loaded.Parent?.Globals.RemoveValue(GetLeafName(module.Name), module);
        }
    }

    private PythonModuleValue? GetLoadedParent(string name, TextSpan span)
    {
        var separator = name.AsSpan().LastIndexOf('.');
        if (separator < 0)
        {
            return null;
        }

        var parentName = name[..separator];
        if (_loaded.TryGetValue(parentName, out var parent))
        {
            return parent.Module;
        }

        throw new PythonRuntimeException(
            "DPY4026",
            $"Parent package '{parentName}' must be initialized before submodule '{name}'.",
            span
        );
    }

    private string GetPackageName(string name)
    {
        if (_packages.Contains(name))
        {
            return name;
        }

        var separator = name.AsSpan().LastIndexOf('.');
        return separator < 0 ? string.Empty : name[..separator];
    }

    private static string NormalizeName(string name, string currentPackage, TextSpan span)
    {
        var leadingDots = 0;
        while (leadingDots < name.Length && name[leadingDots] == '.')
        {
            leadingDots++;
        }

        if (leadingDots == 0)
        {
            return name;
        }

        if (string.IsNullOrEmpty(currentPackage))
        {
            throw new PythonRuntimeException(
                "DPY4025",
                $"Relative import '{name}' has no known parent package.",
                span
            );
        }

        var packageParts = currentPackage.Split('.');
        var retainedPartCount = packageParts.Length - leadingDots + 1;
        if (retainedPartCount <= 0)
        {
            throw new PythonRuntimeException(
                "DPY4025",
                $"Relative import '{name}' goes beyond the top-level package.",
                span
            );
        }

        var prefix = string.Join('.', packageParts.AsSpan(0, retainedPartCount).ToArray());
        var suffix = name[leadingDots..];
        return suffix.Length == 0 ? prefix : prefix + "." + suffix;
    }

    private static string GetLeafName(string name)
    {
        var separator = name.AsSpan().LastIndexOf('.');
        return separator < 0 ? name : name[(separator + 1)..];
    }

    private static bool IsDottedModuleName(string name)
    {
        var result = PythonParser.Parse(new SourceText($"import {name}"));
        return result.Success
            && result.Module.Statements
                is [
                    PythonImportStatement
                    {
                        Imports: [PythonImportAlias { Name: var importedName, Alias: null }],
                    },
                ]
            && string.Equals(name, importedName, StringComparison.Ordinal);
    }

    private sealed class LoadedPythonModule(PythonModuleValue module, PythonModuleValue? parent)
    {
        internal bool IsInitialized { get; set; }

        internal PythonModuleValue Module { get; } = module;

        internal PythonModuleValue? Parent { get; set; } = parent;
    }
}

internal readonly record struct PythonModuleImport(
    PythonModuleValue Module,
    PreparedPythonCode? InitializationCode
);
