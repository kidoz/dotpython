using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Contracts;
using DotPython.Interop.Contracts;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.ParserGenerator;

namespace DotPython.Build.Tasks;

internal static class DotPythonModuleBuilder
{
    internal static bool Build(DotPythonModuleBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = ReadSource(options.SourcePath);
        var stub = ReadSource(options.ContractPath);
        var contractResult = PythonStubContractParser.Parse(
            stub,
            new PythonStubContractOptions
            {
                ModuleName = options.ModuleName,
                ClrNamespace = options.ClrNamespace,
                ClrTypeName = options.ClrTypeName,
                StatePolicy = options.StatePolicy,
            }
        );
        if (!ReportDiagnostics(contractResult.Source, contractResult.Diagnostics))
        {
            return false;
        }

        var contract = contractResult.Contract!;
        var parseResult = PythonParser.Parse(source);
        if (!ReportDiagnostics(parseResult.Source, parseResult.Diagnostics))
        {
            return false;
        }

        if (!ValidateSourceContract(parseResult.Module, contract, source))
        {
            return false;
        }

        var compilation = PythonCompiler.Compile(parseResult.Module, options.ModuleName);
        if (!ReportDiagnostics(source, compilation.Diagnostics))
        {
            return false;
        }

        var exports = contract.Functions.Select(function => new DotPythonModuleExport(
            function.PythonName,
            function.ClrName,
            DotPythonExportKind.Function
        ));
        var artifact = DotPythonModuleArtifact.Create(
            options.ModuleName,
            compilation.Code,
            exports,
            options.LanguageVersion
        );
        DeterministicFileWriter.Write(
            options.ArtifactOutputPath,
            DotPythonModuleArtifactSerializer.Serialize(artifact)
        );
        DeterministicFileWriter.Write(
            options.ContractOutputPath,
            System.Text.Encoding.UTF8.GetBytes(PythonModuleContractJson.Serialize(contract))
        );
        DeterministicFileWriter.Write(
            options.FacadeOutputPath,
            System.Text.Encoding.UTF8.GetBytes(
                CSharpModuleFacadeGenerator.Generate(contract, options.ArtifactResourceName)
            )
        );
        return true;
    }

    private static SourceText ReadSource(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A DotPython build input does not exist.", path);
        }

        return new SourceText(File.ReadAllText(path), Path.GetFullPath(path));
    }

    private static bool ValidateSourceContract(
        PythonModule module,
        PythonModuleContract contract,
        SourceText source
    )
    {
        var definitions = module
            .Statements.OfType<PythonFunctionDefinitionStatement>()
            .ToDictionary(definition => definition.Name.Name, StringComparer.Ordinal);
        var valid = true;
        foreach (var function in contract.Functions)
        {
            if (!definitions.TryGetValue(function.PythonName, out var definition))
            {
                Report(
                    source,
                    "DPY7004",
                    $"Contract export '{function.PythonName}' has no top-level source function.",
                    module.Span
                );
                valid = false;
                continue;
            }

            if (definition.Parameters.Count != function.Parameters.Count)
            {
                Report(
                    source,
                    "DPY7004",
                    $"Contract export '{function.PythonName}' declares a different parameter count than its source function.",
                    definition.Span
                );
                valid = false;
            }

            if (
                function.CallShape != PythonCallShape.Synchronous
                || function.Parameters.Any(parameter => parameter.HasDefault)
                || function.Parameters.Any(parameter =>
                    !IsSupportedRuntimeType(parameter.Type, allowVoid: false)
                )
                || !IsSupportedRuntimeType(function.ReturnType, allowVoid: true)
            )
            {
                Report(
                    source,
                    "DPY7004",
                    $"Contract export '{function.PythonName}' uses a call or value shape not yet supported by the managed module runtime.",
                    definition.Span
                );
                valid = false;
            }
        }

        return valid;
    }

    private static bool IsSupportedRuntimeType(PythonTypeContract type, bool allowVoid)
    {
        if (type.TypeArguments.Count != 0)
        {
            return false;
        }

        return type.ClrTypeName
                is "System.Boolean"
                    or "System.Numerics.BigInteger"
                    or "System.Double"
                    or "System.String"
                    or "System.Byte[]"
            || allowVoid
                && string.Equals(type.ClrTypeName, "System.Void", StringComparison.Ordinal);
    }

    private static bool ReportDiagnostics(SourceText source, IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Report(
                source,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Span,
                diagnostic.Severity
            );
        }

        return diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
    }

    private static void Report(
        SourceText source,
        string code,
        string message,
        TextSpan span,
        DiagnosticSeverity severity = DiagnosticSeverity.Error
    )
    {
        var position = source.GetLinePosition(Math.Min(span.Start, source.Length));
        var kind = severity == DiagnosticSeverity.Error ? "error" : "warning";
        Console.Error.WriteLine(
            $"{source.FilePath}({position.Line + 1},{position.Character + 1}): {kind} {code}: {message}"
        );
    }
}
