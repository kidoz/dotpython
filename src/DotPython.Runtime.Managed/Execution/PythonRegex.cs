using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotPython.Compiler;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>The flags a pattern is compiled with, as the bits `re` publishes them.</summary>
[Flags]
internal enum PythonRegexFlags
{
    None = 0,
    IgnoreCase = 1 << 1,
    Locale = 1 << 2,
    Multiline = 1 << 3,
    DotAll = 1 << 4,
    Unicode = 1 << 5,
    Verbose = 1 << 6,
    Debug = 1 << 7,
    Ascii = 1 << 8,
}

/// <summary>
/// A pattern that does not compile, with where and in what it failed. A position of -1 is
/// Python's error with no position at all, which is what a look-behind of no fixed width
/// raises.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This internal exception is a parse carrier and is never exposed."
)]
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "This internal exception is translated before it crosses the managed runtime boundary."
)]
internal sealed class PythonRegexParseError(string message, int position) : Exception(message)
{
    internal int Position { get; } = position;
}

/// <summary>
/// The runtime's own regular-expression engine: a pattern is parsed into a tree and
/// matched by backtracking, leftmost-first, which is what Python's own engine does.
/// </summary>
internal static class PythonRegex
{
    /// <summary>What an unbounded width is, the way Python's own parser reports it.</summary>
    private const int MaxWidth = int.MaxValue;

    // -- the compiled pattern ----------------------------------------------------------

    internal sealed class Program
    {
        internal Program(
            string pattern,
            PythonRegexFlags flags,
            int groupCount,
            IReadOnlyList<(string Name, int Index)> groupNames,
            Node root
        )
        {
            Pattern = pattern;
            Flags = flags;
            GroupCount = groupCount;
            GroupNames = groupNames;
            Root = root;
        }

        internal string Pattern { get; }

        internal PythonRegexFlags Flags { get; }

        /// <summary>Capturing groups, group 0 not counted.</summary>
        internal int GroupCount { get; }

        /// <summary>The named groups, in the order they were opened.</summary>
        internal IReadOnlyList<(string Name, int Index)> GroupNames { get; }

        internal Node Root { get; }
    }

    /// <summary>One match: where it starts and ends, and what each group took.</summary>
    internal sealed class Result(int start, int end, int?[] starts, int?[] ends, int? lastIndex)
    {
        internal int Start { get; } = start;

        internal int End { get; } = end;

        internal int? StartOf(int group) => starts[group];

        internal int? EndOf(int group) => ends[group];

        /// <summary>The group that closed last, which is what `lastindex` reports.</summary>
        internal int? LastIndex { get; } = lastIndex;
    }

    // -- the tree ----------------------------------------------------------------------

    internal abstract class Node;

    internal sealed class SequenceNode(List<Node> items) : Node
    {
        internal List<Node> Items { get; } = items;
    }

    internal sealed class AlternationNode(List<Node> branches) : Node
    {
        internal List<Node> Branches { get; } = branches;
    }

    internal sealed class LiteralNode(char value, PythonRegexFlags flags) : Node
    {
        internal char Value { get; } = value;

        internal PythonRegexFlags Flags { get; } = flags;
    }

    /// <summary>A character class: the ranges and the class names its characters satisfy.</summary>
    internal sealed class ClassNode(bool negated, PythonRegexFlags flags) : Node
    {
        internal bool Negated { get; } = negated;

        internal PythonRegexFlags Flags { get; } = flags;

        internal List<(char First, char Last)> Ranges { get; } = [];

        /// <summary>`\d`, `\w`, `\s` and their negations, as the letters they were written by.</summary>
        internal List<char> Predicates { get; } = [];
    }

    internal sealed class AnyCharNode(PythonRegexFlags flags) : Node
    {
        internal PythonRegexFlags Flags { get; } = flags;
    }

    /// <summary>A capturing group with the name it was given, if any.</summary>
    internal sealed class GroupNode(int index, string? name, Node body) : Node
    {
        internal int Index { get; } = index;

        internal string? Name { get; } = name;

        internal Node Body { get; } = body;
    }

    /// <summary>`\1` and `(?P=name)`: whatever the group it names took.</summary>
    internal sealed class BackrefNode(int group, PythonRegexFlags flags) : Node
    {
        internal int Group { get; } = group;

        internal PythonRegexFlags Flags { get; } = flags;
    }

    /// <summary>`(?=…)`, `(?!…)`, `(?<=…)` and `(?<!…)`: a test that takes nothing.</summary>
    internal sealed class LookNode(int width, bool ahead, bool positive, Node body) : Node
    {
        /// <summary>The width a look-behind reads before the position it stands at.</summary>
        internal int Width { get; } = width;

        internal bool Ahead { get; } = ahead;

        internal bool Positive { get; } = positive;

        internal Node Body { get; } = body;
    }

    /// <summary>`(?(id)yes|no)`: one of two branches, by whether the group matched.</summary>
    internal sealed class ConditionalNode(int group, Node? yes, Node? no) : Node
    {
        internal int Group { get; } = group;

        internal Node? Yes { get; } = yes;

        internal Node? No { get; } = no;
    }

    /// <summary>`(?>…)`: the body matched once, and never given back.</summary>
    internal sealed class AtomicNode(Node body) : Node
    {
        internal Node Body { get; } = body;
    }

    internal sealed class RepeatNode(
        Node body,
        int minimum,
        int maximum,
        bool lazy,
        bool possessive
    ) : Node
    {
        internal Node Body { get; } = body;

        internal int Minimum { get; } = minimum;

        internal int Maximum { get; } = maximum;

        internal bool Lazy { get; } = lazy;

        internal bool Possessive { get; } = possessive;
    }

    internal sealed class AnchorNode(char value, PythonRegexFlags flags) : Node
    {
        internal char Value { get; } = value;

        internal PythonRegexFlags Flags { get; } = flags;
    }

    // -- parsing -----------------------------------------------------------------------

    private sealed class Parser(string pattern, string text, PythonRegexFlags flags)
    {
        /// <summary>The pattern as read — the same text, less what `re.VERBOSE` drops.</summary>
        private readonly string _pattern = text;

        /// <summary>The pattern as written, which is what `Pattern.pattern` reports.</summary>
        private readonly string _source = pattern;

        private readonly List<(string Name, int Index)> _groupNames = [];

        /// <summary>The width of every group that has closed, by its number.</summary>
        private readonly List<(int Min, int Max)?> _groupWidths = [null];

        private int _position;
        private int _groupCount;
        private int _depth;

        /// <summary>Whether the item being parsed is the very first of the pattern.</summary>
        private bool _first = true;

        /// <summary>The group count when the look-behind being read opened, if one did.</summary>
        private int? _lookbehindGroups;

        /// <summary>
        /// The groups a conditional named and where it named them: Python checks these
        /// once the whole pattern is read, so a conditional may name a group that opens
        /// later — a backreference may not.
        /// </summary>
        private readonly List<(int Group, int Position)> _conditionalRefs = [];

        /// <summary>The flags in force where the parser stands, which a scope may change.</summary>
        private PythonRegexFlags _flags = flags;

        internal Program Parse()
        {
            var root = ParseAlternation();
            SkipVerbose();
            if (_position < _pattern.Length)
            {
                // A `)` that no group opened is the only thing that stops a branch.
                throw new PythonRegexParseError("unbalanced parenthesis", _position);
            }
            foreach (var (group, position) in _conditionalRefs)
            {
                if (group > _groupCount)
                {
                    throw new PythonRegexParseError($"invalid group reference {group}", position);
                }
            }
            return new Program(_source, _flags, _groupCount, _groupNames, root);
        }

        private Node ParseAlternation()
        {
            SkipVerbose();
            var branches = new List<Node> { ParseBranch() };
            while (Current == '|')
            {
                _position++;
                SkipVerbose();
                branches.Add(ParseBranch());
            }
            return branches.Count == 1 ? branches[0] : new AlternationNode(branches);
        }

        private Node ParseBranch()
        {
            var items = new List<Node>();
            SkipVerbose();
            while (_position < _pattern.Length && Current is not ('|' or ')'))
            {
                var first = _first && items.Count == 0;
                var piece = ParsePiece();
                _first = false;
                if (piece is not null)
                {
                    items.Add(piece);
                    SkipVerbose();
                    continue;
                }
                // A construct that contributed nothing — a comment, or a group of global
                // flags — leaves the branch as it was.
                _first = first;
                SkipVerbose();
            }
            return items.Count == 1 ? items[0] : new SequenceNode(items);
        }

        /// <summary>One piece, or none when the construct before it contributed nothing.</summary>
        private Node? ParsePiece()
        {
            var atom = ParseAtom();
            if (atom is null)
            {
                return null;
            }
            SkipVerbose();
            var quantifier = _position;
            var minimum = -1;
            var maximum = -1;
            switch (Current)
            {
                case '*':
                    minimum = 0;
                    maximum = MaxWidth;
                    _position++;
                    break;
                case '+':
                    minimum = 1;
                    maximum = MaxWidth;
                    _position++;
                    break;
                case '?':
                    minimum = 0;
                    maximum = 1;
                    _position++;
                    break;
                case '{':
                    if (!TryReadCounts(out minimum, out maximum))
                    {
                        return atom;
                    }
                    break;
                default:
                    return atom;
            }

            if (!Repeats(atom))
            {
                // An anchor, or a group that contributed nothing, has nothing to repeat.
                throw new PythonRegexParseError("nothing to repeat", quantifier);
            }

            var lazy = false;
            var possessive = false;
            if (Current == '?')
            {
                lazy = true;
                _position++;
            }
            else if (Current == '+')
            {
                possessive = true;
                _position++;
            }
            if (Current is '*' or '+' or '?' or '{')
            {
                throw new PythonRegexParseError("multiple repeat", _position);
            }
            return new RepeatNode(atom, minimum, maximum, lazy, possessive);
        }

        /// <summary>Whether a quantifier may follow this atom at all.</summary>
        private static bool Repeats(Node atom) =>
            atom switch
            {
                AnchorNode => false,
                SequenceNode sequence => sequence.Items.Count > 0,
                _ => true,
            };

        /// <summary>`{m}`, `{m,}` and `{m,n}`, or none when the braces are literal text.</summary>
        private bool TryReadCounts(out int minimum, out int maximum)
        {
            var start = _position;
            _position++;
            var first = ReadNumber();
            if (Current == '}')
            {
                _position++;
                if (first is null)
                {
                    throw new PythonRegexParseError("nothing to repeat", start);
                }
                minimum = first.Value;
                maximum = first.Value;
                return true;
            }
            if (Current != ',')
            {
                _position = start;
                minimum = 0;
                maximum = 0;
                return false;
            }
            _position++;
            var second = ReadNumber();
            if (Current != '}')
            {
                _position = start;
                minimum = 0;
                maximum = 0;
                return false;
            }
            _position++;
            minimum = first ?? 0;
            maximum = second ?? MaxWidth;
            if (minimum > maximum)
            {
                // Python points at the minimum it read.
                throw new PythonRegexParseError("min repeat greater than max repeat", start + 1);
            }
            return true;
        }

        private int? ReadNumber()
        {
            var start = _position;
            var value = 0L;
            while (Current is >= '0' and <= '9')
            {
                value = value * 10 + (Current - '0');
                _position++;
                if (value > 4294967295)
                {
                    throw new PythonRegexParseError("the repetition number is too large", start);
                }
            }
            return _position == start ? null : (int)value;
        }

        private char Current => _position < _pattern.Length ? _pattern[_position] : '\0';

        /// <summary>
        /// `re.VERBOSE` ignores unescaped whitespace and `#` comments outside a character
        /// class, which is read where the flags in force say so — a scope may turn it on
        /// or off part-way through a pattern.
        /// </summary>
        private void SkipVerbose()
        {
            if (!_flags.HasFlag(PythonRegexFlags.Verbose))
            {
                return;
            }
            while (_position < _pattern.Length)
            {
                var value = _pattern[_position];
                if (char.IsWhiteSpace(value))
                {
                    _position++;
                    continue;
                }
                if (value == '#')
                {
                    while (_position < _pattern.Length && _pattern[_position] != '\n')
                    {
                        _position++;
                    }
                    continue;
                }
                break;
            }
        }

        private Node? ParseAtom()
        {
            var value = Current;
            switch (value)
            {
                case '\0':
                    throw new PythonRegexParseError("unexpected end of pattern", _position);
                case '(':
                    return ParseGroup();
                case '[':
                    return ParseClass();
                case '.':
                    _position++;
                    return new AnyCharNode(_flags);
                case '^':
                case '$':
                    _position++;
                    return new AnchorNode(value, _flags);
                case '\\':
                    return ParseEscape();
                case '*':
                case '+':
                case '?':
                    throw new PythonRegexParseError("nothing to repeat", _position);
                case '{':
                    // A count that reads as one repeats nothing; the braces are literal
                    // text when it does not, which is what `a{,2}` is made of.
                    var brace = _position;
                    if (TryReadCounts(out _, out _))
                    {
                        throw new PythonRegexParseError("nothing to repeat", brace);
                    }
                    break;
                case ')':
                    throw new PythonRegexParseError("unbalanced parenthesis", _position);
            }
            _position++;
            return new LiteralNode(value, _flags);
        }

        /// <summary>
        /// A group: capturing with an optional name, or one of the extensions — a plain
        /// `(?:…)`, a comment, a lookaround, an atomic group, a scope of flags, or a
        /// named backreference. `null` is a group that contributed nothing.
        /// </summary>
        private Node? ParseGroup()
        {
            var start = _position;
            _position++;
            string? name = null;
            var capture = true;
            var atomic = false;
            var addFlags = PythonRegexFlags.None;
            var delFlags = PythonRegexFlags.None;
            var index = 0;
            var named = false;
            if (Current == '?')
            {
                _position++;
                if (_position >= _pattern.Length)
                {
                    throw new PythonRegexParseError("unexpected end of pattern", _position);
                }
                switch (Current)
                {
                    case ':':
                        capture = false;
                        _position++;
                        break;
                    case '#':
                        _position++;
                        while (_position < _pattern.Length && Current != ')')
                        {
                            _position++;
                        }
                        if (_position >= _pattern.Length)
                        {
                            throw new PythonRegexParseError(
                                "missing ), unterminated comment",
                                start
                            );
                        }
                        _position++;
                        return null;
                    case '=':
                        _position++;
                        return ParseLook(start, ahead: true, positive: true);
                    case '!':
                        _position++;
                        return ParseLook(start, ahead: true, positive: false);
                    case '<':
                        _position++;
                        if (_position >= _pattern.Length)
                        {
                            throw new PythonRegexParseError("unexpected end of pattern", _position);
                        }
                        switch (Current)
                        {
                            case '=':
                                _position++;
                                return ParseLook(start, ahead: false, positive: true);
                            case '!':
                                _position++;
                                return ParseLook(start, ahead: false, positive: false);
                            case char other:
                                throw new PythonRegexParseError(
                                    $"unknown extension ?<{other}",
                                    start + 1
                                );
                        }
                    case '>':
                        atomic = true;
                        capture = false;
                        _position++;
                        break;
                    case 'P':
                        _position++;
                        if (_position >= _pattern.Length)
                        {
                            throw new PythonRegexParseError("unexpected end of pattern", _position);
                        }
                        if (Current == '<')
                        {
                            _position++;
                            var nameStart = _position;
                            name = ReadUntil('>', "group name");
                            CheckGroupName(name, nameStart);
                            index = OpenGroup(name, nameStart);
                            named = true;
                            break;
                        }
                        if (Current == '=')
                        {
                            _position++;
                            var refStart = _position;
                            var referred = ReadUntil(')', "group name");
                            CheckGroupName(referred, refStart);
                            return ParseNamedBackref(start, referred, refStart);
                        }
                        throw new PythonRegexParseError(
                            $"unknown extension ?P{Current}",
                            start + 1
                        );
                    case '(':
                        _position++;
                        return ParseConditional(start);
                    case 'i' or 'm' or 's' or 'x' or 'a' or 'u' or 'L' or '-':
                        var scoped = ParseFlags(start, out addFlags, out delFlags);
                        if (!scoped)
                        {
                            // A group of global flags, which the pattern must open with.
                            if (!_first || _depth > 0)
                            {
                                throw new PythonRegexParseError(
                                    "global flags not at the start of the expression",
                                    start
                                );
                            }
                            return null;
                        }
                        capture = false;
                        break;
                    default:
                        throw new PythonRegexParseError($"unknown extension ?{Current}", start + 1);
                }
            }

            if (capture && !named)
            {
                // The group is numbered where it opens: a nested group inside its body
                // takes the next index, not this one.
                index = OpenGroup(name: null, nameStart: start);
            }

            var saved = _flags;
            _flags = (_flags | addFlags) & ~delFlags;
            _depth++;
            var body = ParseAlternation();
            _depth--;
            _flags = saved;
            ExpectClose(start);
            if (capture)
            {
                CloseGroup(index, body);
            }
            if (atomic)
            {
                return new AtomicNode(body);
            }
            return new GroupNode(index, name, body);
        }

        /// <summary>
        /// `(?(id)yes|no)` and `(?(name)yes|no)`: the branch the group's own matchedness
        /// picks. A name must already name a group; a number may name one that opens
        /// later, which Python checks when the pattern ends.
        /// </summary>
        private ConditionalNode ParseConditional(int start)
        {
            var nameStart = _position;
            var referred = ReadUntil(')', "group name");
            int group;
            if (AllAsciiDigits(referred))
            {
                group = int.Parse(referred, CultureInfo.InvariantCulture);
                if (group == 0)
                {
                    throw new PythonRegexParseError("bad group number", nameStart);
                }
                if (group is < 0 or > 2147483647)
                {
                    throw new PythonRegexParseError($"invalid group reference {group}", nameStart);
                }
                _conditionalRefs.Add((group, nameStart));
            }
            else
            {
                CheckGroupName(referred, nameStart);
                var found = 0;
                foreach (var (taken, index) in _groupNames)
                {
                    if (taken == referred)
                    {
                        found = index;
                    }
                }
                if (found == 0)
                {
                    throw new PythonRegexParseError($"unknown group name '{referred}'", nameStart);
                }
                group = found;
            }
            if (_lookbehindGroups is { } lookbehindGroups && group >= lookbehindGroups)
            {
                if (_groupWidths[group] is null)
                {
                    throw new PythonRegexParseError("cannot refer to an open group", _position);
                }
                throw new PythonRegexParseError(
                    "cannot refer to group defined in the same lookbehind subpattern",
                    _position
                );
            }

            _depth++;
            var yes = ParseBranch();
            Node? no = null;
            if (Current == '|')
            {
                _position++;
                SkipVerbose();
                no = ParseBranch();
                if (Current == '|')
                {
                    throw new PythonRegexParseError(
                        "conditional backref with more than two branches",
                        _position
                    );
                }
            }
            _depth--;
            ExpectClose(start);
            return new ConditionalNode(
                group,
                yes is SequenceNode { Items.Count: 0 } ? null : yes,
                no is SequenceNode { Items.Count: 0 } ? null : no
            );
        }

        private static bool AllAsciiDigits(string value)
        {
            foreach (var character in value)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }
            return value.Length > 0;
        }

        /// <summary>`(?=…)`, `(?!…)`, `(?&lt;=…)` and `(?&lt;!…)`: the body tests a position.</summary>
        private LookNode ParseLook(int start, bool ahead, bool positive)
        {
            var outer = _lookbehindGroups;
            if (!ahead && outer is null)
            {
                // Python counts group 0 in the record, so a group opened before the
                // look-behind stays referable and one opened inside it does not.
                _lookbehindGroups = _groupCount + 1;
            }
            _depth++;
            var body = ParseAlternation();
            _depth--;
            if (!ahead && outer is null)
            {
                _lookbehindGroups = null;
            }
            ExpectClose(start);
            var width = 0;
            if (!ahead)
            {
                // A look-behind reads a fixed width of text before the position, so the
                // body it tests has to be one width wide.
                var (minimum, maximum) = Width(body);
                if (minimum != maximum)
                {
                    throw new PythonRegexParseError("look-behind requires fixed-width pattern", -1);
                }
                width = minimum;
            }
            return new LookNode(width, ahead, positive, body);
        }

        /// <summary>
        /// `(?i)`, `(?i:…)` and their like. Answers whether the flags scope over a group;
        /// a group of global flags answers false and leaves them in force for good.
        /// </summary>
        private bool ParseFlags(int start, out PythonRegexFlags add, out PythonRegexFlags del)
        {
            add = PythonRegexFlags.None;
            del = PythonRegexFlags.None;
            var adding = Current != '-';
            while (adding)
            {
                var bit = FlagBit(Current);
                if (bit == PythonRegexFlags.Locale)
                {
                    throw new PythonRegexParseError(
                        "bad inline flags: cannot use 'L' flag with a str pattern",
                        _position + 1
                    );
                }
                add |= bit;
                var typeFlags = add & (PythonRegexFlags.Ascii | PythonRegexFlags.Unicode);
                if (
                    (bit & (PythonRegexFlags.Ascii | PythonRegexFlags.Unicode)) != 0
                    && typeFlags != bit
                )
                {
                    throw new PythonRegexParseError(
                        "bad inline flags: flags 'a', 'u' and 'L' are incompatible",
                        _position + 1
                    );
                }
                _position++;
                if (_position >= _pattern.Length)
                {
                    throw new PythonRegexParseError("missing -, : or )", _position);
                }
                if (Current is ')' or '-' or ':')
                {
                    break;
                }
                if (!IsFlagChar(Current))
                {
                    throw new PythonRegexParseError(
                        char.IsAsciiLetter(Current) ? "unknown flag" : "missing -, : or )",
                        _position
                    );
                }
            }

            if (Current == ')')
            {
                _position++;
                _flags |= add;
                return false;
            }
            if (Current == '-')
            {
                _position++;
                if (_position >= _pattern.Length)
                {
                    throw new PythonRegexParseError("missing flag", _position);
                }
                if (!IsFlagChar(Current))
                {
                    throw new PythonRegexParseError(
                        char.IsAsciiLetter(Current) ? "unknown flag" : "missing flag",
                        _position
                    );
                }
                while (true)
                {
                    var bit = FlagBit(Current);
                    if (
                        (
                            bit
                            & (
                                PythonRegexFlags.Ascii
                                | PythonRegexFlags.Unicode
                                | PythonRegexFlags.Locale
                            )
                        ) != 0
                    )
                    {
                        throw new PythonRegexParseError(
                            "bad inline flags: cannot turn off flags 'a', 'u' and 'L'",
                            _position + 1
                        );
                    }
                    del |= bit;
                    _position++;
                    if (_position >= _pattern.Length)
                    {
                        throw new PythonRegexParseError("missing :", _position);
                    }
                    var next = _pattern[_position];
                    if (next == ':')
                    {
                        break;
                    }
                    if (!IsFlagChar(next))
                    {
                        throw new PythonRegexParseError(
                            char.IsAsciiLetter(next) ? "unknown flag" : "missing :",
                            _position
                        );
                    }
                }
            }
            if (Current != ':')
            {
                throw new PythonRegexParseError("missing :", _position);
            }
            if ((add & del) != 0)
            {
                throw new PythonRegexParseError(
                    "bad inline flags: flag turned on and off",
                    _position
                );
            }
            _position++;
            return true;
        }

        private static bool IsFlagChar(char value) =>
            value is 'i' or 'm' or 's' or 'x' or 'a' or 'u' or 'L';

        private static PythonRegexFlags FlagBit(char value) =>
            value switch
            {
                'i' => PythonRegexFlags.IgnoreCase,
                'm' => PythonRegexFlags.Multiline,
                's' => PythonRegexFlags.DotAll,
                'x' => PythonRegexFlags.Verbose,
                'a' => PythonRegexFlags.Ascii,
                'u' => PythonRegexFlags.Unicode,
                _ => PythonRegexFlags.Locale,
            };

        /// <summary>Opens a capturing group, refusing a name another group already took.</summary>
        private int OpenGroup(string? name, int nameStart)
        {
            _groupCount++;
            _groupWidths.Add(null);
            if (name is null)
            {
                return _groupCount;
            }
            foreach (var (taken, index) in _groupNames)
            {
                if (taken == name)
                {
                    throw new PythonRegexParseError(
                        $"redefinition of group name '{name}' as group {_groupCount}; was group {index}",
                        nameStart
                    );
                }
            }
            _groupNames.Add((name, _groupCount));
            return _groupCount;
        }

        /// <summary>
        /// Closes a group, recording the width of what it matched: a reference to it from
        /// a look-behind is measured by it, which is why Python keeps the same record.
        /// </summary>
        private void CloseGroup(int index, Node body) => _groupWidths[index] = Width(body);

        /// <summary>A name Python would accept for a group, or Python's own refusal.</summary>
        private static void CheckGroupName(string name, int nameStart)
        {
            if (!PythonIdentifier.IsIdentifier(name))
            {
                throw new PythonRegexParseError($"bad character in group name '{name}'", nameStart);
            }
        }

        private BackrefNode ParseNamedBackref(int start, string name, int nameStart)
        {
            foreach (var (taken, index) in _groupNames)
            {
                if (taken == name)
                {
                    return Referenced(index, start);
                }
            }
            throw new PythonRegexParseError($"unknown group name '{name}'", nameStart);
        }

        /// <summary>A reference to a group that is already closed, or Python's refusal.</summary>
        private BackrefNode Referenced(int group, int start)
        {
            if (_groupWidths[group] is null)
            {
                throw new PythonRegexParseError("cannot refer to an open group", start);
            }
            if (_lookbehindGroups is { } lookbehindGroups && group >= lookbehindGroups)
            {
                throw new PythonRegexParseError(
                    "cannot refer to group defined in the same lookbehind subpattern",
                    _position
                );
            }
            return new BackrefNode(group, _flags);
        }

        /// <summary>Reads up to <paramref name="terminator"/>, the way Python's own reader does.</summary>
        private string ReadUntil(char terminator, string what)
        {
            var result = new StringBuilder();
            while (true)
            {
                if (_position >= _pattern.Length)
                {
                    throw result.Length == 0
                        ? new PythonRegexParseError($"missing {what}", _position)
                        : new PythonRegexParseError(
                            $"missing {terminator}, unterminated name",
                            _position - result.Length
                        );
                }
                var value = _pattern[_position];
                _position++;
                if (value == terminator)
                {
                    if (result.Length == 0)
                    {
                        throw new PythonRegexParseError($"missing {what}", _position - 1);
                    }
                    return result.ToString();
                }
                result.Append(value);
            }
        }

        private ClassNode ParseClass()
        {
            var start = _position;
            _position++;
            var negated = false;
            if (Current == '^')
            {
                negated = true;
                _position++;
            }
            var node = new ClassNode(negated, _flags);
            if (Current == ']')
            {
                // A `]` first in a class stands for itself.
                node.Ranges.Add((']', ']'));
                _position++;
            }
            while (_position < _pattern.Length && Current != ']')
            {
                var itemStart = _position;
                if (Current == '\\' && _position + 1 < _pattern.Length)
                {
                    var escape = _pattern[_position + 1];
                    if (escape is 'd' or 'D' or 'w' or 'W' or 's' or 'S')
                    {
                        node.Predicates.Add(escape);
                        _position += 2;
                        continue;
                    }
                }

                var first = ReadClassCharacter();
                if (
                    Current == '-'
                    && _position + 1 < _pattern.Length
                    && _pattern[_position + 1] != ']'
                )
                {
                    _position++;
                    var last = ReadClassCharacter();
                    if (first > last)
                    {
                        throw new PythonRegexParseError(
                            $"bad character range {first}-{last}",
                            itemStart
                        );
                    }
                    node.Ranges.Add((first, last));
                    continue;
                }
                node.Ranges.Add((first, first));
            }
            if (_position >= _pattern.Length)
            {
                throw new PythonRegexParseError("unterminated character set", start);
            }
            _position++;
            return node;
        }

        /// <summary>One character of a class, an escape read as the character it stands for.</summary>
        private char ReadClassCharacter()
        {
            var value = _pattern[_position];
            if (value == '\\')
            {
                return ReadEscapeCharacter(inClass: true);
            }
            _position++;
            return value;
        }

        private Node ParseEscape()
        {
            var start = _position;
            var value = ReadEscapeCharacter(inClass: false);
            switch (value)
            {
                case 'd' or 'D' or 'w' or 'W' or 's' or 'S':
                {
                    var node = new ClassNode(negated: false, _flags);
                    node.Predicates.Add(value);
                    return node;
                }
                case 'b' or 'B' or 'A' or 'Z' or 'z':
                    return new AnchorNode(value, _flags);
                case >= '1' and <= '9':
                    return ReadGroupReference(start, value);
            }
            return new LiteralNode(value, _flags);
        }

        /// <summary>
        /// `\1` and its like: a group reference, or — when three octal digits follow —
        /// the character they spell, which is the one ambiguity Python reads this way.
        /// </summary>
        private Node ReadGroupReference(int start, char first)
        {
            var digits = 1;
            var second = 'x';
            if (Current is >= '0' and <= '9')
            {
                digits = 2;
                second = Current;
                _position++;
                if (
                    first is >= '0' and <= '7'
                    && second is >= '0' and <= '7'
                    && Current is >= '0' and <= '7'
                )
                {
                    var third = Current;
                    _position++;
                    var value = (first - '0') * 64 + (second - '0') * 8 + (third - '0');
                    if (value > 0xFF)
                    {
                        throw new PythonRegexParseError(
                            $"octal escape value \\{first}{second}{third} outside of range 0-0o377",
                            start
                        );
                    }
                    return new LiteralNode((char)value, _flags);
                }
            }
            var group = digits == 2 ? (first - '0') * 10 + (second - '0') : first - '0';
            if (group > _groupCount)
            {
                throw new PythonRegexParseError($"invalid group reference {group}", start + 1);
            }
            return Referenced(group, start);
        }

        /// <summary>
        /// The character an escape stands for, or the class name, boundary letter or digit
        /// the caller reads for itself. An escape Python does not know is refused in its words.
        /// </summary>
        private char ReadEscapeCharacter(bool inClass)
        {
            var start = _position;
            _position++;
            if (_position >= _pattern.Length)
            {
                throw new PythonRegexParseError("bad escape (end of pattern)", start);
            }
            var value = _pattern[_position];
            _position++;
            switch (value)
            {
                case 'n':
                    return '\n';
                case 't':
                    return '\t';
                case 'r':
                    return '\r';
                case 'f':
                    return '\f';
                case 'v':
                    return '\v';
                case 'a':
                    return '\a';
                case 'b':
                    // A boundary outside a character class, a backspace inside one.
                    return inClass ? '\b' : 'b';
                case 'x':
                    return ReadHex(2, start);
                case 'u':
                    return ReadHex(4, start);
                case 'U':
                    return ReadHex(8, start);
                case 'N':
                    return ReadCharacterName(start);
            }

            if (value is >= '0' and <= '9')
            {
                // Outside a class a digit names a group; inside one the digits that are
                // octal read as the character they spell, and 8 and 9 are no escape at all.
                if (value == '0' || inClass)
                {
                    if (value <= '7')
                    {
                        return ReadOctal(value);
                    }
                    throw new PythonRegexParseError($"bad escape \\{value}", start);
                }
                return value;
            }
            if (value is 'd' or 'D' or 'w' or 'W' or 's' or 'S')
            {
                // A class name, read by the caller in both contexts.
                return value;
            }
            if (!inClass && value is 'B' or 'A' or 'Z' or 'z')
            {
                return value;
            }
            if (char.IsAsciiLetter(value))
            {
                throw new PythonRegexParseError($"bad escape \\{value}", start);
            }
            return value;
        }

        /// <summary>
        /// `\N{NAME}`: the character the name spells, read through the table a `\N{…}` in
        /// a string literal reads, in Python's own words where it does not.
        /// </summary>
        private char ReadCharacterName(int start)
        {
            if (Current != '{')
            {
                throw new PythonRegexParseError("missing {", _position);
            }
            _position++;
            var nameStart = _position;
            while (_position < _pattern.Length && _pattern[_position] != '}')
            {
                _position++;
            }
            if (_position >= _pattern.Length)
            {
                throw _position == nameStart
                    ? new PythonRegexParseError("missing character name", _position)
                    : new PythonRegexParseError("missing }, unterminated name", nameStart);
            }
            var name = _pattern[nameStart.._position];
            _position++;
            if (name.Length == 0)
            {
                throw new PythonRegexParseError("missing character name", nameStart);
            }
            if (!PythonUnicodeNameIndex.TryGetCodePoint(name, out var codePoint))
            {
                throw new PythonRegexParseError($"undefined character name '{name}'", start);
            }
            return (char)codePoint;
        }

        private char ReadOctal(char first)
        {
            var value = first - '0';
            var digits = 1;
            while (digits < 3 && Current is >= '0' and <= '7')
            {
                value = value * 8 + (Current - '0');
                _position++;
                digits++;
            }
            return (char)value;
        }

        private char ReadHex(int digits, int start)
        {
            var value = 0;
            for (var index = 0; index < digits; index++)
            {
                if (!char.IsAsciiHexDigit(Current))
                {
                    throw new PythonRegexParseError(
                        $"incomplete escape \\{_pattern[(start + 1).._position]}",
                        start
                    );
                }
                value =
                    value * 16
                    + (
                        char.IsAsciiDigit(Current)
                            ? Current - '0'
                            : char.ToLowerInvariant(Current) - 'a' + 10
                    );
                _position++;
            }
            return (char)value;
        }

        private void ExpectClose(int start)
        {
            SkipVerbose();
            if (_position >= _pattern.Length || _pattern[_position] != ')')
            {
                throw new PythonRegexParseError("missing ), unterminated subpattern", start);
            }
            _position++;
            _first = false;
        }

        /// <summary>
        /// The width a pattern piece matches, as the range Python's own parser computes:
        /// a repeat of an unbounded count has no upper bound.
        /// </summary>
        private (int Min, int Max) Width(Node node)
        {
            switch (node)
            {
                case SequenceNode sequence:
                {
                    var min = 0;
                    var max = 0;
                    foreach (var item in sequence.Items)
                    {
                        var (itemMin, itemMax) = Width(item);
                        min += itemMin;
                        max = max == MaxWidth || itemMax == MaxWidth ? MaxWidth : max + itemMax;
                    }
                    return (min, max);
                }
                case AlternationNode alternation:
                {
                    var min = MaxWidth;
                    var max = 0;
                    foreach (var branch in alternation.Branches)
                    {
                        var (branchMin, branchMax) = Width(branch);
                        min = Math.Min(min, branchMin);
                        max = Math.Max(max, branchMax);
                    }
                    return (alternation.Branches.Count == 0 ? 0 : min, max);
                }
                case GroupNode group:
                    return Width(group.Body);
                case AtomicNode atomic:
                    return Width(atomic.Body);
                case LookNode:
                case AnchorNode:
                    return (0, 0);
                case ConditionalNode conditional:
                {
                    var yes = conditional.Yes is null ? (0, 0) : Width(conditional.Yes);
                    var no = conditional.No is null ? (0, 0) : Width(conditional.No);
                    return (Math.Min(yes.Item1, no.Item1), Math.Max(yes.Item2, no.Item2));
                }
                case RepeatNode repeat:
                {
                    var (itemMin, itemMax) = Width(repeat.Body);
                    var min = itemMin * repeat.Minimum;
                    var max =
                        repeat.Maximum == MaxWidth || itemMax == MaxWidth
                            ? (itemMax == 0 ? 0 : MaxWidth)
                            : itemMax * repeat.Maximum;
                    return (min, max);
                }
                case BackrefNode backref:
                    return _groupWidths[backref.Group] ?? (0, MaxWidth);
                default:
                    return (1, 1);
            }
        }
    }

    /// <summary>Parses a pattern with the flags it was given.</summary>
    /// <exception cref="PythonRegexParseError">The pattern is not one this engine reads.</exception>
    internal static Program Compile(string pattern, PythonRegexFlags flags) =>
        new Parser(pattern, pattern, flags).Parse();

    // -- matching ----------------------------------------------------------------------

    private sealed class State
    {
        internal State(string text, int start, int end, int groups)
        {
            Text = text;
            BoundsStart = start;
            BoundsEnd = end;
            Starts = new int?[groups + 1];
            Ends = new int?[groups + 1];
        }

        internal string Text { get; }

        internal int BoundsStart { get; }

        internal int BoundsEnd { get; }

        internal int?[] Starts { get; }

        internal int?[] Ends { get; }

        internal int? LastIndex { get; set; }
    }

    private delegate bool Continuation(State state, int position);

    /// <summary>The leftmost match at or after `start`, or none.</summary>
    internal static Result? Search(Program program, string text, int start, int? end)
    {
        var bound = Math.Min(end ?? text.Length, text.Length);
        for (var position = Math.Min(start, bound); position <= bound; position++)
        {
            var result = MatchAt(program, text, position, bound, requireEnd: false);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    /// <summary>The match anchored where the search starts, or none.</summary>
    internal static Result? Match(Program program, string text, int start, int? end) =>
        MatchAt(program, text, start, Math.Min(end ?? text.Length, text.Length), requireEnd: false);

    /// <summary>
    /// The match that starts where the search starts and ends where it ends, or none:
    /// what `fullmatch` asks for. The end requirement is the match's own finishing test,
    /// so the pattern backtracks into a different alternative to satisfy it.
    /// </summary>
    internal static Result? FullMatch(Program program, string text, int start, int? end) =>
        MatchAt(program, text, start, Math.Min(end ?? text.Length, text.Length), requireEnd: true);

    private static Result? MatchAt(
        Program program,
        string text,
        int position,
        int bound,
        bool requireEnd
    )
    {
        var state = new State(text, position, bound, program.GroupCount);
        var end = -1;
        var matched = Match(
            program.Root,
            state,
            position,
            (_, final) =>
            {
                if (requireEnd && final != bound)
                {
                    return false;
                }
                end = final;
                return true;
            }
        );
        if (!matched)
        {
            return null;
        }
        state.Starts[0] = position;
        state.Ends[0] = end;
        return new Result(position, end, state.Starts, state.Ends, state.LastIndex);
    }

    private static bool Match(Node node, State state, int position, Continuation next) =>
        node switch
        {
            SequenceNode sequence => MatchSequence(sequence, 0, state, position, next),
            AlternationNode alternation => MatchAlternation(alternation, state, position, next),
            LiteralNode literal => position < state.BoundsEnd
                && EqualTo(literal.Value, state.Text[position], literal.Flags)
                && next(state, position + 1),
            AnyCharNode any => position < state.BoundsEnd
                && (state.Text[position] != '\n' || any.Flags.HasFlag(PythonRegexFlags.DotAll))
                && next(state, position + 1),
            ClassNode characterClass => position < state.BoundsEnd
                && InClass(characterClass, state.Text[position])
                && next(state, position + 1),
            GroupNode group => MatchGroup(group, state, position, next),
            BackrefNode backref => MatchBackref(backref, state, position, next),
            LookNode look => MatchLook(look, state, position, next),
            ConditionalNode conditional => MatchConditional(conditional, state, position, next),
            AtomicNode atomic => MatchAtomic(atomic, state, position, next),
            RepeatNode repeat => MatchRepeat(repeat, state, position, next, count: 0),
            AnchorNode anchor => AtAnchor(anchor, state, position) && next(state, position),
            _ => next(state, position),
        };

    private static bool MatchAlternation(
        AlternationNode alternation,
        State state,
        int position,
        Continuation next
    )
    {
        foreach (var branch in alternation.Branches)
        {
            var starts = (int?[])state.Starts.Clone();
            var ends = (int?[])state.Ends.Clone();
            var lastIndex = state.LastIndex;
            if (Match(branch, state, position, next))
            {
                return true;
            }
            Array.Copy(starts, state.Starts, starts.Length);
            Array.Copy(ends, state.Ends, ends.Length);
            state.LastIndex = lastIndex;
        }
        return false;
    }

    private static bool MatchSequence(
        SequenceNode sequence,
        int index,
        State state,
        int position,
        Continuation next
    ) =>
        index == sequence.Items.Count
            ? next(state, position)
            : Match(
                sequence.Items[index],
                state,
                position,
                (inner, innerPosition) =>
                    MatchSequence(sequence, index + 1, inner, innerPosition, next)
            );

    private static bool MatchGroup(GroupNode group, State state, int position, Continuation next)
    {
        if (group.Index == 0)
        {
            return Match(group.Body, state, position, next);
        }
        var savedStart = state.Starts[group.Index];
        var savedEnd = state.Ends[group.Index];
        var savedLastIndex = state.LastIndex;
        var matched = Match(
            group.Body,
            state,
            position,
            (inner, innerPosition) =>
            {
                var previousStart = inner.Starts[group.Index];
                var previousEnd = inner.Ends[group.Index];
                var previousLastIndex = inner.LastIndex;
                inner.Starts[group.Index] = position;
                inner.Ends[group.Index] = innerPosition;
                inner.LastIndex = group.Index;
                if (next(inner, innerPosition))
                {
                    return true;
                }
                inner.Starts[group.Index] = previousStart;
                inner.Ends[group.Index] = previousEnd;
                inner.LastIndex = previousLastIndex;
                return false;
            }
        );
        if (!matched)
        {
            state.Starts[group.Index] = savedStart;
            state.Ends[group.Index] = savedEnd;
            state.LastIndex = savedLastIndex;
        }
        return matched;
    }

    /// <summary>`\1`: the text the group took, matched again where the pattern stands.</summary>
    private static bool MatchBackref(
        BackrefNode backref,
        State state,
        int position,
        Continuation next
    )
    {
        if (
            state.Starts[backref.Group] is not { } start
            || state.Ends[backref.Group] is not { } end
        )
        {
            // A group that never matched gives nothing back to refer to.
            return false;
        }
        var length = end - start;
        if (position + length > state.BoundsEnd)
        {
            return false;
        }
        for (var index = 0; index < length; index++)
        {
            if (!EqualTo(state.Text[start + index], state.Text[position + index], backref.Flags))
            {
                return false;
            }
        }
        return next(state, position + length);
    }

    /// <summary>
    /// A lookaround: the body is tried at the position the assertion stands at, and what
    /// it captured stands for the rest of the pattern. A negative one gives nothing back.
    /// </summary>
    private static bool MatchLook(LookNode look, State state, int position, Continuation next)
    {
        var starts = (int?[])state.Starts.Clone();
        var ends = (int?[])state.Ends.Clone();
        var lastIndex = state.LastIndex;
        var found = false;
        if (look.Ahead)
        {
            found = Match(
                look.Body,
                state,
                position,
                (_, _) =>
                {
                    found = true;
                    return true;
                }
            );
        }
        else
        {
            var start = position - look.Width;
            found =
                start >= 0
                && Match(
                    look.Body,
                    state,
                    start,
                    (_, end) =>
                    {
                        found = end == position;
                        return found;
                    }
                );
        }
        if (look.Positive)
        {
            if (found)
            {
                // What a lookahead captured stands for the rest of the pattern.
                return next(state, position);
            }
            Array.Copy(starts, state.Starts, starts.Length);
            Array.Copy(ends, state.Ends, ends.Length);
            state.LastIndex = lastIndex;
            return false;
        }
        // A negative look gives nothing back, whether its body matched or not.
        Array.Copy(starts, state.Starts, starts.Length);
        Array.Copy(ends, state.Ends, ends.Length);
        state.LastIndex = lastIndex;
        return !found && next(state, position);
    }

    /// <summary>`(?(id)yes|no)`: the branch the group's own matchedness picks.</summary>
    private static bool MatchConditional(
        ConditionalNode conditional,
        State state,
        int position,
        Continuation next
    )
    {
        var branch = state.Starts[conditional.Group] is null ? conditional.No : conditional.Yes;
        // A branch that was not written stands for nothing at all.
        return branch is null ? next(state, position) : Match(branch, state, position, next);
    }

    /// <summary>`(?>…)`: the body is matched once, and the rest runs from where it ended.</summary>
    private static bool MatchAtomic(AtomicNode atomic, State state, int position, Continuation next)
    {
        var end = -1;
        if (
            !Match(
                atomic.Body,
                state,
                position,
                (_, innerEnd) =>
                {
                    end = innerEnd;
                    return true;
                }
            )
        )
        {
            return false;
        }
        return next(state, end);
    }

    private static bool MatchRepeat(
        RepeatNode repeat,
        State state,
        int position,
        Continuation next,
        int count
    )
    {
        if (repeat.Possessive)
        {
            // A possessive repeat takes the longest run it can and never gives a step
            // back, so the rest of the pattern starts after it.
            var current = position;
            var taken = 0;
            while (taken < repeat.Maximum)
            {
                var advanced = -1;
                if (
                    !Match(
                        repeat.Body,
                        state,
                        current,
                        (inner, innerPosition) =>
                        {
                            if (innerPosition == current)
                            {
                                return false;
                            }
                            advanced = innerPosition;
                            return true;
                        }
                    )
                )
                {
                    break;
                }
                current = advanced;
                taken++;
            }
            return taken >= repeat.Minimum && next(state, current);
        }

        if (count < repeat.Minimum)
        {
            // A step the quantifier requires runs before the rest of the pattern does,
            // and may take nothing — the requirement is the step itself.
            return Match(
                repeat.Body,
                state,
                position,
                (inner, innerPosition) => MatchRepeat(repeat, inner, innerPosition, next, count + 1)
            );
        }
        if (count >= repeat.Maximum)
        {
            return next(state, position);
        }
        // A further step must take something: a body that matched nothing would repeat
        // forever, so the repetition ends here instead.
        return repeat.Lazy
            ? next(state, position)
                || Match(repeat.Body, state, position, Take(repeat, position, next, count))
            : Match(repeat.Body, state, position, Take(repeat, position, next, count))
                || next(state, position);
    }

    /// <summary>The continuation one more iteration runs: it must advance, then repeat.</summary>
    private static Continuation Take(
        RepeatNode repeat,
        int position,
        Continuation next,
        int count
    ) =>
        (inner, innerPosition) =>
            innerPosition != position && MatchRepeat(repeat, inner, innerPosition, next, count + 1);

    /// <summary>
    /// The anchors, as `sre` reads them: `^` and `\A` are the start of the <em>string</em>
    /// (`^` also at a line start under MULTILINE, `pos` never counting as one), and `$`
    /// and `\Z` are the end of the searched region, `$` also just before a final newline.
    /// </summary>
    private static bool AtAnchor(AnchorNode anchor, State state, int position)
    {
        var text = state.Text;
        var multiline = anchor.Flags.HasFlag(PythonRegexFlags.Multiline);
        switch (anchor.Value)
        {
            case '^':
                return position == 0 || (multiline && position > 0 && text[position - 1] == '\n');
            case '$':
                return position == state.BoundsEnd
                    || (position == state.BoundsEnd - 1 && text[position] == '\n')
                    || (multiline && position < state.BoundsEnd && text[position] == '\n');
            case 'A':
                return position == 0;
            case 'Z':
            case 'z':
                return position == state.BoundsEnd;
            case 'b':
                return IsWord(text, position - 1, anchor.Flags)
                    != IsWord(text, position, anchor.Flags);
            default:
                return IsWord(text, position - 1, anchor.Flags)
                    == IsWord(text, position, anchor.Flags);
        }
    }

    private static bool IsWord(string text, int position, PythonRegexFlags flags) =>
        position >= 0 && position < text.Length && IsWordCharacter(text[position], flags);

    private static bool InClass(ClassNode node, char value)
    {
        var inside = false;
        foreach (var (first, last) in node.Ranges)
        {
            if (InRange(first, last, value, node.Flags))
            {
                inside = true;
                break;
            }
        }
        if (!inside)
        {
            foreach (var predicate in node.Predicates)
            {
                if (InPredicate(predicate, value, node.Flags))
                {
                    inside = true;
                    break;
                }
            }
        }
        return node.Negated ? !inside : inside;
    }

    private static bool InPredicate(char predicate, char value, PythonRegexFlags flags)
    {
        var matched = char.ToLowerInvariant(predicate) switch
        {
            'd' => char.IsAsciiDigit(value) || (!AsciiOnly(flags) && char.IsDigit(value)),
            'w' => IsWordCharacter(value, flags),
            _ => IsSpace(value, flags),
        };
        return IsAsciiUpper(predicate) ? !matched : matched;
    }

    private static bool InRange(char first, char last, char value, PythonRegexFlags flags)
    {
        if (value >= first && value <= last)
        {
            return true;
        }
        if (!flags.HasFlag(PythonRegexFlags.IgnoreCase))
        {
            return false;
        }
        var other =
            AsciiOnly(flags) ? AsciiFold(value)
            : IsAsciiUpper(value) ? char.ToLowerInvariant(value)
            : char.ToUpperInvariant(value);
        return other >= first && other <= last;
    }

    /// <summary>ASCII-only case folding: bytes expressions never fold other characters.</summary>
    private static char AsciiFold(char value) =>
        value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    private static bool IsWordCharacter(char value, PythonRegexFlags flags) =>
        value == '_'
        || char.IsAsciiLetterOrDigit(value)
        || (!AsciiOnly(flags) && char.IsLetterOrDigit(value));

    private static bool IsSpace(char value, PythonRegexFlags flags) =>
        char.IsAscii(value)
            ? char.IsWhiteSpace(value)
            : !AsciiOnly(flags) && char.IsWhiteSpace(value);

    private static bool IsAsciiUpper(char value) => value is >= 'A' and <= 'Z';

    private static bool AsciiOnly(PythonRegexFlags flags) => flags.HasFlag(PythonRegexFlags.Ascii);

    private static bool EqualTo(char patternValue, char textValue, PythonRegexFlags flags)
    {
        if (patternValue == textValue)
        {
            return true;
        }
        if (!flags.HasFlag(PythonRegexFlags.IgnoreCase))
        {
            return false;
        }
        if (AsciiOnly(flags))
        {
            return AsciiFold(patternValue) == AsciiFold(textValue);
        }
        return char.ToUpperInvariant(patternValue) == char.ToUpperInvariant(textValue)
            || char.ToLowerInvariant(patternValue) == char.ToLowerInvariant(textValue);
    }
}
