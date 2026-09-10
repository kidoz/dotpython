using DotPython.Hosting.Packaging;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.Runtime.Managed;
using DotPython.Runtime.Managed.Execution;

namespace DotPython.Cli;

internal static class DotPythonCommand
{
    public static int Run(
        IReadOnlyList<string> arguments,
        TextReader standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (arguments.Count == 0)
        {
            standardError.WriteLine(
                "dotpython: interactive mode is not implemented; use -c, -, or a script path"
            );
            return 2;
        }

        if (arguments[0] is "-h" or "--help")
        {
            WriteHelp(standardOutput);
            return 0;
        }

        if (arguments[0] is "-V" or "--version")
        {
            var compatibility = ManagedRuntimeDescriptor.Compatibility;
            standardOutput.WriteLine(
                $"DotPython {compatibility.Implementation} (Python {compatibility.LanguageVersion})"
            );
            return 0;
        }

        if (arguments[0] == "wheel")
        {
            return RunWheelCommand(arguments, standardOutput, standardError);
        }

        if (arguments[0] == "lint")
        {
            return PythonLintCommand.Run(
                [.. arguments.Skip(1)],
                standardInput,
                standardOutput,
                standardError,
                cancellationToken
            );
        }

        if (!TryReadInstructionLimit(ref arguments, standardError, out var instructionLimit))
        {
            return 2;
        }

        if (arguments.Count == 0)
        {
            standardError.WriteLine("dotpython: expected -c, -, or a script path");
            return 2;
        }

        try
        {
            if (
                !TryReadSource(
                    arguments,
                    standardInput,
                    standardError,
                    out var source,
                    out var moduleSearchPath
                )
            )
            {
                return 2;
            }
            var engine = new ManagedPythonEngine(
                new ManagedModuleDiscoveryOptions { SearchPaths = [moduleSearchPath] }
            );
            var result = engine.Execute(
                source,
                standardOutput,
                new ManagedExecutionOptions
                {
                    StandardInput = standardInput,
                    StandardError = standardError,
                    Arguments = BuildProgramArguments(arguments),
                    InstructionLimit = instructionLimit,
                },
                cancellationToken
            );
            if (result.ExitCode is { } exitCode)
            {
                // sys.exit(): the message (if any) goes to stderr without a traceback.
                if (result.ExitMessage is { } exitMessage)
                {
                    standardError.WriteLine(exitMessage);
                }

                return exitCode;
            }

            if (result.Success)
            {
                return 0;
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                WriteDiagnostic(result.Source, diagnostic, standardError);
            }

            return 1;
        }
        catch (PythonRuntimeException exception)
            when (exception.PythonExceptionTypeName == "SyntaxError")
        {
            standardError.WriteLine($"{arguments[0]}: SyntaxError: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            standardError.WriteLine("dotpython: execution cancelled");
            return 130;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or ArgumentException
            )
        {
            standardError.WriteLine($"dotpython: module discovery failed: {exception.Message}");
            return 1;
        }
    }

    private static int RunWheelCommand(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError
    )
    {
        if (arguments.Count != 3 || arguments[1] != "inspect")
        {
            standardError.WriteLine("dotpython: usage: dotpython wheel inspect <artifact.whl>");
            return 2;
        }

        try
        {
            var inspection = PythonWheelInspector.Inspect(arguments[2]);
            standardOutput.WriteLine(PythonWheelInspectionJson.Serialize(inspection));
            return inspection.IsValid ? 0 : 1;
        }
        catch (IOException exception)
        {
            standardError.WriteLine(
                $"dotpython: cannot inspect '{arguments[2]}': {exception.Message}"
            );
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            standardError.WriteLine(
                $"dotpython: cannot inspect '{arguments[2]}': {exception.Message}"
            );
            return 1;
        }
    }

    /// <summary>
    /// `sys.argv`: the program name (`-c`, `-`, or the script path as given) followed by
    /// the remaining command-line arguments, as CPython reports them.
    /// </summary>
    private static string[] BuildProgramArguments(IReadOnlyList<string> arguments)
    {
        var programArguments = new List<string>();
        var skip = arguments[0] == "-c" ? 2 : 1;
        programArguments.Add(arguments[0]);
        for (var index = skip; index < arguments.Count; index++)
        {
            programArguments.Add(arguments[index]);
        }

        return [.. programArguments];
    }

    /// <summary>
    /// Consumes a leading `--instruction-limit N` (or `unlimited`) option; the managed
    /// VM's cooperative budget defaults to one million instructions.
    /// </summary>
    private static bool TryReadInstructionLimit(
        ref IReadOnlyList<string> arguments,
        TextWriter standardError,
        out long instructionLimit
    )
    {
        instructionLimit = ManagedExecutionOptions.DefaultInstructionLimit;
        if (arguments.Count == 0)
        {
            return true;
        }

        string? value = null;
        var consumed = 0;
        if (arguments[0] == "--instruction-limit")
        {
            if (arguments.Count < 2)
            {
                standardError.WriteLine("dotpython: argument expected for --instruction-limit");
                return false;
            }

            value = arguments[1];
            consumed = 2;
        }
        else if (arguments[0].StartsWith("--instruction-limit=", StringComparison.Ordinal))
        {
            value = arguments[0]["--instruction-limit=".Length..];
            consumed = 1;
        }

        if (value is null)
        {
            return true;
        }

        if (string.Equals(value, "unlimited", StringComparison.OrdinalIgnoreCase))
        {
            instructionLimit = long.MaxValue;
        }
        else if (
            !long.TryParse(
                value.Replace("_", string.Empty, StringComparison.Ordinal),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out instructionLimit
            )
            || instructionLimit <= 0
        )
        {
            standardError.WriteLine(
                $"dotpython: --instruction-limit expects a positive integer or 'unlimited', not '{value}'"
            );
            return false;
        }

        arguments = [.. arguments.Skip(consumed)];
        return true;
    }

    private static bool TryReadSource(
        IReadOnlyList<string> arguments,
        TextReader standardInput,
        TextWriter standardError,
        out SourceText source,
        out string moduleSearchPath
    )
    {
        if (arguments[0] == "-c")
        {
            if (arguments.Count < 2)
            {
                standardError.WriteLine("dotpython: argument expected for -c");
                source = new SourceText(string.Empty, "<string>");
                moduleSearchPath = Directory.GetCurrentDirectory();
                return false;
            }

            source = new SourceText(DedentCommand(arguments[1]), "<string>");
            moduleSearchPath = Directory.GetCurrentDirectory();
            return true;
        }

        if (arguments[0] == "-")
        {
            source = new SourceText(standardInput.ReadToEnd(), "<stdin>");
            moduleSearchPath = Directory.GetCurrentDirectory();
            return true;
        }

        if (arguments[0].StartsWith('-'))
        {
            standardError.WriteLine($"dotpython: unsupported option '{arguments[0]}'");
            source = new SourceText(string.Empty, "<command-line>");
            moduleSearchPath = Directory.GetCurrentDirectory();
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(arguments[0]);
            source = PythonSourceDecoder.ReadFile(fullPath);
            moduleSearchPath = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            return true;
        }
        catch (IOException exception)
        {
            standardError.WriteLine(
                $"dotpython: cannot read '{arguments[0]}': {exception.Message}"
            );
            source = new SourceText(string.Empty, arguments[0]);
            moduleSearchPath = Directory.GetCurrentDirectory();
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            standardError.WriteLine(
                $"dotpython: cannot read '{arguments[0]}': {exception.Message}"
            );
            source = new SourceText(string.Empty, arguments[0]);
            moduleSearchPath = Directory.GetCurrentDirectory();
            return false;
        }
    }

    private static string DedentCommand(string command)
    {
        // Python 3.14+ dedents only -c input, before tokenization. Tabs and spaces
        // are distinct characters; line endings are not normalized at this stage.
        var lines = command.Split('\n');
        string? margin = null;
        foreach (var line in lines)
        {
            var indentation = 0;
            while (indentation < line.Length && line[indentation] is ' ' or '\t')
            {
                indentation++;
            }

            if (indentation == line.Length)
            {
                continue;
            }

            if (margin is null)
            {
                margin = line[..indentation];
            }
            else
            {
                var commonLength = 0;
                while (
                    commonLength < indentation
                    && commonLength < margin.Length
                    && line[commonLength] == margin[commonLength]
                )
                {
                    commonLength++;
                }

                margin = margin[..commonLength];
            }

            if (margin.Length == 0)
            {
                // CPython's CLI preserves blank-line whitespace when no common
                // margin exists, including whitespace inside multiline literals.
                return command;
            }
        }

        if (margin is null)
        {
            return command;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            lines[index] = line.AsSpan().Trim(" \t").IsEmpty ? string.Empty : line[margin.Length..];
        }

        return string.Join('\n', lines);
    }

    private static void WriteDiagnostic(
        SourceText source,
        Diagnostic diagnostic,
        TextWriter standardError
    )
    {
        var position = source.GetLinePosition(Math.Min(diagnostic.Span.Start, source.Length));
        standardError.WriteLine(
            $"{source.FilePath ?? "<input>"}:{position.Line + 1}:{position.Character + 1}: "
                + $"{diagnostic.Code}: {diagnostic.Message}"
        );
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: dotpython [options] -c command [args]");
        output.WriteLine("       dotpython [options] - [args]");
        output.WriteLine("       dotpython [options] script.py [args]");
        output.WriteLine("       dotpython wheel inspect artifact.whl");
        output.WriteLine("       dotpython lint [options] <files/directories... | ->");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine(
            "  --instruction-limit N   cooperative VM instruction budget (default "
                + ManagedExecutionOptions.DefaultInstructionLimit.ToString(
                    "N0",
                    System.Globalization.CultureInfo.InvariantCulture
                )
                + "; 'unlimited' disables it)"
        );
        output.WriteLine("  -h, --help              show this help");
        output.WriteLine("  -V, --version           show the runtime and Python language version");
        output.WriteLine();
        output.WriteLine(
            "DotPython runs a managed subset of Python "
                + ManagedRuntimeDescriptor.Compatibility.LanguageVersion
                + " with a cooperative instruction budget; see the compatibility matrix"
        );
        output.WriteLine("for the supported statements, builtins, and modules.");
    }
}
