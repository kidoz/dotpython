using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

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

/// <summary>A pattern that does not compile, with where and in what it failed.</summary>
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
/// matched by backtracking, leftmost-first, which is what Python's own engine does. The
/// syntax this runtime does not implement — named groups, backreferences, lookaround and
/// scoped flags — is refused where it is parsed.
/// </summary>
internal static class PythonRegex
{
    // -- the compiled pattern ----------------------------------------------------------

    internal sealed class Program
    {
        internal Program(string pattern, PythonRegexFlags flags, int groupCount, Node root)
        {
            Pattern = pattern;
            Flags = flags;
            GroupCount = groupCount;
            Root = root;
        }

        internal string Pattern { get; }

        internal PythonRegexFlags Flags { get; }

        /// <summary>Capturing groups, group 0 not counted.</summary>
        internal int GroupCount { get; }

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

    internal sealed class LiteralNode(char value) : Node
    {
        internal char Value { get; } = value;
    }

    /// <summary>A character class: the ranges and the class names its characters satisfy.</summary>
    internal sealed class ClassNode(bool negated) : Node
    {
        internal bool Negated { get; } = negated;

        internal List<(char First, char Last)> Ranges { get; } = [];

        /// <summary>`\d`, `\w`, `\s` and their negations, as the letters they were written by.</summary>
        internal List<char> Predicates { get; } = [];
    }

    internal sealed class AnyCharNode : Node;

    /// <summary>A capturing group; group index 0 is a non-capturing one.</summary>
    internal sealed class GroupNode(int index, Node body) : Node
    {
        internal int Index { get; } = index;

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

    internal sealed class AnchorNode(char value) : Node
    {
        internal char Value { get; } = value;
    }

    // -- parsing -----------------------------------------------------------------------

    private sealed class Parser(string pattern, string text, PythonRegexFlags flags)
    {
        /// <summary>The pattern as read — the same text, less what `re.VERBOSE` drops.</summary>
        private readonly string _pattern = text;

        /// <summary>The pattern as written, which is what `Pattern.pattern` reports.</summary>
        private readonly string _source = pattern;

        private int _position;
        private int _groupCount;
        private PythonRegexFlags _flags = flags;

        internal Program Parse()
        {
            var root = ParseAlternation();
            if (_position < _pattern.Length)
            {
                // A `)` that no group opened is the only thing that stops a branch.
                throw new PythonRegexParseError("unbalanced parenthesis", _position);
            }
            return new Program(_source, _flags, _groupCount, root);
        }

        private Node ParseAlternation()
        {
            var branches = new List<Node> { ParseBranch() };
            while (Current == '|')
            {
                _position++;
                branches.Add(ParseBranch());
            }
            return branches.Count == 1 ? branches[0] : new AlternationNode(branches);
        }

        private Node ParseBranch()
        {
            var items = new List<Node>();
            while (_position < _pattern.Length && Current is not ('|' or ')'))
            {
                items.Add(ParsePiece());
            }
            return items.Count == 1 ? items[0] : new SequenceNode(items);
        }

        private Node ParsePiece()
        {
            var atom = ParseAtom();
            var quantifier = _position;
            var minimum = -1;
            var maximum = -1;
            switch (Current)
            {
                case '*':
                    minimum = 0;
                    maximum = int.MaxValue;
                    _position++;
                    break;
                case '+':
                    minimum = 1;
                    maximum = int.MaxValue;
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
            maximum = second ?? int.MaxValue;
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

        private Node ParseAtom()
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
                    return new AnyCharNode();
                case '^':
                case '$':
                    _position++;
                    return new AnchorNode(value);
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
            return new LiteralNode(value);
        }

        private Node ParseGroup()
        {
            var start = _position;
            _position++;
            if (Current != '?')
            {
                // The group is numbered where it opens: a nested group inside its body
                // takes the next index, not this one.
                var index = ++_groupCount;
                var body = ParseAlternation();
                ExpectClose(start);
                return new GroupNode(index, body);
            }

            _position++;
            if (_position >= _pattern.Length)
            {
                throw new PythonRegexParseError("unexpected end of pattern", _position);
            }
            switch (Current)
            {
                case ':':
                    _position++;
                    var plain = ParseAlternation();
                    ExpectClose(start);
                    return new GroupNode(0, plain);
                case '#':
                    while (_position < _pattern.Length && Current != ')')
                    {
                        _position++;
                    }
                    ExpectClose(start);
                    return new SequenceNode([]);
                case 'P':
                    _position++;
                    if (_position >= _pattern.Length)
                    {
                        throw new PythonRegexParseError("unexpected end of pattern", _position);
                    }
                    // `(?P<name>...)` and `(?P=name)` are the syntax this slice does not read.
                    throw new PythonRegexParseError(
                        "named groups are not supported in this runtime slice",
                        start
                    );
                case '<'
                    when _position + 1 < _pattern.Length && _pattern[_position + 1] is '=' or '!':
                    throw new PythonRegexParseError(
                        "lookbehind is not supported in this runtime slice",
                        start
                    );
                case '=':
                case '!':
                    throw new PythonRegexParseError(
                        "lookahead is not supported in this runtime slice",
                        start
                    );
                case '>':
                    throw new PythonRegexParseError(
                        "atomic groups are not supported in this runtime slice",
                        start
                    );
                case '<':
                    throw new PythonRegexParseError(
                        $"unknown extension ?<{_pattern[_position + 1]}",
                        start + 1
                    );
                case 'i' or 'm' or 's' or 'x' or 'a' or 'u' or 'L':
                    return ParseInlineFlags(start);
            }
            throw new PythonRegexParseError($"unknown extension ?{Current}", start + 1);
        }

        /// <summary>
        /// `(?i)` and its like, which set the flags for the whole pattern. Python allows
        /// them only at the start, and the scoped form `(?i:...)` is not implemented here.
        /// </summary>
        private SequenceNode ParseInlineFlags(int start)
        {
            if (start != 0)
            {
                throw new PythonRegexParseError(
                    "global flags not at the start of the expression",
                    start
                );
            }
            while (_position < _pattern.Length && Current != ')')
            {
                var bit = Current switch
                {
                    'i' => PythonRegexFlags.IgnoreCase,
                    'm' => PythonRegexFlags.Multiline,
                    's' => PythonRegexFlags.DotAll,
                    'x' => PythonRegexFlags.Verbose,
                    'a' => PythonRegexFlags.Ascii,
                    'u' => PythonRegexFlags.Unicode,
                    'L' => PythonRegexFlags.Locale,
                    _ => throw new PythonRegexParseError("missing -, : or )", _position),
                };
                _position++;
                if (Current is ':' or '-')
                {
                    throw new PythonRegexParseError(
                        "scoped flags are not supported in this runtime slice",
                        start
                    );
                }
                _flags |= bit;
            }
            ExpectClose(start);
            return new SequenceNode([]);
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
            var node = new ClassNode(negated);
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

                var first = ReadClassCharacter(inClass: true);
                if (
                    Current == '-'
                    && _position + 1 < _pattern.Length
                    && _pattern[_position + 1] != ']'
                )
                {
                    _position++;
                    var last = ReadClassCharacter(inClass: true);
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
        private char ReadClassCharacter(bool inClass)
        {
            var value = _pattern[_position];
            if (value == '\\')
            {
                return ReadEscapeCharacter(inClass);
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
                    var node = new ClassNode(negated: false);
                    node.Predicates.Add(value);
                    return node;
                }
                case 'b' or 'B' or 'A' or 'Z' or 'z':
                    return new AnchorNode(value);
            }
            return new LiteralNode(value);
        }

        /// <summary>
        /// The character an escape stands for, or the class name or boundary letter the
        /// caller reads for itself. An escape Python does not know is refused in its words.
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
                    throw new PythonRegexParseError(
                        "`\\N{...}` is not supported in this runtime slice",
                        start
                    );
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
                throw InvalidGroupReference(start);
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

        /// <summary>`\12`: the group reference this runtime does not implement.</summary>
        private PythonRegexParseError InvalidGroupReference(int start)
        {
            var end = start + 1;
            while (end < _pattern.Length && char.IsAsciiDigit(_pattern[end]))
            {
                end++;
            }
            return new PythonRegexParseError(
                $"invalid group reference {_pattern[(start + 1)..end]}",
                start + 1
            );
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
            if (_position >= _pattern.Length || _pattern[_position] != ')')
            {
                throw new PythonRegexParseError("missing ), unterminated subpattern", start);
            }
            _position++;
        }
    }

    /// <summary>Parses a pattern with the flags it was given.</summary>
    /// <exception cref="PythonRegexParseError">The pattern is not one this engine reads.</exception>
    internal static Program Compile(string pattern, PythonRegexFlags flags)
    {
        var text = flags.HasFlag(PythonRegexFlags.Verbose) ? VerboseStrip(pattern) : pattern;
        return new Parser(pattern, text, flags).Parse();
    }

    /// <summary>`re.VERBOSE` takes unescaped whitespace and comments out of a pattern.</summary>
    private static string VerboseStrip(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        var insideClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var value = pattern[index];
            if (value == '\\' && index + 1 < pattern.Length)
            {
                builder.Append(value).Append(pattern[index + 1]);
                index++;
                continue;
            }
            if (value == '[')
            {
                insideClass = true;
            }
            else if (value == ']')
            {
                insideClass = false;
            }
            if (!insideClass && value == '#')
            {
                while (index < pattern.Length && pattern[index] != '\n')
                {
                    index++;
                }
                continue;
            }
            if (!insideClass && char.IsWhiteSpace(value))
            {
                continue;
            }
            builder.Append(value);
        }
        return builder.ToString();
    }

    // -- matching ----------------------------------------------------------------------

    private sealed class State
    {
        internal State(string text, PythonRegexFlags flags, int start, int end, int groups)
        {
            Text = text;
            Flags = flags;
            BoundsStart = start;
            BoundsEnd = end;
            Starts = new int?[groups + 1];
            Ends = new int?[groups + 1];
        }

        internal string Text { get; }

        internal PythonRegexFlags Flags { get; }

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
        var state = new State(text, program.Flags, position, bound, program.GroupCount);
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
                && EqualTo(literal.Value, state.Text[position], state.Flags)
                && next(state, position + 1),
            AnyCharNode => position < state.BoundsEnd
                && (state.Text[position] != '\n' || state.Flags.HasFlag(PythonRegexFlags.DotAll))
                && next(state, position + 1),
            ClassNode characterClass => position < state.BoundsEnd
                && InClass(characterClass, state.Text[position], state.Flags)
                && next(state, position + 1),
            GroupNode group => MatchGroup(group, state, position, next),
            RepeatNode repeat => MatchRepeat(repeat, state, position, next, count: 0),
            AnchorNode anchor => AtAnchor(anchor.Value, state, position) && next(state, position),
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

    private static bool AtAnchor(char anchor, State state, int position)
    {
        var text = state.Text;
        var multiline = state.Flags.HasFlag(PythonRegexFlags.Multiline);
        switch (anchor)
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
                return IsWord(text, position - 1, state.Flags)
                    != IsWord(text, position, state.Flags);
            default:
                return IsWord(text, position - 1, state.Flags)
                    == IsWord(text, position, state.Flags);
        }
    }

    private static bool IsWord(string text, int position, PythonRegexFlags flags) =>
        position >= 0 && position < text.Length && IsWordCharacter(text[position], flags);

    private static bool InClass(ClassNode node, char value, PythonRegexFlags flags)
    {
        var inside = false;
        foreach (var (first, last) in node.Ranges)
        {
            if (InRange(first, last, value, flags))
            {
                inside = true;
                break;
            }
        }
        if (!inside)
        {
            foreach (var predicate in node.Predicates)
            {
                if (InPredicate(predicate, value, flags))
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
        var other = IsAsciiUpper(value)
            ? char.ToLowerInvariant(value)
            : char.ToUpperInvariant(value);
        return other >= first && other <= last;
    }

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
        return char.ToUpperInvariant(patternValue) == char.ToUpperInvariant(textValue)
            || char.ToLowerInvariant(patternValue) == char.ToLowerInvariant(textValue);
    }
}
