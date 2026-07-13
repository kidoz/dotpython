using System.Globalization;
using System.Text;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Language.Syntax;

public static class PythonTokenizer
{
    public static TokenizationResult Tokenize(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Lexer(source).Tokenize();
    }

    private sealed class Lexer
    {
        private const int TabWidth = 8;

        private readonly List<Diagnostic> _diagnostics = [];
        private readonly Stack<Delimiter> _delimiters = [];
        private readonly List<IndentationLevel> _indentation = [new(0, 0)];
        private readonly SourceText _source;
        private readonly string _text;
        private readonly List<SyntaxToken> _tokens = [];
        private bool _atBeginningOfLine = true;
        private bool _logicalLineHasCode;
        private int _position;

        internal Lexer(SourceText source)
        {
            _source = source;
            _text = source.Content;
        }

        internal TokenizationResult Tokenize()
        {
            while (_position < _text.Length)
            {
                if (_atBeginningOfLine)
                {
                    if (_delimiters.Count == 0)
                    {
                        if (ReadBeginningOfLine())
                        {
                            continue;
                        }
                    }
                    else
                    {
                        _atBeginningOfLine = false;
                    }
                }

                if (IsHorizontalWhitespace(Current))
                {
                    _position++;
                    continue;
                }

                if (Current == '#')
                {
                    ReadComment();
                    continue;
                }

                if (IsNewLine(Current))
                {
                    ReadNewLine(_delimiters.Count == 0);
                    continue;
                }

                if (Current == '\\' && IsNewLine(Peek(1)))
                {
                    ReadExplicitLineJoining();
                    continue;
                }

                if (TryReadString())
                {
                    _logicalLineHasCode = true;
                    continue;
                }

                if (char.IsAsciiDigit(Current) || Current == '.' && char.IsAsciiDigit(Peek(1)))
                {
                    ReadNumber();
                    _logicalLineHasCode = true;
                    continue;
                }

                if (IsIdentifierStartAt(_position))
                {
                    ReadIdentifier();
                    _logicalLineHasCode = true;
                    continue;
                }

                if (TryReadOperator())
                {
                    _logicalLineHasCode = true;
                    continue;
                }

                ReadBadToken();
                _logicalLineHasCode = true;
            }

            FinishTokenization();
            return new TokenizationResult(_source, _tokens, _diagnostics);
        }

        private char Current => Peek(0);

        private bool ReadBeginningOfLine()
        {
            var whitespaceStart = _position;
            var column = 0;
            var alternateColumn = 0;

            while (_position < _text.Length && IsHorizontalWhitespace(Current))
            {
                switch (Current)
                {
                    case ' ':
                        column++;
                        alternateColumn++;
                        break;
                    case '\t':
                        column = (column / TabWidth + 1) * TabWidth;
                        alternateColumn++;
                        break;
                    case '\f':
                        column = 0;
                        alternateColumn = 0;
                        break;
                }

                _position++;
            }

            if (Current == '#')
            {
                ReadComment();
            }

            if (IsNewLine(Current))
            {
                ReadNewLine(significant: false);
                return true;
            }

            if (_position >= _text.Length)
            {
                return true;
            }

            ApplyIndentation(whitespaceStart, column, alternateColumn);
            _atBeginningOfLine = false;
            return false;
        }

        private void ApplyIndentation(int whitespaceStart, int column, int alternateColumn)
        {
            var current = _indentation[^1];
            var span = TextSpan.FromBounds(whitespaceStart, _position);

            if (column == current.Column)
            {
                if (alternateColumn != current.AlternateColumn)
                {
                    ReportInconsistentTabsAndSpaces(span);
                }

                return;
            }

            if (column > current.Column)
            {
                if (alternateColumn <= current.AlternateColumn)
                {
                    ReportInconsistentTabsAndSpaces(span);
                }

                _indentation.Add(new IndentationLevel(column, alternateColumn));
                AddToken(SyntaxTokenKind.Indent, whitespaceStart, _position);
                return;
            }

            while (_indentation.Count > 1 && column < _indentation[^1].Column)
            {
                _indentation.RemoveAt(_indentation.Count - 1);
                AddToken(SyntaxTokenKind.Dedent, _position, _position);
            }

            current = _indentation[^1];
            if (column != current.Column)
            {
                Report("DPY1002", "Unindent does not match any outer indentation level.", span);
            }
            else if (alternateColumn != current.AlternateColumn)
            {
                ReportInconsistentTabsAndSpaces(span);
            }
        }

        private void ReadComment()
        {
            while (_position < _text.Length && !IsNewLine(Current))
            {
                _position++;
            }
        }

        private void ReadNewLine(bool significant)
        {
            var start = _position;
            AdvanceNewLine();
            AddToken(
                significant ? SyntaxTokenKind.NewLine : SyntaxTokenKind.NonSignificantNewLine,
                start,
                _position
            );
            _atBeginningOfLine = true;

            if (significant)
            {
                _logicalLineHasCode = false;
            }
        }

        private void ReadExplicitLineJoining()
        {
            _position++;
            AdvanceNewLine();
            _atBeginningOfLine = false;
        }

        private bool TryReadString()
        {
            var start = _position;
            var prefixLength = GetStringPrefixLength(start);
            var quotePosition = start + prefixLength;

            if (quotePosition >= _text.Length || !IsQuote(_text[quotePosition]))
            {
                return false;
            }

            var prefix = _text.AsSpan(start, prefixLength);
            var kind = GetStringKind(prefix);
            _position = quotePosition;

            if (
                kind
                is SyntaxTokenKind.FormattedStringLiteral
                    or SyntaxTokenKind.TemplateStringLiteral
            )
            {
                ReadInterpolatedString(start);
            }
            else
            {
                ReadOrdinaryString(start);
            }

            AddToken(kind, start, _position);
            return true;
        }

        private void ReadOrdinaryString(int tokenStart)
        {
            var quote = Current;
            var quoteLength = HasRepeatedQuote(_position, quote) ? 3 : 1;
            _position += quoteLength;

            while (_position < _text.Length)
            {
                if (MatchesQuote(_position, quote, quoteLength))
                {
                    _position += quoteLength;
                    return;
                }

                if (IsNewLine(Current) && quoteLength == 1)
                {
                    ReportUnterminatedString(tokenStart);
                    return;
                }

                if (Current == '\\')
                {
                    _position++;
                    if (_position < _text.Length)
                    {
                        if (IsNewLine(Current))
                        {
                            AdvanceNewLine();
                        }
                        else
                        {
                            AdvanceCodePoint();
                        }
                    }

                    continue;
                }

                AdvanceCodePoint();
            }

            ReportUnterminatedString(tokenStart);
        }

        private void ReadInterpolatedString(int tokenStart)
        {
            var quote = Current;
            var quoteLength = HasRepeatedQuote(_position, quote) ? 3 : 1;
            var replacementDepth = 0;
            _position += quoteLength;

            while (_position < _text.Length)
            {
                if (replacementDepth == 0 && MatchesQuote(_position, quote, quoteLength))
                {
                    _position += quoteLength;
                    return;
                }

                if (replacementDepth == 0)
                {
                    if (Current == '{')
                    {
                        if (Peek(1) == '{')
                        {
                            _position += 2;
                        }
                        else
                        {
                            replacementDepth = 1;
                            _position++;
                        }

                        continue;
                    }

                    if (Current == '}' && Peek(1) == '}')
                    {
                        _position += 2;
                        continue;
                    }

                    if (IsNewLine(Current) && quoteLength == 1)
                    {
                        ReportUnterminatedString(tokenStart);
                        return;
                    }

                    AdvanceStringCharacter();
                    continue;
                }

                if (Current == '#')
                {
                    ReadComment();
                    continue;
                }

                if (TrySkipNestedString())
                {
                    continue;
                }

                if (Current == '{')
                {
                    replacementDepth++;
                    _position++;
                    continue;
                }

                if (Current == '}')
                {
                    replacementDepth--;
                    _position++;
                    continue;
                }

                AdvanceCodePoint();
            }

            ReportUnterminatedString(tokenStart);
        }

        private bool TrySkipNestedString()
        {
            var start = _position;
            var prefixLength = GetStringPrefixLength(start);
            var quotePosition = start + prefixLength;
            if (quotePosition >= _text.Length || !IsQuote(_text[quotePosition]))
            {
                return false;
            }

            _position = quotePosition;
            var nestedKind = GetStringKind(_text.AsSpan(start, prefixLength));
            if (
                nestedKind
                is SyntaxTokenKind.FormattedStringLiteral
                    or SyntaxTokenKind.TemplateStringLiteral
            )
            {
                ReadInterpolatedString(start);
            }
            else
            {
                ReadOrdinaryString(start);
            }

            return true;
        }

        private void AdvanceStringCharacter()
        {
            if (Current != '\\')
            {
                AdvanceCodePoint();
                return;
            }

            _position++;
            if (_position >= _text.Length)
            {
                return;
            }

            if (IsNewLine(Current))
            {
                AdvanceNewLine();
            }
            else
            {
                AdvanceCodePoint();
            }
        }

        private void ReadNumber()
        {
            var start = _position;
            var kind = SyntaxTokenKind.IntegerLiteral;
            var valid = true;

            if (Current == '.')
            {
                kind = SyntaxTokenKind.FloatLiteral;
                _position++;
                valid &= ReadDigitPart(IsDecimalDigit, allowLeadingUnderscore: false);
            }
            else if (Current == '0' && IsBasePrefix(Peek(1)))
            {
                var prefix = char.ToLowerInvariant(Peek(1));
                _position += 2;
                Func<char, bool> digitPredicate = prefix switch
                {
                    'b' => IsBinaryDigit,
                    'o' => IsOctalDigit,
                    _ => IsHexadecimalDigit,
                };

                valid &= ReadDigitPart(digitPredicate, allowLeadingUnderscore: true);
                while (char.IsAsciiLetterOrDigit(Current) || Current == '_')
                {
                    valid = false;
                    _position++;
                }
            }
            else
            {
                valid &= ReadDigitPart(IsDecimalDigit, allowLeadingUnderscore: false);

                if (Current == '.')
                {
                    kind = SyntaxTokenKind.FloatLiteral;
                    _position++;
                    if (IsDecimalDigitPartCharacter(Current))
                    {
                        valid &= ReadDigitPart(IsDecimalDigit, allowLeadingUnderscore: false);
                    }
                }

                if (Current is 'e' or 'E')
                {
                    kind = SyntaxTokenKind.FloatLiteral;
                    _position++;
                    if (Current is '+' or '-')
                    {
                        _position++;
                    }

                    valid &= ReadDigitPart(IsDecimalDigit, allowLeadingUnderscore: false);
                }

                if (Current is 'j' or 'J')
                {
                    kind = SyntaxTokenKind.ImaginaryLiteral;
                    _position++;
                }
            }

            AddToken(kind, start, _position);
            if (!valid)
            {
                Report(
                    "DPY1006",
                    "Invalid numeric literal.",
                    TextSpan.FromBounds(start, _position)
                );
            }
        }

        private bool ReadDigitPart(Func<char, bool> isDigit, bool allowLeadingUnderscore)
        {
            var sawDigit = false;
            var previousWasUnderscore = false;
            var isValid = true;

            if (allowLeadingUnderscore && Current == '_')
            {
                previousWasUnderscore = true;
                _position++;
            }

            while (isDigit(Current) || Current == '_')
            {
                if (Current == '_')
                {
                    if (!sawDigit || previousWasUnderscore)
                    {
                        isValid = false;
                        previousWasUnderscore = true;
                        _position++;
                        continue;
                    }

                    previousWasUnderscore = true;
                }
                else
                {
                    sawDigit = true;
                    previousWasUnderscore = false;
                }

                _position++;
            }

            return isValid && sawDigit && !previousWasUnderscore;
        }

        private void ReadIdentifier()
        {
            var start = _position;
            AdvanceCodePoint();

            while (IsIdentifierContinueAt(_position))
            {
                AdvanceCodePoint();
            }

            AddToken(SyntaxTokenKind.Identifier, start, _position);
        }

        private bool TryReadOperator()
        {
            var start = _position;
            SyntaxTokenKind kind;
            var length = 1;

            switch (Current)
            {
                case '(':
                    kind = SyntaxTokenKind.LeftParenthesis;
                    break;
                case ')':
                    kind = SyntaxTokenKind.RightParenthesis;
                    break;
                case '[':
                    kind = SyntaxTokenKind.LeftBracket;
                    break;
                case ']':
                    kind = SyntaxTokenKind.RightBracket;
                    break;
                case '{':
                    kind = SyntaxTokenKind.LeftBrace;
                    break;
                case '}':
                    kind = SyntaxTokenKind.RightBrace;
                    break;
                case ':':
                    kind = Peek(1) == '=' ? SyntaxTokenKind.ColonEqual : SyntaxTokenKind.Colon;
                    length = kind == SyntaxTokenKind.ColonEqual ? 2 : 1;
                    break;
                case ',':
                    kind = SyntaxTokenKind.Comma;
                    break;
                case ';':
                    kind = SyntaxTokenKind.Semicolon;
                    break;
                case '.':
                    kind =
                        Peek(1) == '.' && Peek(2) == '.'
                            ? SyntaxTokenKind.Ellipsis
                            : SyntaxTokenKind.Dot;
                    length = kind == SyntaxTokenKind.Ellipsis ? 3 : 1;
                    break;
                case '@':
                    kind = Peek(1) == '=' ? SyntaxTokenKind.AtEqual : SyntaxTokenKind.At;
                    length = kind == SyntaxTokenKind.AtEqual ? 2 : 1;
                    break;
                case '+':
                    kind = Peek(1) == '=' ? SyntaxTokenKind.PlusEqual : SyntaxTokenKind.Plus;
                    length = kind == SyntaxTokenKind.PlusEqual ? 2 : 1;
                    break;
                case '-':
                    kind = Peek(1) switch
                    {
                        '=' => SyntaxTokenKind.MinusEqual,
                        '>' => SyntaxTokenKind.Arrow,
                        _ => SyntaxTokenKind.Minus,
                    };
                    length = kind == SyntaxTokenKind.Minus ? 1 : 2;
                    break;
                case '*':
                    (kind, length) = ReadRepeatedOperator(
                        SyntaxTokenKind.Star,
                        SyntaxTokenKind.StarEqual,
                        SyntaxTokenKind.DoubleStar,
                        SyntaxTokenKind.DoubleStarEqual
                    );
                    break;
                case '/':
                    (kind, length) = ReadRepeatedOperator(
                        SyntaxTokenKind.Slash,
                        SyntaxTokenKind.SlashEqual,
                        SyntaxTokenKind.DoubleSlash,
                        SyntaxTokenKind.DoubleSlashEqual
                    );
                    break;
                case '|':
                    kind =
                        Peek(1) == '='
                            ? SyntaxTokenKind.VerticalBarEqual
                            : SyntaxTokenKind.VerticalBar;
                    length = kind == SyntaxTokenKind.VerticalBarEqual ? 2 : 1;
                    break;
                case '&':
                    kind =
                        Peek(1) == '=' ? SyntaxTokenKind.AmpersandEqual : SyntaxTokenKind.Ampersand;
                    length = kind == SyntaxTokenKind.AmpersandEqual ? 2 : 1;
                    break;
                case '<':
                    (kind, length) = ReadComparisonOperator(
                        SyntaxTokenKind.LessThan,
                        SyntaxTokenKind.LessThanOrEqual,
                        SyntaxTokenKind.LeftShift,
                        SyntaxTokenKind.LeftShiftEqual
                    );
                    break;
                case '>':
                    (kind, length) = ReadComparisonOperator(
                        SyntaxTokenKind.GreaterThan,
                        SyntaxTokenKind.GreaterThanOrEqual,
                        SyntaxTokenKind.RightShift,
                        SyntaxTokenKind.RightShiftEqual
                    );
                    break;
                case '=':
                    kind = Peek(1) == '=' ? SyntaxTokenKind.EqualEqual : SyntaxTokenKind.Equal;
                    length = kind == SyntaxTokenKind.EqualEqual ? 2 : 1;
                    break;
                case '%':
                    kind = Peek(1) == '=' ? SyntaxTokenKind.PercentEqual : SyntaxTokenKind.Percent;
                    length = kind == SyntaxTokenKind.PercentEqual ? 2 : 1;
                    break;
                case '~':
                    kind = SyntaxTokenKind.Tilde;
                    break;
                case '^':
                    kind = Peek(1) == '=' ? SyntaxTokenKind.CaretEqual : SyntaxTokenKind.Caret;
                    length = kind == SyntaxTokenKind.CaretEqual ? 2 : 1;
                    break;
                case '!':
                    kind = Peek(1) == '=' ? SyntaxTokenKind.NotEqual : SyntaxTokenKind.Exclamation;
                    length = kind == SyntaxTokenKind.NotEqual ? 2 : 1;
                    break;
                default:
                    return false;
            }

            _position += length;
            AddToken(kind, start, _position);
            UpdateDelimiterStack(kind, start);
            return true;
        }

        private (SyntaxTokenKind Kind, int Length) ReadRepeatedOperator(
            SyntaxTokenKind single,
            SyntaxTokenKind singleEqual,
            SyntaxTokenKind repeated,
            SyntaxTokenKind repeatedEqual
        )
        {
            if (Peek(1) == Current)
            {
                return Peek(2) == '=' ? (repeatedEqual, 3) : (repeated, 2);
            }

            return Peek(1) == '=' ? (singleEqual, 2) : (single, 1);
        }

        private (SyntaxTokenKind Kind, int Length) ReadComparisonOperator(
            SyntaxTokenKind single,
            SyntaxTokenKind singleEqual,
            SyntaxTokenKind shift,
            SyntaxTokenKind shiftEqual
        )
        {
            if (Peek(1) == Current)
            {
                return Peek(2) == '=' ? (shiftEqual, 3) : (shift, 2);
            }

            return Peek(1) == '=' ? (singleEqual, 2) : (single, 1);
        }

        private void UpdateDelimiterStack(SyntaxTokenKind kind, int start)
        {
            if (
                kind
                is SyntaxTokenKind.LeftParenthesis
                    or SyntaxTokenKind.LeftBracket
                    or SyntaxTokenKind.LeftBrace
            )
            {
                _delimiters.Push(new Delimiter(kind, start));
                return;
            }

            if (
                kind
                is not (
                    SyntaxTokenKind.RightParenthesis
                    or SyntaxTokenKind.RightBracket
                    or SyntaxTokenKind.RightBrace
                )
            )
            {
                return;
            }

            if (_delimiters.Count == 0)
            {
                Report(
                    "DPY1004",
                    "Closing delimiter has no matching opening delimiter.",
                    new TextSpan(start, 1)
                );
                return;
            }

            var opening = _delimiters.Pop();
            if (!DelimitersMatch(opening.Kind, kind))
            {
                Report(
                    "DPY1004",
                    "Closing delimiter does not match the opening delimiter.",
                    new TextSpan(start, 1)
                );
            }
        }

        private void ReadBadToken()
        {
            var start = _position;
            AdvanceCodePoint();
            AddToken(SyntaxTokenKind.BadToken, start, _position);
            Report(
                "DPY1001",
                $"Unexpected character {FormatCharacter(_text.AsSpan(start, _position - start))}.",
                TextSpan.FromBounds(start, _position)
            );
        }

        private void FinishTokenization()
        {
            if (_logicalLineHasCode)
            {
                AddToken(SyntaxTokenKind.NewLine, _position, _position);
            }

            while (_indentation.Count > 1)
            {
                _indentation.RemoveAt(_indentation.Count - 1);
                AddToken(SyntaxTokenKind.Dedent, _position, _position);
            }

            foreach (var delimiter in _delimiters)
            {
                Report(
                    "DPY1005",
                    "Opening delimiter is not closed.",
                    new TextSpan(delimiter.Position, 1)
                );
            }

            AddToken(SyntaxTokenKind.EndOfFile, _position, _position);
        }

        private void AddToken(SyntaxTokenKind kind, int start, int end)
        {
            var span = TextSpan.FromBounds(start, end);
            _tokens.Add(new SyntaxToken(kind, span, _source.GetText(span)));
        }

        private void Report(string code, string message, TextSpan span) =>
            _diagnostics.Add(new Diagnostic(code, message, DiagnosticSeverity.Error, span));

        private void ReportInconsistentTabsAndSpaces(TextSpan span) =>
            Report("DPY1003", "Inconsistent use of tabs and spaces in indentation.", span);

        private void ReportUnterminatedString(int start) =>
            Report(
                "DPY1007",
                "String literal is not terminated.",
                TextSpan.FromBounds(start, _position)
            );

        private int GetStringPrefixLength(int start)
        {
            if (start >= _text.Length)
            {
                return 0;
            }

            if (IsQuote(_text[start]))
            {
                return 0;
            }

            if (
                start + 2 < _text.Length
                && IsValidTwoCharacterStringPrefix(_text[start], _text[start + 1])
                && IsQuote(_text[start + 2])
            )
            {
                return 2;
            }

            return
                start + 1 < _text.Length
                && IsValidOneCharacterStringPrefix(_text[start])
                && IsQuote(_text[start + 1])
                ? 1
                : 0;
        }

        private char Peek(int offset)
        {
            var index = _position + offset;
            return index >= 0 && index < _text.Length ? _text[index] : '\0';
        }

        private void AdvanceCodePoint()
        {
            if (Rune.TryGetRuneAt(_text, _position, out var rune))
            {
                _position += rune.Utf16SequenceLength;
            }
            else
            {
                _position++;
            }
        }

        private void AdvanceNewLine()
        {
            if (Current == '\r' && Peek(1) == '\n')
            {
                _position += 2;
            }
            else
            {
                _position++;
            }
        }

        private bool IsIdentifierStartAt(int position) =>
            TryGetRune(position, out var rune) && IsIdentifierStart(rune);

        private bool IsIdentifierContinueAt(int position) =>
            TryGetRune(position, out var rune) && IsIdentifierContinue(rune);

        private bool TryGetRune(int position, out Rune rune)
        {
            if (position >= 0 && position < _text.Length)
            {
                return Rune.TryGetRuneAt(_text, position, out rune);
            }

            rune = default;
            return false;
        }

        private static bool IsIdentifierStart(Rune rune)
        {
            if (rune.Value == '_')
            {
                return true;
            }

            var category = Rune.GetUnicodeCategory(rune);
            return category
                    is UnicodeCategory.UppercaseLetter
                        or UnicodeCategory.LowercaseLetter
                        or UnicodeCategory.TitlecaseLetter
                        or UnicodeCategory.ModifierLetter
                        or UnicodeCategory.OtherLetter
                        or UnicodeCategory.LetterNumber
                || rune.Value is 0x1885 or 0x1886 or 0x2118 or 0x212E or 0x309B or 0x309C;
        }

        private static bool IsIdentifierContinue(Rune rune)
        {
            if (IsIdentifierStart(rune))
            {
                return true;
            }

            var category = Rune.GetUnicodeCategory(rune);
            return category
                    is UnicodeCategory.NonSpacingMark
                        or UnicodeCategory.SpacingCombiningMark
                        or UnicodeCategory.DecimalDigitNumber
                        or UnicodeCategory.ConnectorPunctuation
                || rune.Value is 0x00B7 or 0x0387 or 0x19DA
                || rune.Value is >= 0x1369 and <= 0x1371;
        }

        private static bool IsValidOneCharacterStringPrefix(char value) =>
            char.ToLowerInvariant(value) is 'b' or 'f' or 'r' or 't' or 'u';

        private static bool IsValidTwoCharacterStringPrefix(char first, char second)
        {
            first = char.ToLowerInvariant(first);
            second = char.ToLowerInvariant(second);
            return (first, second)
                is
                    ('b', 'r')
                    or
                    ('r', 'b')
                    or
                    ('f', 'r')
                    or
                    ('r', 'f')
                    or
                    ('t', 'r')
                    or
                    ('r', 't');
        }

        private static SyntaxTokenKind GetStringKind(ReadOnlySpan<char> prefix)
        {
            foreach (var character in prefix)
            {
                switch (char.ToLowerInvariant(character))
                {
                    case 'b':
                        return SyntaxTokenKind.BytesLiteral;
                    case 'f':
                        return SyntaxTokenKind.FormattedStringLiteral;
                    case 't':
                        return SyntaxTokenKind.TemplateStringLiteral;
                }
            }

            return SyntaxTokenKind.StringLiteral;
        }

        private static bool IsQuote(char value) => value is '\'' or '"';

        private bool HasRepeatedQuote(int position, char quote) =>
            position + 2 < _text.Length
            && _text[position + 1] == quote
            && _text[position + 2] == quote;

        private bool MatchesQuote(int position, char quote, int quoteLength)
        {
            if (position >= _text.Length || _text[position] != quote)
            {
                return false;
            }

            return quoteLength == 1 || HasRepeatedQuote(position, quote);
        }

        private static bool IsHorizontalWhitespace(char value) => value is ' ' or '\t' or '\f';

        private static bool IsNewLine(char value) => value is '\r' or '\n';

        private static bool IsBasePrefix(char value) =>
            char.ToLowerInvariant(value) is 'b' or 'o' or 'x';

        private static bool IsBinaryDigit(char value) => value is '0' or '1';

        private static bool IsOctalDigit(char value) => value is >= '0' and <= '7';

        private static bool IsDecimalDigit(char value) => char.IsAsciiDigit(value);

        private static bool IsDecimalDigitPartCharacter(char value) =>
            char.IsAsciiDigit(value) || value == '_';

        private static bool IsHexadecimalDigit(char value) => char.IsAsciiHexDigit(value);

        private static bool DelimitersMatch(SyntaxTokenKind opening, SyntaxTokenKind closing) =>
            (opening, closing)
                is
                    (SyntaxTokenKind.LeftParenthesis, SyntaxTokenKind.RightParenthesis)
                    or
                    (SyntaxTokenKind.LeftBracket, SyntaxTokenKind.RightBracket)
                    or
                    (SyntaxTokenKind.LeftBrace, SyntaxTokenKind.RightBrace);

        private static string FormatCharacter(ReadOnlySpan<char> character) =>
            character.Length == 1 && char.IsControl(character[0])
                ? $"U+{(int)character[0]:X4}"
                : $"'{character.ToString()}'";

        private readonly record struct IndentationLevel(int Column, int AlternateColumn);

        private readonly record struct Delimiter(SyntaxTokenKind Kind, int Position);
    }
}
