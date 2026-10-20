using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `fnmatch` module: the shell-pattern translator CPython 3.14 ships — the
/// alternation-free translation with atomic groups for interior stars, the class
/// rewriting for ranges and set operations, and the empty-class/set-operation cases — and
/// the four matching functions over the runtime's own `re`, which is what CPython's
/// `_compile_pattern` compiles. `os.path.normcase` is the POSIX identity, so `fnmatch`
/// and `fnmatchcase` agree. Both `str` and `bytes` patterns match: a bytes pattern is
/// decoded as Latin-1 and matched as text, which is CPython's own byte-for-character
/// correspondence, and mixing the two reports CPython's messages. The module publishes
/// only its own names, unlike CPython's namespace, which also leaks its imports.
/// </summary>
internal static class PythonFnmatchModule
{
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue(
            "translate",
            PythonModuleFunction.Create("translate", ["pat"], [], [], Translate)
        );
        globals.SetValue(
            "fnmatchcase",
            PythonModuleFunction.Create("fnmatchcase", ["name", "pat"], [], [], FnmatchCase)
        );
        globals.SetValue(
            "fnmatch",
            PythonModuleFunction.Create("fnmatch", ["name", "pat"], [], [], Fnmatch)
        );
        globals.SetValue(
            "filter",
            PythonModuleFunction.Create("filter", ["names", "pat"], [], [], Filter)
        );
        globals.SetValue(
            "filterfalse",
            PythonModuleFunction.Create("filterfalse", ["names", "pat"], [], [], FilterFalse)
        );
        globals.SetValue(
            "__all__",
            new PythonListValue([
                new PythonTextValue("filter"),
                new PythonTextValue("filterfalse"),
                new PythonTextValue("fnmatch"),
                new PythonTextValue("fnmatchcase"),
                new PythonTextValue("translate"),
            ])
        );
    }

    private static PythonValue Translate(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        _ = span;
        if (arguments[0] is PythonByteSequenceValue)
        {
            // CPython's translator iterates the bytes as integers and hands one to
            // `re.escape`, which decodes it back to text and fails.
            throw TypeFault("decoding to str: need a bytes-like object, int found");
        }
        return new PythonTextValue(TranslatePattern(RequirePattern(arguments[0], span).Text));
    }

    private static PythonValue Fnmatch(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        Match(
            RequirePattern(NormCase(arguments[1], span), span),
            RequireName(NormCase(arguments[0], span), span),
            span
        );

    private static PythonValue FnmatchCase(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        Match(RequirePattern(arguments[1], span), RequireName(arguments[0], span), span);

    private static PythonValue Filter(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        FilterNames(arguments[0], arguments[1], span, keepMatches: true);

    private static PythonValue FilterFalse(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        FilterNames(arguments[0], arguments[1], span, keepMatches: false);

    private static PythonListValue FilterNames(
        PythonValue names,
        PythonValue patternValue,
        TextSpan span,
        bool keepMatches
    )
    {
        var pattern = RequirePattern(NormCase(patternValue, span), span);
        var matcher = CompileTextMatcher(pattern.Text, span);
        var result = new List<PythonValue>();
        foreach (var name in ManagedObjectProtocols.MaterializeValues(names, span))
        {
            var text = RequireName(name, span);
            if (TextMatches(pattern, text, matcher, span) == keepMatches)
            {
                result.Add(name);
            }
        }
        return new PythonListValue(result);
    }

    private static PythonTruthValue Match(
        ShellPattern pattern,
        RequireNameResult name,
        TextSpan span
    ) =>
        PythonTruthValue.FromBoolean(
            TextMatches(pattern, name, CompileTextMatcher(pattern.Text, span), span)
        );

    private static bool TextMatches(
        ShellPattern pattern,
        RequireNameResult name,
        Func<string, bool> matcher,
        TextSpan span
    )
    {
        _ = span;
        if (name.IsBytes && !pattern.IsBytes)
        {
            throw TypeFault("cannot use a string pattern on a bytes-like object");
        }
        if (!name.IsBytes && pattern.IsBytes)
        {
            throw TypeFault("cannot use a bytes pattern on a string-like object");
        }
        return matcher(name.Text);
    }

    private readonly record struct ShellPattern(bool IsBytes, string Text);

    private readonly record struct RequireNameResult(bool IsBytes, string Text);

    /// <summary>The pattern as text: a bytes pattern decodes byte-for-character.</summary>
    private static ShellPattern RequirePattern(PythonValue value, TextSpan span)
    {
        _ = span;
        switch (value)
        {
            case PythonTextValue text:
                return new ShellPattern(false, text.Value);
            case PythonByteSequenceValue bytes:
                return new ShellPattern(true, Encoding.Latin1.GetString(bytes.Value));
            default:
                throw TypeFault(
                    $"object of type '{ManagedObjectProtocols.GetTypeName(value)}' has no len()"
                );
        }
    }

    private static RequireNameResult RequireName(PythonValue value, TextSpan span)
    {
        _ = span;
        switch (value)
        {
            case PythonTextValue text:
                return new RequireNameResult(false, text.Value);
            case PythonByteSequenceValue bytes:
                return new RequireNameResult(true, Encoding.Latin1.GetString(bytes.Value));
            default:
                throw TypeFault(
                    $"expected string or bytes-like object, got "
                        + $"'{ManagedObjectProtocols.GetTypeName(value)}'"
                );
        }
    }

    /// <summary>`posixpath.normcase`: the value as `os.fspath` hands it back.</summary>
    private static PythonValue NormCase(PythonValue value, TextSpan span)
    {
        if (value is PythonTextValue or PythonByteSequenceValue)
        {
            return value;
        }
        var os = UserObjectProtocols.Dispatcher!.ImportModule("os", string.Empty, span);
        var fspath = ManagedObjectProtocols.GetAttribute(os, "fspath", span);
        return UserObjectProtocols.Dispatcher!.Invoke(fspath, [value], span);
    }

    /// <summary>The shell pattern's translation, as CPython's `translate` prints it.</summary>
    internal static string TranslatePattern(string pattern)
    {
        var (parts, stars) = TranslateParts(pattern, "*", ".");
        return JoinTranslatedParts(parts, stars);
    }

    /// <summary>
    /// `fnmatch._translate`: the pattern as regex pieces. `glob.translate` reuses the
    /// pieces with its own star and single-character forms.
    /// </summary>
    internal static (List<string> Parts, List<int> Stars) TranslateParts(
        string pattern,
        string star,
        string questionMark
    )
    {
        var parts = new List<string>();
        var stars = new List<int>();
        var i = 0;
        var n = pattern.Length;
        while (i < n)
        {
            var c = pattern[i];
            i++;
            if (c == '*')
            {
                stars.Add(parts.Count);
                parts.Add(star);
                while (i < n && pattern[i] == '*')
                {
                    i++;
                }
            }
            else if (c == '?')
            {
                parts.Add(questionMark);
            }
            else if (c == '[')
            {
                var j = i;
                if (j < n && pattern[j] == '!')
                {
                    j++;
                }
                if (j < n && pattern[j] == ']')
                {
                    j++;
                }
                while (j < n && pattern[j] != ']')
                {
                    j++;
                }
                if (j >= n)
                {
                    parts.Add("\\[");
                }
                else
                {
                    var stuff = pattern[i..j];
                    if (!stuff.Contains('-', StringComparison.Ordinal))
                    {
                        stuff = stuff.Replace("\\", "\\\\", StringComparison.Ordinal);
                    }
                    else
                    {
                        var chunks = new List<string>();
                        var k = pattern[i] == '!' ? i + 2 : i + 1;
                        while (true)
                        {
                            k = Find(pattern, '-', k, j);
                            if (k < 0)
                            {
                                break;
                            }
                            chunks.Add(pattern[i..k]);
                            i = k + 1;
                            k += 3;
                        }
                        var chunk = pattern[i..j];
                        if (chunk.Length != 0)
                        {
                            chunks.Add(chunk);
                        }
                        else
                        {
                            chunks[^1] += "-";
                        }
                        for (var index = chunks.Count - 1; index > 0; index--)
                        {
                            if (chunks[index - 1][^1] > chunks[index][0])
                            {
                                chunks[index - 1] = chunks[index - 1][..^1] + chunks[index][1..];
                                chunks.RemoveAt(index);
                            }
                        }
                        stuff = string.Join(
                            "-",
                            chunks.Select(entry =>
                                entry
                                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                                    .Replace("-", "\\-", StringComparison.Ordinal)
                            )
                        );
                    }
                    i = j + 1;
                    if (stuff.Length == 0)
                    {
                        parts.Add("(?!)");
                    }
                    else if (stuff == "!")
                    {
                        parts.Add(".");
                    }
                    else
                    {
                        stuff = EscapeSetOperations(stuff);
                        if (stuff[0] == '!')
                        {
                            stuff = "^" + stuff[1..];
                        }
                        else if (stuff[0] is '^' or '[')
                        {
                            stuff = "\\" + stuff;
                        }
                        parts.Add($"[{stuff}]");
                    }
                }
            }
            else
            {
                parts.Add(ReEscape(c.ToString()));
            }
        }
        return (parts, stars);
    }

    /// <summary>`fnmatch._join_translated_parts`: interior stars become atomic groups.</summary>
    internal static string JoinTranslatedParts(List<string> parts, List<int> stars)
    {
        if (stars.Count == 0)
        {
            return $"(?s:{string.Concat(parts)})\\z";
        }
        var buffer = new List<string>(parts.GetRange(0, stars[0]));
        var previous = stars[0];
        var index = previous + 1;
        foreach (var next in stars.Skip(1))
        {
            buffer.Add("(?>.*?");
            buffer.AddRange(parts.GetRange(index, next - index));
            buffer.Add(")");
            index = next + 1;
        }
        buffer.Add(".*");
        buffer.AddRange(parts.GetRange(index, parts.Count - index));
        return $"(?s:{string.Concat(buffer)})\\z";
    }

    /// <summary>`re.escape` over one character: its own fixed set, which includes the regex metacharacters and whitespace.</summary>
    internal static string ReEscape(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if ("()[]{}?*+-|^$\\.&~# \t\n\r\v\f".Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>`fnmatch._re_setops_sub`: the set operations become literal characters.</summary>
    private static string EscapeSetOperations(string stuff)
    {
        var builder = new StringBuilder(stuff.Length * 2);
        foreach (var c in stuff)
        {
            if (c is '&' or '~' or '|')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static int Find(string text, char search, int start, int end)
    {
        if (start < 0)
        {
            start = 0;
        }
        if (start >= text.Length || start >= end)
        {
            return -1;
        }
        return text.IndexOf(search, start, Math.Min(end, text.Length) - start);
    }

    /// <summary>
    /// The translated pattern compiled by the runtime's own `re`, which is what CPython
    /// compiles too; the returned delegate answers whether a name matches in full.
    /// </summary>
    internal static Func<string, bool> CompileTextMatcher(string pattern, TextSpan span)
    {
        var translated = TranslatePattern(pattern);
        var re = UserObjectProtocols.Dispatcher!.ImportModule("re", string.Empty, span);
        var compile = ManagedObjectProtocols.GetAttribute(re, "compile", span);
        var compiled = UserObjectProtocols.Dispatcher!.Invoke(
            compile,
            [new PythonTextValue(translated)],
            span
        );
        var match = ManagedObjectProtocols.GetAttribute(compiled, "match", span);
        return name =>
            UserObjectProtocols.Dispatcher!.Invoke(match, [new PythonTextValue(name)], span)
                is not PythonNoneValue;
    }

    private static PythonRuntimeException TypeFault(string message) =>
        ManagedObjectProtocols.Fault("DPY4057", message, default, "TypeError");
}
