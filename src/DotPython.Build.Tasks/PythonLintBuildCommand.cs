using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.Lint;

namespace DotPython.Build.Tasks;

internal static class PythonLintBuildCommand
{
    internal static int Run(IReadOnlyList<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            if (
                index + 1 >= arguments.Count
                || name
                    is not (
                        "--lint-source"
                        or "--lint-select"
                        or "--lint-ignore"
                        or "--lint-warnings-as-errors"
                    )
                || !values.TryAdd(name, arguments[index + 1])
            )
            {
                throw new ArgumentException(
                    $"Invalid or duplicate lint build option '{name}'.",
                    nameof(arguments)
                );
            }
        }
        if (!values.TryGetValue("--lint-source", out var path) || path.Length == 0)
        {
            throw new ArgumentException(
                "Expected --lint-source with a source path.",
                nameof(arguments)
            );
        }
        var errors = false;
        if (
            values.TryGetValue("--lint-warnings-as-errors", out var value)
            && !bool.TryParse(value, out errors)
        )
        {
            throw new ArgumentException(
                "--lint-warnings-as-errors expects true or false.",
                nameof(arguments)
            );
        }
        var options = new PythonLintOptions
        {
            Select =
                values.TryGetValue("--lint-select", out var select) && select.Length > 0
                    ? SplitCodes(select)
                    : null,
            Ignore =
                values.TryGetValue("--lint-ignore", out var ignore) && ignore.Length > 0
                    ? SplitCodes(ignore)
                    : [],
        };
        PythonLinter.ValidateOptions(options);
        var source = new SourceText(File.ReadAllText(path), Path.GetFullPath(path));
        var result = PythonLinter.Analyze(source, options);
        var failed = false;
        foreach (var diagnostic in result.Diagnostics)
        {
            var isError = errors || diagnostic.Severity == DiagnosticSeverity.Error;
            failed |= isError;
            var position = source.GetLinePosition(diagnostic.Span.Start);
            Console.Error.WriteLine(
                $"{source.FilePath}({position.Line + 1},{position.Character + 1}): {(isError ? "error" : "warning")} {diagnostic.Code}: {diagnostic.Message}"
            );
        }
        return failed ? 1 : 0;
    }

    private static string[] SplitCodes(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries);
}
