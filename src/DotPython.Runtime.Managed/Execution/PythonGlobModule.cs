using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `glob` module over the host filesystem: CPython 3.14's own algorithm — the
/// literal-directory fast path, the recursive walk behind `**`, the hidden-name rule, and
/// `glob.translate`'s segment-wise regular expression with the runtime's `re` behind it.
/// `glob.glob` and `glob.iglob` walk with `Directory` enumeration the way CPython walks
/// with `os.scandir`, following symlinks; `escape` and `has_magic` cover text and bytes.
/// The slice is `str` pathnames on the POSIX separator — bytes pathnames and `dir_fd` are
/// the recorded gaps — and `iglob` answers an iterator over a materialized list rather
/// than CPython's generator.
/// </summary>
internal static class PythonGlobModule
{
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue(
            "glob",
            PythonModuleFunction.Create(
                "glob",
                ["pathname"],
                ["root_dir", "dir_fd", "recursive", "include_hidden"],
                [PythonNoneValue.Instance, PythonNoneValue.Instance, PythonFalse(), PythonFalse()],
                (arguments, span) =>
                    new PythonListValue([.. ComputeGlob(arguments, span).Select(Text)])
            )
        );
        globals.SetValue(
            "iglob",
            PythonModuleFunction.Create(
                "iglob",
                ["pathname"],
                ["root_dir", "dir_fd", "recursive", "include_hidden"],
                [PythonNoneValue.Instance, PythonNoneValue.Instance, PythonFalse(), PythonFalse()],
                (arguments, span) =>
                    ManagedObjectProtocols.GetIterator(
                        new PythonListValue([.. ComputeGlob(arguments, span).Select(Text)]),
                        span
                    )
            )
        );
        globals.SetValue(
            "translate",
            PythonModuleFunction.Create(
                "translate",
                ["pat"],
                ["recursive", "include_hidden", "seps"],
                [PythonFalse(), PythonFalse(), PythonNoneValue.Instance],
                TranslateFunction
            )
        );
        globals.SetValue(
            "escape",
            PythonModuleFunction.Create("escape", ["pathname"], [], [], EscapeFunction)
        );
        globals.SetValue(
            "has_magic",
            PythonModuleFunction.Create("has_magic", ["s"], [], [], HasMagicFunction)
        );
        globals.SetValue(
            "__all__",
            new PythonListValue([
                new PythonTextValue("glob"),
                new PythonTextValue("iglob"),
                new PythonTextValue("escape"),
                new PythonTextValue("translate"),
            ])
        );
        var re = UserObjectProtocols.Dispatcher!.ImportModule("re", string.Empty, default);
        var compile = ManagedObjectProtocols.GetAttribute(re, "compile", default);
        globals.SetValue(
            "magic_check",
            UserObjectProtocols.Dispatcher!.Invoke(
                compile,
                [new PythonTextValue("([*?[])")],
                default
            )
        );
    }

    private static PythonTextValue Text(string value) => new(value);

    private static PythonTruthValue PythonFalse() => PythonTruthValue.False;

    private sealed record Options(string Root, bool Recursive, bool IncludeHidden);

    private static List<string> ComputeGlob(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments[2] is not PythonNoneValue)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4058",
                "dir_fd is not supported in this runtime",
                span,
                "NotImplementedError"
            );
        }
        var rootGiven = arguments[1] is not PythonNoneValue;
        var pathname = RequirePathname(arguments[0], rootGiven, span);
        var root = rootGiven ? RequireRoot(arguments[1], span) : string.Empty;
        var options = new Options(
            root,
            ManagedObjectProtocols.IsTrue(arguments[3]),
            ManagedObjectProtocols.IsTrue(arguments[4])
        );
        var results = new List<string>(IGlob(options, pathname, dirOnly: false));
        var recursivePrefix = pathname.Length >= 2 ? pathname[..2] : pathname;
        if (
            (pathname.Length == 0 || (options.Recursive && recursivePrefix == "**"))
            && results.Count > 0
            && results[0].Length == 0
        )
        {
            // `iglob` drops the empty string a leading `**` walk and an empty pattern
            // start with.
            results.RemoveAt(0);
        }
        return results;
    }

    private static string RequirePathname(PythonValue value, bool rootGiven, TextSpan span)
    {
        if (value is PythonTextValue text)
        {
            return text.Value;
        }
        if (value is PythonByteSequenceValue)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4058",
                "bytes pathnames are not supported in this runtime",
                span,
                "NotImplementedError"
            );
        }
        throw ManagedObjectProtocols.Fault(
            "DPY4009",
            rootGiven
                ? "expected str, bytes or os.PathLike object, not "
                    + ManagedObjectProtocols.GetTypeName(value)
                : $"'{ManagedObjectProtocols.GetTypeName(value)}' object is not subscriptable",
            span,
            "TypeError"
        );
    }

    private static string RequireRoot(PythonValue value, TextSpan span)
    {
        var result = Fspath(value, span);
        if (result is PythonTextValue text)
        {
            return text.Value;
        }
        if (result is PythonByteSequenceValue)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4058",
                "bytes pathnames are not supported in this runtime",
                span,
                "NotImplementedError"
            );
        }
        throw ManagedObjectProtocols.Fault(
            "DPY4009",
            "expected str, bytes or os.PathLike object, not "
                + ManagedObjectProtocols.GetTypeName(result),
            span,
            "TypeError"
        );
    }

    /// <summary>`os.fspath`, with strings and bytes answered without the import.</summary>
    private static PythonValue Fspath(PythonValue value, TextSpan span)
    {
        if (value is PythonTextValue or PythonByteSequenceValue)
        {
            return value;
        }
        var os = UserObjectProtocols.Dispatcher!.ImportModule("os", string.Empty, span);
        var fspath = ManagedObjectProtocols.GetAttribute(os, "fspath", span);
        return UserObjectProtocols.Dispatcher!.Invoke(fspath, [value], span);
    }

    private static IEnumerable<string> IGlob(Options options, string pathname, bool dirOnly)
    {
        var (dirname, basename) = Split(pathname);
        if (!HasMagic(pathname))
        {
            if (basename.Length != 0)
            {
                if (Lexists(Join(options.Root, pathname)))
                {
                    yield return pathname;
                }
            }
            else if (IsDir(Join(options.Root, dirname)))
            {
                // Patterns ending with a separator match only directories.
                yield return pathname;
            }
            yield break;
        }
        if (dirname.Length == 0)
        {
            var source =
                options.Recursive && IsRecursive(basename)
                    ? Glob2(options, options.Root, basename, dirOnly)
                    : Glob1(options, options.Root, basename, dirOnly);
            foreach (var name in source)
            {
                yield return name;
            }
            yield break;
        }
        var dirs =
            dirname != pathname && HasMagic(dirname)
                ? IGlob(options, dirname, dirOnly: true)
                : [dirname];
        foreach (var dir in dirs)
        {
            var listing = Join(options.Root, dir);
            var names = HasMagic(basename)
                ? options.Recursive && IsRecursive(basename)
                    ? Glob2(options, listing, basename, dirOnly)
                    : Glob1(options, listing, basename, dirOnly)
                : Glob0(listing, basename);
            foreach (var name in names)
            {
                yield return Join(dir, name);
            }
        }
    }

    private static IEnumerable<string> Glob1(
        Options options,
        string dirname,
        string pattern,
        bool dirOnly
    )
    {
        var names = ListNames(dirname, dirOnly);
        if (!(options.IncludeHidden || IsHidden(pattern)))
        {
            names = [.. names.Where(name => !IsHidden(name))];
        }
        var matcher = PythonFnmatchModule.CompileTextMatcher(pattern, default);
        return names.Where(matcher);
    }

    private static IEnumerable<string> Glob0(string dirname, string basename)
    {
        if (basename.Length != 0)
        {
            return Lexists(Join(dirname, basename)) ? [basename] : [];
        }
        return IsDir(dirname) ? [basename] : [];
    }

    private static IEnumerable<string> Glob2(
        Options options,
        string dirname,
        string pattern,
        bool dirOnly
    )
    {
        if (dirname.Length == 0 || IsDir(dirname))
        {
            yield return string.Empty;
        }
        foreach (var name in RListDir(options, dirname, dirOnly, depth: 0))
        {
            yield return name;
        }
        _ = pattern;
    }

    private static IEnumerable<string> RListDir(
        Options options,
        string dirname,
        bool dirOnly,
        int depth
    )
    {
        if (depth > 512)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4058",
                "maximum recursion depth exceeded in glob",
                default,
                "RecursionError"
            );
        }
        var names = ListNames(dirname, dirOnly);
        foreach (var name in names)
        {
            if (!(options.IncludeHidden || !IsHidden(name)))
            {
                continue;
            }
            yield return name;
            var path = dirname.Length == 0 ? name : Join(dirname, name);
            foreach (var nested in RListDir(options, path, dirOnly, depth + 1))
            {
                yield return Join(name, nested);
            }
        }
    }

    private static List<string> ListNames(string dirname, bool dirOnly)
    {
        var target = dirname.Length == 0 ? "." : dirname;
        var names = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(target))
            {
                if (dirOnly && !Directory.Exists(entry))
                {
                    continue;
                }
                var name = Path.GetFileName(entry);
                if (name.Length != 0)
                {
                    names.Add(name);
                }
            }
        }
        catch (IOException)
        {
            // CPython's scandir swallows the same failures.
        }
        catch (UnauthorizedAccessException)
        {
            // CPython's scandir swallows the same failures.
        }
        return names;
    }

    private static bool IsDir(string pathname) => Directory.Exists(pathname);

    private static bool Lexists(string pathname)
    {
        if (File.Exists(pathname) || Directory.Exists(pathname))
        {
            return true;
        }
        try
        {
            // A symbolic link to a missing target still exists.
            return new FileInfo(pathname).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasMagic(string value) => value.AsSpan().IndexOfAny("*?[") >= 0;

    private static bool IsHidden(string value) => value.Length != 0 && value[0] == '.';

    private static bool IsRecursive(string pattern) => pattern == "**";

    /// <summary>POSIX `os.path.split`.</summary>
    internal static (string Dir, string Base) Split(string path)
    {
        var index = path.LastIndexOf('/') + 1;
        var head = path[..index];
        var tail = path[index..];
        if (head.Length != 0 && head.Any(c => c != '/'))
        {
            head = head.TrimEnd('/');
        }
        return (head, tail);
    }

    /// <summary>POSIX `os.path.join` for two components.</summary>
    internal static string Join(string first, string second)
    {
        if (second.StartsWith('/'))
        {
            return second;
        }
        if (first.Length == 0 || first.EndsWith('/'))
        {
            return first + second;
        }
        return first + "/" + second;
    }

    // -----------------------------------------------------------------------------
    // glob.translate, escape, has_magic
    // -----------------------------------------------------------------------------

    private static PythonValue TranslateFunction(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        string pattern;
        switch (arguments[0])
        {
            case PythonTextValue text:
                pattern = text.Value;
                break;
            case PythonByteSequenceValue:
                throw ManagedObjectProtocols.Fault(
                    "DPY4009",
                    "cannot use a string pattern on a bytes-like object",
                    span,
                    "TypeError"
                );
            default:
                throw ManagedObjectProtocols.Fault(
                    "DPY4009",
                    "expected string or bytes-like object, got "
                        + $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}'",
                    span,
                    "TypeError"
                );
        }
        var separators = Separators(arguments[3], span);
        return new PythonTextValue(
            TranslatePattern(
                pattern,
                ManagedObjectProtocols.IsTrue(arguments[1]),
                ManagedObjectProtocols.IsTrue(arguments[2]),
                separators
            )
        );
    }

    private static string[] Separators(PythonValue value, TextSpan span)
    {
        if (value is PythonNoneValue)
        {
            return ["/"];
        }
        if (value is PythonTextValue text)
        {
            return [.. text.Value.Select(c => c.ToString())];
        }
        var result = new List<string>();
        foreach (var entry in ManagedObjectProtocols.MaterializeValues(value, span))
        {
            if (entry is not PythonTextValue entryText)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4058",
                    "decoding to str: need a bytes-like object, "
                        + $"{ManagedObjectProtocols.GetTypeName(entry)} found",
                    span,
                    "TypeError"
                );
            }
            result.Add(entryText.Value);
        }
        return [.. result];
    }

    /// <summary>`glob.translate`: the pattern as one regular expression over segments.</summary>
    internal static string TranslatePattern(
        string pattern,
        bool recursive,
        bool includeHidden,
        string[] separators
    )
    {
        var escaped = string.Concat(separators.Select(PythonFnmatchModule.ReEscape));
        var anySep = separators.Length > 1 ? $"[{escaped}]" : escaped;
        var notSep = $"[^{escaped}]";
        string oneLastSegment,
            oneSegment,
            anySegments,
            anyLastSegments;
        if (includeHidden)
        {
            oneLastSegment = $"{notSep}+";
            oneSegment = $"{oneLastSegment}{anySep}";
            anySegments = $"(?:.+{anySep})?";
            anyLastSegments = ".*";
        }
        else
        {
            oneLastSegment = $"[^{escaped}.]{notSep}*";
            oneSegment = $"{oneLastSegment}{anySep}";
            anySegments = $"(?:{oneSegment})*";
            anyLastSegments = $"{anySegments}(?:{oneLastSegment})?";
        }
        var results = new List<string>();
        var parts = SplitAny(pattern, separators);
        var last = parts.Count - 1;
        for (var index = 0; index <= last; index++)
        {
            var part = parts[index];
            if (part == "*")
            {
                results.Add(index < last ? oneSegment : oneLastSegment);
            }
            else if (recursive && part == "**")
            {
                if (index < last)
                {
                    if (parts[index + 1] != "**")
                    {
                        results.Add(anySegments);
                    }
                }
                else
                {
                    results.Add(anyLastSegments);
                }
            }
            else
            {
                if (part.Length != 0)
                {
                    if (!includeHidden && part[0] is '*' or '?')
                    {
                        results.Add(@"(?!\.)");
                    }
                    var (pieces, _) = PythonFnmatchModule.TranslateParts(
                        part,
                        $"{notSep}*",
                        notSep
                    );
                    results.AddRange(pieces);
                }
                if (index < last)
                {
                    results.Add(anySep);
                }
            }
        }
        return $"(?s:{string.Concat(results)})\\z";
    }

    private static List<string> SplitAny(string text, string[] separators)
    {
        var parts = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (separators.Any(separator => separator.Length == 1 && separator[0] == text[index]))
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static PythonValue EscapeFunction(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var value = Fspath(arguments[0], span);
        if (value is PythonTextValue text)
        {
            return new PythonTextValue(EscapeText(text.Value));
        }
        if (value is PythonByteSequenceValue bytes)
        {
            return new PythonByteSequenceValue([.. EscapeBytes(bytes.Value)]);
        }
        throw ManagedObjectProtocols.Fault(
            "DPY4009",
            "expected str, bytes or os.PathLike object, not "
                + ManagedObjectProtocols.GetTypeName(value),
            span,
            "TypeError"
        );
    }

    private static string EscapeText(string pathname)
    {
        var builder = new StringBuilder(pathname.Length);
        foreach (var c in pathname)
        {
            if (c is '*' or '?' or '[')
            {
                builder.Append('[').Append(c).Append(']');
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private static IEnumerable<byte> EscapeBytes(byte[] pathname)
    {
        foreach (var value in pathname)
        {
            if (value is (byte)'*' or (byte)'?' or (byte)'[')
            {
                yield return (byte)'[';
                yield return value;
                yield return (byte)']';
            }
            else
            {
                yield return value;
            }
        }
    }

    private static PythonValue HasMagicFunction(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var magic = arguments[0] switch
        {
            PythonTextValue text => HasMagic(text.Value),
            PythonByteSequenceValue bytes => bytes.Value.Any(value =>
                value is (byte)'*' or (byte)'?' or (byte)'['
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4009",
                "expected string or bytes-like object, got "
                    + $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}'",
                span,
                "TypeError"
            ),
        };
        return PythonTruthValue.FromBoolean(magic);
    }
}
