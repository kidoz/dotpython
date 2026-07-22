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
                else if (IsKeyword("try"))
                {
                    statements.Add(ParseTryStatement());
                }
                else if (IsKeyword("def"))
                {
                    statements.Add(ParseFunctionDefinition());
                }
                else if (IsKeyword("class"))
                {
                    statements.Add(ParseClassDefinition());
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
            var sawDefault = false;
            if (Current.Kind != SyntaxTokenKind.RightParenthesis)
            {
                while (true)
                {
                    var parameter = Expect(SyntaxTokenKind.Identifier, "a parameter name");
                    PythonExpression? defaultValue = null;
                    if (Match(SyntaxTokenKind.Equal))
                    {
                        defaultValue = ParseExpression();
                        if (defaultValue is null)
                        {
                            ReportExpected("a default value after '='", Current.Span);
                        }
                    }

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

                        if (defaultValue is not null)
                        {
                            sawDefault = true;
                        }
                        else if (sawDefault)
                        {
                            Report(
                                "DPY2016",
                                "A parameter without a default follows a parameter with a default.",
                                parameter.Span
                            );
                        }

                        parameters.Add(
                            new PythonParameter(
                                parameter.Text,
                                defaultValue,
                                defaultValue is null
                                    ? parameter.Span
                                    : TextSpan.FromBounds(
                                        parameter.Span.Start,
                                        defaultValue.Span.End
                                    )
                            )
                        );
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

        private PythonClassDefinitionStatement ParseClassDefinition()
        {
            var start = Advance().Span.Start;
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
            var colon = Expect(SyntaxTokenKind.Colon, "':' after the class name");
            var enclosingFunctionDepth = _functionDepth;
            _functionDepth = 0;
            IReadOnlyList<PythonStatement> body;
            try
            {
                body = ParseSuite();
            }
            finally
            {
                _functionDepth = enclosingFunctionDepth;
            }

            return new PythonClassDefinitionStatement(
                name,
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

            var expression = ParseExpression();
            if (expression is null)
            {
                return null;
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

        private PythonTupleExpression ParseBareTupleTail(PythonExpression first)
        {
            var elements = new List<PythonExpression> { first };
            var end = first.Span.End;
            while (Match(SyntaxTokenKind.Comma, out var comma))
            {
                end = comma.Span.End;
                if (!StartsExpression())
                {
                    break;
                }

                var element = ParseExpression();
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
                SyntaxTokenKind.Identifier => Current.Text == "not"
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
                    && tuple.Elements.All(IsAssignableTarget),
                _ => false,
            };

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
                            if (
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
                SyntaxTokenKind.FormattedStringLiteral => PythonConstantKind.FormattedStringLiteral,
                SyntaxTokenKind.TemplateStringLiteral => PythonConstantKind.TemplateStringLiteral,
                _ => (PythonConstantKind?)null,
            };

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

            var first = ParseExpression();
            if (first is null)
            {
                ReportExpected("an expression", Current.Span);
                return null;
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
                var element = ParseExpression();
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
                var element = ParseExpression();
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
                var key = ParseExpression();
                if (key is null)
                {
                    ReportExpected("a dictionary key", Current.Span);
                    break;
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

                    var iterable = ParseRequiredExpression("an iterable after 'in'");
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
                    var condition = ParseRequiredExpression("a condition after 'if'");
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
                    or "return"
                    or "break"
                    or "continue"
                    or "pass"
                    or "global"
                    or "nonlocal"
                    or "assert"
                    or "del";

        private static bool IsUnsupportedStatementKeyword(string value) =>
            value
                is "async"
                    or "class"
                    or "def"
                    or "elif"
                    or "else"
                    or "from"
                    or "if"
                    or "import"
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
