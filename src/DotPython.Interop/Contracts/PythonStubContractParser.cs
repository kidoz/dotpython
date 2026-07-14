using DotPython.Contracts;
using DotPython.Language.Diagnostics;
using DotPython.Language.Syntax;
using DotPython.Language.Text;

namespace DotPython.Interop.Contracts;

/// <summary>Parses a safe, non-executing subset of Python stub files into typed export contracts.</summary>
public static class PythonStubContractParser
{
    private const int MaximumStubLength = 4 * 1024 * 1024;

    /// <summary>Parses and maps a <c>.pyi</c> source file without importing or executing Python.</summary>
    public static PythonContractCompilationResult Parse(
        SourceText source,
        PythonStubContractOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (source.Length > MaximumStubLength)
        {
            return new PythonContractCompilationResult(
                source,
                null,
                [
                    new Diagnostic(
                        PythonContractDiagnosticCodes.InvalidSyntax,
                        "The Python stub contract is too large.",
                        DiagnosticSeverity.Error,
                        new TextSpan(0, 0)
                    ),
                ]
            );
        }

        var tokenization = PythonTokenizer.Tokenize(source);
        var parser = new Parser(source, tokenization.Tokens, tokenization.Diagnostics, options);
        return parser.Parse();
    }

    private static void ValidateOptions(PythonStubContractOptions options)
    {
        if (!PythonContractNameConverter.IsValidQualifiedName(options.ModuleName))
        {
            throw new ArgumentException(
                "The module name must be a dotted portable Python identifier.",
                nameof(options)
            );
        }

        if (!PythonContractNameConverter.IsValidQualifiedName(options.ClrNamespace))
        {
            throw new ArgumentException(
                "The CLR namespace must contain portable identifier segments.",
                nameof(options)
            );
        }

        if (!PythonContractNameConverter.IsValidIdentifier(options.ClrTypeName))
        {
            throw new ArgumentException(
                "The CLR type name must be a portable identifier.",
                nameof(options)
            );
        }

        if (
            !Enum.IsDefined(options.StatePolicy)
            || options.StatePolicy == PythonModuleStatePolicy.None
        )
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private sealed class Parser
    {
        private readonly List<Diagnostic> _diagnostics;
        private readonly List<PythonFunctionContract> _functions = [];
        private readonly Dictionary<string, string> _imports = new(StringComparer.Ordinal);
        private readonly PythonToClrTypeMapper _mapper;
        private readonly PythonStubContractOptions _options;
        private readonly SourceText _source;
        private readonly IReadOnlyList<SyntaxToken> _tokens;
        private int _position;

        internal Parser(
            SourceText source,
            IReadOnlyList<SyntaxToken> tokens,
            IReadOnlyList<Diagnostic> tokenizationDiagnostics,
            PythonStubContractOptions options
        )
        {
            _source = source;
            _tokens =
            [
                .. tokens.Where(token => token.Kind != SyntaxTokenKind.NonSignificantNewLine),
            ];
            _diagnostics = [.. tokenizationDiagnostics];
            _options = options;
            _mapper = new PythonToClrTypeMapper(options.ExternalTypeMappings);
        }

        internal PythonContractCompilationResult Parse()
        {
            while (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                if (Match(SyntaxTokenKind.NewLine))
                {
                    continue;
                }

                if (IsIdentifier("from"))
                {
                    ParseFromImport();
                }
                else if (IsIdentifier("import"))
                {
                    ParseImport();
                }
                else if (IsIdentifier("async") || IsIdentifier("def"))
                {
                    ParseFunction();
                }
                else
                {
                    Report(
                        PythonContractDiagnosticCodes.UnsupportedDeclaration,
                        "Only imports and top-level function declarations are supported in an export stub.",
                        Current.Span
                    );
                    RecoverTopLevel();
                }
            }

            PythonModuleContract? contract = null;
            if (_diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error))
            {
                contract = new PythonModuleContract(
                    DotPythonContractFormat.CurrentVersion,
                    _options.ModuleName,
                    _options.ClrNamespace,
                    _options.ClrTypeName,
                    _options.StatePolicy,
                    _functions
                );
            }

            return new PythonContractCompilationResult(_source, contract, _diagnostics);
        }

        private SyntaxToken Current => Peek(0);

        private void ParseImport()
        {
            Advance();
            do
            {
                var module = ParseQualifiedName();
                if (module is null)
                {
                    RecoverTopLevel();
                    return;
                }

                var alias = module.Split('.')[0];
                if (IsIdentifier("as"))
                {
                    Advance();
                    alias = ExpectIdentifier("Expected an alias after 'as'.")?.Text ?? alias;
                }

                AddImport(alias, module, Current.Span);
            } while (Match(SyntaxTokenKind.Comma));

            ExpectLineEnd();
        }

        private void ParseFromImport()
        {
            Advance();
            var module = ParseQualifiedName();
            if (module is null || !IsIdentifier("import"))
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidSyntax,
                    "Expected 'from module import name'.",
                    Current.Span
                );
                RecoverTopLevel();
                return;
            }

            Advance();
            do
            {
                var imported = ExpectIdentifier("Expected an imported type name.");
                if (imported is null)
                {
                    RecoverTopLevel();
                    return;
                }

                var alias = imported.Text;
                if (IsIdentifier("as"))
                {
                    Advance();
                    alias = ExpectIdentifier("Expected an alias after 'as'.")?.Text ?? alias;
                }

                AddImport(alias, module + "." + imported.Text, imported.Span);
            } while (Match(SyntaxTokenKind.Comma));

            ExpectLineEnd();
        }

        private void ParseFunction()
        {
            var isAsync = false;
            if (IsIdentifier("async"))
            {
                isAsync = true;
                Advance();
            }

            if (!IsIdentifier("def"))
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidSyntax,
                    "Expected 'def' after 'async'.",
                    Current.Span
                );
                RecoverTopLevel();
                return;
            }

            Advance();
            var nameToken = ExpectIdentifier("Expected a function name.");
            if (nameToken is null || !Match(SyntaxTokenKind.LeftParenthesis))
            {
                RecoverTopLevel();
                return;
            }

            var parameters = ParseParameters();
            if (parameters is null)
            {
                RecoverTopLevel();
                return;
            }

            if (!Match(SyntaxTokenKind.Arrow))
            {
                Report(
                    PythonContractDiagnosticCodes.MissingAnnotation,
                    $"Exported function '{nameToken.Text}' must declare a return type.",
                    nameToken.Span
                );
                RecoverTopLevel();
                return;
            }

            var returnSyntax = ParseType();
            var returnType = returnSyntax is null ? null : MapType(returnSyntax, allowNone: true);
            if (!Match(SyntaxTokenKind.Colon) || !ParseStubBody())
            {
                RecoverTopLevel();
                return;
            }

            if (nameToken.Text.StartsWith('_'))
            {
                return;
            }

            string clrName;
            try
            {
                clrName = PythonContractNameConverter.ToClrMemberName(nameToken.Text);
            }
            catch (ArgumentException)
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidClrSurface,
                    $"Python export name '{nameToken.Text}' cannot form a portable CLR member name.",
                    nameToken.Span
                );
                return;
            }

            if (parameters.Any(parameter => !parameter.Type.IsClsCompliant))
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidClrSurface,
                    $"Function '{nameToken.Text}' has a non-CLS-compliant parameter type.",
                    nameToken.Span
                );
                return;
            }

            if (returnType is null)
            {
                return;
            }

            if (!returnType.IsClsCompliant)
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidClrSurface,
                    $"Function '{nameToken.Text}' has a non-CLS-compliant return type.",
                    returnSyntax!.Span
                );
                return;
            }

            if (
                _functions.Any(function =>
                    string.Equals(function.PythonName, nameToken.Text, StringComparison.Ordinal)
                    || string.Equals(function.ClrName, clrName, StringComparison.Ordinal)
                )
            )
            {
                Report(
                    PythonContractDiagnosticCodes.DuplicateExport,
                    $"Function '{nameToken.Text}' duplicates an existing Python or CLR export name.",
                    nameToken.Span
                );
                return;
            }

            try
            {
                _functions.Add(
                    new PythonFunctionContract(
                        nameToken.Text,
                        clrName,
                        isAsync ? PythonCallShape.Asynchronous : PythonCallShape.Synchronous,
                        parameters,
                        returnType
                    )
                );
            }
            catch (ArgumentException exception)
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidSyntax,
                    exception.Message,
                    nameToken.Span
                );
            }
        }

        private List<PythonParameterContract>? ParseParameters()
        {
            var parameters = new List<PythonParameterContract>();
            var foundDefault = false;
            if (Match(SyntaxTokenKind.RightParenthesis))
            {
                return parameters;
            }

            while (true)
            {
                if (
                    Current.Kind
                    is SyntaxTokenKind.Star
                        or SyntaxTokenKind.DoubleStar
                        or SyntaxTokenKind.Slash
                )
                {
                    Report(
                        PythonContractDiagnosticCodes.UnsupportedDeclaration,
                        "Variadic, positional-only, and keyword-only parameters are not supported yet.",
                        Current.Span
                    );
                    return null;
                }

                var name = ExpectIdentifier("Expected a parameter name.");
                if (name is null)
                {
                    return null;
                }

                if (!Match(SyntaxTokenKind.Colon))
                {
                    Report(
                        PythonContractDiagnosticCodes.MissingAnnotation,
                        $"Parameter '{name.Text}' must declare a type.",
                        name.Span
                    );
                    return null;
                }

                var syntax = ParseType();
                var type = syntax is null ? null : MapType(syntax, allowNone: false);
                var hasDefault = false;
                if (Match(SyntaxTokenKind.Equal))
                {
                    hasDefault = true;
                    if (!Match(SyntaxTokenKind.Ellipsis))
                    {
                        Report(
                            PythonContractDiagnosticCodes.UnsupportedDeclaration,
                            "Only an ellipsis may represent a parameter default in the initial contract format.",
                            Current.Span
                        );
                        return null;
                    }
                }

                if (!hasDefault && foundDefault)
                {
                    Report(
                        PythonContractDiagnosticCodes.InvalidSyntax,
                        "A required parameter cannot follow a parameter with a default.",
                        name.Span
                    );
                    return null;
                }

                foundDefault |= hasDefault;

                if (type is not null)
                {
                    try
                    {
                        parameters.Add(
                            new PythonParameterContract(
                                name.Text,
                                PythonContractNameConverter.ToClrParameterName(name.Text),
                                PythonParameterKind.PositionalOrKeyword,
                                hasDefault,
                                type
                            )
                        );
                    }
                    catch (ArgumentException)
                    {
                        Report(
                            PythonContractDiagnosticCodes.InvalidClrSurface,
                            $"Parameter name '{name.Text}' cannot form a portable CLR identifier.",
                            name.Span
                        );
                        return null;
                    }
                }

                if (Match(SyntaxTokenKind.RightParenthesis))
                {
                    return parameters;
                }

                if (!Match(SyntaxTokenKind.Comma))
                {
                    Report(
                        PythonContractDiagnosticCodes.InvalidSyntax,
                        "Expected ',' or ')' after a parameter.",
                        Current.Span
                    );
                    return null;
                }

                if (Match(SyntaxTokenKind.RightParenthesis))
                {
                    return parameters;
                }
            }
        }

        private PythonTypeSyntax? ParseType()
        {
            var first = ParseNamedType();
            if (first is null || Current.Kind != SyntaxTokenKind.VerticalBar)
            {
                return first;
            }

            var members = new List<PythonTypeSyntax> { first };
            while (Match(SyntaxTokenKind.VerticalBar))
            {
                var member = ParseNamedType();
                if (member is null)
                {
                    return null;
                }

                members.Add(member);
            }

            return new PythonUnionTypeSyntax(
                members,
                TextSpan.FromBounds(members[0].Span.Start, members[^1].Span.End)
            );
        }

        private PythonNamedTypeSyntax? ParseNamedType()
        {
            var start = Current.Span.Start;
            var name = ParseQualifiedName();
            if (name is null)
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidSyntax,
                    "Expected a type annotation.",
                    Current.Span
                );
                return null;
            }

            var arguments = new List<PythonTypeSyntax>();
            if (Match(SyntaxTokenKind.LeftBracket))
            {
                do
                {
                    var argument = ParseType();
                    if (argument is null)
                    {
                        return null;
                    }

                    arguments.Add(argument);
                } while (Match(SyntaxTokenKind.Comma));

                if (!Match(SyntaxTokenKind.RightBracket))
                {
                    Report(
                        PythonContractDiagnosticCodes.InvalidSyntax,
                        "Expected ']' after generic type arguments.",
                        Current.Span
                    );
                    return null;
                }
            }

            return new PythonNamedTypeSyntax(
                ResolveName(name),
                arguments,
                TextSpan.FromBounds(start, Previous.Span.End)
            );
        }

        private PythonTypeContract? MapType(PythonTypeSyntax syntax, bool allowNone)
        {
            if (syntax is PythonUnionTypeSyntax union)
            {
                var nonNone = union.Members.Where(member => !IsNoneType(member)).ToList();
                if (union.Members.Count != 2 || nonNone.Count != 1)
                {
                    Report(
                        PythonContractDiagnosticCodes.UnsupportedType,
                        "Only a two-member union with None is supported at the CLR boundary.",
                        union.Span
                    );
                    return null;
                }

                return MapNamedType(nonNone[0], isNullable: true, allowNone: false);
            }

            return MapNamedType(syntax, isNullable: false, allowNone);
        }

        private PythonTypeContract? MapNamedType(
            PythonTypeSyntax syntax,
            bool isNullable,
            bool allowNone
        )
        {
            if (syntax is not PythonNamedTypeSyntax named)
            {
                Report(
                    PythonContractDiagnosticCodes.UnsupportedType,
                    "Nested non-null unions are not supported at the CLR boundary.",
                    syntax.Span
                );
                return null;
            }

            if (named.Name is "typing.Optional")
            {
                if (named.TypeArguments.Count != 1)
                {
                    Report(
                        PythonContractDiagnosticCodes.UnsupportedType,
                        "Optional must have exactly one type argument.",
                        named.Span
                    );
                    return null;
                }

                return MapNamedType(named.TypeArguments[0], isNullable: true, allowNone: false);
            }

            var arguments = new List<PythonTypeContract>();
            foreach (var argumentSyntax in named.TypeArguments)
            {
                var argument = MapType(argumentSyntax, allowNone: false);
                if (argument is null)
                {
                    return null;
                }

                arguments.Add(argument);
            }

            if (!_mapper.TryMap(named.Name, arguments, isNullable, out var mapped))
            {
                var code = PythonToClrTypeMapper.IsIntrinsicOrCollectionType(named.Name)
                    ? PythonContractDiagnosticCodes.UnsupportedType
                    : PythonContractDiagnosticCodes.UnresolvedExternalType;
                Report(
                    code,
                    $"Python type '{named.Name}' has no supported CLR mapping.",
                    named.Span
                );
                return null;
            }

            if (
                !allowNone
                && string.Equals(mapped.ClrTypeName, "System.Void", StringComparison.Ordinal)
            )
            {
                Report(
                    PythonContractDiagnosticCodes.UnsupportedType,
                    "None is only valid as a function return type or as the nullable union member.",
                    named.Span
                );
                return null;
            }

            return mapped;
        }

        private bool ParseStubBody()
        {
            if (Match(SyntaxTokenKind.Ellipsis))
            {
                return ExpectLineEnd();
            }

            if (
                Match(SyntaxTokenKind.NewLine)
                && Match(SyntaxTokenKind.Indent)
                && Match(SyntaxTokenKind.Ellipsis)
                && Match(SyntaxTokenKind.NewLine)
                && Match(SyntaxTokenKind.Dedent)
            )
            {
                return true;
            }

            Report(
                PythonContractDiagnosticCodes.UnsupportedDeclaration,
                "A stub function body must contain only an ellipsis.",
                Current.Span
            );
            return false;
        }

        private string? ParseQualifiedName()
        {
            var first = ExpectIdentifier("Expected an identifier.");
            if (first is null)
            {
                return null;
            }

            var parts = new List<string> { first.Text };
            while (Match(SyntaxTokenKind.Dot))
            {
                var part = ExpectIdentifier("Expected an identifier after '.'.");
                if (part is null)
                {
                    return null;
                }

                parts.Add(part.Text);
            }

            return string.Join('.', parts);
        }

        private string ResolveName(string name)
        {
            var separator = name.IndexOf('.', StringComparison.Ordinal);
            var first = separator < 0 ? name : name[..separator];
            if (!_imports.TryGetValue(first, out var imported))
            {
                return name;
            }

            return separator < 0 ? imported : imported + name[separator..];
        }

        private static bool IsNoneType(PythonTypeSyntax syntax) =>
            syntax
                is PythonNamedTypeSyntax { Name: "None", TypeArguments.Count: 0 }
                    or PythonNamedTypeSyntax { Name: "builtins.None", TypeArguments.Count: 0 };

        private void AddImport(string alias, string qualifiedName, TextSpan span)
        {
            if (!_imports.TryAdd(alias, qualifiedName))
            {
                Report(
                    PythonContractDiagnosticCodes.InvalidSyntax,
                    $"Import alias '{alias}' is declared more than once.",
                    span
                );
            }
        }

        private bool ExpectLineEnd()
        {
            if (Match(SyntaxTokenKind.NewLine) || Current.Kind == SyntaxTokenKind.EndOfFile)
            {
                return true;
            }

            Report(
                PythonContractDiagnosticCodes.InvalidSyntax,
                "Expected the end of the logical line.",
                Current.Span
            );
            RecoverTopLevel();
            return false;
        }

        private SyntaxToken? ExpectIdentifier(string message)
        {
            if (Current.Kind == SyntaxTokenKind.Identifier)
            {
                return Advance();
            }

            Report(PythonContractDiagnosticCodes.InvalidSyntax, message, Current.Span);
            return null;
        }

        private bool IsIdentifier(string value) =>
            Current.Kind == SyntaxTokenKind.Identifier
            && string.Equals(Current.Text, value, StringComparison.Ordinal);

        private bool Match(SyntaxTokenKind kind)
        {
            if (Current.Kind != kind)
            {
                return false;
            }

            Advance();
            return true;
        }

        private SyntaxToken Advance()
        {
            var token = Current;
            if (_position < _tokens.Count - 1)
            {
                _position++;
            }

            return token;
        }

        private SyntaxToken Peek(int offset) =>
            _tokens[Math.Clamp(_position + offset, 0, _tokens.Count - 1)];

        private SyntaxToken Previous => Peek(-1);

        private void RecoverTopLevel()
        {
            while (
                Current.Kind
                    is not SyntaxTokenKind.NewLine
                        and not SyntaxTokenKind.EndOfFile
                        and not SyntaxTokenKind.Dedent
            )
            {
                Advance();
            }

            Match(SyntaxTokenKind.NewLine);
            Match(SyntaxTokenKind.Dedent);
        }

        private void Report(string code, string message, TextSpan span) =>
            _diagnostics.Add(new Diagnostic(code, message, DiagnosticSeverity.Error, span));
    }
}
