using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The managed <c>re</c> module: a native regular-expression engine on the surface CPython
/// publishes. Named groups, backreferences, lookaround and scoped flags are not part of
/// this slice, and a pattern that uses one is refused where it is read. Patterns are `str`
/// only; the `bytes` forms are not part of the slice either.
/// </summary>
internal static class PythonRegexModule
{
    private const string Module = "re";

    private const int FlagIgnoreCase = 1 << 1;
    private const int FlagLocale = 1 << 2;
    private const int FlagMultiline = 1 << 3;
    private const int FlagDotAll = 1 << 4;
    private const int FlagUnicode = 1 << 5;
    private const int FlagVerbose = 1 << 6;
    private const int FlagDebug = 1 << 7;
    private const int FlagAscii = 1 << 8;

    private const int MaxCache = 512;

    /// <summary>CPython cuts a match's representation at this length.</summary>
    private const int MaxMatchReprLength = 89;

    /// <summary>The names the module publishes; `re.__all__` is exactly this list.</summary>
    private static readonly string[] PublicNames =
    [
        "match",
        "fullmatch",
        "search",
        "sub",
        "subn",
        "split",
        "findall",
        "finditer",
        "compile",
        "purge",
        "escape",
        "error",
        "Pattern",
        "Match",
        "A",
        "I",
        "L",
        "M",
        "S",
        "X",
        "U",
        "ASCII",
        "IGNORECASE",
        "LOCALE",
        "MULTILINE",
        "DOTALL",
        "VERBOSE",
        "UNICODE",
        "NOFLAG",
        "RegexFlag",
        "PatternError",
    ];

    /// <summary>Every flag's two spellings, in the order the enum declares them.</summary>
    private static readonly (string Alias, string Name)[] FlagAliases =
    [
        ("NOFLAG", "NOFLAG"),
        ("A", "ASCII"),
        ("ASCII", "ASCII"),
        ("I", "IGNORECASE"),
        ("IGNORECASE", "IGNORECASE"),
        ("L", "LOCALE"),
        ("LOCALE", "LOCALE"),
        ("U", "UNICODE"),
        ("UNICODE", "UNICODE"),
        ("M", "MULTILINE"),
        ("MULTILINE", "MULTILINE"),
        ("S", "DOTALL"),
        ("DOTALL", "DOTALL"),
        ("X", "VERBOSE"),
        ("VERBOSE", "VERBOSE"),
        ("DEBUG", "DEBUG"),
    ];

    /// <summary>The names a flag's bits are spelled by, in the order the enum declares them.</summary>
    private static readonly (string Name, int Bit)[] FlagNames =
    [
        ("ASCII", FlagAscii),
        ("IGNORECASE", FlagIgnoreCase),
        ("LOCALE", FlagLocale),
        ("UNICODE", FlagUnicode),
        ("MULTILINE", FlagMultiline),
        ("DOTALL", FlagDotAll),
        ("VERBOSE", FlagVerbose),
        ("DEBUG", FlagDebug),
    ];

    private static readonly PythonManagedTypeValue MatchType = CreateMatchType();
    private static readonly PythonManagedTypeValue PatternType = CreatePatternType();
    private static readonly PythonManagedTypeValue ErrorType = CreateErrorType();
    private static readonly Dictionary<
        (string Pattern, long Flags),
        PythonManagedObjectValue
    > Cache = [];
    private static PythonManagedTypeValue? _flagType;

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        var flags = FlagType;
        globals.SetValue("RegexFlag", flags);
        foreach (var (alias, name) in FlagAliases)
        {
            globals.SetValue(alias, ReadFlag(flags, name));
        }
        globals.SetValue("Pattern", PatternType);
        globals.SetValue("Match", MatchType);
        globals.SetValue("PatternError", ErrorType);
        globals.SetValue("error", ErrorType);
        globals.SetValue(
            "compile",
            Function("compile", Compile, ["pattern", "flags"], required: 1)
        );
        globals.SetValue(
            "match",
            Function("match", Match, ["pattern", "string", "flags"], required: 2)
        );
        globals.SetValue(
            "fullmatch",
            Function("fullmatch", FullMatch, ["pattern", "string", "flags"], required: 2)
        );
        globals.SetValue(
            "search",
            Function("search", Search, ["pattern", "string", "flags"], required: 2)
        );
        globals.SetValue(
            "findall",
            Function("findall", FindAll, ["pattern", "string", "flags"], required: 2)
        );
        globals.SetValue(
            "finditer",
            Function("finditer", FindIter, ["pattern", "string", "flags"], required: 2)
        );
        globals.SetValue(
            "sub",
            Function(
                "sub",
                Substitute,
                ["pattern", "repl", "string", "count", "flags"],
                required: 3
            )
        );
        globals.SetValue(
            "subn",
            Function(
                "subn",
                SubstituteCount,
                ["pattern", "repl", "string", "count", "flags"],
                required: 3
            )
        );
        globals.SetValue(
            "split",
            Function("split", Split, ["pattern", "string", "maxsplit", "flags"], required: 2)
        );
        globals.SetValue("escape", Function("escape", Escape, ["pattern"], required: 1));
        globals.SetValue("purge", Function("purge", Purge, [], required: 0));
        // CPython's cache sizes, which its `dir()` shows even though the cache itself is
        // an implementation detail.
        globals.SetValue("_MAXCACHE", Integer(MaxCache));
        globals.SetValue("_MAXCACHE2", Integer(MaxCache / 2));
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. PublicNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
    }

    // -----------------------------------------------------------------------------
    // The flag type
    // -----------------------------------------------------------------------------

    /// <summary>
    /// `re.RegexFlag`, the `enum.IntFlag` the module defines for itself: a flag is a value
    /// the engine reads as an integer and prints as the name it was written by.
    /// </summary>
    private static PythonManagedTypeValue FlagType => _flagType ??= CreateFlagType();

    private static PythonManagedTypeValue CreateFlagType()
    {
        var members = new List<PythonValue>();
        void Add(string name, int value) =>
            members.Add(new PythonTupleValue([new PythonTextValue(name), Integer(value)]));
        Add("NOFLAG", 0);
        Add("ASCII", FlagAscii);
        Add("A", FlagAscii);
        Add("IGNORECASE", FlagIgnoreCase);
        Add("I", FlagIgnoreCase);
        Add("LOCALE", FlagLocale);
        Add("L", FlagLocale);
        Add("UNICODE", FlagUnicode);
        Add("U", FlagUnicode);
        Add("MULTILINE", FlagMultiline);
        Add("M", FlagMultiline);
        Add("DOTALL", FlagDotAll);
        Add("S", FlagDotAll);
        Add("VERBOSE", FlagVerbose);
        Add("X", FlagVerbose);
        Add("DEBUG", FlagDebug);
        var type = PythonEnum.CreateIntFlag("RegexFlag", Module, new PythonListValue(members));
        // CPython's own `RegexFlag` names the flags it is made of in both spellings.
        var representation = Method(
            "RegexFlag",
            "__repr__",
            (receiver, _) => new PythonTextValue(FormatFlags(Index(receiver!, default)))
        );
        type.Attributes["__repr__"] = representation;
        type.Attributes["__str__"] = representation;
        return type;
    }

    /// <summary>`re.IGNORECASE|re.MULTILINE`, or `0x...` for the bits no flag names.</summary>
    private static string FormatFlags(long flags)
    {
        var rest = flags;
        var parts = new List<string>();
        foreach (var (name, bit) in FlagNames)
        {
            if ((rest & bit) == bit)
            {
                parts.Add("re." + name);
                rest &= ~bit;
            }
        }
        if (rest != 0)
        {
            parts.Add($"0x{rest:x}");
        }
        return parts.Count == 0 ? "re.NOFLAG" : string.Join('|', parts);
    }

    private static PythonValue ReadFlag(PythonManagedTypeValue flags, string name) =>
        ManagedObjectProtocols.GetAttribute(flags, name, default);

    // -----------------------------------------------------------------------------
    // Module-level functions
    // -----------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue Function(
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body,
        string[] parameters,
        int required
    ) =>
        new PythonBuiltinFunctionValue(
            name,
            (arguments, span) => body(Check(name, parameters, required, arguments, span), span)
        ).WithSignature(parameters, Defaults(parameters.Length, required));

    /// <summary>Which parameters have a default, and which a call must supply.</summary>
    private static PythonValue?[] Defaults(int count, int required) =>
        [
            .. Enumerable
                .Range(0, count)
                .Select(index => index < required ? null : (PythonValue?)PythonNoneValue.Instance),
        ];

    /// <summary>
    /// The arguments a body reads: a call that leaves out a required parameter is reported
    /// the way Python reports it. A call that named its arguments bound them already, so
    /// only a positional call arrives short.
    /// </summary>
    private static IReadOnlyList<PythonValue> Check(
        string name,
        string[] parameters,
        int required,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count > parameters.Length)
        {
            var plural = parameters.Length == 1 ? string.Empty : "s";
            var takes =
                required == parameters.Length
                    ? $"{required} positional argument{(required == 1 ? string.Empty : "s")}"
                    : $"from {required} to {parameters.Length} positional arguments";
            throw Fault(
                $"{name}() takes {takes} but {arguments.Count} were given",
                span,
                "TypeError"
            );
        }
        if (arguments.Count < required)
        {
            var missing = parameters[arguments.Count..required];
            throw Fault(
                $"{name}() missing {missing.Length} required positional argument{(missing.Length == 1 ? string.Empty : "s")}: {JoinNames(missing)}",
                span,
                "TypeError"
            );
        }
        // A call that left the optional arguments out hands the body a short list,
        // which every body reads by position.
        return arguments;
    }

    private static string JoinNames(string[] names) =>
        names.Length switch
        {
            1 => $"'{names[0]}'",
            2 => $"'{names[0]}' and '{names[1]}'",
            _ => string.Join(", ", names.Take(names.Length - 1).Select(name => $"'{name}'"))
                + $", and '{names[^1]}'",
        };

    private static PythonValue Compile(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        CompilePattern(arguments[0], arguments.Count > 1 ? arguments[1] : null, span);

    /// <summary>
    /// The pattern a module-level call works on: a string compiled here — through the
    /// cache, which is why two compiles of one pattern are the same object — or a
    /// `Pattern` already compiled, which no flags may follow.
    /// </summary>
    private static PythonManagedObjectValue CompilePattern(
        PythonValue pattern,
        PythonValue? flags,
        TextSpan span
    )
    {
        if (pattern is PythonManagedObjectValue { Payload: PatternState } instance)
        {
            if (Flags(flags, span) != 0)
            {
                throw Fault(
                    "cannot process flags argument with a compiled pattern",
                    span,
                    "ValueError"
                );
            }
            return instance;
        }
        if (pattern is not PythonTextValue text)
        {
            throw Fault("first argument must be string or compiled pattern", span, "TypeError");
        }
        return CompileCached(text.Value, Flags(flags, span), span);
    }

    private static PythonManagedObjectValue CompileCached(string text, long flags, TextSpan span)
    {
        if (Cache.TryGetValue((text, flags), out var cached))
        {
            return cached;
        }
        var compiled = CompilePatternText(text, flags, span);
        if (Cache.Count >= MaxCache)
        {
            Cache.Clear();
        }
        Cache[(text, flags)] = compiled;
        return compiled;
    }

    private static PythonManagedObjectValue CompilePatternText(
        string text,
        long flags,
        TextSpan span
    )
    {
        if ((flags & FlagLocale) != 0)
        {
            throw Fault("cannot use LOCALE flag with a str pattern", span, "ValueError");
        }
        if ((flags & FlagAscii) != 0 && (flags & FlagUnicode) != 0)
        {
            throw Fault("ASCII and UNICODE flags are incompatible", span, "ValueError");
        }
        PythonRegex.Program program;
        try
        {
            program = PythonRegex.Compile(text, (PythonRegexFlags)(flags & int.MaxValue));
        }
        catch (PythonRegexParseError error)
        {
            throw ParseFault(error, text);
        }
        // The flags a pattern reports are the ones it was read with: a `(?i)` written
        // into the pattern is part of what the compiled pattern says it carries.
        return new PythonManagedObjectValue(
            PatternType,
            new PatternState(program, text, ReportedFlags((long)program.Flags))
        );
    }

    /// <summary>What `Pattern.flags` reports: the flags given, and the implicit UNICODE.</summary>
    private static long ReportedFlags(long flags) =>
        (flags & (FlagAscii | FlagLocale | FlagUnicode)) == 0 ? flags | FlagUnicode : flags;

    private static PythonValue Match(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        RunMatch(arguments, span, anchored: true, whole: false);

    private static PythonValue FullMatch(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        RunMatch(arguments, span, anchored: true, whole: true);

    private static PythonValue Search(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        RunMatch(arguments, span, anchored: false, whole: false);

    private static PythonValue RunMatch(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span,
        bool anchored,
        bool whole
    )
    {
        var pattern = CompilePattern(arguments[0], arguments.Count > 2 ? arguments[2] : null, span);
        var text = RequireText(arguments[1], span);
        return Find(pattern, text, 0, null, anchored, whole, span) is { } match
            ? match
            : PythonNoneValue.Instance;
    }

    private static PythonValue FindAll(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var pattern = CompilePattern(arguments[0], arguments.Count > 2 ? arguments[2] : null, span);
        var text = RequireText(arguments[1], span);
        var found = new List<PythonValue>();
        foreach (var match in Walk(pattern, text, 0, null, span))
        {
            found.Add(Grouped(MatchOf(match)));
        }
        return new PythonListValue(found);
    }

    private static PythonValue FindIter(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var pattern = CompilePattern(arguments[0], arguments.Count > 2 ? arguments[2] : null, span);
        var text = RequireText(arguments[1], span);
        var matches = Walk(pattern, text, 0, null, span).Cast<PythonValue>().ToList();
        return new PythonIteratorValue(new PythonListValue(matches), matches.Count);
    }

    private static PythonValue Substitute(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        RunSubstitute(
            CompilePattern(arguments[0], arguments.Count > 4 ? arguments[4] : null, span),
            arguments[1],
            arguments[2],
            arguments.Count > 3 ? arguments[3] : null,
            span,
            countResults: false
        );

    private static PythonValue SubstituteCount(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) =>
        RunSubstitute(
            CompilePattern(arguments[0], arguments.Count > 4 ? arguments[4] : null, span),
            arguments[1],
            arguments[2],
            arguments.Count > 3 ? arguments[3] : null,
            span,
            countResults: true
        );

    private static PythonValue RunSubstitute(
        PythonManagedObjectValue pattern,
        PythonValue replacement,
        PythonValue textValue,
        PythonValue? countValue,
        TextSpan span,
        bool countResults
    )
    {
        var text = RequireText(textValue, span);
        var count = Index(countValue, span);
        var builder = new StringBuilder(text.Length);
        long done = 0;
        var position = 0;
        var last = 0;
        while ((count <= 0 || done < count) && position <= text.Length)
        {
            var match = Find(pattern, text, position, null, anchored: false, whole: false, span);
            if (match is null)
            {
                break;
            }
            var matched = MatchOf(match);
            builder.Append(text, last, matched.Result.Start - last);
            builder.Append(Replacement(replacement, match, span));
            last = matched.Result.End;
            position = matched.Result.End == matched.Result.Start ? last + 1 : last;
            done++;
        }
        builder.Append(text, last, text.Length - last);
        return countResults
            ? new PythonTupleValue([new PythonTextValue(builder.ToString()), Integer(done)])
            : new PythonTextValue(builder.ToString());
    }

    private static PythonValue Split(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        RunSplit(
            CompilePattern(arguments[0], arguments.Count > 3 ? arguments[3] : null, span),
            arguments[1],
            arguments.Count > 2 ? arguments[2] : null,
            span
        );

    private static PythonListValue RunSplit(
        PythonManagedObjectValue pattern,
        PythonValue textValue,
        PythonValue? maxsplitValue,
        TextSpan span
    )
    {
        var text = RequireText(textValue, span);
        var maxsplit = Index(maxsplitValue, span);
        var pieces = new List<PythonValue>();
        long done = 0;
        var position = 0;
        var last = 0;
        while ((maxsplit <= 0 || done < maxsplit) && position <= text.Length)
        {
            var match = Find(pattern, text, position, null, anchored: false, whole: false, span);
            if (match is null)
            {
                break;
            }
            var matched = MatchOf(match);
            pieces.Add(new PythonTextValue(text[last..matched.Result.Start]));
            for (var group = 1; group <= matched.Program.GroupCount; group++)
            {
                pieces.Add(GroupText(matched, group));
            }
            last = matched.Result.End;
            position = matched.Result.End == matched.Result.Start ? last + 1 : last;
            done++;
        }
        pieces.Add(new PythonTextValue(text[last..]));
        return new PythonListValue(pieces);
    }

    private static PythonValue Escape(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var value = arguments[0];
        if (value is not PythonTextValue text)
        {
            throw Fault(
                $"decoding to str: need a bytes-like object, {ManagedObjectProtocols.GetTypeName(value)} found",
                span,
                "TypeError"
            );
        }
        const string Special = "()[]{}?*+-|^$\\.&~# \t\n\r\v\f";
        var output = new StringBuilder(text.Value.Length);
        foreach (var character in text.Value)
        {
            if (Special.Contains(character, StringComparison.Ordinal))
            {
                output.Append('\\');
            }
            output.Append(character);
        }
        return new PythonTextValue(output.ToString());
    }

    private static PythonValue Purge(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        Cache.Clear();
        return PythonNoneValue.Instance;
    }

    // -----------------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------------

    /// <summary>A compiled pattern: the program, the text it was read from, its flags.</summary>
    private sealed class PatternState(PythonRegex.Program program, string text, long flags)
    {
        private PythonDictionaryValue? _groupIndex;
        private string?[]? _groupNames;

        internal PythonRegex.Program Program { get; } = program;

        internal string Text { get; } = text;

        internal long Flags { get; } = flags;

        /// <summary>The named groups by name, which is what `Pattern.groupindex` reports.</summary>
        internal PythonDictionaryValue GroupIndex
        {
            get
            {
                if (_groupIndex is null)
                {
                    _groupIndex = new PythonDictionaryValue([]);
                    foreach (var (name, index) in Program.GroupNames)
                    {
                        ManagedObjectProtocols.SetDictionaryItem(
                            _groupIndex,
                            new PythonTextValue(name),
                            Integer(index),
                            default
                        );
                    }
                }
                return _groupIndex;
            }
        }

        /// <summary>The number a group name names, or none where no group took it.</summary>
        internal int? NumberOf(string name)
        {
            foreach (var (taken, index) in Program.GroupNames)
            {
                if (taken == name)
                {
                    return index;
                }
            }
            return null;
        }

        /// <summary>The name of a group, where it has one.</summary>
        internal string? NameOf(int group)
        {
            if (_groupNames is null)
            {
                _groupNames = new string?[Program.GroupCount + 1];
                foreach (var (name, index) in Program.GroupNames)
                {
                    _groupNames[index] = name;
                }
            }
            return group >= 0 && group < _groupNames.Length ? _groupNames[group] : null;
        }
    }

    /// <summary>One match: the pattern it came from, the string, and what it captured.</summary>
    private sealed class MatchState(
        PythonManagedObjectValue pattern,
        PythonRegex.Program program,
        string text,
        PythonRegex.Result result,
        long pos,
        long endpos
    )
    {
        internal PythonManagedObjectValue Pattern { get; } = pattern;

        internal PythonRegex.Program Program { get; } = program;

        internal string Text { get; } = text;

        internal PythonRegex.Result Result { get; } = result;

        internal long Pos { get; } = pos;

        internal long EndPos { get; } = endpos;
    }

    private static PatternState StateOf(PythonValue pattern) =>
        pattern is PythonManagedObjectValue { Payload: PatternState state }
            ? state
            : throw PatternDescriptor("match", pattern);

    /// <summary>The number a group name names in the pattern this match came from.</summary>
    private static int? PatternNumber(this MatchState state, string name) =>
        StateOf(state.Pattern).NumberOf(name);

    /// <summary>The name of a group of the pattern this match came from.</summary>
    private static string? NameOfPattern(this MatchState state, int group) =>
        StateOf(state.Pattern).NameOf(group);

    private static MatchState MatchOf(PythonValue match) =>
        match is PythonManagedObjectValue { Payload: MatchState state }
            ? state
            : throw Fault(
                "descriptor 'group' for 're.Match' objects doesn't apply to a "
                    + $"'{ManagedObjectProtocols.GetTypeName(match)}' object",
                default,
                "TypeError"
            );

    private static PythonRuntimeException PatternDescriptor(string name, PythonValue target) =>
        Fault(
            $"descriptor '{name}' for 're.Pattern' objects doesn't apply to a "
                + $"'{ManagedObjectProtocols.GetTypeName(target)}' object",
            default,
            "TypeError"
        );

    private static string RequireText(PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw Fault(
                $"expected string or bytes-like object, got '{ManagedObjectProtocols.GetTypeName(value)}'",
                span,
                "TypeError"
            );

    /// <summary>
    /// One search: from `pos` up to `endpos`, anchored at the start for `match` and at
    /// both ends for `fullmatch`.
    /// </summary>
    private static PythonManagedObjectValue? Find(
        PythonManagedObjectValue pattern,
        string text,
        long pos,
        long? endpos,
        bool anchored,
        bool whole,
        TextSpan span
    )
    {
        var state = StateOf(pattern);
        var start = Clamp(pos, text.Length);
        var bound = Bound(text, endpos);
        var result =
            whole ? PythonRegex.FullMatch(state.Program, text, start, bound)
            : anchored ? PythonRegex.Match(state.Program, text, start, bound)
            : PythonRegex.Search(state.Program, text, start, bound);
        return result is null
            ? null
            : new PythonManagedObjectValue(
                MatchType,
                new MatchState(pattern, state.Program, text, result, start, bound ?? text.Length)
            );
    }

    /// <summary>Every non-overlapping match, the way CPython's scanner walks them.</summary>
    private static IEnumerable<PythonManagedObjectValue> Walk(
        PythonManagedObjectValue pattern,
        string text,
        long pos,
        long? endpos,
        TextSpan span
    )
    {
        var state = StateOf(pattern);
        var start = Clamp(pos, text.Length);
        var bound = Bound(text, endpos);
        var position = start;
        while (position <= (bound ?? text.Length))
        {
            var result = PythonRegex.Search(state.Program, text, position, bound);
            if (result is null)
            {
                yield break;
            }
            yield return new PythonManagedObjectValue(
                MatchType,
                new MatchState(pattern, state.Program, text, result, start, bound ?? text.Length)
            );
            // A match that took nothing moves the scan on, so the walk ends.
            position = result.End == result.Start ? result.End + 1 : result.End;
        }
    }

    private static int Clamp(long pos, int length) => pos < 0 ? 0 : (int)Math.Min(pos, length);

    private static int? Bound(string text, long? endpos) =>
        endpos is null ? null : (int)Math.Min(endpos.Value, text.Length);

    // -----------------------------------------------------------------------------
    // Match objects
    // -----------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateMatchType()
    {
        var type = new PythonManagedTypeValue("Match") { Module = Module, QualName = "Match" };
        type.Attributes["__new__"] = RefuseNew("re.Match");
        type.Attributes["group"] = Method(
            "Match",
            "group",
            Group,
            positionalOnly: 1,
            variable: true
        );
        type.Attributes["groups"] = Method(
            "Match",
            "groups",
            Groups,
            parameters: ["default"],
            defaults: [PythonNoneValue.Instance]
        );
        type.Attributes["groupdict"] = Method(
            "Match",
            "groupdict",
            GroupDict,
            parameters: ["default"],
            defaults: [PythonNoneValue.Instance]
        );
        type.Attributes["start"] = Method(
            "Match",
            "start",
            (receiver, arguments) => Bound0(receiver, arguments, start: true),
            parameters: ["group"],
            defaults: [Integer(0)]
        );
        type.Attributes["end"] = Method(
            "Match",
            "end",
            (receiver, arguments) => Bound0(receiver, arguments, start: false),
            parameters: ["group"],
            defaults: [Integer(0)]
        );
        type.Attributes["span"] = Method(
            "Match",
            "span",
            Span,
            parameters: ["group"],
            defaults: [Integer(0)]
        );
        type.Attributes["expand"] = Method(
            "Match",
            "expand",
            ExpandMatch,
            parameters: ["template"]
        );
        type.Attributes["__getitem__"] = Method("Match", "__getitem__", GetItem);
        // `m[i]` reads a group, but a match is not a sequence to walk: CPython's own
        // `__getitem__` does not make it iterable, and this runtime's fallback would.
        type.Attributes["__iter__"] = new PythonProtocolFunctionValue(
            "__iter__",
            (receiver, _) =>
                throw Fault(
                    $"'{DisplayName(receiver)}' object is not iterable",
                    default,
                    "TypeError"
                )
        );
        type.Attributes["__repr__"] = Method(
            "Match",
            "__repr__",
            (receiver, _) => new PythonTextValue(Represent(MatchOf(receiver!)))
        );
        type.Attributes["__copy__"] = Method("Match", "__copy__", (receiver, _) => receiver!);
        type.Attributes["__deepcopy__"] = Method(
            "Match",
            "__deepcopy__",
            (receiver, _) => receiver!
        );
        type.Attributes["pos"] = ReadOnlyMember(
            "pos",
            "re.Match",
            receiver => Integer(MatchOf(receiver).Pos)
        );
        type.Attributes["endpos"] = ReadOnlyMember(
            "endpos",
            "re.Match",
            receiver => Integer(MatchOf(receiver).EndPos)
        );
        type.Attributes["lastindex"] = ReadOnlyMember(
            "lastindex",
            "re.Match",
            receiver =>
                MatchOf(receiver).Result.LastIndex is { } index
                    ? Integer(index)
                    : PythonNoneValue.Instance,
            named: true
        );
        type.Attributes["lastgroup"] = ReadOnlyMember(
            "lastgroup",
            "re.Match",
            receiver =>
                MatchOf(receiver).Result.LastIndex is { } index
                && MatchOf(receiver).NameOfPattern(index) is { } name
                    ? new PythonTextValue(name)
                    : PythonNoneValue.Instance,
            named: true
        );
        type.Attributes["re"] = ReadOnlyMember(
            "re",
            "re.Match",
            receiver => MatchOf(receiver).Pattern
        );
        type.Attributes["string"] = ReadOnlyMember(
            "string",
            "re.Match",
            receiver => new PythonTextValue(MatchOf(receiver).Text)
        );
        type.Attributes["regs"] = ReadOnlyMember("regs", "re.Match", Registers, named: true);
        return type;
    }

    private static PythonManagedTypeValue CreatePatternType()
    {
        var type = new PythonManagedTypeValue("Pattern") { Module = Module, QualName = "Pattern" };
        type.Attributes["__new__"] = RefuseNew("re.Pattern");
        type.Attributes["match"] = Method(
            "Pattern",
            "match",
            (receiver, arguments) => PatternFind(receiver, arguments, FindKind.Match),
            parameters: ["string", "pos", "endpos"],
            defaults: [null, Integer(0), Integer(int.MaxValue)]
        );
        type.Attributes["fullmatch"] = Method(
            "Pattern",
            "fullmatch",
            (receiver, arguments) => PatternFind(receiver, arguments, FindKind.FullMatch),
            parameters: ["string", "pos", "endpos"],
            defaults: [null, Integer(0), Integer(int.MaxValue)]
        );
        type.Attributes["search"] = Method(
            "Pattern",
            "search",
            (receiver, arguments) => PatternFind(receiver, arguments, FindKind.Search),
            parameters: ["string", "pos", "endpos"],
            defaults: [null, Integer(0), Integer(int.MaxValue)]
        );
        type.Attributes["findall"] = Method(
            "Pattern",
            "findall",
            PatternFindAll,
            parameters: ["string", "pos", "endpos"],
            defaults: [null, Integer(0), Integer(int.MaxValue)]
        );
        type.Attributes["finditer"] = Method(
            "Pattern",
            "finditer",
            PatternFindIter,
            parameters: ["string", "pos", "endpos"],
            defaults: [null, Integer(0), Integer(int.MaxValue)]
        );
        type.Attributes["sub"] = Method(
            "Pattern",
            "sub",
            (receiver, arguments) => PatternSubstitute(receiver, arguments, countResults: false),
            parameters: ["repl", "string", "count"],
            defaults: [null, null, Integer(0)]
        );
        type.Attributes["subn"] = Method(
            "Pattern",
            "subn",
            (receiver, arguments) => PatternSubstitute(receiver, arguments, countResults: true),
            parameters: ["repl", "string", "count"],
            defaults: [null, null, Integer(0)]
        );
        type.Attributes["split"] = Method(
            "Pattern",
            "split",
            PatternSplit,
            parameters: ["string", "maxsplit"],
            defaults: [null, Integer(0)]
        );
        type.Attributes["__repr__"] = Method(
            "Pattern",
            "__repr__",
            (receiver, _) => new PythonTextValue(RepresentPattern(StateOf(receiver!)))
        );
        type.Attributes["__copy__"] = Method("Pattern", "__copy__", (receiver, _) => receiver!);
        type.Attributes["__deepcopy__"] = Method(
            "Pattern",
            "__deepcopy__",
            (receiver, _) => receiver!
        );
        type.Attributes["pattern"] = ReadOnlyMember(
            "pattern",
            "re.Pattern",
            receiver => new PythonTextValue(StateOf(receiver).Text)
        );
        type.Attributes["flags"] = ReadOnlyMember(
            "flags",
            "re.Pattern",
            receiver => Integer(StateOf(receiver).Flags)
        );
        type.Attributes["groups"] = ReadOnlyMember(
            "groups",
            "re.Pattern",
            receiver => Integer(StateOf(receiver).Program.GroupCount)
        );
        type.Attributes["groupindex"] = ReadOnlyMember(
            "groupindex",
            "re.Pattern",
            // Python publishes it read-only, as the mapping proxy `types.MappingProxyType`
            // makes.
            receiver => new PythonMappingProxyValue(StateOf(receiver).GroupIndex),
            named: true
        );
        return type;
    }

    /// <summary>A class whose instances only the module's own functions make.</summary>
    private static PythonProtocolFunctionValue RefuseNew(string owner) =>
        new(
            "__new__",
            (_, _) => throw Fault($"cannot create '{owner}' instances", default, "TypeError")
        );

    private enum FindKind
    {
        Match,
        FullMatch,
        Search,
    }

    private static PythonValue PatternFind(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        FindKind kind
    )
    {
        var pattern = RequirePattern(receiver);
        var text = RequireText(arguments[0], default);
        return
            Find(
                pattern,
                text,
                OptionalIndex(arguments, 1),
                OptionalBound(arguments, 2),
                kind is not FindKind.Search,
                kind is FindKind.FullMatch,
                default
            )
                is { } match
            ? match
            : PythonNoneValue.Instance;
    }

    private static PythonValue PatternFindAll(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var pattern = RequirePattern(receiver);
        var text = RequireText(arguments[0], default);
        var found = new List<PythonValue>();
        foreach (
            var match in Walk(
                pattern,
                text,
                OptionalIndex(arguments, 1),
                OptionalBound(arguments, 2),
                default
            )
        )
        {
            found.Add(Grouped(MatchOf(match)));
        }
        return new PythonListValue(found);
    }

    private static PythonValue PatternFindIter(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var pattern = RequirePattern(receiver);
        var text = RequireText(arguments[0], default);
        var matches = Walk(
                pattern,
                text,
                OptionalIndex(arguments, 1),
                OptionalBound(arguments, 2),
                default
            )
            .Cast<PythonValue>()
            .ToList();
        return new PythonIteratorValue(new PythonListValue([.. matches]), matches.Count);
    }

    private static PythonValue PatternSubstitute(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool countResults
    ) =>
        RunSubstitute(
            RequirePattern(receiver),
            arguments[0],
            arguments[1],
            arguments.Count > 2 ? arguments[2] : null,
            default,
            countResults
        );

    private static PythonValue PatternSplit(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    ) =>
        RunSplit(
            RequirePattern(receiver),
            arguments[0],
            arguments.Count > 1 ? arguments[1] : null,
            default
        );

    /// <summary>A `pos` argument: the number written, or zero where none was.</summary>
    private static long OptionalIndex(IReadOnlyList<PythonValue> arguments, int index) =>
        arguments.Count > index ? RequireIndex(arguments[index], default) : 0;

    /// <summary>An `endpos` argument: the number written, or none where none was.</summary>
    private static long? OptionalBound(IReadOnlyList<PythonValue> arguments, int index) =>
        arguments.Count > index ? RequireIndex(arguments[index], default) : null;

    private static PythonManagedObjectValue RequirePattern(PythonValue? receiver) =>
        receiver is PythonManagedObjectValue { Payload: PatternState } instance
            ? instance
            : throw PatternDescriptor("match", receiver ?? PythonNoneValue.Instance);

    /// <summary>One group's text for a positional `start`/`end` call.</summary>
    private static PythonWholeNumberValue Bound0(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        bool start
    )
    {
        var state = MatchOf(receiver!);
        var group = arguments.Count > 0 ? GroupIndex(state, arguments[0]) : 0;
        var bound = start ? state.Result.StartOf(group) : state.Result.EndOf(group);
        return Integer(bound ?? -1);
    }

    /// <summary>`m.group()` and `m.group(1, 2)`: one value, or a tuple of them.</summary>
    private static PythonValue Group(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = MatchOf(receiver!);
        if (arguments.Count == 0)
        {
            return GroupText(state, 0);
        }
        if (arguments.Count == 1)
        {
            return GroupText(state, GroupIndex(state, arguments[0]));
        }
        return new PythonTupleValue([
            .. arguments.Select(argument => GroupText(state, GroupIndex(state, argument))),
        ]);
    }

    private static PythonValue Groups(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = MatchOf(receiver!);
        var fallback = arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance;
        var values = new List<PythonValue>(state.Program.GroupCount);
        for (var group = 1; group <= state.Program.GroupCount; group++)
        {
            var value = GroupText(state, group);
            values.Add(value is PythonNoneValue ? fallback : value);
        }
        return new PythonTupleValue([.. values]);
    }

    private static PythonValue GroupDict(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var state = MatchOf(receiver!);
        var fallback = arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance;
        var found = new PythonDictionaryValue([]);
        foreach (var (name, index) in state.Program.GroupNames)
        {
            var value = GroupText(state, index);
            ManagedObjectProtocols.SetDictionaryItem(
                found,
                new PythonTextValue(name),
                value is PythonNoneValue ? fallback : value,
                default
            );
        }
        return found;
    }

    private static PythonValue Span(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = MatchOf(receiver!);
        var group = arguments.Count > 0 ? GroupIndex(state, arguments[0]) : 0;
        return new PythonTupleValue([
            Integer(state.Result.StartOf(group) ?? -1),
            Integer(state.Result.EndOf(group) ?? -1),
        ]);
    }

    private static PythonValue GetItem(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var state = MatchOf(receiver!);
        return GroupText(state, GroupIndex(state, arguments[0]));
    }

    private static PythonValue ExpandMatch(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    ) =>
        new PythonTextValue(
            Expand(RequireText(arguments[0], default), MatchOf(receiver!), default)
        );

    private static PythonValue Registers(PythonValue receiver)
    {
        var state = MatchOf(receiver);
        var registers = new List<PythonValue>(state.Program.GroupCount + 1);
        for (var group = 0; group <= state.Program.GroupCount; group++)
        {
            registers.Add(
                new PythonTupleValue([
                    Integer(state.Result.StartOf(group) ?? -1),
                    Integer(state.Result.EndOf(group) ?? -1),
                ])
            );
        }
        return new PythonTupleValue([.. registers]);
    }

    /// <summary>The group a call names: an index, or a name no group of this slice has.</summary>
    private static int GroupIndex(MatchState state, PythonValue group)
    {
        // Anything a group cannot be read as is the same IndexError a group that is not
        // there reports: CPython reads the argument's index and lets the failure fall
        // through to this message.
        long index;
        try
        {
            index = group is PythonTextValue name
                ? state.PatternNumber(name.Value) ?? -1
                : RequireIndex(group, default);
        }
        catch (PythonRuntimeException)
        {
            throw Fault("no such group", default, "IndexError");
        }
        if (index < 0 || index > state.Program.GroupCount)
        {
            throw Fault("no such group", default, "IndexError");
        }
        return (int)index;
    }

    /// <summary>What one group took, or None where it never matched.</summary>
    private static PythonValue GroupText(MatchState state, int group)
    {
        if (group < 0 || group > state.Program.GroupCount)
        {
            throw Fault("no such group", default, "IndexError");
        }
        var start = state.Result.StartOf(group);
        return start is null
            ? PythonNoneValue.Instance
            : new PythonTextValue(state.Text[start.Value..state.Result.EndOf(group)!.Value]);
    }

    /// <summary>What `findall` yields for one match: the text, a group, or a tuple of groups.</summary>
    private static PythonValue Grouped(MatchState state)
    {
        if (state.Program.GroupCount == 0)
        {
            return GroupText(state, 0);
        }
        if (state.Program.GroupCount == 1)
        {
            return GroupText(state, 1) is PythonNoneValue
                ? new PythonTextValue(string.Empty)
                : GroupText(state, 1);
        }
        return new PythonTupleValue([
            .. Enumerable
                .Range(1, state.Program.GroupCount)
                .Select(group => GroupText(state, group))
                .Select(value =>
                    value is PythonNoneValue
                        ? (PythonValue)new PythonTextValue(string.Empty)
                        : value
                ),
        ]);
    }

    private static string Represent(MatchState state)
    {
        var text = state.Text[state.Result.Start..state.Result.End];
        var span = $"({state.Result.Start}, {state.Result.End})";
        var quoted = new PythonTextValue(text).ToRepresentationString();
        var full = $"<re.Match object; span={span}, match={quoted}>";
        // A match too long to show whole is cut to the first 49 characters of the text
        // as it would have been quoted, and the closing quote goes with the rest.
        return full.Length <= MaxMatchReprLength
            ? full
            : $"<re.Match object; span={span}, match={quoted[..(MaxMatchReprLength - 39)]}>";
    }

    private static string RepresentPattern(PatternState state) =>
        $"re.compile({new PythonTextValue(state.Text).ToRepresentationString()}{FlagSuffix(state.Flags)})";

    /// <summary>The flags a pattern prints with, apart from the implicit UNICODE.</summary>
    private static string FlagSuffix(long flags)
    {
        var shown = flags & ~(long)FlagUnicode;
        return shown == 0 ? string.Empty : ", " + FormatFlags(shown);
    }

    // -----------------------------------------------------------------------------
    // Replacement templates
    // -----------------------------------------------------------------------------

    /// <summary>
    /// What a substitution writes for one match: what calling the replacement yields, or
    /// a template with its group references expanded.
    /// </summary>
    private static string Replacement(PythonValue replacement, PythonValue match, TextSpan span)
    {
        if (replacement is PythonTextValue text)
        {
            return Expand(text.Value, MatchOf(match), span);
        }
        if (ManagedObjectProtocols.IsCallable(replacement))
        {
            var produced = UserObjectProtocols.Dispatcher!.Invoke(replacement, [match], span);
            return produced is PythonTextValue producedText
                ? producedText.Value
                : throw Fault(
                    $"sequence item 0: expected str instance, {ManagedObjectProtocols.GetTypeName(produced)} found",
                    span,
                    "TypeError"
                );
        }
        // A replacement that is neither text nor callable is read as the pieces of a
        // string a `join` would take, which is where CPython's own words come from.
        throw Fault(
            ManagedObjectProtocols.GetTypeName(replacement) == "bytes"
                ? "sequence item 0: expected str instance, bytes found"
                : $"decoding to str: need a bytes-like object, {ManagedObjectProtocols.GetTypeName(replacement)} found",
            span,
            "TypeError"
        );
    }

    /// <summary>
    /// `_parse_template`'s expansion: `\1` and `\g<1>` name a group, `\n` and its like a
    /// character, and anything else escaped is refused in Python's words.
    /// </summary>
    private static string Expand(string template, MatchState state, TextSpan span)
    {
        var output = new StringBuilder(template.Length);
        var position = 0;
        while (position < template.Length)
        {
            var character = template[position];
            if (character != '\\')
            {
                output.Append(character);
                position++;
                continue;
            }
            var start = position;
            position++;
            if (position >= template.Length)
            {
                throw TemplateFault("bad escape (end of pattern)", start, template);
            }
            character = template[position];
            if (char.IsAsciiDigit(character))
            {
                var number = character - '0';
                var end = position + 1;
                if (end < template.Length && char.IsAsciiDigit(template[end]))
                {
                    number = number * 10 + (template[end] - '0');
                    end++;
                    if (
                        character is >= '0' and <= '7'
                        && template[position + 1] is >= '0' and <= '7'
                        && end < template.Length
                        && template[end] is >= '0' and <= '7'
                    )
                    {
                        // Three octal digits spell a character, which is the one escape a
                        // replacement reads as something other than a group.
                        WriteOctal(
                            output,
                            template,
                            start,
                            position,
                            (character - '0') * 64
                                + (template[position + 1] - '0') * 8
                                + (template[end] - '0')
                        );
                        position = end + 1;
                        continue;
                    }
                }
                if (character == '0')
                {
                    WriteOctal(output, template, start, position, number);
                    position = end;
                    continue;
                }
                if (number > state.Program.GroupCount)
                {
                    throw TemplateFault($"invalid group reference {number}", start + 1, template);
                }
                position = end;
                Append(output, state, number);
                continue;
            }
            if (character == 'g')
            {
                position++;
                if (position >= template.Length || template[position] != '<')
                {
                    throw TemplateFault("missing <", position, template);
                }
                position++;
                var nameStart = position;
                while (position < template.Length && template[position] != '>')
                {
                    position++;
                }
                if (position >= template.Length)
                {
                    throw position == nameStart
                        ? TemplateFault("missing group name", nameStart, template)
                        : TemplateFault("missing >, unterminated name", nameStart, template);
                }
                var name = template[nameStart..position];
                if (name.Length == 0)
                {
                    throw TemplateFault("missing group name", nameStart, template);
                }
                position++;
                var group = 0;
                if (AllDigits(name))
                {
                    group = int.Parse(name, System.Globalization.CultureInfo.InvariantCulture);
                    if (group > state.Program.GroupCount)
                    {
                        throw TemplateFault(
                            $"invalid group reference {group}",
                            nameStart,
                            template
                        );
                    }
                }
                else
                {
                    var found = StateOf(state.Pattern).NumberOf(name);
                    if (found is null)
                    {
                        throw new PythonRaisedException(
                            new PythonExceptionValue("IndexError", $"unknown group name '{name}'")
                            {
                                Arguments = [new PythonTextValue($"unknown group name '{name}'")],
                            }
                        );
                    }
                    group = found.Value;
                }
                Append(output, state, group);
                continue;
            }
            position++;
            switch (character)
            {
                case 'n':
                    output.Append('\n');
                    continue;
                case 't':
                    output.Append('\t');
                    continue;
                case 'r':
                    output.Append('\r');
                    continue;
                case 'f':
                    output.Append('\f');
                    continue;
                case 'v':
                    output.Append('\v');
                    continue;
                case 'a':
                    output.Append('\a');
                    continue;
                case 'b':
                    output.Append('\b');
                    continue;
                case '\\':
                    output.Append('\\');
                    continue;
            }
            if (char.IsAsciiLetter(character))
            {
                throw TemplateFault($"bad escape \\{character}", start, template);
            }
            output.Append(character);
        }
        return output.ToString();
    }

    /// <summary>An octal escape of a template, which keeps only its lowest byte.</summary>
    private static void WriteOctal(
        StringBuilder output,
        string template,
        int start,
        int digitsStart,
        int value
    )
    {
        if (value > 0xFF)
        {
            throw TemplateFault(
                $"octal escape value \\{template[(start + 1)..(digitsStart + 3)]} outside of range 0-0o377",
                start,
                template
            );
        }
        output.Append((char)(value & 0xFF));
    }

    private static void Append(StringBuilder output, MatchState state, int group) =>
        output.Append(
            GroupText(state, group) is PythonTextValue matched ? matched.Value : string.Empty
        );

    /// <summary>Whether a name is a run of ASCII digits, which is what `\g<2>` is.</summary>
    private static bool AllDigits(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);

    /// <summary>The number a run of digits spells, and where the run ended.</summary>
    private static (int Group, int End) ReadGroupNumber(string text, int position, int limit)
    {
        var group = 0;
        var digits = 0;
        while (position < text.Length && digits < limit && char.IsAsciiDigit(text[position]))
        {
            group = group * 10 + (text[position] - '0');
            position++;
            digits++;
        }
        return (group, position);
    }

    // -----------------------------------------------------------------------------
    // Errors
    // -----------------------------------------------------------------------------

    /// <summary>`re.PatternError`, the exception `re.error` names.</summary>
    private static PythonManagedTypeValue CreateErrorType()
    {
        var exceptionBase = PythonBuiltinTypes.GetExceptionType("Exception");
        var type = new PythonManagedTypeValue("PatternError", exceptionBaseName: "Exception")
        {
            Module = Module,
            LayoutBase = exceptionBase,
        };
        type.Attributes["__module__"] = new PythonTextValue(Module);
        type.Attributes["__qualname__"] = new PythonTextValue("PatternError");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Exception raised for invalid regular expressions.\n\nAttributes:\n\n"
                + "    msg: The unformatted error message\n"
                + "    pattern: The regular expression pattern\n"
                + "    pos: The index in the pattern where compilation failed (may be None)\n"
                + "    lineno: The line corresponding to pos (may be None)\n"
                + "    colno: The column corresponding to pos (may be None)\n"
        );
        type.Attributes["__init__"] = Method(
            "PatternError",
            "__init__",
            Initialize,
            parameters: ["msg", "pattern", "pos"],
            defaults: [null, PythonNoneValue.Instance, PythonNoneValue.Instance]
        );
        type.SetDeclaredBases(new PythonTupleValue([exceptionBase]));
        type.SetResolutionOrder(
            new PythonTupleValue([
                type,
                exceptionBase,
                PythonBuiltinTypes.GetExceptionType("BaseException"),
                PythonBuiltinFunctions.Object,
            ])
        );
        return type;
    }

    /// <summary>
    /// `PatternError.__init__`: the message, the pattern and the position, composed the
    /// way CPython's own class composes them — the line and column a newline adds go
    /// into the message its `args` carries.
    /// </summary>
    private static PythonValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count == 0 || rest[0] is not PythonTextValue message)
        {
            throw Fault("PatternError() missing 1 required argument: 'msg'", default, "TypeError");
        }
        Apply(
            self,
            message.Value,
            rest.Count > 1 ? rest[1] : PythonNoneValue.Instance,
            rest.Count > 2 ? rest[2] : PythonNoneValue.Instance
        );
        return PythonNoneValue.Instance;
    }

    private static void Apply(
        PythonValue self,
        string message,
        PythonValue pattern,
        PythonValue pos
    )
    {
        var composed = message;
        PythonValue lineNo = PythonNoneValue.Instance;
        PythonValue columnNo = PythonNoneValue.Instance;
        if (pattern is PythonTextValue patternText && pos is PythonWholeNumberValue position)
        {
            var value = (long)position.Value;
            composed = $"{message} at position {value}";
            var line = 1;
            var lastNewline = -1;
            for (long index = 0; index < Math.Min(value, patternText.Value.Length); index++)
            {
                if (patternText.Value[(int)index] == '\n')
                {
                    line++;
                    lastNewline = (int)index;
                }
            }
            var column = value - lastNewline;
            lineNo = Integer(line);
            columnNo = Integer(column);
            if (patternText.Value.Contains('\n', StringComparison.Ordinal))
            {
                composed = $"{composed} (line {line}, column {column})";
            }
        }
        ManagedObjectProtocols.SetAttribute(self, "msg", new PythonTextValue(message), default);
        ManagedObjectProtocols.SetAttribute(self, "pattern", pattern, default);
        ManagedObjectProtocols.SetAttribute(self, "pos", pos, default);
        ManagedObjectProtocols.SetAttribute(self, "lineno", lineNo, default);
        ManagedObjectProtocols.SetAttribute(self, "colno", columnNo, default);
        ManagedObjectProtocols.SetAttribute(
            self,
            "args",
            new PythonTupleValue([new PythonTextValue(composed)]),
            default
        );
    }

    /// <summary>
    /// The fault a replacement template raises: the template takes the pattern's place in
    /// the error, which is where the position the message names is counted.
    /// </summary>
    private static PythonRaisedException TemplateFault(string message, int pos, string template) =>
        ErrorFault(message, template, pos, default);

    /// <summary>
    /// The fault a pattern that did not compile raises. An error with no position — the
    /// look-behind of no fixed width, which Python raises after the parse — carries no
    /// pattern either.
    /// </summary>
    private static PythonRaisedException ParseFault(PythonRegexParseError error, string pattern) =>
        ErrorFault(error.Message, error.Position < 0 ? null : pattern, error.Position, default);

    private static PythonRaisedException ErrorFault(
        string message,
        string? pattern,
        int pos,
        TextSpan span
    )
    {
        var value = new PythonExceptionValue("PatternError", string.Empty)
        {
            ManagedType = ErrorType,
        };
        Apply(
            value,
            message,
            pattern is null ? PythonNoneValue.Instance : new PythonTextValue(pattern),
            pos < 0 ? PythonNoneValue.Instance : Integer(pos)
        );
        return new PythonRaisedException(value);
    }

    private static PythonRuntimeException Fault(string message, TextSpan span, string type) =>
        ManagedObjectProtocols.Fault("DPY4051", message, span, type);

    // -----------------------------------------------------------------------------
    // Values and members
    // -----------------------------------------------------------------------------

    /// <summary>
    /// A method of one of the module's types, called the way Python reaches it: with the
    /// receiver the call bound, and with the keywords the call named.
    /// </summary>
    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body,
        string[]? parameters = null,
        PythonValue?[]? defaults = null,
        int positionalOnly = 0,
        bool variable = false
    )
    {
        if (variable || parameters is null)
        {
            // `group` takes any number of positional arguments, and a method declared
            // without parameters takes no keywords.
            return PythonUserTypes.Method(owner, name, body);
        }
        var declared = defaults ?? new PythonValue?[parameters.Length];
        var required = 0;
        while (required < declared.Length && declared[required] is null)
        {
            required++;
        }
        var guarded = (PythonValue? receiver, IReadOnlyList<PythonValue> arguments) =>
            body(receiver, Check(name, parameters, required, arguments, default));
        return PythonUserTypes.Method(
            owner,
            name,
            guarded,
            null,
            parameters,
            declared,
            positionalOnly
        );
    }

    /// <summary>
    /// A member a program reads but cannot write. CPython's descriptors split into two
    /// wordings: the ones that carry a name report it, the older ones say only that the
    /// attribute is read-only.
    /// </summary>
    private static PythonPropertyValue ReadOnlyMember(
        string name,
        string owner,
        Func<PythonValue, PythonValue> get,
        bool named = false
    ) =>
        new(
            new PythonProtocolFunctionValue(name, (_, arguments) => get(arguments[0])),
            new PythonProtocolFunctionValue(
                "setter",
                (_, _) => throw NotWritable(name, owner, named)
            ),
            new PythonProtocolFunctionValue(
                "deleter",
                (_, _) => throw NotWritable(name, owner, named)
            )
        );

    private static PythonRuntimeException NotWritable(string name, string owner, bool named) =>
        Fault(
            named
                ? $"attribute '{name}' of '{owner}' objects is not writable"
                : "readonly attribute",
            default,
            "AttributeError"
        );

    /// <summary>The name an error message calls a value's type by.</summary>
    private static string DisplayName(PythonValue? value) =>
        value is PythonManagedObjectValue instance && instance.Type.Module is { } module
            ? $"{module}.{instance.Type.Name}"
            : ManagedObjectProtocols.GetTypeName(value ?? PythonNoneValue.Instance);

    private static PythonWholeNumberValue Integer(long value) =>
        PythonWholeNumberValue.Create(value);

    private static long RequireIndex(PythonValue value, TextSpan span) =>
        (long)PythonBuiltinFunctions.RequireIndex(value, span);

    private static long Index(PythonValue? value, TextSpan span) =>
        value is null or PythonNoneValue ? 0 : RequireIndex(value, span);

    /// <summary>`flags=`, which may be a flag value, a plain integer, or nothing at all.</summary>
    private static long Flags(PythonValue? value, TextSpan span)
    {
        if (value is null or PythonNoneValue)
        {
            return 0;
        }
        try
        {
            return RequireIndex(value, span);
        }
        catch (PythonRuntimeException fault) when (fault.PythonExceptionTypeName == "TypeError")
        {
            // The pattern's own flags are combined with the default ones by arithmetic,
            // which is where CPython's wording for a flags argument comes from.
            throw Fault(
                $"unsupported operand type(s) for &: '{ManagedObjectProtocols.GetTypeName(value)}' and 'int'",
                span,
                "TypeError"
            );
        }
    }
}
