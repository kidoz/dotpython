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

    private readonly Func<string, PythonModuleDefinition, TextSpan, PreparedPythonCode> _compile;
    private readonly Dictionary<string, LoadedPythonModule> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PythonModuleDefinition> _definitions;
    private int _initializingCount;

    internal PythonCodecErrorRegistry CodecErrors { get; } = new();

    internal PythonTypeHierarchy TypeHierarchy { get; } = new();

    internal PythonManagedTypeValue ExceptionGroupType { get; }

    /// <summary>The mapping `sys.modules` exposes, kept in step as modules load.</summary>
    internal PythonDictionaryValue Modules { get; } = ManagedObjectProtocols.CreateDictionary();

    /// <summary>The `ModuleSpec` type `importlib.machinery` publishes.</summary>
    internal PythonManagedTypeValue ModuleSpecType { get; } =
        new("ModuleSpec") { Module = "importlib.machinery" };

    internal PythonModuleRegistry(
        IReadOnlyDictionary<string, PythonModuleDefinition> definitions,
        Func<string, PythonModuleDefinition, TextSpan, PreparedPythonCode> compile
    )
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(compile);
        ExceptionGroupType = CreateExceptionGroupType(TypeHierarchy);
        _compile = compile;
        _definitions = new Dictionary<string, PythonModuleDefinition>(StringComparer.Ordinal);

        if (definitions.Count > MaxModuleCount)
        {
            throw new ArgumentException(
                $"The managed module catalog exceeds the {MaxModuleCount} module limit.",
                nameof(definitions)
            );
        }

        var totalSourceLength = 0L;
        foreach (var (name, definition) in definitions)
        {
            if (_definitions.Count >= MaxModuleCount)
            {
                throw new ArgumentException(
                    $"The managed module catalog exceeds the {MaxModuleCount} module limit.",
                    nameof(definitions)
                );
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(definition);
            if (name.Length > MaxModuleNameLength)
            {
                throw new ArgumentException(
                    $"Managed module name '{name[..Math.Min(name.Length, 80)]}…' exceeds the "
                        + $"{MaxModuleNameLength} character limit.",
                    nameof(definitions)
                );
            }

            if (!IsDottedModuleName(name))
            {
                throw new ArgumentException(
                    $"Managed module name '{name}' must be one or more dot-separated Python identifiers.",
                    nameof(definitions)
                );
            }

            if (definition.SourceLengthUpperBound > MaxModuleSourceLength)
            {
                throw new ArgumentException(
                    $"Managed module '{name}' exceeds the {MaxModuleSourceLength} character source limit.",
                    nameof(definitions)
                );
            }

            // Supported byte encodings never expand past one UTF-16 code unit
            // per byte. Account conservatively without decoding unimported files.
            totalSourceLength += definition.SourceLengthUpperBound;
            if (totalSourceLength > MaxTotalSourceLength)
            {
                throw new ArgumentException(
                    $"The managed module catalog exceeds the {MaxTotalSourceLength} character source limit.",
                    nameof(definitions)
                );
            }

            _definitions.Add(name, definition);
            if (definition.IsPackage)
            {
                _packages.Add(name);
            }
        }

        foreach (var name in _definitions.Keys)
        {
            var separator = name.AsSpan().LastIndexOf('.');
            if (separator < 0)
            {
                continue;
            }

            var parentName = name[..separator];
            if (!_definitions.ContainsKey(parentName))
            {
                throw new ArgumentException(
                    $"Managed package '{parentName}' must be registered before submodule '{name}'.",
                    nameof(definitions)
                );
            }

            _packages.Add(parentName);
        }
    }

    private static PythonManagedTypeValue CreateExceptionGroupType(PythonTypeHierarchy hierarchy)
    {
        var groupBase = PythonBuiltinTypes.GetExceptionType("BaseExceptionGroup");
        var exceptionBase = PythonBuiltinTypes.GetExceptionType("Exception");
        var type = new PythonManagedTypeValue(
            "ExceptionGroup",
            exceptionBaseName: "BaseExceptionGroup"
        )
        {
            Module = "builtins",
            IsBuiltinExceptionGroup = true,
            OwnerHierarchy = hierarchy,
            LayoutBase = groupBase,
        };
        type.Attributes["__module__"] = new PythonTextValue("builtins");
        type.Attributes["__doc__"] = PythonNoneValue.Instance;
        type.SetDeclaredBases(new PythonTupleValue([groupBase, exceptionBase]));
        type.SetResolutionOrder(
            new PythonTupleValue([
                type,
                groupBase,
                exceptionBase,
                PythonBuiltinTypes.GetExceptionType("BaseException"),
                PythonBuiltinFunctions.Object,
            ])
        );
        return type;
    }

    /// <summary>
    /// Records a module the host itself loaded — the running `__main__` — so an import
    /// finds it and `sys.modules` lists it.
    /// </summary>
    internal void RegisterLoaded(string name, PythonModuleValue module)
    {
        // The host registers the running program once per execution, so the entry it
        // already has is replaced rather than added again.
        _loaded[name] = new LoadedPythonModule(module, null) { IsInitialized = true };
        SetModuleEntry(name, module);
    }

    /// <summary>
    /// `importlib.reload`: the code that runs a module's body again on the module it
    /// already is, or none for a module the runtime initialized natively.
    /// </summary>
    internal PythonModuleImport Reload(PythonModuleValue module, TextSpan span)
    {
        if (
            !_definitions.TryGetValue(module.Name, out var definition)
            || definition.Initialize is not null
        )
        {
            return new PythonModuleImport(module, null);
        }
        return new PythonModuleImport(module, _compile(module.Name, definition, span));
    }

    private void SetModuleEntry(string name, PythonModuleValue module) =>
        ManagedObjectProtocols.SetDictionaryItem(
            Modules,
            new PythonTextValue(name),
            module,
            default
        );

    private void ClearModuleEntry(string name)
    {
        if (
            ManagedObjectProtocols.TryFindDictionaryItem(
                Modules,
                new PythonTextValue(name),
                out var item
            )
        )
        {
            Modules.RemoveItem(item);
        }
    }

    internal bool ContainsAbsolute(string name) => _definitions.ContainsKey(name);

    /// <summary>Whether the module of that name is one other modules may live under.</summary>
    internal bool IsPackage(string name) =>
        _packages.Contains(name)
        || (_definitions.TryGetValue(name, out var definition) && definition.IsPackage);

    internal static string NormalizeModuleName(string name, string currentPackage, TextSpan span) =>
        NormalizeName(name, currentPackage, span);

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

        if (!_definitions.TryGetValue(name, out var definition))
        {
            // A dotted name whose parent is a known non-package says so, as CPython does.
            var message = $"No module named '{name}'";
            var lastDot = name.LastIndexOf('.');
            if (
                lastDot > 0
                && _definitions.TryGetValue(name[..lastDot], out var parentDefinition)
                && !parentDefinition.IsPackage
            )
            {
                message += $"; '{name[..lastDot]}' is not a package";
            }
            throw new PythonRuntimeException("DPY4020", message, span);
        }

        if (definition.IsNativeExtension)
        {
            throw new PythonRuntimeException(
                "DPY4027",
                $"Native extension module '{name}' at '{definition.NativeExtensionPath}' cannot execute in the managed runtime.",
                span
            );
        }

        var parent = GetLoadedParent(name, span);
        var globals = new PythonGlobalNamespace();
        globals.SetValue("__name__", new PythonTextValue(name));
        // Every module carries `__builtins__`: the interpreter's `builtins` module in
        // `__main__`, and — as the import system leaves it — that module's dictionary in
        // the modules it loads.
        if (name != "builtins" && _definitions.ContainsKey("builtins"))
        {
            globals.SetValue(
                "__builtins__",
                ResolveAbsolute("builtins", span).Module.Globals.Dictionary
            );
        }

        globals.SetValue("__package__", new PythonTextValue(GetPackageName(name)));
        globals.SetValue("__spec__", CreateModuleSpec(name, definition));
        if (definition.IsPackage)
        {
            // A package is a module other modules live under, and `__path__` is where
            // they — and a fromlist — are looked for. A package the runtime itself holds
            // has no directory to name.
            globals.SetValue(
                "__path__",
                new PythonListValue([
                    .. SearchLocations(definition)
                        .Select(location => (PythonValue)new PythonTextValue(location)),
                ])
            );
        }
        if (Path.IsPathRooted(definition.Origin))
        {
            globals.SetValue("__file__", new PythonTextValue(definition.Origin));
        }

        var module = new PythonModuleValue(name, globals);
        if (definition.Initialize is not null)
        {
            // A native module's names, the type tables they hand out and the hierarchy
            // registration that follows are the runtime's own setup, so the program's
            // budget does not pay for them.
            UserObjectProtocols.Setup(() =>
            {
                definition.Initialize(globals);
                TypeHierarchy.RegisterModuleTypes(globals);
            });
            var initialized = new LoadedPythonModule(module, parent) { IsInitialized = true };
            _loaded.Add(name, initialized);
            SetModuleEntry(name, module);
            parent?.Globals.SetValue(GetLeafName(name), module);
            return new PythonModuleImport(module, null);
        }

        if (_initializingCount >= MaxImportDepth)
        {
            throw new PythonRuntimeException(
                "DPY4024",
                $"Managed module initialization exceeded the {MaxImportDepth} import depth limit.",
                span
            );
        }

        var code = _compile(name, definition, span);
        _loaded.Add(name, new LoadedPythonModule(module, parent));
        SetModuleEntry(name, module);
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
            ClearModuleEntry(module.Name);
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

    /// <summary>
    /// The spec a module reports: its name, where it came from, the package it belongs to
    /// and — for a package — the directories its submodules live in. The runtime loads its
    /// own modules, so the loader is None where CPython names one.
    /// </summary>
    private PythonManagedObjectValue CreateModuleSpec(
        string name,
        PythonModuleDefinition definition
    )
    {
        var builtIn = definition.Initialize is not null;
        var spec = new PythonManagedObjectValue(ModuleSpecType);
        spec.Attributes["name"] = new PythonTextValue(name);
        spec.Attributes["loader"] = PythonNoneValue.Instance;
        spec.Attributes["loader_state"] = PythonNoneValue.Instance;
        spec.Attributes["origin"] = new PythonTextValue(builtIn ? "built-in" : definition.Origin);
        spec.Attributes["has_location"] = PythonTruthValue.FromBoolean(
            !builtIn && Path.IsPathRooted(definition.Origin)
        );
        spec.Attributes["parent"] = new PythonTextValue(GetPackageName(name));
        spec.Attributes["submodule_search_locations"] = definition.IsPackage
            ? new PythonListValue([
                .. SearchLocations(definition)
                    .Select(location => (PythonValue)new PythonTextValue(location)),
            ])
            : PythonNoneValue.Instance;
        spec.Attributes["cached"] = PythonNoneValue.Instance;
        return spec;
    }

    /// <summary>The directories a package's submodules live in, by its own origin.</summary>
    private static List<string> SearchLocations(PythonModuleDefinition definition) =>
        Path.IsPathRooted(definition.Origin)
            ?
            [
                .. Path.GetDirectoryName(definition.Origin) is { Length: > 0 } directory
                    ? [directory]
                    : (IEnumerable<string>)[],
            ]
            : [];

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
                "attempted relative import with no known parent package",
                span
            );
        }

        var packageParts = currentPackage.Split('.');
        var retainedPartCount = packageParts.Length - leadingDots + 1;
        if (retainedPartCount <= 0)
        {
            throw new PythonRuntimeException(
                "DPY4025",
                "attempted relative import beyond top-level package",
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
