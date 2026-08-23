using System.Collections.ObjectModel;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Syntax;
using DotPython.Language.Text;
using DotPython.ParserGenerator.Generation;

namespace DotPython.ParserGenerator;

public static class PythonParser
{
    public static PythonParseResult Parse(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tokenization = PythonTokenizer.Tokenize(source);
        var executableTokens = tokenization
            .Tokens.Where(token => token.Kind != SyntaxTokenKind.NonSignificantNewLine)
            .ToArray();
        var grammarMatch = GeneratedPythonGrammar.Grammar.Match(executableTokens);
        var result = new Parser(tokenization).Parse();
        if (!result.Success || grammarMatch.Success)
        {
            return result;
        }

        var tokenIndex = Math.Min(grammarMatch.FurthestTokenIndex, executableTokens.Length - 1);
        var diagnostics = new List<Diagnostic>(result.Diagnostics)
        {
            new(
                "DPY2011",
                "The generated Python grammar rejected syntax accepted by the AST builder.",
                DiagnosticSeverity.Error,
                executableTokens[tokenIndex].Span
            ),
        };
        return new PythonParseResult(source, result.Module, diagnostics);
    }

    private sealed class Parser
    {
        private readonly List<Diagnostic> _diagnostics;
        private readonly SourceText _source;
        private readonly SyntaxToken[] _tokens;
        private int _functionDepth;
        private readonly List<bool> _functionYieldFlags = [];
        private readonly List<bool> _functionAsyncFlags = [];
        private int _position;

        internal Parser(TokenizationResult tokenization)
        {
            _source = tokenization.Source;
            _diagnostics = [.. tokenization.Diagnostics];
            _tokens =
            [
                .. tokenization.Tokens.Where(token =>
                    token.Kind != SyntaxTokenKind.NonSignificantNewLine
                ),
            ];
        }

        internal PythonParseResult Parse()
        {
            var statements = ParseStatements(stopAtDedent: false);
            var moduleSpan =
                statements.Count == 0
                    ? new TextSpan(0, 0)
                    : TextSpan.FromBounds(statements[0].Span.Start, statements[^1].Span.End);
            var module = new PythonModule(statements, moduleSpan);
            return new PythonParseResult(_source, module, _diagnostics);
        }

        private ReadOnlyCollection<PythonStatement> ParseStatements(bool stopAtDedent)
        {
            var statements = new List<PythonStatement>();
            SkipNewLines();

            while (
                Current.Kind != SyntaxTokenKind.EndOfFile
                && !(stopAtDedent && Current.Kind == SyntaxTokenKind.Dedent)
            )
            {
                var start = _position;

                if (Current.Kind == SyntaxTokenKind.Indent)
                {
                    Report("DPY2006", "Unexpected indentation.", Current.Span);
                    _position++;
                }
                else if (Current.Kind == SyntaxTokenKind.Dedent)
                {
                    Report("DPY2007", "Unexpected dedent.", Current.Span);
                    _position++;
                }
                else if (IsKeyword("if"))
                {
                    statements.Add(ParseIfStatement());
                }
                else if (IsKeyword("while"))
                {
                    statements.Add(ParseWhileStatement());
                }
                else if (IsKeyword("for"))
                {
                    statements.Add(ParseForStatement());
                }
                else if (IsKeyword("with"))
                {
                    statements.Add(ParseWithStatement());
                }
                else if (IsKeyword("try"))
                {
                    statements.Add(ParseTryStatement());
                }
                else if (IsKeyword("def"))
                {
                    statements.Add(ParseFunctionDefinition(Array.Empty<PythonExpression>()));
                }
                else if (IsKeyword("class"))
                {
                    statements.Add(ParseClassDefinition(Array.Empty<PythonExpression>()));
                }
                else if (IsKeyword("async"))
                {
                    var asyncStatement = ParseAsyncStatement();
                    if (asyncStatement is not null)
                    {
                        statements.Add(asyncStatement);
                    }
                }
                else if (LooksLikeMatchStatement())
                {
                    statements.Add(ParseMatchStatement());
                }
                else if (Current.Kind == SyntaxTokenKind.At)
                {
                    var decorated = ParseDecoratedDefinition();
                    if (decorated is not null)
                    {
                        statements.Add(decorated);
                    }
                }
                else
                {
                    ParseSimpleStatements(statements);
                }

                if (_position == start)
                {
                    ReportExpected("a statement", Current.Span);
                    _position++;
                }

                SkipNewLines();
            }

            return statements.AsReadOnly();
        }

        private PythonIfStatement ParseIfStatement()
        {
            var start = Advance().Span.Start;
            var clauses = new List<PythonConditionalClause>();

            while (true)
            {
                var condition = ParseRequiredExpression("a condition after 'if' or 'elif'");
                var colon = Expect(SyntaxTokenKind.Colon, "':' after the condition");
                var body = ParseSuite();
                var clauseEnd = GetBodyEnd(body, colon.Span.End);
                clauses.Add(
                    new PythonConditionalClause(
                        condition,
                        body,
                        TextSpan.FromBounds(condition.Span.Start, clauseEnd)
                    )
                );

                if (!MatchKeyword("elif", out _))
                {
                    break;
                }
            }

            IReadOnlyList<PythonStatement> elseBody = Array.Empty<PythonStatement>();
            var end = clauses[^1].Span.End;
            if (MatchKeyword("else", out _))
            {
                var colon = Expect(SyntaxTokenKind.Colon, "':' after 'else'");
                elseBody = ParseSuite();
                end = GetBodyEnd(elseBody, colon.Span.End);
            }

            return new PythonIfStatement(
                clauses.AsReadOnly(),
                elseBody,
                TextSpan.FromBounds(start, end)
            );
        }

        private PythonWhileStatement ParseWhileStatement()
        {
            var start = Advance().Span.Start;
            var condition = ParseRequiredExpression("a condition after 'while'");
            var colon = Expect(SyntaxTokenKind.Colon, "':' after the condition");
            var body = ParseSuite();

            IReadOnlyList<PythonStatement> elseBody = Array.Empty<PythonStatement>();
            var end = GetBodyEnd(body, colon.Span.End);
            if (MatchKeyword("else", out _))
            {
                var elseColon = Expect(SyntaxTokenKind.Colon, "':' after 'else'");
                elseBody = ParseSuite();
                end = GetBodyEnd(elseBody, elseColon.Span.End);
            }

            return new PythonWhileStatement(
                condition,
                body,
                elseBody,
                TextSpan.FromBounds(start, end)
            );
        }

        private PythonForStatement ParseForStatement()
        {
            var start = Advance().Span.Start;
            var target = ParseForTargets();
            if (!MatchKeyword("in", out _))
            {
                ReportExpected("'in' after the loop target", Current.Span);
            }

            var iterable = ParseRequiredExpressionList("an iterable after 'in'");
            var colon = Expect(SyntaxTokenKind.Colon, "':' after the iterable");
            var body = ParseSuite();

            IReadOnlyList<PythonStatement> elseBody = Array.Empty<PythonStatement>();
            var end = GetBodyEnd(body, colon.Span.End);
            if (MatchKeyword("else", out _))
            {
                var elseColon = Expect(SyntaxTokenKind.Colon, "':' after 'else'");
                elseBody = ParseSuite();
                end = GetBodyEnd(elseBody, elseColon.Span.End);
            }

            return new PythonForStatement(
                target,
                iterable,
                body,
                elseBody,
                TextSpan.FromBounds(start, end)
            );
        }

        private PythonExpression ParseForTargets()
        {
            var first = ParseForTargetAtom();
            if (Current.Kind != SyntaxTokenKind.Comma)
            {
                return first;
            }

            var elements = new List<PythonExpression> { first };
            while (Match(SyntaxTokenKind.Comma))
            {
                if (IsKeyword("in") || Current.Kind == SyntaxTokenKind.Colon)
                {
                    break;
                }

                elements.Add(ParseForTargetAtom());
            }

            return new PythonTupleExpression(
                elements.AsReadOnly(),
                TextSpan.FromBounds(elements[0].Span.Start, elements[^1].Span.End)
            );
        }

        private PythonExpression ParseForTargetAtom()
        {
            if (Match(SyntaxTokenKind.LeftParenthesis, out var leftParenthesis))
            {
                var inner = ParseForTargets();
                var end = ExpectClosingDelimiter(
                    SyntaxTokenKind.RightParenthesis,
                    "')'",
                    inner.Span.End
                );
                return inner is PythonTupleExpression tuple
                    ? tuple with
                    {
                        Span = TextSpan.FromBounds(leftParenthesis.Span.Start, end),
                    }
                    : inner;
            }

            if (Match(SyntaxTokenKind.Star, out var starToken))
            {
                var starred = ParseForTargetAtom();
                return new PythonStarredExpression(
                    starred,
                    TextSpan.FromBounds(starToken.Span.Start, starred.Span.End)
                );
            }

            var targetToken = Expect(SyntaxTokenKind.Identifier, "a target after 'for'");
            if (targetToken.Text.Length != 0 && IsReservedKeyword(targetToken.Text))
            {
                Report(
                    "DPY2010",
                    $"The keyword '{targetToken.Text}' cannot be used as a loop target.",
                    targetToken.Span
                );
            }

            return new PythonNameExpression(targetToken.Text, targetToken.Span);
        }

        /// <summary>
        /// `match` is a soft keyword: this is a match statement only when the next token
        /// can start a subject expression and the logical line ends with ':' at bracket
        /// depth zero. `match = 5`, `match(x)`, and `match[0]: int = 5` stay expressions.
        /// </summary>
        private bool LooksLikeMatchStatement()
        {
            if (Current.Kind != SyntaxTokenKind.Identifier || Current.Text != "match")
            {
                return false;
            }

            var next = Peek(1);
            var startsSubject = next.Kind switch
            {
                SyntaxTokenKind.Identifier => next.Text is "not" or "lambda"
                    || !IsExpressionKeyword(next.Text),
                SyntaxTokenKind.IntegerLiteral
                or SyntaxTokenKind.FloatLiteral
                or SyntaxTokenKind.ImaginaryLiteral
                or SyntaxTokenKind.StringLiteral
                or SyntaxTokenKind.BytesLiteral
                or SyntaxTokenKind.FormattedStringLiteral
                or SyntaxTokenKind.TemplateStringLiteral
                or SyntaxTokenKind.LeftParenthesis
                or SyntaxTokenKind.LeftBracket
                or SyntaxTokenKind.LeftBrace
                or SyntaxTokenKind.Minus
                or SyntaxTokenKind.Plus
                or SyntaxTokenKind.Star => true,
                _ => false,
            };
            if (!startsSubject)
            {
                return false;
            }

            return LogicalLineEndsWithColon(offset: 1);
        }

        private bool LogicalLineEndsWithColon(int offset)
        {
            var depth = 0;
            var sawColonLast = false;
            for (var index = offset; ; index++)
            {
                var token = Peek(index);
                switch (token.Kind)
                {
                    case SyntaxTokenKind.LeftParenthesis
                    or SyntaxTokenKind.LeftBracket
                    or SyntaxTokenKind.LeftBrace:
                        depth++;
                        sawColonLast = false;
                        break;
                    case SyntaxTokenKind.RightParenthesis
                    or SyntaxTokenKind.RightBracket
                    or SyntaxTokenKind.RightBrace:
                        depth--;
                        sawColonLast = false;
                        break;
                    case SyntaxTokenKind.NewLine or SyntaxTokenKind.EndOfFile:
                        return sawColonLast;
                    case SyntaxTokenKind.Colon when depth == 0:
                        sawColonLast = true;
                        break;
                    default:
                        sawColonLast = false;
                        break;
                }
            }
        }

        private PythonMatchStatement ParseMatchStatement()
        {
            var start = Advance().Span.Start;
            var subject = ParseRequiredExpressionList("a subject after 'match'");
            Expect(SyntaxTokenKind.Colon, "':' after the match subject");
            if (!Match(SyntaxTokenKind.NewLine))
            {
                ReportExpected("a new line after ':'", Current.Span);
            }

            SkipNewLines();
            if (!Match(SyntaxTokenKind.Indent))
            {
                ReportExpected("an indented case block", Current.Span);
                return new PythonMatchStatement(
                    subject,
                    [],
                    TextSpan.FromBounds(start, subject.Span.End)
                );
            }

            var cases = new List<PythonMatchCase>();
            while (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "case")
            {
                cases.Add(ParseCaseBlock());
                SkipNewLines();
            }

            if (cases.Count == 0)
            {
                Report("DPY2024", "A match statement requires at least one case.", Current.Span);
            }

            if (!Match(SyntaxTokenKind.Dedent))
            {
                ReportExpected("the end of the match statement", Current.Span);
            }

            var end = cases.Count == 0 ? subject.Span.End : cases[^1].Span.End;
            return new PythonMatchStatement(
                subject,
                cases.AsReadOnly(),
                TextSpan.FromBounds(start, end)
            );
        }

        private PythonMatchCase ParseCaseBlock()
        {
            var start = Advance().Span.Start;
            var pattern = ParseStarCasePattern();
            if (Current.Kind == SyntaxTokenKind.Comma || pattern is PythonSequenceStarMarker)
            {
                var elements = new List<PythonPattern> { pattern };
                while (Match(SyntaxTokenKind.Comma))
                {
                    if (Current.Kind == SyntaxTokenKind.Colon || IsKeyword("if"))
                    {
                        break;
                    }

                    elements.Add(ParseStarCasePattern());
                }

                pattern = BuildSequencePattern(
                    elements,
                    TextSpan.FromBounds(elements[0].Span.Start, elements[^1].Span.End)
                );
            }

            PythonExpression? guard = null;
            if (MatchKeyword("if", out _))
            {
                guard = ParseRequiredExpression("a guard condition after 'if'");
            }

            var colon = Expect(SyntaxTokenKind.Colon, "':' after the case pattern");
            var body = ParseSuite();
            return new PythonMatchCase(
                pattern,
                guard,
                body,
                TextSpan.FromBounds(start, GetBodyEnd(body, colon.Span.End))
            );
        }

        /// <summary>Internal marker for a parsed `*name` element; never leaves the parser.</summary>
        private sealed record PythonSequenceStarMarker(string? Name, TextSpan Span)
            : PythonPattern(Span);

        private PythonPattern ParseStarCasePattern()
        {
            if (Match(SyntaxTokenKind.Star, out var starToken))
            {
                var nameToken = Expect(SyntaxTokenKind.Identifier, "a name after '*'");
                return new PythonSequenceStarMarker(
                    nameToken.Text == "_" ? null : nameToken.Text,
                    TextSpan.FromBounds(starToken.Span.Start, nameToken.Span.End)
                );
            }

            return ParseAsPattern();
        }

        private PythonSequencePattern BuildSequencePattern(
            List<PythonPattern> elements,
            TextSpan span
        )
        {
            var starIndex = -1;
            string? starName = null;
            var patterns = new List<PythonPattern>();
            for (var index = 0; index < elements.Count; index++)
            {
                if (elements[index] is PythonSequenceStarMarker marker)
                {
                    if (starIndex >= 0)
                    {
                        Report(
                            "DPY2024",
                            "Only one starred sub-pattern is allowed in a sequence pattern.",
                            marker.Span
                        );
                        continue;
                    }

                    starIndex = patterns.Count;
                    starName = marker.Name;
                }
                else
                {
                    patterns.Add(elements[index]);
                }
            }

            return new PythonSequencePattern(patterns.AsReadOnly(), starIndex, starName, span);
        }

        private PythonClassPattern ParseClassPattern(PythonExpression cls)
        {
            Advance();
            var positional = new List<PythonPattern>();
            var keyword = new List<PythonClassPatternKeyword>();
            while (Current.Kind != SyntaxTokenKind.RightParenthesis)
            {
                if (
                    Current.Kind == SyntaxTokenKind.Identifier
                    && Peek(1).Kind == SyntaxTokenKind.Equal
                    && !IsReservedKeyword(Current.Text)
                )
                {
                    var nameToken = Advance();
                    Advance();
                    var valuePattern = ParseAsPattern();
                    keyword.Add(
                        new PythonClassPatternKeyword(
                            nameToken.Text,
                            valuePattern,
                            TextSpan.FromBounds(nameToken.Span.Start, valuePattern.Span.End)
                        )
                    );
                }
                else
                {
                    if (keyword.Count != 0)
                    {
                        Report(
                            "DPY2024",
                            "A positional sub-pattern follows a keyword sub-pattern.",
                            Current.Span
                        );
                    }

                    positional.Add(ParseAsPattern());
                }

                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }
            }

            var end = ExpectClosingDelimiter(SyntaxTokenKind.RightParenthesis, "')'", cls.Span.End);
            return new PythonClassPattern(
                cls,
                positional.AsReadOnly(),
                keyword.AsReadOnly(),
                TextSpan.FromBounds(cls.Span.Start, end)
            );
        }

        private PythonPattern ParseAsPattern()
        {
            var pattern = ParseOrPattern();
            if (MatchKeyword("as", out _))
            {
                var nameToken = Expect(SyntaxTokenKind.Identifier, "a name after 'as'");
                if (IsReservedKeyword(nameToken.Text))
                {
                    Report(
                        "DPY2010",
                        $"The keyword '{nameToken.Text}' cannot be used as a capture name.",
                        nameToken.Span
                    );
                }

                return new PythonAsPattern(
                    pattern,
                    nameToken.Text,
                    TextSpan.FromBounds(pattern.Span.Start, nameToken.Span.End)
                );
            }

            return pattern;
        }

        private PythonPattern ParseOrPattern()
        {
            var first = ParseClosedPattern();
            if (Current.Kind != SyntaxTokenKind.VerticalBar)
            {
                return first;
            }

            var alternatives = new List<PythonPattern> { first };
            while (Match(SyntaxTokenKind.VerticalBar))
            {
                alternatives.Add(ParseClosedPattern());
            }

            return new PythonOrPattern(
                alternatives.AsReadOnly(),
                TextSpan.FromBounds(first.Span.Start, alternatives[^1].Span.End)
            );
        }

        private PythonPattern ParseClosedPattern()
        {
            if (Match(SyntaxTokenKind.LeftParenthesis, out var leftParenthesisToken))
            {
                if (Match(SyntaxTokenKind.RightParenthesis, out var emptyClose))
                {
                    return new PythonSequencePattern(
                        [],
                        -1,
                        null,
                        TextSpan.FromBounds(leftParenthesisToken.Span.Start, emptyClose.Span.End)
                    );
                }

                var first = ParseStarCasePattern();
                if (Current.Kind != SyntaxTokenKind.Comma && first is not PythonSequenceStarMarker)
                {
                    ExpectClosingDelimiter(SyntaxTokenKind.RightParenthesis, "')'", first.Span.End);
                    return first;
                }

                var elements = new List<PythonPattern> { first };
                while (Match(SyntaxTokenKind.Comma))
                {
                    if (Current.Kind == SyntaxTokenKind.RightParenthesis)
                    {
                        break;
                    }

                    elements.Add(ParseStarCasePattern());
                }

                var tupleEnd = ExpectClosingDelimiter(
                    SyntaxTokenKind.RightParenthesis,
                    "')'",
                    elements[^1].Span.End
                );
                return BuildSequencePattern(
                    elements,
                    TextSpan.FromBounds(leftParenthesisToken.Span.Start, tupleEnd)
                );
            }

            if (Match(SyntaxTokenKind.LeftBracket, out var leftBracketToken))
            {
                var elements = new List<PythonPattern>();
                while (Current.Kind != SyntaxTokenKind.RightBracket)
                {
                    elements.Add(ParseStarCasePattern());
                    if (!Match(SyntaxTokenKind.Comma))
                    {
                        break;
                    }
                }

                var end = ExpectClosingDelimiter(
                    SyntaxTokenKind.RightBracket,
                    "']'",
                    elements.Count == 0 ? leftBracketToken.Span.End : elements[^1].Span.End
                );
                return BuildSequencePattern(
                    elements,
                    TextSpan.FromBounds(leftBracketToken.Span.Start, end)
                );
            }

            if (Match(SyntaxTokenKind.LeftBrace, out var leftBraceToken))
            {
                var items = new List<PythonMappingPatternItem>();
                string? restName = null;
                while (Current.Kind != SyntaxTokenKind.RightBrace)
                {
                    if (Match(SyntaxTokenKind.DoubleStar, out var restToken))
                    {
                        var restIdentifier = Expect(
                            SyntaxTokenKind.Identifier,
                            "a name after '**'"
                        );
                        if (restName is not null)
                        {
                            Report(
                                "DPY2024",
                                "Only one '**' rest sub-pattern is allowed.",
                                restToken.Span
                            );
                        }

                        restName = restIdentifier.Text;
                    }
                    else
                    {
                        var key = ParseDisjunction();
                        if (key is null)
                        {
                            ReportExpected("a mapping pattern key", Current.Span);
                            break;
                        }

                        Expect(SyntaxTokenKind.Colon, "':' after the mapping pattern key");
                        var valuePattern = ParseAsPattern();
                        items.Add(
                            new PythonMappingPatternItem(
                                key,
                                valuePattern,
                                TextSpan.FromBounds(key.Span.Start, valuePattern.Span.End)
                            )
                        );
                    }

                    if (!Match(SyntaxTokenKind.Comma))
                    {
                        break;
                    }
                }

                var braceEnd = ExpectClosingDelimiter(
                    SyntaxTokenKind.RightBrace,
                    "'}'",
                    items.Count == 0 ? leftBraceToken.Span.End : items[^1].Span.End
                );
                return new PythonMappingPattern(
                    items.AsReadOnly(),
                    restName,
                    TextSpan.FromBounds(leftBraceToken.Span.Start, braceEnd)
                );
            }

            if (
                Current.Kind
                    is SyntaxTokenKind.IntegerLiteral
                        or SyntaxTokenKind.FloatLiteral
                        or SyntaxTokenKind.ImaginaryLiteral
                        or SyntaxTokenKind.StringLiteral
                        or SyntaxTokenKind.BytesLiteral
                || (
                    Current.Kind is SyntaxTokenKind.Minus or SyntaxTokenKind.Plus
                    && Peek(1).Kind
                        is SyntaxTokenKind.IntegerLiteral
                            or SyntaxTokenKind.FloatLiteral
                            or SyntaxTokenKind.ImaginaryLiteral
                )
            )
            {
                var literal = ParseDisjunction();
                if (literal is null)
                {
                    ReportExpected("a literal pattern", Current.Span);
                    return new PythonCapturePattern(null, Current.Span);
                }

                return new PythonLiteralPattern(literal, UseIdentity: false, literal.Span);
            }

            if (Current.Kind == SyntaxTokenKind.Identifier)
            {
                if (Current.Text is "None" or "True" or "False")
                {
                    var constant = ParseDisjunction()!;
                    return new PythonLiteralPattern(constant, UseIdentity: true, constant.Span);
                }

                if (IsReservedKeyword(Current.Text) || IsExpressionKeyword(Current.Text))
                {
                    Report("DPY2024", $"'{Current.Text}' cannot start a pattern.", Current.Span);
                    return new PythonCapturePattern(null, Advance().Span);
                }

                var nameToken = Advance();
                PythonExpression reference = new PythonNameExpression(
                    nameToken.Text,
                    nameToken.Span
                );
                var isDotted = Current.Kind == SyntaxTokenKind.Dot;
                while (Match(SyntaxTokenKind.Dot))
                {
                    var attribute = Expect(
                        SyntaxTokenKind.Identifier,
                        "an attribute name after '.'"
                    );
                    reference = new PythonAttributeExpression(
                        reference,
                        attribute.Text,
                        TextSpan.FromBounds(reference.Span.Start, attribute.Span.End)
                    );
                }

                if (Current.Kind == SyntaxTokenKind.LeftParenthesis)
                {
                    return ParseClassPattern(reference);
                }

                if (isDotted)
                {
                    return new PythonValuePattern(reference, reference.Span);
                }

                return new PythonCapturePattern(
                    nameToken.Text == "_" ? null : nameToken.Text,
                    nameToken.Span
                );
            }

            ReportExpected("a pattern", Current.Span);
            return new PythonCapturePattern(null, Current.Span);
        }

        private PythonWithStatement ParseWithStatement()
        {
            var start = Advance().Span.Start;
            var items = new List<PythonWithItem>();
            while (true)
            {
                var context = ParseRequiredExpression("a context manager after 'with'");
                PythonExpression? target = null;
                if (MatchKeyword("as", out _))
                {
                    target = ParseExpression();
                    if (target is null)
                    {
                        ReportExpected("a target after 'as'", Current.Span);
                    }
                    else if (!IsAssignableTarget(target))
                    {
                        Report("DPY2005", "This expression cannot be assigned to.", target.Span);
                        target = null;
                    }
                }

                items.Add(
                    new PythonWithItem(
                        context,
                        target,
                        TextSpan.FromBounds(
                            context.Span.Start,
                            target?.Span.End ?? context.Span.End
                        )
                    )
                );
                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }
            }

            var colon = Expect(SyntaxTokenKind.Colon, "':' after the context managers");
            var body = ParseSuite();
            var end = GetBodyEnd(body, colon.Span.End);
            return new PythonWithStatement(
                items.AsReadOnly(),
                body,
                TextSpan.FromBounds(start, end)
            );
        }

        private PythonTryStatement ParseTryStatement()
        {
            var start = Advance().Span.Start;
            var colon = Expect(SyntaxTokenKind.Colon, "':' after 'try'");
            var body = ParseSuite();
            var handlers = new List<PythonExceptHandler>();
            var sawBareHandler = false;
            var end = GetBodyEnd(body, colon.Span.End);

            while (MatchKeyword("except", out var exceptToken))
            {
                PythonExpression? type = null;
                PythonNameExpression? target = null;
                if (Current.Kind != SyntaxTokenKind.Colon)
                {
                    type = ParseRequiredExpression("an exception type after 'except'");
                    if (MatchKeyword("as", out _))
                    {
                        var targetToken = Expect(SyntaxTokenKind.Identifier, "a target after 'as'");
                        if (IsReservedKeyword(targetToken.Text))
                        {
                            Report(
                                "DPY2010",
                                $"The keyword '{targetToken.Text}' cannot be used as an exception target.",
                                targetToken.Span
                            );
                        }

                        target = new PythonNameExpression(targetToken.Text, targetToken.Span);
                    }
                }
                else
                {
                    if (sawBareHandler)
                    {
                        Report(
                            "DPY2012",
                            "A try statement cannot contain more than one bare except clause.",
                            exceptToken.Span
                        );
                    }

                    sawBareHandler = true;
                }

                var handlerColon = Expect(SyntaxTokenKind.Colon, "':' after the except clause");
                var handlerBody = ParseSuite();
                end = GetBodyEnd(handlerBody, handlerColon.Span.End);
                handlers.Add(
                    new PythonExceptHandler(
                        type,
                        target,
                        handlerBody,
                        TextSpan.FromBounds(exceptToken.Span.Start, end)
                    )
                );

                if (sawBareHandler && IsKeyword("except"))
                {
                    Report(
                        "DPY2013",
                        "A bare except clause must be the last exception handler.",
                        Current.Span
                    );
                }
            }

            IReadOnlyList<PythonStatement> elseBody = Array.Empty<PythonStatement>();
            if (MatchKeyword("else", out var elseToken))
            {
                if (handlers.Count == 0)
                {
                    Report(
                        "DPY2014",
                        "A try statement requires an except clause before 'else'.",
                        elseToken.Span
                    );
                }

                var elseColon = Expect(SyntaxTokenKind.Colon, "':' after 'else'");
                elseBody = ParseSuite();
                end = GetBodyEnd(elseBody, elseColon.Span.End);
            }

            IReadOnlyList<PythonStatement> finallyBody = Array.Empty<PythonStatement>();
            if (MatchKeyword("finally", out _))
            {
                var finallyColon = Expect(SyntaxTokenKind.Colon, "':' after 'finally'");
                finallyBody = ParseSuite();
                end = GetBodyEnd(finallyBody, finallyColon.Span.End);
            }

            if (handlers.Count == 0 && finallyBody.Count == 0)
            {
                Report(
                    "DPY2015",
                    "A try statement requires at least one except or finally clause.",
                    new TextSpan(start, Math.Max(1, end - start))
                );
            }

            return new PythonTryStatement(
                body,
                handlers.AsReadOnly(),
                elseBody,
                finallyBody,
                TextSpan.FromBounds(start, end)
            );
        }

        private PythonStatement? ParseDecoratedDefinition()
        {
            var start = Current.Span.Start;
            var decorators = new List<PythonExpression>();
            while (Current.Kind == SyntaxTokenKind.At)
            {
                Advance();
                var decorator = ParsePrimary();
                if (decorator is null)
                {
                    ReportExpected("a decorator expression after '@'", Current.Span);
                    SynchronizeLine();
                }
                else
                {
                    decorators.Add(decorator);
                    if (!Match(SyntaxTokenKind.NewLine))
                    {
                        ReportExpected("a new line after the decorator", Current.Span);
                        SynchronizeLine();
                    }
                }

                SkipNewLines();
            }

            if (IsKeyword("def"))
            {
                return ParseFunctionDefinition(decorators.AsReadOnly(), start);
            }

            if (IsKeyword("async"))
            {
                return ParseAsyncStatement(decorators.AsReadOnly(), start);
            }

            if (IsKeyword("class"))
            {
                return ParseClassDefinition(decorators.AsReadOnly(), start);
            }

            Report(
                "DPY2020",
                "Expected a function or class definition after the decorators.",
                Current.Span
            );
            return null;
        }

        private PythonStatement? ParseAsyncStatement(
            IReadOnlyList<PythonExpression>? decorators = null,
            int? decoratedStart = null
        )
        {
            var asyncToken = Advance();
            if (IsKeyword("def"))
            {
                return ParseFunctionDefinition(
                    decorators ?? Array.Empty<PythonExpression>(),
                    decoratedStart ?? asyncToken.Span.Start,
                    isAsync: true
                );
            }

            if (IsKeyword("for") || IsKeyword("with"))
            {
                Report(
                    "DPY2027",
                    $"The 'async {Current.Text}' statement is not supported.",
                    asyncToken.Span
                );
                return IsKeyword("for") ? ParseForStatement() : ParseWithStatement();
            }

            Report("DPY2027", "Expected 'def' after 'async'.", asyncToken.Span);
            return null;
        }

        private PythonFunctionDefinitionStatement ParseFunctionDefinition(
            IReadOnlyList<PythonExpression> decorators,
            int? decoratedStart = null,
            bool isAsync = false
        )
        {
            var start = decoratedStart ?? Current.Span.Start;
            Advance();
            var nameToken = Expect(SyntaxTokenKind.Identifier, "a function name after 'def'");
            if (IsReservedKeyword(nameToken.Text))
            {
                Report(
                    "DPY2010",
                    $"The keyword '{nameToken.Text}' cannot be used as a function name.",
                    nameToken.Span
                );
            }

            var name = new PythonNameExpression(nameToken.Text, nameToken.Span);
            Expect(SyntaxTokenKind.LeftParenthesis, "'(' after the function name");
            var parameters = ParseParameters(SyntaxTokenKind.RightParenthesis);
            Expect(SyntaxTokenKind.RightParenthesis, "')' after the parameters");
            PythonExpression? returnAnnotation = null;
            if (Match(SyntaxTokenKind.Arrow))
            {
                returnAnnotation = ParseExpression();
                if (returnAnnotation is null)
                {
                    ReportExpected("a return annotation after '->'", Current.Span);
                }
            }

            var colon = Expect(SyntaxTokenKind.Colon, "':' after the function signature");
            _functionDepth++;
            _functionYieldFlags.Add(false);
            _functionAsyncFlags.Add(isAsync);
            IReadOnlyList<PythonStatement> body;
            bool isGenerator;
            try
            {
                body = ParseSuite();
            }
            finally
            {
                _functionDepth--;
                isGenerator = _functionYieldFlags[^1];
                _functionYieldFlags.RemoveAt(_functionYieldFlags.Count - 1);
                _functionAsyncFlags.RemoveAt(_functionAsyncFlags.Count - 1);
            }

            return new PythonFunctionDefinitionStatement(
                decorators,
                name,
                parameters,
                body,
                TextSpan.FromBounds(start, GetBodyEnd(body, colon.Span.End)),
                returnAnnotation,
                isGenerator && !isAsync,
                isAsync
            );
        }

        private PythonClassDefinitionStatement ParseClassDefinition(
            IReadOnlyList<PythonExpression> decorators,
            int? decoratedStart = null
        )
        {
            var start = decoratedStart ?? Current.Span.Start;
            Advance();
            var nameToken = Expect(SyntaxTokenKind.Identifier, "a class name after 'class'");
            if (IsReservedKeyword(nameToken.Text))
            {
                Report(
                    "DPY2010",
                    $"The keyword '{nameToken.Text}' cannot be used as a class name.",
                    nameToken.Span
                );
            }

            var name = new PythonNameExpression(nameToken.Text, nameToken.Span);
            var bases = new List<PythonExpression>();
            if (Match(SyntaxTokenKind.LeftParenthesis))
            {
                while (Current.Kind != SyntaxTokenKind.RightParenthesis)
                {
                    var baseExpression = ParseExpression();
                    if (baseExpression is null)
                    {
                        ReportExpected("a base class expression", Current.Span);
                        break;
                    }

                    bases.Add(baseExpression);
                    if (!Match(SyntaxTokenKind.Comma))
                    {
                        break;
                    }
                }

                Expect(SyntaxTokenKind.RightParenthesis, "')' after the base classes");
            }

            var colon = Expect(SyntaxTokenKind.Colon, "':' after the class name");
            var enclosingFunctionDepth = _functionDepth;
            _functionDepth = 0;
            var enclosingYieldFlags = new List<bool>(_functionYieldFlags);
            _functionYieldFlags.Clear();
            var enclosingAsyncFlags = new List<bool>(_functionAsyncFlags);
            _functionAsyncFlags.Clear();
            IReadOnlyList<PythonStatement> body;
            try
            {
                body = ParseSuite();
            }
            finally
            {
                _functionDepth = enclosingFunctionDepth;
                _functionYieldFlags.Clear();
                _functionYieldFlags.AddRange(enclosingYieldFlags);
                _functionAsyncFlags.Clear();
                _functionAsyncFlags.AddRange(enclosingAsyncFlags);
            }

            return new PythonClassDefinitionStatement(
                decorators,
                name,
                bases.AsReadOnly(),
                body,
                TextSpan.FromBounds(start, GetBodyEnd(body, colon.Span.End))
            );
        }

        private IReadOnlyList<PythonStatement> ParseSuite()
        {
            if (!Match(SyntaxTokenKind.NewLine))
            {
                var statements = new List<PythonStatement>();
                ParseSimpleStatements(statements);
                if (statements.Count == 0)
                {
                    ReportExpected("a simple statement suite", Current.Span);
                }

                return statements.AsReadOnly();
            }

            SkipNewLines();
            if (!Match(SyntaxTokenKind.Indent))
            {
                ReportExpected("an indented suite", Current.Span);
                return Array.Empty<PythonStatement>();
            }

            var body = ParseStatements(stopAtDedent: true);
            if (!Match(SyntaxTokenKind.Dedent))
            {
                ReportExpected("the end of the indented suite", Current.Span);
            }

            if (body.Count == 0)
            {
                ReportExpected("at least one statement in the suite", Current.Span);
            }

            return body;
        }

        private void ParseSimpleStatements(List<PythonStatement> statements)
        {
            while (
                Current.Kind
                    is not (
                        SyntaxTokenKind.NewLine
                        or SyntaxTokenKind.Dedent
                        or SyntaxTokenKind.EndOfFile
                    )
            )
            {
                var statement = ParseSimpleStatement();
                if (statement is not null)
                {
                    statements.Add(statement);
                }
                else
                {
                    SynchronizeStatement();
                }

                if (!Match(SyntaxTokenKind.Semicolon))
                {
                    break;
                }

                if (
                    Current.Kind
                    is SyntaxTokenKind.NewLine
                        or SyntaxTokenKind.Dedent
                        or SyntaxTokenKind.EndOfFile
                )
                {
                    break;
                }
            }

            if (Current.Kind == SyntaxTokenKind.NewLine)
            {
                _position++;
            }
            else if (Current.Kind is not (SyntaxTokenKind.Dedent or SyntaxTokenKind.EndOfFile))
            {
                Report(
                    "DPY2003",
                    "Expected a new line or semicolon after the statement.",
                    Current.Span
                );
                SynchronizeLine();
            }
        }

        private PythonStatement? ParseSimpleStatement()
        {
            if (IsKeyword("return"))
            {
                return ParseReturnStatement();
            }

            if (IsKeyword("break"))
            {
                return new PythonBreakStatement(Advance().Span);
            }

            if (IsKeyword("continue"))
            {
                return new PythonContinueStatement(Advance().Span);
            }

            if (IsKeyword("pass"))
            {
                return new PythonPassStatement(Advance().Span);
            }

            if (IsKeyword("assert"))
            {
                return ParseAssertStatement();
            }

            if (IsKeyword("del"))
            {
                return ParseDeleteStatement();
            }

            if (IsKeyword("global"))
            {
                return ParseScopeDeclarationStatement(isGlobal: true);
            }

            if (IsKeyword("nonlocal"))
            {
                return ParseScopeDeclarationStatement(isGlobal: false);
            }

            if (IsKeyword("raise"))
            {
                return ParseRaiseStatement();
            }

            if (IsKeyword("import"))
            {
                return ParseImportStatement();
            }

            if (IsKeyword("from"))
            {
                return ParseFromImportStatement();
            }

            if (
                Current.Kind == SyntaxTokenKind.Identifier
                && IsUnsupportedStatementKeyword(Current.Text)
            )
            {
                Report(
                    "DPY2004",
                    $"The '{Current.Text}' statement is not supported in this position.",
                    Current.Span
                );
                return null;
            }

            var expression =
                Current.Kind == SyntaxTokenKind.Star
                    ? ParsePossiblyStarredExpression()
                    : ParseExpression();
            if (expression is null)
            {
                return null;
            }

            if (
                Current.Kind == SyntaxTokenKind.Colon
                && expression
                    is PythonNameExpression
                        or PythonAttributeExpression
                        or PythonSubscriptionExpression
            )
            {
                Advance();
                var annotation = ParseRequiredExpression("an annotation after ':'");
                PythonExpression? annotatedValue = null;
                if (Match(SyntaxTokenKind.Equal))
                {
                    annotatedValue = ParseExpressionListValue();
                    if (annotatedValue is null)
                    {
                        ReportExpected("an expression after '='", Current.Span);
                    }
                }

                return new PythonAnnotatedAssignmentStatement(
                    expression,
                    annotation,
                    annotatedValue,
                    TextSpan.FromBounds(
                        expression.Span.Start,
                        (annotatedValue ?? annotation).Span.End
                    )
                );
            }

            if (Current.Kind == SyntaxTokenKind.Comma)
            {
                expression = ParseBareTupleTail(expression);
            }

            if (Match(SyntaxTokenKind.Equal))
            {
                var value = ParseExpressionListValue();
                if (value is null)
                {
                    ReportExpected("an expression after '='", Current.Span);
                    return null;
                }

                if (!IsAssignableTarget(expression))
                {
                    Report("DPY2005", "This expression cannot be assigned to.", expression.Span);
                    return null;
                }

                return new PythonAssignmentStatement(
                    expression,
                    value,
                    TextSpan.FromBounds(expression.Span.Start, value.Span.End)
                );
            }

            var augmentedOperator = Current.Kind switch
            {
                SyntaxTokenKind.PlusEqual => PythonBinaryOperator.Add,
                SyntaxTokenKind.MinusEqual => PythonBinaryOperator.Subtract,
                SyntaxTokenKind.StarEqual => PythonBinaryOperator.Multiply,
                SyntaxTokenKind.SlashEqual => PythonBinaryOperator.TrueDivide,
                SyntaxTokenKind.DoubleSlashEqual => PythonBinaryOperator.FloorDivide,
                SyntaxTokenKind.PercentEqual => PythonBinaryOperator.Modulo,
                SyntaxTokenKind.DoubleStarEqual => PythonBinaryOperator.Power,
                _ => (PythonBinaryOperator?)null,
            };
            if (augmentedOperator is not null)
            {
                Advance();
                var value = ParseExpressionListValue();
                if (value is null)
                {
                    ReportExpected(
                        "an expression after the augmented assignment operator",
                        Current.Span
                    );
                    return null;
                }

                if (
                    expression
                    is not (
                        PythonNameExpression
                        or PythonSubscriptionExpression
                        or PythonAttributeExpression
                    )
                )
                {
                    Report("DPY2005", "This expression cannot be assigned to.", expression.Span);
                    return null;
                }

                return new PythonAugmentedAssignmentStatement(
                    expression,
                    augmentedOperator.Value,
                    value,
                    TextSpan.FromBounds(expression.Span.Start, value.Span.End)
                );
            }

            return new PythonExpressionStatement(expression, expression.Span);
        }

        private PythonImportStatement ParseImportStatement()
        {
            var start = Advance().Span.Start;
            var imports = ParseImportAliases("a module name after 'import'", allowDotted: true);
            var end = imports.Count == 0 ? start : imports[^1].Span.End;
            return new PythonImportStatement(imports, TextSpan.FromBounds(start, end));
        }

        private PythonFromImportStatement ParseFromImportStatement()
        {
            var start = Advance().Span.Start;
            var (moduleName, moduleSpan) = ParseRelativeModuleName();
            if (!MatchKeyword("import", out _))
            {
                ReportExpected("'import' after the module name", Current.Span);
            }

            var parenthesized = Match(SyntaxTokenKind.LeftParenthesis, out _);
            var imports = ParseImportAliases(
                "a name after 'import'",
                allowDotted: false,
                allowTrailingComma: parenthesized
            );
            var end = imports.Count == 0 ? moduleSpan.End : imports[^1].Span.End;
            if (parenthesized)
            {
                end = ExpectClosingDelimiter(SyntaxTokenKind.RightParenthesis, "')'", end);
            }

            return new PythonFromImportStatement(
                moduleName,
                imports,
                TextSpan.FromBounds(start, end)
            );
        }

        private ReadOnlyCollection<PythonImportAlias> ParseImportAliases(
            string expected,
            bool allowDotted,
            bool allowTrailingComma = false
        )
        {
            var imports = new List<PythonImportAlias>();
            while (true)
            {
                var (name, nameSpan) = allowDotted
                    ? ParseDottedImportName(expected)
                    : ReadSimpleImportName(expected);
                SyntaxToken? alias = null;
                if (MatchKeyword("as", out _))
                {
                    alias = ExpectImportName("an alias after 'as'");
                }

                var end = alias?.Span.End ?? nameSpan.End;
                if (name.Length != 0)
                {
                    imports.Add(
                        new PythonImportAlias(
                            name,
                            alias is { Text.Length: > 0 } ? alias.Text : null,
                            TextSpan.FromBounds(nameSpan.Start, end)
                        )
                    );
                }

                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }

                if (allowTrailingComma && Current.Kind == SyntaxTokenKind.RightParenthesis)
                {
                    break;
                }
            }

            return imports.AsReadOnly();
        }

        private (string Name, TextSpan Span) ParseRelativeModuleName()
        {
            if (Current.Kind is not (SyntaxTokenKind.Dot or SyntaxTokenKind.Ellipsis))
            {
                return ParseDottedImportName("a module name after 'from'");
            }

            var start = Current.Span.Start;
            var dotCount = 0;
            var end = start;
            while (Current.Kind is SyntaxTokenKind.Dot or SyntaxTokenKind.Ellipsis)
            {
                var token = Advance();
                dotCount += token.Kind == SyntaxTokenKind.Ellipsis ? 3 : 1;
                end = token.Span.End;
            }

            var suffix = string.Empty;
            if (Current.Kind == SyntaxTokenKind.Identifier && !IsKeyword("import"))
            {
                (suffix, var suffixSpan) = ParseDottedImportName("a module name after the dots");
                end = suffixSpan.End;
            }

            return (new string('.', dotCount) + suffix, TextSpan.FromBounds(start, end));
        }

        private (string Name, TextSpan Span) ParseDottedImportName(string expected)
        {
            var first = ExpectImportName(expected);
            if (first.Text.Length == 0)
            {
                return (string.Empty, first.Span);
            }

            var parts = new List<string> { first.Text };
            var end = first.Span.End;
            while (Match(SyntaxTokenKind.Dot))
            {
                var part = ExpectImportName("a module name after '.'");
                if (part.Text.Length == 0)
                {
                    break;
                }

                parts.Add(part.Text);
                end = part.Span.End;
            }

            return (string.Join('.', parts), TextSpan.FromBounds(first.Span.Start, end));
        }

        private (string Name, TextSpan Span) ReadSimpleImportName(string expected)
        {
            var token = ExpectImportName(expected);
            return (token.Text, token.Span);
        }

        private SyntaxToken ExpectImportName(string expected)
        {
            var token = Expect(SyntaxTokenKind.Identifier, expected);
            if (token.Text.Length != 0 && IsReservedKeyword(token.Text))
            {
                Report(
                    "DPY2010",
                    $"The keyword '{token.Text}' cannot be used as an import name.",
                    token.Span
                );
            }

            return token;
        }

        private PythonAssertStatement ParseAssertStatement()
        {
            var assertToken = Advance();
            var condition = ParseRequiredExpression("a condition after 'assert'");
            PythonExpression? message = null;
            if (Match(SyntaxTokenKind.Comma))
            {
                message = ParseExpression();
                if (message is null)
                {
                    ReportExpected("a message after ','", Current.Span);
                }
            }

            var end = message?.Span.End ?? condition.Span.End;
            return new PythonAssertStatement(
                condition,
                message,
                TextSpan.FromBounds(assertToken.Span.Start, end)
            );
        }

        private PythonDeleteStatement ParseDeleteStatement()
        {
            var deleteToken = Advance();
            var targets = new List<PythonExpression>();
            while (true)
            {
                var target = ParseExpression();
                if (target is null)
                {
                    ReportExpected("a target after 'del'", Current.Span);
                    break;
                }

                if (
                    target
                    is not (
                        PythonNameExpression
                        or PythonSubscriptionExpression
                        or PythonAttributeExpression
                    )
                )
                {
                    Report("DPY2005", "This expression cannot be deleted.", target.Span);
                }
                else
                {
                    targets.Add(target);
                }

                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }

                if (!StartsExpression())
                {
                    break;
                }
            }

            var end = targets.Count == 0 ? deleteToken.Span.End : targets[^1].Span.End;
            return new PythonDeleteStatement(
                targets.AsReadOnly(),
                TextSpan.FromBounds(deleteToken.Span.Start, end)
            );
        }

        private PythonStatement ParseScopeDeclarationStatement(bool isGlobal)
        {
            var keywordToken = Advance();
            var names = new List<PythonNameExpression>();
            while (true)
            {
                var nameToken = Expect(
                    SyntaxTokenKind.Identifier,
                    $"a name after '{keywordToken.Text}'"
                );
                if (nameToken.Text.Length != 0)
                {
                    if (IsReservedKeyword(nameToken.Text))
                    {
                        Report(
                            "DPY2010",
                            $"The keyword '{nameToken.Text}' cannot be used as a declared name.",
                            nameToken.Span
                        );
                    }
                    else
                    {
                        names.Add(new PythonNameExpression(nameToken.Text, nameToken.Span));
                    }
                }

                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }
            }

            var end = names.Count == 0 ? keywordToken.Span.End : names[^1].Span.End;
            var span = TextSpan.FromBounds(keywordToken.Span.Start, end);
            return isGlobal
                ? new PythonGlobalStatement(names.AsReadOnly(), span)
                : new PythonNonlocalStatement(names.AsReadOnly(), span);
        }

        private PythonReturnStatement ParseReturnStatement()
        {
            var returnToken = Advance();
            if (_functionDepth == 0)
            {
                Report("DPY2008", "'return' outside function.", returnToken.Span);
            }

            if (
                Current.Kind
                is SyntaxTokenKind.Semicolon
                    or SyntaxTokenKind.NewLine
                    or SyntaxTokenKind.Dedent
                    or SyntaxTokenKind.EndOfFile
            )
            {
                return new PythonReturnStatement(null, returnToken.Span);
            }

            var value = ParseExpressionListValue();
            if (value is null)
            {
                ReportExpected("an expression after 'return'", Current.Span);
                return new PythonReturnStatement(null, returnToken.Span);
            }

            return new PythonReturnStatement(
                value,
                TextSpan.FromBounds(returnToken.Span.Start, value.Span.End)
            );
        }

        private PythonRaiseStatement ParseRaiseStatement()
        {
            var raiseToken = Advance();
            if (
                Current.Kind
                is SyntaxTokenKind.Semicolon
                    or SyntaxTokenKind.NewLine
                    or SyntaxTokenKind.Dedent
                    or SyntaxTokenKind.EndOfFile
            )
            {
                return new PythonRaiseStatement(null, null, raiseToken.Span);
            }

            var exception = ParseExpression();
            if (exception is null)
            {
                ReportExpected("an exception after 'raise'", Current.Span);
                return new PythonRaiseStatement(null, null, raiseToken.Span);
            }

            PythonExpression? cause = null;
            if (MatchKeyword("from", out _))
            {
                cause = ParseExpression();
                if (cause is null)
                {
                    ReportExpected("an exception after 'from'", Current.Span);
                }
            }

            return new PythonRaiseStatement(
                exception,
                cause,
                TextSpan.FromBounds(raiseToken.Span.Start, cause?.Span.End ?? exception.Span.End)
            );
        }

        private PythonExpression ParseRequiredExpression(string expected)
        {
            var expression = ParseExpression();
            if (expression is not null)
            {
                return expression;
            }

            ReportExpected(expected, Current.Span);
            return new PythonConstantExpression(
                PythonConstantKind.BooleanLiteral,
                "False",
                Current.Span
            );
        }

        private PythonExpression ParseRequiredExpressionList(string expected)
        {
            var expression = ParseExpressionListValue();
            if (expression is not null)
            {
                return expression;
            }

            ReportExpected(expected, Current.Span);
            return new PythonConstantExpression(
                PythonConstantKind.BooleanLiteral,
                "False",
                Current.Span
            );
        }

        private PythonExpression? ParseExpressionListValue()
        {
            var first = ParseExpression();
            if (first is null)
            {
                return null;
            }

            return Current.Kind == SyntaxTokenKind.Comma ? ParseBareTupleTail(first) : first;
        }

        private PythonExpression? ParsePossiblyStarredExpression()
        {
            if (Current.Kind != SyntaxTokenKind.Star)
            {
                return ParseExpression();
            }

            var starToken = Advance();
            var operand = ParseExpression();
            if (operand is null)
            {
                ReportExpected("an expression after '*'", Current.Span);
                return null;
            }

            return new PythonStarredExpression(
                operand,
                TextSpan.FromBounds(starToken.Span.Start, operand.Span.End)
            );
        }

        private PythonTupleExpression ParseBareTupleTail(PythonExpression first)
        {
            var elements = new List<PythonExpression> { first };
            var end = first.Span.End;
            while (Match(SyntaxTokenKind.Comma, out var comma))
            {
                end = comma.Span.End;
                if (!StartsExpression() && Current.Kind != SyntaxTokenKind.Star)
                {
                    break;
                }

                var element = ParsePossiblyStarredExpression();
                if (element is null)
                {
                    break;
                }

                elements.Add(element);
                end = element.Span.End;
            }

            return new PythonTupleExpression(
                elements.AsReadOnly(),
                TextSpan.FromBounds(first.Span.Start, end)
            );
        }

        private bool StartsExpression() =>
            Current.Kind switch
            {
                SyntaxTokenKind.Identifier => Current.Text is "not" or "lambda" or "yield"
                    || !IsExpressionKeyword(Current.Text),
                SyntaxTokenKind.IntegerLiteral
                or SyntaxTokenKind.FloatLiteral
                or SyntaxTokenKind.ImaginaryLiteral
                or SyntaxTokenKind.StringLiteral
                or SyntaxTokenKind.BytesLiteral
                or SyntaxTokenKind.FormattedStringLiteral
                or SyntaxTokenKind.TemplateStringLiteral
                or SyntaxTokenKind.LeftParenthesis
                or SyntaxTokenKind.LeftBracket
                or SyntaxTokenKind.LeftBrace
                or SyntaxTokenKind.Plus
                or SyntaxTokenKind.Minus
                or SyntaxTokenKind.Tilde => true,
                _ => false,
            };

        private static bool IsAssignableTarget(PythonExpression expression) =>
            expression switch
            {
                PythonNameExpression or PythonSubscriptionExpression or PythonAttributeExpression =>
                    true,
                PythonParenthesizedExpression parenthesized => IsAssignableTarget(
                    parenthesized.Expression
                ),
                PythonTupleExpression tuple => tuple.Elements.Count != 0
                    && tuple.Elements.All(element =>
                        element is PythonStarredExpression starred
                            ? IsAssignableTarget(starred.Operand)
                            : IsAssignableTarget(element)
                    )
                    && tuple.Elements.Count(element => element is PythonStarredExpression) <= 1,
                _ => false,
            };

        private PythonExpression? ParseExpression()
        {
            if (IsKeyword("lambda"))
            {
                return ParseLambdaExpression();
            }

            if (IsKeyword("yield"))
            {
                return ParseYieldExpression();
            }

            if (
                Current.Kind == SyntaxTokenKind.Identifier
                && Peek(1).Kind == SyntaxTokenKind.ColonEqual
                && !IsExpressionKeyword(Current.Text)
            )
            {
                var nameToken = Advance();
                Advance();
                if (IsReservedKeyword(nameToken.Text))
                {
                    Report(
                        "DPY2010",
                        $"The keyword '{nameToken.Text}' cannot be used as an assignment target.",
                        nameToken.Span
                    );
                }

                var walrusValue = ParseRequiredExpression("an expression after ':='");
                return new PythonAssignmentExpression(
                    new PythonNameExpression(nameToken.Text, nameToken.Span),
                    walrusValue,
                    TextSpan.FromBounds(nameToken.Span.Start, walrusValue.Span.End)
                );
            }

            var expression = ParseDisjunction();
            if (expression is null || !IsKeyword("if"))
            {
                return expression;
            }

            Advance();
            var condition = ParseDisjunction();
            if (condition is null)
            {
                ReportExpected("a condition in the conditional expression", Current.Span);
                return expression;
            }

            if (!MatchKeyword("else", out _))
            {
                ReportExpected("'else' in the conditional expression", Current.Span);
                return expression;
            }

            var falseResult = ParseRequiredExpression("an expression after 'else'");
            return new PythonConditionalExpression(
                condition,
                expression,
                falseResult,
                TextSpan.FromBounds(expression.Span.Start, falseResult.Span.End)
            );
        }

        private PythonExpression ParseRequiredDisjunction(string expected)
        {
            var expression = ParseDisjunction();
            if (expression is not null)
            {
                return expression;
            }

            ReportExpected(expected, Current.Span);
            return new PythonConstantExpression(
                PythonConstantKind.BooleanLiteral,
                "False",
                Current.Span
            );
        }

        private ReadOnlyCollection<PythonParameter> ParseParameters(SyntaxTokenKind terminator)
        {
            var allowAnnotations = terminator == SyntaxTokenKind.RightParenthesis;
            var parameters = new List<PythonParameter>();
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            var sawDefault = false;
            var sawStar = false;
            var sawBareStar = false;
            var sawDoubleStar = false;
            var sawKeywordOnly = false;
            while (
                Current.Kind == SyntaxTokenKind.Star
                || Current.Kind == SyntaxTokenKind.DoubleStar
                || (
                    Current.Kind == SyntaxTokenKind.Identifier
                    && (!IsExpressionKeyword(Current.Text) || IsReservedKeyword(Current.Text))
                )
            )
            {
                if (sawDoubleStar)
                {
                    Report("DPY2021", "No parameters may follow the '**' parameter.", Current.Span);
                }

                if (Match(SyntaxTokenKind.DoubleStar))
                {
                    var nameToken = Expect(
                        SyntaxTokenKind.Identifier,
                        "a parameter name after '**'"
                    );
                    sawDoubleStar = true;
                    var doubleStarAnnotation = ParseParameterAnnotation(allowAnnotations);
                    AddParameter(
                        parameters,
                        parameterNames,
                        nameToken,
                        null,
                        PythonParameterKind.VariadicKeywords,
                        doubleStarAnnotation
                    );
                }
                else if (Current.Kind == SyntaxTokenKind.Star)
                {
                    var starToken = Advance();
                    if (sawStar)
                    {
                        Report("DPY2021", "Only one '*' parameter is allowed.", starToken.Span);
                    }

                    sawStar = true;
                    if (
                        Current.Kind == SyntaxTokenKind.Identifier
                        && !IsExpressionKeyword(Current.Text)
                    )
                    {
                        var starName = Advance();
                        var starAnnotation = ParseParameterAnnotation(allowAnnotations);
                        AddParameter(
                            parameters,
                            parameterNames,
                            starName,
                            null,
                            PythonParameterKind.VariadicPositional,
                            starAnnotation
                        );
                    }
                    else
                    {
                        sawBareStar = true;
                    }
                }
                else
                {
                    var nameToken = Advance();
                    var annotation = ParseParameterAnnotation(allowAnnotations);
                    PythonExpression? defaultValue = null;
                    if (Match(SyntaxTokenKind.Equal))
                    {
                        defaultValue = ParseExpression();
                        if (defaultValue is null)
                        {
                            ReportExpected("a default value after '='", Current.Span);
                        }
                    }

                    var kind = sawStar
                        ? PythonParameterKind.KeywordOnly
                        : PythonParameterKind.Positional;
                    if (kind == PythonParameterKind.KeywordOnly)
                    {
                        sawKeywordOnly = true;
                    }
                    else if (defaultValue is not null)
                    {
                        sawDefault = true;
                    }
                    else if (sawDefault)
                    {
                        Report(
                            "DPY2016",
                            "A parameter without a default follows a parameter with a default.",
                            nameToken.Span
                        );
                    }

                    AddParameter(
                        parameters,
                        parameterNames,
                        nameToken,
                        defaultValue,
                        kind,
                        annotation
                    );
                }

                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }
            }

            if (sawBareStar && !sawKeywordOnly)
            {
                Report(
                    "DPY2021",
                    "Named parameters must follow a bare '*' parameter.",
                    Current.Span
                );
            }

            if (Current.Kind != terminator)
            {
                ReportExpected("a parameter", Current.Span);
            }

            return parameters.AsReadOnly();
        }

        private PythonExpression? ParseParameterAnnotation(bool allowAnnotations)
        {
            if (Current.Kind != SyntaxTokenKind.Colon || !allowAnnotations)
            {
                // Lambda parameter lists cannot carry annotations; there the ':' ends the list.
                return null;
            }

            Advance();
            // PEP 649 semantics: annotations are recorded syntax, never evaluated.
            var annotation = ParseExpression();
            if (annotation is null)
            {
                ReportExpected("an annotation after ':'", Current.Span);
            }

            return annotation;
        }

        private void AddParameter(
            List<PythonParameter> parameters,
            HashSet<string> parameterNames,
            SyntaxToken nameToken,
            PythonExpression? defaultValue,
            PythonParameterKind kind,
            PythonExpression? annotation = null
        )
        {
            if (string.IsNullOrEmpty(nameToken.Text))
            {
                return;
            }

            if (IsReservedKeyword(nameToken.Text))
            {
                Report(
                    "DPY2010",
                    $"The keyword '{nameToken.Text}' cannot be used as a parameter.",
                    nameToken.Span
                );
                return;
            }

            if (!parameterNames.Add(nameToken.Text))
            {
                Report("DPY2009", $"Duplicate parameter '{nameToken.Text}'.", nameToken.Span);
                return;
            }

            parameters.Add(
                new PythonParameter(
                    nameToken.Text,
                    defaultValue,
                    defaultValue is null
                        ? nameToken.Span
                        : TextSpan.FromBounds(nameToken.Span.Start, defaultValue.Span.End),
                    kind,
                    annotation
                )
            );
        }

        private PythonExpression ParseYieldExpression()
        {
            var yieldToken = Advance();
            if (_functionYieldFlags.Count == 0)
            {
                Report("DPY2022", "'yield' is only allowed inside a function.", yieldToken.Span);
            }
            else if (_functionAsyncFlags[^1])
            {
                Report(
                    "DPY2026",
                    "'yield' inside an async function (an async generator) is not supported.",
                    yieldToken.Span
                );
            }
            else
            {
                _functionYieldFlags[^1] = true;
            }

            if (MatchKeyword("from", out _))
            {
                var source = ParseRequiredExpression("an iterable after 'yield from'");
                return new PythonYieldFromExpression(
                    source,
                    TextSpan.FromBounds(yieldToken.Span.Start, source.Span.End)
                );
            }

            PythonExpression? value = null;
            if (StartsExpression() || Current.Kind == SyntaxTokenKind.Star)
            {
                value = ParseExpressionListValue();
            }

            return new PythonYieldExpression(
                value,
                TextSpan.FromBounds(yieldToken.Span.Start, value?.Span.End ?? yieldToken.Span.End)
            );
        }

        private PythonLambdaExpression ParseLambdaExpression()
        {
            var lambdaToken = Advance();
            var parameters = ParseParameters(SyntaxTokenKind.Colon);

            Expect(SyntaxTokenKind.Colon, "':' after the lambda parameters");
            var body = ParseExpression();
            if (body is null)
            {
                ReportExpected("a lambda body expression", Current.Span);
                body = new PythonConstantExpression(
                    PythonConstantKind.NoneLiteral,
                    "None",
                    Current.Span
                );
            }

            return new PythonLambdaExpression(
                parameters.AsReadOnly(),
                body,
                TextSpan.FromBounds(lambdaToken.Span.Start, body.Span.End)
            );
        }

        private PythonExpression? ParseDisjunction()
        {
            var left = ParseConjunction();
            if (left is null)
            {
                return null;
            }

            while (MatchKeyword("or", out _))
            {
                var right = ParseConjunction();
                if (right is null)
                {
                    ReportExpected("an expression after 'or'", Current.Span);
                    return left;
                }

                left = new PythonBinaryExpression(
                    left,
                    PythonBinaryOperator.Or,
                    right,
                    TextSpan.FromBounds(left.Span.Start, right.Span.End)
                );
            }

            return left;
        }

        private PythonExpression? ParseConjunction()
        {
            var left = ParseInversion();
            if (left is null)
            {
                return null;
            }

            while (MatchKeyword("and", out _))
            {
                var right = ParseInversion();
                if (right is null)
                {
                    ReportExpected("an expression after 'and'", Current.Span);
                    return left;
                }

                left = new PythonBinaryExpression(
                    left,
                    PythonBinaryOperator.And,
                    right,
                    TextSpan.FromBounds(left.Span.Start, right.Span.End)
                );
            }

            return left;
        }

        private PythonExpression? ParseInversion()
        {
            if (!MatchKeyword("not", out var notToken))
            {
                return ParseComparison();
            }

            var operand = ParseInversion();
            if (operand is null)
            {
                ReportExpected("an expression after 'not'", Current.Span);
                return null;
            }

            return new PythonUnaryExpression(
                PythonUnaryOperator.Not,
                operand,
                TextSpan.FromBounds(notToken.Span.Start, operand.Span.End)
            );
        }

        private PythonExpression? ParseComparison()
        {
            var left = ParseSum();
            if (left is null)
            {
                return null;
            }

            var comparisons = new List<PythonComparisonPart>();
            while (TryReadComparisonOperator(out var @operator, out var operatorToken))
            {
                var right = ParseSum();
                if (right is null)
                {
                    ReportExpected("an expression after the comparison operator", Current.Span);
                    break;
                }

                comparisons.Add(
                    new PythonComparisonPart(
                        @operator,
                        right,
                        TextSpan.FromBounds(operatorToken.Span.Start, right.Span.End)
                    )
                );
            }

            return comparisons.Count == 0
                ? left
                : new PythonComparisonExpression(
                    left,
                    comparisons.AsReadOnly(),
                    TextSpan.FromBounds(left.Span.Start, comparisons[^1].Span.End)
                );
        }

        private PythonExpression? ParseSum()
        {
            var left = ParseTerm();
            if (left is null)
            {
                return null;
            }

            while (Current.Kind is SyntaxTokenKind.Plus or SyntaxTokenKind.Minus)
            {
                var operatorToken = Advance();
                var right = ParseTerm();
                if (right is null)
                {
                    ReportExpected("an expression after the operator", Current.Span);
                    return left;
                }

                left = new PythonBinaryExpression(
                    left,
                    operatorToken.Kind == SyntaxTokenKind.Plus
                        ? PythonBinaryOperator.Add
                        : PythonBinaryOperator.Subtract,
                    right,
                    TextSpan.FromBounds(left.Span.Start, right.Span.End)
                );
            }

            return left;
        }

        private PythonExpression? ParseTerm()
        {
            var left = ParseFactor();
            if (left is null)
            {
                return null;
            }

            while (
                Current.Kind
                    is SyntaxTokenKind.Star
                        or SyntaxTokenKind.Slash
                        or SyntaxTokenKind.DoubleSlash
                        or SyntaxTokenKind.Percent
            )
            {
                var operatorToken = Advance();
                var right = ParseFactor();
                if (right is null)
                {
                    ReportExpected("an expression after the operator", Current.Span);
                    return left;
                }

                left = new PythonBinaryExpression(
                    left,
                    GetMultiplicativeOperator(operatorToken.Kind),
                    right,
                    TextSpan.FromBounds(left.Span.Start, right.Span.End)
                );
            }

            return left;
        }

        private PythonExpression? ParseFactor()
        {
            if (
                Current.Kind
                is SyntaxTokenKind.Plus
                    or SyntaxTokenKind.Minus
                    or SyntaxTokenKind.Tilde
            )
            {
                var operatorToken = Advance();
                var operand = ParseFactor();
                if (operand is null)
                {
                    ReportExpected("an expression after the unary operator", Current.Span);
                    return null;
                }

                return new PythonUnaryExpression(
                    GetUnaryOperator(operatorToken.Kind),
                    operand,
                    TextSpan.FromBounds(operatorToken.Span.Start, operand.Span.End)
                );
            }

            return ParsePower();
        }

        private PythonExpression? ParsePower()
        {
            var left = IsKeyword("await") ? ParseAwaitExpression() : ParsePrimary();
            if (left is null || !Match(SyntaxTokenKind.DoubleStar))
            {
                return left;
            }

            var right = ParseFactor();
            if (right is null)
            {
                ReportExpected("an exponent", Current.Span);
                return left;
            }

            return new PythonBinaryExpression(
                left,
                PythonBinaryOperator.Power,
                right,
                TextSpan.FromBounds(left.Span.Start, right.Span.End)
            );
        }

        private PythonAwaitExpression? ParseAwaitExpression()
        {
            var awaitToken = Advance();
            if (_functionAsyncFlags.Count == 0 || !_functionAsyncFlags[^1])
            {
                Report(
                    "DPY2025",
                    "'await' is only allowed inside an async function.",
                    awaitToken.Span
                );
            }

            var operand = ParsePrimary();
            if (operand is null)
            {
                ReportExpected("an expression after 'await'", Current.Span);
                return null;
            }

            return new PythonAwaitExpression(
                operand,
                TextSpan.FromBounds(awaitToken.Span.Start, operand.Span.End)
            );
        }

        private PythonExpression? ParsePrimary()
        {
            var expression = ParseAtom();
            if (expression is null)
            {
                return null;
            }

            while (true)
            {
                if (Match(SyntaxTokenKind.LeftParenthesis, out _))
                {
                    var arguments = new List<PythonExpression>();
                    var keywordArguments = new List<PythonKeywordArgument>();
                    var keywordNames = new HashSet<string>(StringComparer.Ordinal);
                    if (Current.Kind != SyntaxTokenKind.RightParenthesis)
                    {
                        while (true)
                        {
                            if (Current.Kind == SyntaxTokenKind.Star)
                            {
                                var starToken = Advance();
                                var unpacked = ParseExpression();
                                if (unpacked is null)
                                {
                                    ReportExpected("an iterable after '*'", Current.Span);
                                    break;
                                }

                                if (keywordArguments.Count != 0)
                                {
                                    Report(
                                        "DPY2017",
                                        "An iterable unpacking follows a keyword argument.",
                                        unpacked.Span
                                    );
                                }

                                arguments.Add(
                                    new PythonStarredExpression(
                                        unpacked,
                                        TextSpan.FromBounds(starToken.Span.Start, unpacked.Span.End)
                                    )
                                );
                            }
                            else if (Current.Kind == SyntaxTokenKind.DoubleStar)
                            {
                                var starToken = Advance();
                                var unpacked = ParseExpression();
                                if (unpacked is null)
                                {
                                    ReportExpected("a mapping after '**'", Current.Span);
                                    break;
                                }

                                keywordArguments.Add(
                                    new PythonKeywordArgument(
                                        null,
                                        unpacked,
                                        TextSpan.FromBounds(starToken.Span.Start, unpacked.Span.End)
                                    )
                                );
                            }
                            else if (
                                Current.Kind == SyntaxTokenKind.Identifier
                                && Peek(1).Kind == SyntaxTokenKind.Equal
                                && !IsReservedKeyword(Current.Text)
                            )
                            {
                                var name = Advance();
                                Advance();
                                var value = ParseExpression();
                                if (value is null)
                                {
                                    ReportExpected("a keyword argument value", Current.Span);
                                    break;
                                }

                                if (!keywordNames.Add(name.Text))
                                {
                                    Report(
                                        "DPY2018",
                                        $"Keyword argument repeated: '{name.Text}'.",
                                        name.Span
                                    );
                                }

                                keywordArguments.Add(
                                    new PythonKeywordArgument(
                                        name.Text,
                                        value,
                                        TextSpan.FromBounds(name.Span.Start, value.Span.End)
                                    )
                                );
                            }
                            else
                            {
                                var argument = ParseExpression();
                                if (argument is null)
                                {
                                    ReportExpected("a call argument", Current.Span);
                                    break;
                                }

                                if (keywordArguments.Count != 0)
                                {
                                    Report(
                                        "DPY2017",
                                        "A positional argument follows a keyword argument.",
                                        argument.Span
                                    );
                                }

                                if (IsKeyword("for"))
                                {
                                    var clauses = ParseComprehensionClauses();
                                    argument = new PythonGeneratorExpression(
                                        argument,
                                        clauses,
                                        TextSpan.FromBounds(
                                            argument.Span.Start,
                                            clauses[^1].Span.End
                                        )
                                    );
                                    if (
                                        arguments.Count != 0
                                        || Current.Kind == SyntaxTokenKind.Comma
                                    )
                                    {
                                        Report(
                                            "DPY2023",
                                            "A generator expression must be parenthesized "
                                                + "when it is not the sole argument.",
                                            argument.Span
                                        );
                                    }
                                }

                                arguments.Add(argument);
                            }

                            if (!Match(SyntaxTokenKind.Comma))
                            {
                                break;
                            }

                            if (Current.Kind == SyntaxTokenKind.RightParenthesis)
                            {
                                break;
                            }
                        }
                    }

                    var end = ExpectClosingDelimiter(
                        SyntaxTokenKind.RightParenthesis,
                        "')'",
                        expression.Span.End
                    );
                    expression = new PythonCallExpression(
                        expression,
                        arguments.AsReadOnly(),
                        keywordArguments.AsReadOnly(),
                        TextSpan.FromBounds(expression.Span.Start, end)
                    );
                    continue;
                }

                if (!Match(SyntaxTokenKind.LeftBracket, out var leftBracket))
                {
                    if (!Match(SyntaxTokenKind.Dot, out _))
                    {
                        break;
                    }

                    var attribute = Expect(
                        SyntaxTokenKind.Identifier,
                        "an attribute name after '.'"
                    );
                    if (attribute.Text.Length == 0 || IsReservedKeyword(attribute.Text))
                    {
                        if (attribute.Text.Length != 0)
                        {
                            Report(
                                "DPY2010",
                                $"The keyword '{attribute.Text}' cannot be used as an attribute name.",
                                attribute.Span
                            );
                        }

                        break;
                    }

                    expression = new PythonAttributeExpression(
                        expression,
                        attribute.Text,
                        TextSpan.FromBounds(expression.Span.Start, attribute.Span.End)
                    );
                    continue;
                }

                var index = ParseSubscript(leftBracket);
                if (index is null)
                {
                    break;
                }

                var subscriptionEnd = ExpectClosingDelimiter(
                    SyntaxTokenKind.RightBracket,
                    "']'",
                    index.Span.End
                );
                expression = new PythonSubscriptionExpression(
                    expression,
                    index,
                    TextSpan.FromBounds(expression.Span.Start, subscriptionEnd)
                );
            }

            return expression;
        }

        private PythonExpression ParseFormattedString(SyntaxToken token, bool isTemplate = false)
        {
            var quoteIndex = token.Text.IndexOfAny(['\'', '"']);
            var quote = token.Text[quoteIndex];
            var quoteLength =
                quoteIndex + 2 < token.Text.Length
                && token.Text[quoteIndex + 1] == quote
                && token.Text[quoteIndex + 2] == quote
                    ? 3
                    : 1;
            var prefix = token.Text[..quoteIndex];
            var contentStart = quoteIndex + quoteLength;
            var contentLength = Math.Max(0, token.Text.Length - contentStart - quoteLength);
            var content = token.Text.Substring(contentStart, contentLength);
            var isRaw = prefix.Contains('r', StringComparison.OrdinalIgnoreCase);

            var parts = new List<PythonFormattedStringPart>();
            var literal = new System.Text.StringBuilder();
            var position = 0;
            var contentOffset = token.Span.Start + contentStart;
            while (position < content.Length)
            {
                var current = content[position];
                if (current == '{' && position + 1 < content.Length && content[position + 1] == '{')
                {
                    literal.Append('{');
                    position += 2;
                    continue;
                }

                if (current == '}' && position + 1 < content.Length && content[position + 1] == '}')
                {
                    literal.Append('}');
                    position += 2;
                    continue;
                }

                if (current == '}')
                {
                    Report(
                        "DPY2019",
                        "A single '}' is not allowed inside an f-string.",
                        new TextSpan(contentOffset + position, 1)
                    );
                    position++;
                    continue;
                }

                if (current != '{')
                {
                    literal.Append(current);
                    position++;
                    continue;
                }

                FlushFormattedLiteral(parts, literal, contentOffset, position);
                position++;
                var holeStart = position;
                var end = ScanInterpolation(
                    content,
                    position,
                    out var conversionIndex,
                    out var specIndex
                );
                if (end < 0)
                {
                    Report(
                        "DPY2019",
                        "The f-string interpolation is not terminated.",
                        new TextSpan(contentOffset + holeStart - 1, 1)
                    );
                    break;
                }

                if (conversionIndex >= 0 && specIndex >= 0 && conversionIndex > specIndex)
                {
                    // The '!' sits inside the format specification, not before it.
                    conversionIndex = -1;
                }

                var expressionEnd =
                    conversionIndex >= 0 ? conversionIndex : (specIndex >= 0 ? specIndex : end);

                var expressionText = content[holeStart..expressionEnd];
                char? conversion = conversionIndex >= 0 ? content[conversionIndex + 1] : null;
                string? specification = specIndex >= 0 ? content[(specIndex + 1)..end] : null;
                var holeSpan = new TextSpan(
                    contentOffset + holeStart,
                    Math.Max(1, end - holeStart)
                );
                if (
                    specification is not null
                    && specification.Contains('{', StringComparison.Ordinal)
                )
                {
                    Report(
                        "DPY2019",
                        "Nested interpolations in f-string format specifications are not "
                            + "supported in this runtime slice.",
                        holeSpan
                    );
                    specification = null;
                }

                if (string.IsNullOrWhiteSpace(expressionText))
                {
                    Report(
                        "DPY2019",
                        "The f-string interpolation is missing an expression.",
                        holeSpan
                    );
                }
                else
                {
                    var expression = ParseEmbeddedExpression(expressionText, holeSpan);
                    if (expression is not null)
                    {
                        parts.Add(
                            new PythonFormattedStringInterpolationPart(
                                expression,
                                conversion,
                                specification,
                                holeSpan,
                                expressionText
                            )
                        );
                    }
                }

                position = end + 1;
            }

            FlushFormattedLiteral(parts, literal, contentOffset, position);
            return isTemplate
                ? new PythonTemplateStringExpression(parts.AsReadOnly(), isRaw, token.Span)
                : new PythonFormattedStringExpression(parts.AsReadOnly(), isRaw, token.Span);
        }

        private static void FlushFormattedLiteral(
            List<PythonFormattedStringPart> parts,
            System.Text.StringBuilder literal,
            int contentOffset,
            int position
        )
        {
            if (literal.Length == 0)
            {
                return;
            }

            parts.Add(
                new PythonFormattedStringLiteralPart(
                    literal.ToString(),
                    new TextSpan(contentOffset + position - literal.Length, literal.Length)
                )
            );
            literal.Clear();
        }

        private static int ScanInterpolation(
            string content,
            int start,
            out int conversionIndex,
            out int specIndex
        )
        {
            conversionIndex = -1;
            specIndex = -1;
            var depth = 0;
            var position = start;
            while (position < content.Length)
            {
                var current = content[position];
                if (current is '\'' or '"')
                {
                    var quote = current;
                    position++;
                    while (position < content.Length && content[position] != quote)
                    {
                        position += content[position] == '\\' ? 2 : 1;
                    }

                    position++;
                    continue;
                }

                if (current is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (current is ')' or ']')
                {
                    depth--;
                }
                else if (current == '}')
                {
                    if (depth == 0)
                    {
                        return position;
                    }

                    depth--;
                }
                else if (depth == 0 && specIndex < 0)
                {
                    if (
                        current == '!'
                        && position + 1 < content.Length
                        && content[position + 1] is 's' or 'r' or 'a'
                        && position + 2 < content.Length
                        && content[position + 2] is '}' or ':'
                    )
                    {
                        conversionIndex = position;
                        position += 2;
                        continue;
                    }

                    if (current == ':')
                    {
                        specIndex = position;
                    }
                }

                position++;
            }

            return -1;
        }

        private PythonExpression? ParseEmbeddedExpression(string text, TextSpan holeSpan)
        {
            var tokenization = PythonTokenizer.Tokenize(new SourceText(text, "<fstring>"));
            var parser = new Parser(tokenization);
            var (expression, embeddedDiagnostics) = parser.ParseEmbedded();
            foreach (var diagnostic in embeddedDiagnostics)
            {
                Report(
                    diagnostic.Code,
                    $"Invalid f-string interpolation: {diagnostic.Message}",
                    holeSpan
                );
            }

            return expression;
        }

        internal (
            PythonExpression? Expression,
            IReadOnlyList<Diagnostic> Diagnostics
        ) ParseEmbedded()
        {
            SkipNewLines();
            var expression = ParseExpressionListValue();
            SkipNewLines();
            if (expression is not null && Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                ReportExpected("the end of the interpolated expression", Current.Span);
            }

            return (expression, _diagnostics);
        }

        private PythonExpression? ParseSubscript(SyntaxToken leftBracket)
        {
            PythonExpression? start = null;
            if (Current.Kind != SyntaxTokenKind.Colon)
            {
                start = ParseExpression();
                if (start is null)
                {
                    ReportExpected("a subscription index", Current.Span);
                    return null;
                }

                if (Current.Kind != SyntaxTokenKind.Colon)
                {
                    return start;
                }
            }

            var colon = Advance();
            PythonExpression? stop = null;
            if (Current.Kind is not (SyntaxTokenKind.Colon or SyntaxTokenKind.RightBracket))
            {
                stop = ParseExpression();
                if (stop is null)
                {
                    ReportExpected("a slice stop expression", Current.Span);
                }
            }

            PythonExpression? step = null;
            if (Match(SyntaxTokenKind.Colon))
            {
                if (Current.Kind != SyntaxTokenKind.RightBracket)
                {
                    step = ParseExpression();
                    if (step is null)
                    {
                        ReportExpected("a slice step expression", Current.Span);
                    }
                }
            }

            var spanStart = start?.Span.Start ?? leftBracket.Span.End;
            var end = step?.Span.End ?? stop?.Span.End ?? colon.Span.End;
            return new PythonSliceExpression(
                start,
                stop,
                step,
                TextSpan.FromBounds(spanStart, end)
            );
        }

        private PythonExpression? ParseAtom()
        {
            if (Match(SyntaxTokenKind.LeftParenthesis, out var leftParenthesis))
            {
                return ParseParenthesizedOrTuple(leftParenthesis);
            }

            if (Match(SyntaxTokenKind.LeftBracket, out var leftBracket))
            {
                return ParseListDisplay(leftBracket);
            }

            if (Match(SyntaxTokenKind.LeftBrace, out var leftBrace))
            {
                return ParseDictionaryDisplay(leftBrace);
            }

            if (Current.Kind == SyntaxTokenKind.Identifier)
            {
                if (IsExpressionKeyword(Current.Text))
                {
                    return null;
                }

                var token = Advance();
                return token.Text switch
                {
                    "None" => Constant(token, PythonConstantKind.NoneLiteral),
                    "True" or "False" => Constant(token, PythonConstantKind.BooleanLiteral),
                    _ => new PythonNameExpression(token.Text, token.Span),
                };
            }

            var constantKind = Current.Kind switch
            {
                SyntaxTokenKind.IntegerLiteral => PythonConstantKind.IntegerLiteral,
                SyntaxTokenKind.FloatLiteral => PythonConstantKind.FloatLiteral,
                SyntaxTokenKind.ImaginaryLiteral => PythonConstantKind.ImaginaryLiteral,
                SyntaxTokenKind.StringLiteral => PythonConstantKind.StringLiteral,
                SyntaxTokenKind.BytesLiteral => PythonConstantKind.BytesLiteral,
                SyntaxTokenKind.TemplateStringLiteral => PythonConstantKind.TemplateStringLiteral,
                _ => (PythonConstantKind?)null,
            };

            if (Current.Kind == SyntaxTokenKind.FormattedStringLiteral)
            {
                return ParseFormattedString(Advance());
            }

            if (Current.Kind == SyntaxTokenKind.TemplateStringLiteral)
            {
                return ParseFormattedString(Advance(), isTemplate: true);
            }

            return constantKind is null ? null : Constant(Advance(), constantKind.Value);
        }

        private PythonExpression? ParseParenthesizedOrTuple(SyntaxToken leftParenthesis)
        {
            if (Match(SyntaxTokenKind.RightParenthesis, out var emptyRightParenthesis))
            {
                return new PythonTupleExpression(
                    Array.Empty<PythonExpression>(),
                    TextSpan.FromBounds(leftParenthesis.Span.Start, emptyRightParenthesis.Span.End)
                );
            }

            var first = ParsePossiblyStarredExpression();
            if (first is null)
            {
                ReportExpected("an expression", Current.Span);
                return null;
            }

            if (first is not PythonStarredExpression && IsKeyword("for"))
            {
                var clauses = ParseComprehensionClauses();
                var comprehensionEnd = ExpectClosingDelimiter(
                    SyntaxTokenKind.RightParenthesis,
                    "')'",
                    clauses[^1].Span.End
                );
                return new PythonGeneratorExpression(
                    first,
                    clauses,
                    TextSpan.FromBounds(leftParenthesis.Span.Start, comprehensionEnd)
                );
            }

            if (!Match(SyntaxTokenKind.Comma))
            {
                var groupEnd = ExpectClosingDelimiter(
                    SyntaxTokenKind.RightParenthesis,
                    "')'",
                    first.Span.End
                );
                return new PythonParenthesizedExpression(
                    first,
                    TextSpan.FromBounds(leftParenthesis.Span.Start, groupEnd)
                );
            }

            var elements = new List<PythonExpression> { first };
            while (Current.Kind != SyntaxTokenKind.RightParenthesis)
            {
                var element = ParsePossiblyStarredExpression();
                if (element is null)
                {
                    ReportExpected("a tuple element", Current.Span);
                    break;
                }

                elements.Add(element);
                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }
            }

            var tupleEnd = ExpectClosingDelimiter(
                SyntaxTokenKind.RightParenthesis,
                "')'",
                elements[^1].Span.End
            );
            return new PythonTupleExpression(
                elements.AsReadOnly(),
                TextSpan.FromBounds(leftParenthesis.Span.Start, tupleEnd)
            );
        }

        private PythonExpression ParseListDisplay(SyntaxToken leftBracket)
        {
            var elements = new List<PythonExpression>();
            while (Current.Kind != SyntaxTokenKind.RightBracket)
            {
                var element = ParsePossiblyStarredExpression();
                if (element is null)
                {
                    ReportExpected("a list element", Current.Span);
                    break;
                }

                if (elements.Count == 0 && IsKeyword("for"))
                {
                    var clauses = ParseComprehensionClauses();
                    var comprehensionEnd = ExpectClosingDelimiter(
                        SyntaxTokenKind.RightBracket,
                        "']'",
                        clauses[^1].Span.End
                    );
                    return new PythonListComprehensionExpression(
                        element,
                        clauses,
                        TextSpan.FromBounds(leftBracket.Span.Start, comprehensionEnd)
                    );
                }

                elements.Add(element);
                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }
            }

            var fallbackEnd = elements.Count == 0 ? leftBracket.Span.End : elements[^1].Span.End;
            var end = ExpectClosingDelimiter(SyntaxTokenKind.RightBracket, "']'", fallbackEnd);
            return new PythonListExpression(
                elements.AsReadOnly(),
                TextSpan.FromBounds(leftBracket.Span.Start, end)
            );
        }

        private PythonExpression ParseDictionaryDisplay(SyntaxToken leftBrace)
        {
            var items = new List<PythonDictionaryItem>();
            while (Current.Kind != SyntaxTokenKind.RightBrace)
            {
                if (Match(SyntaxTokenKind.DoubleStar, out var doubleStarToken))
                {
                    var unpacked = ParseRequiredExpression("a mapping after '**'");
                    items.Add(
                        new PythonDictionaryItem(
                            null,
                            unpacked,
                            TextSpan.FromBounds(doubleStarToken.Span.Start, unpacked.Span.End)
                        )
                    );
                    if (!Match(SyntaxTokenKind.Comma))
                    {
                        break;
                    }

                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Star)
                {
                    var starredElement = ParsePossiblyStarredExpression();
                    if (starredElement is null)
                    {
                        break;
                    }

                    return ParseSetDisplay(leftBrace, starredElement);
                }

                var key = ParseExpression();
                if (key is null)
                {
                    ReportExpected("a dictionary key", Current.Span);
                    break;
                }

                if (items.Count == 0 && IsKeyword("for"))
                {
                    var setClauses = ParseComprehensionClauses();
                    var setEnd = ExpectClosingDelimiter(
                        SyntaxTokenKind.RightBrace,
                        "'}'",
                        setClauses[^1].Span.End
                    );
                    return new PythonSetComprehensionExpression(
                        key,
                        setClauses,
                        TextSpan.FromBounds(leftBrace.Span.Start, setEnd)
                    );
                }

                if (items.Count == 0 && Current.Kind != SyntaxTokenKind.Colon)
                {
                    return ParseSetDisplay(leftBrace, key);
                }

                Expect(SyntaxTokenKind.Colon, "':' after the dictionary key");
                var value = ParseExpression();
                if (value is null)
                {
                    ReportExpected("a dictionary value", Current.Span);
                    break;
                }

                if (items.Count == 0 && IsKeyword("for"))
                {
                    var clauses = ParseComprehensionClauses();
                    var comprehensionEnd = ExpectClosingDelimiter(
                        SyntaxTokenKind.RightBrace,
                        "'}'",
                        clauses[^1].Span.End
                    );
                    return new PythonDictionaryComprehensionExpression(
                        key,
                        value,
                        clauses,
                        TextSpan.FromBounds(leftBrace.Span.Start, comprehensionEnd)
                    );
                }

                items.Add(
                    new PythonDictionaryItem(
                        key,
                        value,
                        TextSpan.FromBounds(key.Span.Start, value.Span.End)
                    )
                );
                if (!Match(SyntaxTokenKind.Comma))
                {
                    break;
                }
            }

            var fallbackEnd = items.Count == 0 ? leftBrace.Span.End : items[^1].Span.End;
            var end = ExpectClosingDelimiter(SyntaxTokenKind.RightBrace, "'}'", fallbackEnd);
            return new PythonDictionaryExpression(
                items.AsReadOnly(),
                TextSpan.FromBounds(leftBrace.Span.Start, end)
            );
        }

        private PythonSetExpression ParseSetDisplay(SyntaxToken leftBrace, PythonExpression first)
        {
            var elements = new List<PythonExpression> { first };
            while (Match(SyntaxTokenKind.Comma))
            {
                if (Current.Kind == SyntaxTokenKind.RightBrace)
                {
                    break;
                }

                var element = ParsePossiblyStarredExpression();
                if (element is null)
                {
                    ReportExpected("a set element", Current.Span);
                    break;
                }

                elements.Add(element);
            }

            var end = ExpectClosingDelimiter(
                SyntaxTokenKind.RightBrace,
                "'}'",
                elements[^1].Span.End
            );
            return new PythonSetExpression(
                elements.AsReadOnly(),
                TextSpan.FromBounds(leftBrace.Span.Start, end)
            );
        }

        private ReadOnlyCollection<PythonComprehensionClause> ParseComprehensionClauses()
        {
            var clauses = new List<PythonComprehensionClause>();
            while (true)
            {
                if (MatchKeyword("for", out var forToken))
                {
                    var target = ParseForTargets();
                    if (!MatchKeyword("in", out _))
                    {
                        ReportExpected("'in' after the comprehension target", Current.Span);
                    }

                    var iterable = ParseRequiredDisjunction("an iterable after 'in'");
                    clauses.Add(
                        new PythonComprehensionForClause(
                            target,
                            iterable,
                            TextSpan.FromBounds(forToken.Span.Start, iterable.Span.End)
                        )
                    );
                    continue;
                }

                if (MatchKeyword("if", out var ifToken))
                {
                    var condition = ParseRequiredDisjunction("a condition after 'if'");
                    clauses.Add(
                        new PythonComprehensionIfClause(
                            condition,
                            TextSpan.FromBounds(ifToken.Span.Start, condition.Span.End)
                        )
                    );
                    continue;
                }

                return clauses.AsReadOnly();
            }
        }

        private int ExpectClosingDelimiter(
            SyntaxTokenKind kind,
            string expectation,
            int fallbackEnd
        )
        {
            if (Match(kind, out var delimiter))
            {
                return delimiter.Span.End;
            }

            ReportExpected(expectation, Current.Span);
            return fallbackEnd;
        }

        private bool TryReadComparisonOperator(
            out PythonComparisonOperator @operator,
            out SyntaxToken token
        )
        {
            if (IsKeyword("in"))
            {
                @operator = PythonComparisonOperator.In;
                token = Advance();
                return true;
            }

            if (IsKeyword("not") && Peek(1) is { Kind: SyntaxTokenKind.Identifier, Text: "in" })
            {
                @operator = PythonComparisonOperator.NotIn;
                token = Advance();
                Advance();
                return true;
            }

            if (IsKeyword("is"))
            {
                token = Advance();
                if (MatchKeyword("not", out _))
                {
                    @operator = PythonComparisonOperator.IsNot;
                    return true;
                }

                @operator = PythonComparisonOperator.Is;
                return true;
            }

            var value = Current.Kind switch
            {
                SyntaxTokenKind.EqualEqual => PythonComparisonOperator.Equal,
                SyntaxTokenKind.NotEqual => PythonComparisonOperator.NotEqual,
                SyntaxTokenKind.LessThan => PythonComparisonOperator.LessThan,
                SyntaxTokenKind.LessThanOrEqual => PythonComparisonOperator.LessThanOrEqual,
                SyntaxTokenKind.GreaterThan => PythonComparisonOperator.GreaterThan,
                SyntaxTokenKind.GreaterThanOrEqual => PythonComparisonOperator.GreaterThanOrEqual,
                _ => (PythonComparisonOperator?)null,
            };

            if (value is null)
            {
                @operator = default;
                token = Current;
                return false;
            }

            @operator = value.Value;
            token = Advance();
            return true;
        }

        private SyntaxToken Expect(SyntaxTokenKind kind, string expected)
        {
            if (Match(kind, out var token))
            {
                return token;
            }

            ReportExpected(expected, Current.Span);
            return new SyntaxToken(kind, new TextSpan(Current.Span.Start, 0), string.Empty);
        }

        private void SkipNewLines()
        {
            while (Current.Kind == SyntaxTokenKind.NewLine)
            {
                _position++;
            }
        }

        private void SynchronizeStatement()
        {
            while (
                Current.Kind
                    is not (
                        SyntaxTokenKind.Semicolon
                        or SyntaxTokenKind.NewLine
                        or SyntaxTokenKind.Dedent
                        or SyntaxTokenKind.EndOfFile
                    )
            )
            {
                _position++;
            }
        }

        private void SynchronizeLine()
        {
            while (
                Current.Kind
                    is not (
                        SyntaxTokenKind.NewLine
                        or SyntaxTokenKind.Dedent
                        or SyntaxTokenKind.EndOfFile
                    )
            )
            {
                _position++;
            }

            if (Current.Kind == SyntaxTokenKind.NewLine)
            {
                _position++;
            }
        }

        private bool IsKeyword(string value) =>
            Current.Kind == SyntaxTokenKind.Identifier && Current.Text == value;

        private bool MatchKeyword(string value, out SyntaxToken token)
        {
            token = Current;
            if (!IsKeyword(value))
            {
                return false;
            }

            _position++;
            return true;
        }

        private bool Match(SyntaxTokenKind kind) => Match(kind, out _);

        private bool Match(SyntaxTokenKind kind, out SyntaxToken token)
        {
            token = Current;
            if (token.Kind != kind)
            {
                return false;
            }

            _position++;
            return true;
        }

        private SyntaxToken Advance()
        {
            var token = Current;
            if (token.Kind != SyntaxTokenKind.EndOfFile)
            {
                _position++;
            }

            return token;
        }

        private SyntaxToken Current => Peek(0);

        private SyntaxToken Peek(int offset)
        {
            var index = Math.Min(_position + offset, _tokens.Length - 1);
            return _tokens[index];
        }

        private void ReportExpected(string expected, TextSpan span) =>
            Report("DPY2001", $"Expected {expected}.", span);

        private void Report(string code, string message, TextSpan span) =>
            _diagnostics.Add(new Diagnostic(code, message, DiagnosticSeverity.Error, span));

        private static int GetBodyEnd(IReadOnlyList<PythonStatement> body, int fallback) =>
            body.Count == 0 ? fallback : body[^1].Span.End;

        private static PythonConstantExpression Constant(
            SyntaxToken token,
            PythonConstantKind constantKind
        ) => new(constantKind, token.Text, token.Span);

        private static PythonUnaryOperator GetUnaryOperator(SyntaxTokenKind kind) =>
            kind switch
            {
                SyntaxTokenKind.Plus => PythonUnaryOperator.Positive,
                SyntaxTokenKind.Minus => PythonUnaryOperator.Negative,
                SyntaxTokenKind.Tilde => PythonUnaryOperator.Invert,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

        private static PythonBinaryOperator GetMultiplicativeOperator(SyntaxTokenKind kind) =>
            kind switch
            {
                SyntaxTokenKind.Star => PythonBinaryOperator.Multiply,
                SyntaxTokenKind.Slash => PythonBinaryOperator.TrueDivide,
                SyntaxTokenKind.DoubleSlash => PythonBinaryOperator.FloorDivide,
                SyntaxTokenKind.Percent => PythonBinaryOperator.Modulo,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

        private static bool IsExpressionKeyword(string value) =>
            value
                is "and"
                    or "or"
                    or "not"
                    or "in"
                    or "is"
                    or "as"
                    or "from"
                    or "except"
                    or "finally"
                    or "elif"
                    or "else"
                    or "def"
                    or "lambda"
                    or "return"
                    or "break"
                    or "continue"
                    or "pass"
                    or "global"
                    or "nonlocal"
                    or "assert"
                    or "del"
                    or "with";

        private static bool IsUnsupportedStatementKeyword(string value) =>
            value
                is "class"
                    or "def"
                    or "elif"
                    or "else"
                    or "from"
                    or "if"
                    or "import"
                    or "raise"
                    or "return"
                    or "try"
                    or "while";

        private static bool IsReservedKeyword(string value) =>
            value
                is "False"
                    or "None"
                    or "True"
                    or "and"
                    or "as"
                    or "assert"
                    or "async"
                    or "await"
                    or "break"
                    or "class"
                    or "continue"
                    or "def"
                    or "del"
                    or "elif"
                    or "else"
                    or "except"
                    or "finally"
                    or "for"
                    or "from"
                    or "global"
                    or "if"
                    or "import"
                    or "in"
                    or "is"
                    or "lambda"
                    or "nonlocal"
                    or "not"
                    or "or"
                    or "pass"
                    or "raise"
                    or "return"
                    or "try"
                    or "while"
                    or "yield";
    }
}
