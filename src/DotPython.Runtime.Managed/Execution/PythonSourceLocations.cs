using System.Runtime.CompilerServices;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The source a compiled code object came from, so a frame can report the file and line it
/// is executing — what CPython reads from <c>co_filename</c> and <c>f_lineno</c>, which the
/// warnings module is built on.
/// </summary>
/// <remarks>
/// The compiler keeps spans, which are offsets into the source, but nothing else: the
/// engine registers the source beside the code it produced, for the module code and every
/// code nested in it, and the two together answer a span as `file:line`. The line text a
/// formatted warning shows comes from the registered source, or from the file itself when
/// the warning is about a file this runtime did not compile.
/// </remarks>
internal static class PythonSourceLocations
{
    private sealed record Module(string FileName, SourceText Source);

    private static readonly ConditionalWeakTable<PythonCodeObject, Module> Codes = new();
    private static readonly Dictionary<string, SourceText> Sources = new(StringComparer.Ordinal);

    /// <summary>Registers a compilation unit: the source, its name, and every code in it.</summary>
    internal static void Register(PythonCodeObject code, string fileName, SourceText source)
    {
        lock (Sources)
        {
            Sources[fileName] = source;
            RegisterCode(code, new Module(fileName, source));
        }
    }

    private static void RegisterCode(PythonCodeObject code, Module module)
    {
        Codes.AddOrUpdate(code, module);
        if (code.AnnotateCode is { } annotate)
            RegisterCode(annotate, module);
        foreach (var constant in code.Constants)
        {
            if (constant.Value is PythonCodeObject nested)
                RegisterCode(nested, module);
        }
    }

    /// <summary>
    /// The file and line a span of a code object stands at, counted the way CPython counts
    /// lines.
    /// </summary>
    internal static bool TryLocate(
        PythonCodeObject code,
        int offset,
        out string fileName,
        out int line
    )
    {
        if (Codes.TryGetValue(code, out var module))
        {
            fileName = module.FileName;
            line =
                module
                    .Source.GetLinePosition(Math.Min(Math.Max(offset, 0), module.Source.Length))
                    .Line + 1;
            return true;
        }
        fileName = string.Empty;
        line = 0;
        return false;
    }

    /// <summary>The source line a warning is about, read the way `linecache` reads it.</summary>
    internal static string? TryGetLine(string fileName, int line)
    {
        if (line <= 0)
            return null;
        SourceText? source;
        lock (Sources)
        {
            Sources.TryGetValue(fileName, out source);
        }
        if (source is not null)
            return line <= source.LineCount ? source.GetText(source.GetLineSpan(line - 1)) : null;
        return LineFromFile(fileName, line);
    }

    private static string? LineFromFile(string fileName, int line)
    {
        try
        {
            if (fileName.Length == 0 || fileName.StartsWith('<') || !File.Exists(fileName))
                return null;
            using var reader = new StreamReader(fileName);
            for (var number = 1; number <= line; number++)
            {
                var text = reader.ReadLine();
                if (text is null)
                    return null;
                if (number == line)
                    return text;
            }
        }
        catch (IOException)
        {
            // A warning that cannot read its own source line is still a warning.
        }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
