using System.Text.Json;
using DotPython.Language.Text;
using DotPython.Lint;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Cli;

internal static class PythonLintCommand
{
    internal static int Run(
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var paths = new List<string>();
            string[]? select = null;
            string[] ignore = [];
            string[] knownGlobals = [];
            var format = "text";
            var stdinFilename = "<stdin>";
            var pathsOnly = false;
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (pathsOnly || argument == "-" || !argument.StartsWith('-'))
                {
                    paths.Add(argument);
                    continue;
                }
                if (argument == "--")
                {
                    pathsOnly = true;
                    continue;
                }
                if (argument is "--help" or "-h")
                {
                    WriteHelp(output);
                    return 0;
                }
                if (
                    argument
                    is not (
                        "--select"
                        or "--ignore"
                        or "--known-globals"
                        or "--output-format"
                        or "--stdin-filename"
                    )
                )
                {
                    throw new ArgumentException($"Unknown lint option '{argument}'.");
                }
                if (++index >= arguments.Count)
                {
                    throw new ArgumentException($"Expected a value for '{argument}'.");
                }
                var value = arguments[index];
                switch (argument)
                {
                    case "--select":
                        select = SplitCodes(value);
                        break;
                    case "--ignore":
                        ignore = SplitCodes(value);
                        break;
                    case "--known-globals":
                        knownGlobals = SplitCodes(value);
                        break;
                    case "--output-format":
                        format = value;
                        break;
                    case "--stdin-filename":
                        stdinFilename = value;
                        break;
                }
            }

            if (format is not ("text" or "json"))
            {
                throw new ArgumentException("Lint output format must be 'text' or 'json'.");
            }
            if (paths.Count == 0 || paths.Contains("-") && paths.Count != 1)
            {
                throw new ArgumentException(
                    "Supply files/directories, or '-' alone to lint stdin."
                );
            }
            var options = new PythonLintOptions
            {
                Select = select,
                Ignore = ignore,
                KnownGlobals = knownGlobals,
            };
            PythonLinter.ValidateOptions(options);
            cancellationToken.ThrowIfCancellationRequested();

            var results = new List<PythonLintResult>();
            if (paths[0] == "-")
            {
                results.Add(
                    PythonLinter.Analyze(
                        new SourceText(input.ReadToEnd(), stdinFilename),
                        options,
                        cancellationToken
                    )
                );
            }
            else
            {
                foreach (var path in Discover(paths, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(
                        PythonLinter.Analyze(
                            PythonSourceDecoder.ReadFile(path),
                            options,
                            cancellationToken
                        )
                    );
                }
            }

            if (format == "json")
            {
                WriteJson(results, output);
            }
            else
            {
                foreach (var result in results)
                {
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        var position = result.Source.GetLinePosition(diagnostic.Span.Start);
                        output.WriteLine(
                            $"{result.Source.FilePath}:{position.Line + 1}:{position.Character + 1}: {diagnostic.Code}: {diagnostic.Message}"
                        );
                    }
                }
            }
            return results.All(result => result.IsClean) ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("dotpython lint: cancelled");
            return 130;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or PythonRuntimeException
            )
        {
            error.WriteLine($"dotpython lint: {exception.Message}");
            return 2;
        }
    }

    private static string[] SplitCodes(string value) =>
        value.Length == 0 ? [] : value.Split(',', StringSplitOptions.TrimEntries);

    private static SortedSet<string> Discover(
        IEnumerable<string> paths,
        CancellationToken cancellationToken
    )
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                files.Add(fullPath);
                continue;
            }
            if (!Directory.Exists(fullPath))
            {
                throw new FileNotFoundException($"Input '{path}' does not exist.", path);
            }
            var pending = new Stack<string>();
            pending.Push(fullPath);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    if (entry is DirectoryInfo child)
                    {
                        if (
                            child.Name
                            is not (".git" or ".venv" or "venv" or "__pycache__" or "bin" or "obj")
                        )
                        {
                            pending.Push(child.FullName);
                        }
                    }
                    else if (entry.Extension is ".py" or ".pyi")
                    {
                        files.Add(entry.FullName);
                    }
                }
            }
        }
        return files;
    }

    private static void WriteJson(IEnumerable<PythonLintResult> results, TextWriter output)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var result in results)
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    var start = result.Source.GetLinePosition(diagnostic.Span.Start);
                    var end = result.Source.GetLinePosition(diagnostic.Span.End);
                    writer.WriteStartObject();
                    writer.WriteString("file", result.Source.FilePath);
                    writer.WriteString("code", diagnostic.Code);
                    writer.WriteString(
                        "severity",
                        diagnostic.Severity == Language.Diagnostics.DiagnosticSeverity.Error
                            ? "error"
                            : "warning"
                    );
                    writer.WriteString("message", diagnostic.Message);
                    writer.WriteNumber("line", start.Line + 1);
                    writer.WriteNumber("column", start.Character + 1);
                    writer.WriteNumber("endLine", end.Line + 1);
                    writer.WriteNumber("endColumn", end.Character + 1);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }
        output.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: dotpython lint [options] <files/directories... | ->");
        output.WriteLine("  --select CODES          comma-separated exact rule IDs (default: all)");
        output.WriteLine("  --ignore CODES          comma-separated exact rule IDs to disable");
        output.WriteLine(
            "  --known-globals NAMES   comma-separated global identifiers supplied by the host"
        );
        output.WriteLine("  --output-format FORMAT  text (default) or json");
        output.WriteLine("  --stdin-filename PATH   diagnostic filename for stdin");
        output.WriteLine("  --                      treat remaining arguments as paths");
        output.WriteLine(
            "Checks the supported DotPython Python 3.14 parser profile without executing code."
        );
        foreach (var rule in PythonLinter.Rules)
        {
            output.WriteLine($"  {rule.Code} {rule.Name}: {rule.Description}");
        }
    }
}
