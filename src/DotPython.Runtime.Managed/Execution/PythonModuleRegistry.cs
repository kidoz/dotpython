using DotPython.Language.Ast;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonModuleRegistry
{
    private readonly Func<string, SourceText, TextSpan, PreparedPythonCode> _compile;
    private readonly Dictionary<string, LoadedPythonModule> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceText> _sources;

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

        foreach (var (name, source) in sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(source);
            if (!IsSimpleModuleName(name))
            {
                throw new ArgumentException(
                    $"Managed module name '{name}' must be one top-level Python identifier.",
                    nameof(sources)
                );
            }

            _sources.Add(name, source);
        }
    }

    internal PythonModuleImport Resolve(string name, TextSpan span)
    {
        if (_loaded.TryGetValue(name, out var loaded))
        {
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

        var code = _compile(name, source, span);
        var globals = new PythonGlobalNamespace();
        globals.SetValue("__name__", new PythonTextValue(name));
        var module = new PythonModuleValue(name, globals);
        _loaded.Add(name, new LoadedPythonModule(module));
        return new PythonModuleImport(module, code);
    }

    internal void Complete(PythonModuleValue module)
    {
        if (
            _loaded.TryGetValue(module.Name, out var loaded)
            && ReferenceEquals(loaded.Module, module)
        )
        {
            loaded.IsInitialized = true;
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
        }
    }

    private static bool IsSimpleModuleName(string name)
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

    private sealed class LoadedPythonModule(PythonModuleValue module)
    {
        internal bool IsInitialized { get; set; }

        internal PythonModuleValue Module { get; } = module;
    }
}

internal readonly record struct PythonModuleImport(
    PythonModuleValue Module,
    PreparedPythonCode? InitializationCode
);
