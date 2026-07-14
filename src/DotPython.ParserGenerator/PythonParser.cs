using System.Collections.ObjectModel;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Syntax;
using DotPython.Language.Text;

namespace DotPython.ParserGenerator;

public static class PythonParser
{
    public static PythonParseResult Parse(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tokenization = PythonTokenizer.Tokenize(source);
        return new Parser(tokenization).Parse();
    }

    private sealed class Parser
    {
        private readonly List<Diagnostic> _diagnostics;
        private readonly SourceText _source;
        private readonly SyntaxToken[] _tokens;
        private int _functionDepth;
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
                else if (IsKeyword("def"))
                {
                    statements.Add(ParseFunctionDefinition());
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

        private PythonFunctionDefinitionStatement ParseFunctionDefinition()
        {
            var start = Advance().Span.Start;
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

            var parameters = new List<PythonParameter>();
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            if (Current.Kind != SyntaxTokenKind.RightParenthesis)
            {
                while (true)
                {
                    var parameter = Expect(SyntaxTokenKind.Identifier, "a parameter name");
                    if (!string.IsNullOrEmpty(parameter.Text))
                    {
                        if (IsReservedKeyword(parameter.Text))
                        {
                            Report(
                                "DPY2010",
                                $"The keyword '{parameter.Text}' cannot be used as a parameter.",
                                parameter.Span
                            );
                        }

                        if (!parameterNames.Add(parameter.Text))
                        {
                            Report(
                                "DPY2009",
                                $"Duplicate parameter '{parameter.Text}'.",
                                parameter.Span
                            );
                        }

                        parameters.Add(new PythonParameter(parameter.Text, parameter.Span));
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

            Expect(SyntaxTokenKind.RightParenthesis, "')' after the parameters");
            var colon = Expect(SyntaxTokenKind.Colon, "':' after the function signature");
            _functionDepth++;
            IReadOnlyList<PythonStatement> body;
            try
            {
                body = ParseSuite();
            }
            finally
            {
                _functionDepth--;
            }

            return new PythonFunctionDefinitionStatement(
                name,
                parameters.AsReadOnly(),
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

            if (Current.Kind == SyntaxTokenKind.Identifier && Peek(1).Kind == SyntaxTokenKind.Equal)
            {
                var nameToken = Advance();
                Advance();
                var value = ParseExpression();
                if (value is null)
                {
                    ReportExpected("an expression after '='", Current.Span);
                    return null;
                }

                if (nameToken.Text is "None" or "True" or "False")
                {
                    Report("DPY2005", $"Cannot assign to '{nameToken.Text}'.", nameToken.Span);
                    return null;
                }

                var target = new PythonNameExpression(nameToken.Text, nameToken.Span);
                return new PythonAssignmentStatement(
                    target,
                    value,
                    TextSpan.FromBounds(target.Span.Start, value.Span.End)
                );
            }

            var expression = ParseExpression();
            return expression is null
                ? null
                : new PythonExpressionStatement(expression, expression.Span);
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

            var value = ParseExpression();
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

        private PythonExpression? ParseExpression() => ParseDisjunction();

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
            var left = ParsePrimary();
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

        private PythonExpression? ParsePrimary()
        {
            var expression = ParseAtom();
            if (expression is null)
            {
                return null;
            }

            while (Match(SyntaxTokenKind.LeftParenthesis, out _))
            {
                var arguments = new List<PythonExpression>();
                if (Current.Kind != SyntaxTokenKind.RightParenthesis)
                {
                    while (true)
                    {
                        var argument = ParseExpression();
                        if (argument is null)
                        {
                            ReportExpected("a call argument", Current.Span);
                            break;
                        }

                        arguments.Add(argument);
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

                var end = Current.Span.End;
                if (!Match(SyntaxTokenKind.RightParenthesis, out var rightParenthesis))
                {
                    ReportExpected("')'", Current.Span);
                }
                else
                {
                    end = rightParenthesis.Span.End;
                }

                expression = new PythonCallExpression(
                    expression,
                    arguments.AsReadOnly(),
                    TextSpan.FromBounds(expression.Span.Start, end)
                );
            }

            return expression;
        }

        private PythonExpression? ParseAtom()
        {
            if (Match(SyntaxTokenKind.LeftParenthesis, out var leftParenthesis))
            {
                var expression = ParseExpression();
                if (expression is null)
                {
                    ReportExpected("an expression", Current.Span);
                    return null;
                }

                var end = expression.Span.End;
                if (!Match(SyntaxTokenKind.RightParenthesis, out var rightParenthesis))
                {
                    ReportExpected("')'", Current.Span);
                }
                else
                {
                    end = rightParenthesis.Span.End;
                }

                return new PythonParenthesizedExpression(
                    expression,
                    TextSpan.FromBounds(leftParenthesis.Span.Start, end)
                );
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
                SyntaxTokenKind.FormattedStringLiteral => PythonConstantKind.FormattedStringLiteral,
                SyntaxTokenKind.TemplateStringLiteral => PythonConstantKind.TemplateStringLiteral,
                _ => (PythonConstantKind?)null,
            };

            return constantKind is null ? null : Constant(Advance(), constantKind.Value);
        }

        private bool TryReadComparisonOperator(
            out PythonComparisonOperator @operator,
            out SyntaxToken token
        )
        {
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
            value is "and" or "or" or "not" or "elif" or "else" or "def" or "return";

        private static bool IsUnsupportedStatementKeyword(string value) =>
            value
                is "assert"
                    or "async"
                    or "break"
                    or "class"
                    or "continue"
                    or "def"
                    or "del"
                    or "elif"
                    or "else"
                    or "for"
                    or "from"
                    or "global"
                    or "if"
                    or "import"
                    or "nonlocal"
                    or "pass"
                    or "raise"
                    or "return"
                    or "try"
                    or "while"
                    or "with"
                    or "yield";

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
                    or "with"
                    or "yield";
    }
}
