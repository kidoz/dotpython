using DotPython.Language.Syntax;
using DotPython.Language.Text;

namespace DotPython.Language.Ast;

public abstract record PythonNode(TextSpan Span);

public sealed record PythonModule(IReadOnlyList<PythonStatement> Statements, TextSpan Span)
    : PythonNode(Span);

public abstract record PythonStatement(TextSpan Span) : PythonNode(Span);

public sealed record PythonAssignmentStatement(
    PythonNameExpression Target,
    PythonExpression Value,
    TextSpan Span
) : PythonStatement(Span);

public sealed record PythonExpressionStatement(PythonExpression Expression, TextSpan Span)
    : PythonStatement(Span);

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

public sealed record PythonCallExpression(
    PythonExpression Target,
    IReadOnlyList<PythonExpression> Arguments,
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
}
