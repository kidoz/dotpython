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

        if (!TryReadSource(arguments, standardInput, standardError, out var source))
        {
            return 2;
        }

        try
        {
            var engine = new ManagedPythonEngine();
            var result = engine.Execute(
                source,
                standardOutput,
                cancellationToken: cancellationToken
            );
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
        catch (OperationCanceledException)
        {
            standardError.WriteLine("dotpython: execution cancelled");
            return 130;
        }
    }

    private static bool TryReadSource(
        IReadOnlyList<string> arguments,
        TextReader standardInput,
        TextWriter standardError,
        out SourceText source
    )
    {
        if (arguments[0] == "-c")
        {
            if (arguments.Count < 2)
            {
                standardError.WriteLine("dotpython: argument expected for -c");
                source = new SourceText(string.Empty, "<string>");
                return false;
            }

            source = new SourceText(arguments[1], "<string>");
            return true;
        }

        if (arguments[0] == "-")
        {
            source = new SourceText(standardInput.ReadToEnd(), "<stdin>");
            return true;
        }

        if (arguments[0].StartsWith('-'))
        {
            standardError.WriteLine($"dotpython: unsupported option '{arguments[0]}'");
            source = new SourceText(string.Empty, "<command-line>");
            return false;
        }

        try
        {
            source = new SourceText(File.ReadAllText(arguments[0]), arguments[0]);
            return true;
        }
        catch (IOException exception)
        {
            standardError.WriteLine(
                $"dotpython: cannot read '{arguments[0]}': {exception.Message}"
            );
            source = new SourceText(string.Empty, arguments[0]);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            standardError.WriteLine(
                $"dotpython: cannot read '{arguments[0]}': {exception.Message}"
            );
            source = new SourceText(string.Empty, arguments[0]);
            return false;
        }
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
        output.WriteLine("Usage: dotpython -c command [args]");
        output.WriteLine("       dotpython - [args]");
        output.WriteLine("       dotpython script.py [args]");
        output.WriteLine();
        output.WriteLine(
            "Current managed subset: literals, names, assignment, arithmetic, and calls."
        );
    }
}
