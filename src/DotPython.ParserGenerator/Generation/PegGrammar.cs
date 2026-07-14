using DotPython.Language.Syntax;

namespace DotPython.ParserGenerator.Generation;

internal sealed class PegGrammar
{
    private static readonly HashSet<string> ReservedKeywords =
    [
        "False",
        "None",
        "True",
        "and",
        "as",
        "assert",
        "async",
        "await",
        "break",
        "class",
        "continue",
        "def",
        "del",
        "elif",
        "else",
        "except",
        "finally",
        "for",
        "from",
        "global",
        "if",
        "import",
        "in",
        "is",
        "lambda",
        "nonlocal",
        "not",
        "or",
        "pass",
        "raise",
        "return",
        "try",
        "while",
        "with",
        "yield",
    ];

    private readonly Dictionary<string, PegExpression> _rules;

    private PegGrammar(Dictionary<string, PegExpression> rules)
    {
        _rules = rules;
    }

    internal int RuleCount => _rules.Count;

    internal static PegGrammar Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rules = new Dictionary<string, PegExpression>(StringComparer.Ordinal);
        foreach (var line in NormalizeRuleSource(source).Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                throw new InvalidDataException($"Invalid PEG rule: '{line}'.");
            }

            var name = line[..colonIndex].Trim();
            if (!IsIdentifier(name))
            {
                throw new InvalidDataException($"Invalid PEG rule name '{name}'.");
            }

            var expression = new ExpressionParser(line[(colonIndex + 1)..]).Parse();
            if (!rules.TryAdd(name, expression))
            {
                throw new InvalidDataException($"Duplicate PEG rule '{name}'.");
            }
        }

        if (!rules.ContainsKey("file"))
        {
            throw new InvalidDataException("The PEG grammar must define a 'file' rule.");
        }

        ValidateReferences(rules);
        return new PegGrammar(rules);
    }

    internal static string NormalizeRuleSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var lines = source
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => RemoveComment(line).Trim())
            .Where(line => line.Length != 0);
        return string.Join('\n', lines);
    }

    internal PegMatchResult Match(SyntaxToken[] tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var matcher = new Matcher(_rules, tokens);
        var position = 0;
        var success = matcher.MatchRule("file", ref position) && position == tokens.Length;
        return new PegMatchResult(success, matcher.FurthestPosition);
    }

    private static void ValidateReferences(IReadOnlyDictionary<string, PegExpression> rules)
    {
        foreach (var (ruleName, expression) in rules)
        {
            foreach (var reference in EnumerateReferences(expression))
            {
                if (!IsTokenName(reference) && !rules.ContainsKey(reference))
                {
                    throw new InvalidDataException(
                        $"PEG rule '{ruleName}' references unknown rule or token '{reference}'."
                    );
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateReferences(PegExpression expression)
    {
        switch (expression)
        {
            case PegName name:
                yield return name.Value;
                break;
            case PegSequence sequence:
                foreach (var item in sequence.Items)
                {
                    foreach (var reference in EnumerateReferences(item))
                    {
                        yield return reference;
                    }
                }

                break;
            case PegChoice choice:
                foreach (var alternative in choice.Alternatives)
                {
                    foreach (var reference in EnumerateReferences(alternative))
                    {
                        yield return reference;
                    }
                }

                break;
            case PegOptional optional:
                foreach (var reference in EnumerateReferences(optional.Expression))
                {
                    yield return reference;
                }

                break;
            case PegRepeat repeat:
                foreach (var reference in EnumerateReferences(repeat.Expression))
                {
                    yield return reference;
                }

                break;
            case PegGather gather:
                foreach (var reference in EnumerateReferences(gather.Separator))
                {
                    yield return reference;
                }

                foreach (var reference in EnumerateReferences(gather.Expression))
                {
                    yield return reference;
                }

                break;
        }
    }

    private static bool IsTokenName(string value) =>
        value is "NAME" or "NUMBER" or "STRING" or "NEWLINE" or "INDENT" or "DEDENT" or "ENDMARKER";

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!(char.IsAsciiLetterOrDigit(value[index]) || value[index] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string RemoveComment(string line)
    {
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private sealed class Matcher(
        IReadOnlyDictionary<string, PegExpression> rules,
        SyntaxToken[] tokens
    )
    {
        private readonly Dictionary<(string Name, int Position), MemoizedMatch> _memoized = [];
        private readonly HashSet<(string Name, int Position)> _pending = [];

        internal int FurthestPosition { get; private set; }

        internal bool MatchRule(string name, ref int position)
        {
            var start = position;
            var key = (name, start);
            if (_memoized.TryGetValue(key, out var memoized))
            {
                position = memoized.EndPosition;
                return memoized.Success;
            }

            if (!_pending.Add(key))
            {
                throw new InvalidDataException(
                    $"Left recursion in PEG rule '{name}' is not supported yet."
                );
            }

            var success = MatchExpression(rules[name], ref position);
            _pending.Remove(key);
            if (!success)
            {
                position = start;
            }

            _memoized.Add(key, new MemoizedMatch(success, position));
            return success;
        }

        private bool MatchExpression(PegExpression expression, ref int position)
        {
            FurthestPosition = Math.Max(FurthestPosition, position);
            switch (expression)
            {
                case PegLiteral literal:
                    return MatchLiteral(literal.Value, ref position);
                case PegName name:
                    return MatchName(name.Value, ref position);
                case PegSequence sequence:
                    return MatchSequence(sequence, ref position);
                case PegChoice choice:
                    return MatchChoice(choice, ref position);
                case PegOptional optional:
                    MatchExpression(optional.Expression, ref position);
                    return true;
                case PegRepeat repeat:
                    return MatchRepeat(repeat, ref position);
                case PegGather gather:
                    return MatchGather(gather, ref position);
                default:
                    throw new InvalidOperationException(
                        $"Unknown PEG expression '{expression.GetType().Name}'."
                    );
            }
        }

        private bool MatchLiteral(string value, ref int position)
        {
            if (
                position >= tokens.Length
                || !string.Equals(tokens[position].Text, value, StringComparison.Ordinal)
            )
            {
                return false;
            }

            position++;
            return true;
        }

        private bool MatchName(string name, ref int position)
        {
            if (!IsTokenName(name))
            {
                return MatchRule(name, ref position);
            }

            if (position >= tokens.Length || !MatchesToken(name, tokens[position]))
            {
                return false;
            }

            position++;
            return true;
        }

        private bool MatchSequence(PegSequence sequence, ref int position)
        {
            var start = position;
            foreach (var item in sequence.Items)
            {
                if (!MatchExpression(item, ref position))
                {
                    position = start;
                    return false;
                }
            }

            return true;
        }

        private bool MatchChoice(PegChoice choice, ref int position)
        {
            foreach (var alternative in choice.Alternatives)
            {
                var start = position;
                if (MatchExpression(alternative, ref position))
                {
                    return true;
                }

                position = start;
            }

            return false;
        }

        private bool MatchRepeat(PegRepeat repeat, ref int position)
        {
            var matches = 0;
            while (true)
            {
                var start = position;
                if (!MatchExpression(repeat.Expression, ref position))
                {
                    position = start;
                    break;
                }

                if (position == start)
                {
                    throw new InvalidDataException("A repeated PEG expression matched no input.");
                }

                matches++;
            }

            return matches >= repeat.Minimum;
        }

        private bool MatchGather(PegGather gather, ref int position)
        {
            var start = position;
            if (!MatchExpression(gather.Expression, ref position))
            {
                position = start;
                return false;
            }

            while (true)
            {
                var separatorStart = position;
                if (
                    !MatchExpression(gather.Separator, ref position)
                    || !MatchExpression(gather.Expression, ref position)
                )
                {
                    position = separatorStart;
                    break;
                }
            }

            return true;
        }

        private static bool MatchesToken(string name, SyntaxToken token) =>
            name switch
            {
                "NAME" => token.Kind == SyntaxTokenKind.Identifier
                    && !ReservedKeywords.Contains(token.Text),
                "NUMBER" => token.Kind
                    is SyntaxTokenKind.IntegerLiteral
                        or SyntaxTokenKind.FloatLiteral
                        or SyntaxTokenKind.ImaginaryLiteral,
                "STRING" => token.Kind
                    is SyntaxTokenKind.StringLiteral
                        or SyntaxTokenKind.BytesLiteral
                        or SyntaxTokenKind.FormattedStringLiteral
                        or SyntaxTokenKind.TemplateStringLiteral,
                "NEWLINE" => token.Kind == SyntaxTokenKind.NewLine,
                "INDENT" => token.Kind == SyntaxTokenKind.Indent,
                "DEDENT" => token.Kind == SyntaxTokenKind.Dedent,
                "ENDMARKER" => token.Kind == SyntaxTokenKind.EndOfFile,
                _ => false,
            };

        private readonly record struct MemoizedMatch(bool Success, int EndPosition);
    }

    private sealed class ExpressionParser(string source)
    {
        private int _position;

        internal PegExpression Parse()
        {
            var expression = ParseChoice();
            SkipWhitespace();
            if (_position != source.Length)
            {
                throw Error($"Unexpected character '{source[_position]}'.");
            }

            return expression;
        }

        private PegExpression ParseChoice()
        {
            var alternatives = new List<PegExpression> { ParseSequence() };
            while (Match('|'))
            {
                alternatives.Add(ParseSequence());
            }

            return alternatives.Count == 1
                ? alternatives[0]
                : new PegChoice(alternatives.ToArray());
        }

        private PegExpression ParseSequence()
        {
            var items = new List<PegExpression>();
            while (CanStartPrimary())
            {
                items.Add(ParseItem());
            }

            return items.Count switch
            {
                0 => throw Error("Expected a PEG expression."),
                1 => items[0],
                _ => new PegSequence(items.ToArray()),
            };
        }

        private PegExpression ParseItem()
        {
            var expression = ParsePrimary();
            if (Match('.'))
            {
                var item = ParsePrimary();
                if (!Match('+'))
                {
                    throw Error("A PEG gather must end with '+'.");
                }

                return new PegGather(expression, item);
            }

            if (Match('*'))
            {
                return new PegRepeat(expression, 0);
            }

            return Match('+') ? new PegRepeat(expression, 1) : expression;
        }

        private PegExpression ParsePrimary()
        {
            SkipWhitespace();
            if (_position >= source.Length)
            {
                throw Error("Expected a PEG primary expression.");
            }

            if (Match('['))
            {
                var expression = ParseChoice();
                Expect(']');
                return new PegOptional(expression);
            }

            if (Match('('))
            {
                var expression = ParseChoice();
                Expect(')');
                return expression;
            }

            if (source[_position] is '\'' or '"')
            {
                return new PegLiteral(ParseString());
            }

            return new PegName(ParseIdentifier());
        }

        private string ParseIdentifier()
        {
            SkipWhitespace();
            var start = _position;
            while (
                _position < source.Length
                && (char.IsAsciiLetterOrDigit(source[_position]) || source[_position] == '_')
            )
            {
                _position++;
            }

            var value = source[start.._position];
            if (!IsIdentifier(value))
            {
                throw Error("Expected a PEG identifier.");
            }

            return value;
        }

        private string ParseString()
        {
            var quote = source[_position++];
            var builder = new System.Text.StringBuilder();
            while (_position < source.Length)
            {
                var character = source[_position++];
                if (character == quote)
                {
                    return builder.ToString();
                }

                if (character == '\\')
                {
                    if (_position == source.Length)
                    {
                        break;
                    }

                    character = source[_position++];
                }

                builder.Append(character);
            }

            throw Error("Unterminated PEG string literal.");
        }

        private bool CanStartPrimary()
        {
            SkipWhitespace();
            return _position < source.Length && source[_position] is not ('|' or ')' or ']');
        }

        private bool Match(char character)
        {
            SkipWhitespace();
            if (_position >= source.Length || source[_position] != character)
            {
                return false;
            }

            _position++;
            return true;
        }

        private void Expect(char character)
        {
            if (!Match(character))
            {
                throw Error($"Expected '{character}'.");
            }
        }

        private void SkipWhitespace()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position]))
            {
                _position++;
            }
        }

        private InvalidDataException Error(string message) =>
            new($"{message} (column {_position + 1} in '{source}').");
    }

    private abstract record PegExpression;

    private sealed record PegLiteral(string Value) : PegExpression;

    private sealed record PegName(string Value) : PegExpression;

    private sealed record PegSequence(PegExpression[] Items) : PegExpression;

    private sealed record PegChoice(PegExpression[] Alternatives) : PegExpression;

    private sealed record PegOptional(PegExpression Expression) : PegExpression;

    private sealed record PegRepeat(PegExpression Expression, int Minimum) : PegExpression;

    private sealed record PegGather(PegExpression Separator, PegExpression Expression)
        : PegExpression;
}

internal readonly record struct PegMatchResult(bool Success, int FurthestTokenIndex);
