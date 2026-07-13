using DotPython.Compiler.Bytecode;
using DotPython.Language.Ast;
using DotPython.Language.Diagnostics;
using DotPython.Language.Text;

namespace DotPython.Compiler;

public static class PythonCompiler
{
    public static PythonCompilationResult Compile(PythonModule module, string codeName = "<module>")
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeName);

        return new Compiler(codeName).Compile(module);
    }

    private sealed class Compiler
    {
        private readonly string _codeName;
        private readonly List<PythonConstant> _constants = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly List<PythonInstruction> _instructions = [];
        private readonly List<string> _names = [];

        internal Compiler(string codeName)
        {
            _codeName = codeName;
        }

        internal PythonCompilationResult Compile(PythonModule module)
        {
            foreach (var statement in module.Statements)
            {
                CompileStatement(statement);
            }

            Emit(PythonOpCode.ReturnNone, 0, new TextSpan(module.Span.End, 0));
            var code = new PythonCodeObject(_codeName, _instructions, _constants, _names);
            return new PythonCompilationResult(code, _diagnostics);
        }

        private void CompileStatement(PythonStatement statement)
        {
            switch (statement)
            {
                case PythonAssignmentStatement assignment:
                    CompileExpression(assignment.Value);
                    Emit(
                        PythonOpCode.StoreName,
                        GetNameIndex(assignment.Target.Name),
                        assignment.Target.Span
                    );
                    break;
                case PythonExpressionStatement expressionStatement:
                    CompileExpression(expressionStatement.Expression);
                    Emit(PythonOpCode.PopTop, 0, expressionStatement.Span);
                    break;
                default:
                    Report(
                        "DPY3001",
                        $"Statement type '{statement.GetType().Name}' is not supported.",
                        statement.Span
                    );
                    break;
            }
        }

        private void CompileExpression(PythonExpression expression)
        {
            switch (expression)
            {
                case PythonConstantExpression constant:
                    Emit(
                        PythonOpCode.LoadConstant,
                        AddConstant(PythonLiteralDecoder.Decode(constant, _diagnostics)),
                        constant.Span
                    );
                    break;
                case PythonNameExpression name:
                    Emit(PythonOpCode.LoadName, GetNameIndex(name.Name), name.Span);
                    break;
                case PythonParenthesizedExpression parenthesized:
                    CompileExpression(parenthesized.Expression);
                    break;
                case PythonUnaryExpression unary:
                    CompileExpression(unary.Operand);
                    Emit(GetUnaryOpCode(unary.Operator), 0, unary.Span);
                    break;
                case PythonBinaryExpression binary:
                    CompileExpression(binary.Left);
                    CompileExpression(binary.Right);
                    Emit(GetBinaryOpCode(binary.Operator), 0, binary.Span);
                    break;
                case PythonCallExpression call:
                    CompileExpression(call.Target);
                    foreach (var argument in call.Arguments)
                    {
                        CompileExpression(argument);
                    }

                    Emit(PythonOpCode.Call, call.Arguments.Count, call.Span);
                    break;
                default:
                    Report(
                        "DPY3002",
                        $"Expression type '{expression.GetType().Name}' is not supported.",
                        expression.Span
                    );
                    Emit(
                        PythonOpCode.LoadConstant,
                        AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                        expression.Span
                    );
                    break;
            }
        }

        private int AddConstant(PythonConstant constant)
        {
            var index = _constants.IndexOf(constant);
            if (index >= 0)
            {
                return index;
            }

            _constants.Add(constant);
            return _constants.Count - 1;
        }

        private int GetNameIndex(string name)
        {
            var index = _names.IndexOf(name);
            if (index >= 0)
            {
                return index;
            }

            _names.Add(name);
            return _names.Count - 1;
        }

        private void Emit(PythonOpCode opCode, int operand, TextSpan span) =>
            _instructions.Add(new PythonInstruction(opCode, operand, span));

        private void Report(string code, string message, TextSpan span) =>
            _diagnostics.Add(new Diagnostic(code, message, DiagnosticSeverity.Error, span));

        private static PythonOpCode GetUnaryOpCode(PythonUnaryOperator @operator) =>
            @operator switch
            {
                PythonUnaryOperator.Positive => PythonOpCode.UnaryPositive,
                PythonUnaryOperator.Negative => PythonOpCode.UnaryNegative,
                PythonUnaryOperator.Invert => PythonOpCode.UnaryInvert,
                _ => throw new ArgumentOutOfRangeException(nameof(@operator)),
            };

        private static PythonOpCode GetBinaryOpCode(PythonBinaryOperator @operator) =>
            @operator switch
            {
                PythonBinaryOperator.Add => PythonOpCode.BinaryAdd,
                PythonBinaryOperator.Subtract => PythonOpCode.BinarySubtract,
                PythonBinaryOperator.Multiply => PythonOpCode.BinaryMultiply,
                PythonBinaryOperator.TrueDivide => PythonOpCode.BinaryTrueDivide,
                PythonBinaryOperator.FloorDivide => PythonOpCode.BinaryFloorDivide,
                PythonBinaryOperator.Modulo => PythonOpCode.BinaryModulo,
                PythonBinaryOperator.Power => PythonOpCode.BinaryPower,
                _ => throw new ArgumentOutOfRangeException(nameof(@operator)),
            };
    }
}
