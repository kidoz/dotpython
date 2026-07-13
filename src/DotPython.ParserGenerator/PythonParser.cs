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
            var statements = new List<PythonStatement>();
            SkipNewLines();

            while (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                var start = _position;
                ParseSimpleStatements(statements);

                if (_position == start)
                {
                    ReportExpected("a statement", Current.Span);
                    _position++;
                }

                SkipNewLines();
            }

            var moduleSpan =
                statements.Count == 0
                    ? new TextSpan(0, 0)
                    : TextSpan.FromBounds(statements[0].Span.Start, statements[^1].Span.End);
            var module = new PythonModule(statements.AsReadOnly(), moduleSpan);
            return new PythonParseResult(_source, module, _diagnostics);
        }

        private void ParseSimpleStatements(List<PythonStatement> statements)
        {
            if (Current.Kind is SyntaxTokenKind.Indent or SyntaxTokenKind.Dedent)
            {
                Report(
                    "DPY2004",
                    "Compound statements and indented suites are not supported yet.",
                    Current.Span
                );
                SynchronizeLine();
                return;
            }

            while (Current.Kind is not (SyntaxTokenKind.NewLine or SyntaxTokenKind.EndOfFile))
            {
                var statement = ParseStatement();
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

                if (Current.Kind is SyntaxTokenKind.NewLine or SyntaxTokenKind.EndOfFile)
                {
                    break;
                }
            }

            if (Current.Kind == SyntaxTokenKind.NewLine)
            {
                _position++;
            }
            else if (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                Report(
                    "DPY2003",
                    "Expected a new line or semicolon after the statement.",
                    Current.Span
                );
                SynchronizeLine();
            }
        }

        private PythonStatement? ParseStatement()
        {
            if (
                Current.Kind == SyntaxTokenKind.Identifier
                && IsUnsupportedStatementKeyword(Current.Text)
            )
            {
                Report(
                    "DPY2004",
                    $"The '{Current.Text}' statement is not supported yet.",
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

        private PythonExpression? ParseExpression() => ParseSum();

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
                        or SyntaxTokenKind.EndOfFile
                    )
            )
            {
                _position++;
            }
        }

        private void SynchronizeLine()
        {
            while (Current.Kind is not (SyntaxTokenKind.NewLine or SyntaxTokenKind.EndOfFile))
            {
                _position++;
            }

            if (Current.Kind == SyntaxTokenKind.NewLine)
            {
                _position++;
            }
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

        private static bool IsUnsupportedStatementKeyword(string value) =>
            value
                is "assert"
                    or "async"
                    or "break"
                    or "class"
                    or "continue"
                    or "def"
                    or "del"
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
    }
}
