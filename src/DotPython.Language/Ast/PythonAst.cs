using DotPython.Language.Syntax;
using DotPython.Language.Text;

namespace DotPython.Language.Ast;

public abstract record PythonNode(TextSpan Span);

public sealed record PythonModule(IReadOnlyList<PythonStatement> Statements, TextSpan Span)
    : PythonNode(Span);

public abstract record PythonStatement(TextSpan Span) : PythonNode(Span);

public sealed record PythonAssignmentStatement(
    PythonExpression Target,
    PythonExpression Value,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonAugmentedAssignmentStatement(
    PythonExpression Target,
    PythonBinaryOperator Operator,
    PythonExpression Value,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonExpressionStatement(PythonExpression Expression, TextSpan Span)
    : PythonStatement(Span);

public sealed record PythonFunctionDefinitionStatement(
    IReadOnlyList<PythonExpression> Decorators,
    PythonNameExpression Name,
    IReadOnlyList<PythonParameter> Parameters,
    IReadOnlyList<PythonStatement> Body,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonClassDefinitionStatement(
    IReadOnlyList<PythonExpression> Decorators,
    PythonNameExpression Name,
    IReadOnlyList<PythonStatement> Body,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonParameter(string Name, PythonExpression? Default, TextSpan Span)
    : PythonNode(Span);

public sealed record PythonReturnStatement(PythonExpression? Value, TextSpan Span)
    : PythonStatement(Span);

public sealed record PythonBreakStatement(TextSpan Span) : PythonStatement(Span);

public sealed record PythonContinueStatement(TextSpan Span) : PythonStatement(Span);

public sealed record PythonPassStatement(TextSpan Span) : PythonStatement(Span);

public sealed record PythonAssertStatement(
    PythonExpression Condition,
    PythonExpression? Message,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonDeleteStatement(IReadOnlyList<PythonExpression> Targets, TextSpan Span)
    : PythonStatement(Span);

public sealed record PythonGlobalStatement(IReadOnlyList<PythonNameExpression> Names, TextSpan Span)
    : PythonStatement(Span);

public sealed record PythonNonlocalStatement(
    IReadOnlyList<PythonNameExpression> Names,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonRaiseStatement(
    PythonExpression? Exception,
    PythonExpression? Cause,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonImportStatement(IReadOnlyList<PythonImportAlias> Imports, TextSpan Span)
    : PythonStatement(Span);

public sealed record PythonFromImportStatement(
    string ModuleName,
    IReadOnlyList<PythonImportAlias> Imports,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonImportAlias(string Name, string? Alias, TextSpan Span)
    : PythonNode(Span);

public sealed record PythonIfStatement(
    IReadOnlyList<PythonConditionalClause> Clauses,
    IReadOnlyList<PythonStatement> ElseBody,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonConditionalClause(
    PythonExpression Condition,
    IReadOnlyList<PythonStatement> Body,
    TextSpan Span
) : PythonNode(Span);

public sealed record PythonWhileStatement(
    PythonExpression Condition,
    IReadOnlyList<PythonStatement> Body,
    IReadOnlyList<PythonStatement> ElseBody,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonForStatement(
    PythonExpression Target,
    PythonExpression Iterable,
    IReadOnlyList<PythonStatement> Body,
    IReadOnlyList<PythonStatement> ElseBody,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonWithStatement(
    IReadOnlyList<PythonWithItem> Items,
    IReadOnlyList<PythonStatement> Body,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonWithItem(
    PythonExpression Context,
    PythonExpression? Target,
    TextSpan Span
) : PythonNode(Span);

public sealed record PythonTryStatement(
    IReadOnlyList<PythonStatement> Body,
    IReadOnlyList<PythonExceptHandler> Handlers,
    IReadOnlyList<PythonStatement> ElseBody,
    IReadOnlyList<PythonStatement> FinallyBody,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonExceptHandler(
    PythonExpression? Type,
    PythonNameExpression? Target,
    IReadOnlyList<PythonStatement> Body,
    TextSpan Span
) : PythonNode(Span);

public abstract record PythonExpression(TextSpan Span) : PythonNode(Span);

public sealed record PythonNameExpression(string Name, TextSpan Span) : PythonExpression(Span);

public sealed record PythonConstantExpression(
    PythonConstantKind ConstantKind,
    string TokenText,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonUnaryExpression(
    PythonUnaryOperator Operator,
    PythonExpression Operand,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonBinaryExpression(
    PythonExpression Left,
    PythonBinaryOperator Operator,
    PythonExpression Right,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonComparisonExpression(
    PythonExpression Left,
    IReadOnlyList<PythonComparisonPart> Comparisons,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonComparisonPart(
    PythonComparisonOperator Operator,
    PythonExpression Right,
    TextSpan Span
) : PythonNode(Span);

public sealed record PythonCallExpression(
    PythonExpression Target,
    IReadOnlyList<PythonExpression> Arguments,
    IReadOnlyList<PythonKeywordArgument> KeywordArguments,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonKeywordArgument(string Name, PythonExpression Value, TextSpan Span)
    : PythonNode(Span);

public sealed record PythonListExpression(IReadOnlyList<PythonExpression> Elements, TextSpan Span)
    : PythonExpression(Span);

public sealed record PythonSetExpression(IReadOnlyList<PythonExpression> Elements, TextSpan Span)
    : PythonExpression(Span);

public sealed record PythonLambdaExpression(
    IReadOnlyList<PythonParameter> Parameters,
    PythonExpression Body,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonConditionalExpression(
    PythonExpression Condition,
    PythonExpression TrueResult,
    PythonExpression FalseResult,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonFormattedStringExpression(
    IReadOnlyList<PythonFormattedStringPart> Parts,
    bool IsRaw,
    TextSpan Span
) : PythonExpression(Span);

public abstract record PythonFormattedStringPart(TextSpan Span) : PythonNode(Span);

public sealed record PythonFormattedStringLiteralPart(string RawText, TextSpan Span)
    : PythonFormattedStringPart(Span);

public sealed record PythonFormattedStringInterpolationPart(
    PythonExpression Expression,
    char? Conversion,
    string? FormatSpecification,
    TextSpan Span
) : PythonFormattedStringPart(Span);

public abstract record PythonComprehensionClause(TextSpan Span) : PythonNode(Span);

public sealed record PythonComprehensionForClause(
    PythonExpression Target,
    PythonExpression Iterable,
    TextSpan Span
) : PythonComprehensionClause(Span);

public sealed record PythonComprehensionIfClause(PythonExpression Condition, TextSpan Span)
    : PythonComprehensionClause(Span);

public sealed record PythonListComprehensionExpression(
    PythonExpression Element,
    IReadOnlyList<PythonComprehensionClause> Clauses,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonDictionaryComprehensionExpression(
    PythonExpression Key,
    PythonExpression Value,
    IReadOnlyList<PythonComprehensionClause> Clauses,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonTupleExpression(IReadOnlyList<PythonExpression> Elements, TextSpan Span)
    : PythonExpression(Span);

public sealed record PythonDictionaryExpression(
    IReadOnlyList<PythonDictionaryItem> Items,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonDictionaryItem(
    PythonExpression Key,
    PythonExpression Value,
    TextSpan Span
) : PythonNode(Span);

public sealed record PythonSubscriptionExpression(
    PythonExpression Target,
    PythonExpression Index,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonSliceExpression(
    PythonExpression? Start,
    PythonExpression? Stop,
    PythonExpression? Step,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonAttributeExpression(
    PythonExpression Target,
    string AttributeName,
    TextSpan Span
) : PythonExpression(Span);

public sealed record PythonParenthesizedExpression(PythonExpression Expression, TextSpan Span)
    : PythonExpression(Span);

public enum PythonConstantKind
{
    NoneLiteral,
    BooleanLiteral,
    IntegerLiteral,
    FloatLiteral,
    ImaginaryLiteral,
    StringLiteral,
    BytesLiteral,
    FormattedStringLiteral,
    TemplateStringLiteral,
}

public enum PythonUnaryOperator
{
    Positive,
    Negative,
    Invert,
    Not,
}

public enum PythonBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    TrueDivide,
    FloorDivide,
    Modulo,
    Power,
    And,
    Or,
}

public enum PythonComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    In,
    NotIn,
    Is,
    IsNot,
}
