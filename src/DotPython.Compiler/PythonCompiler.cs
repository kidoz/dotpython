using DotPython.Compiler.Binding;
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

        var binding = PythonSymbolBinder.Bind(module);
        var diagnostics = new List<Diagnostic>(binding.Diagnostics);
        return new Compiler(codeName, binding.ModuleScope, diagnostics).Compile(module);
    }

    private sealed class Compiler
    {
        private readonly string _codeName;
        private readonly List<PythonConstant> _constants = [];
        private readonly List<Diagnostic> _diagnostics;
        private readonly List<PythonInstruction> _instructions = [];
        private readonly List<string> _names = [];
        private readonly PythonBoundScope _scope;

        internal Compiler(string codeName, PythonBoundScope scope, List<Diagnostic> diagnostics)
        {
            _codeName = codeName;
            _scope = scope;
            _diagnostics = diagnostics;
        }

        internal PythonCompilationResult Compile(PythonModule module)
        {
            var code = CompileCode(module.Statements, module.Span.End);
            return new PythonCompilationResult(code, _diagnostics);
        }

        private PythonCodeObject CompileCode(
            IReadOnlyList<PythonStatement> statements,
            int endPosition
        )
        {
            CompileStatements(statements);
            Emit(PythonOpCode.ReturnNone, 0, new TextSpan(endPosition, 0));
            return new PythonCodeObject(
                _codeName,
                _instructions,
                _constants,
                _names,
                [.. _scope.LocalNames],
                [.. _scope.CellVariableNames],
                [.. _scope.FreeVariableNames],
                _scope.Parameters.Count
            );
        }

        private void CompileStatement(PythonStatement statement)
        {
            switch (statement)
            {
                case PythonAssignmentStatement assignment:
                    CompileExpression(assignment.Value);
                    CompileAssignmentTarget(assignment.Target);
                    break;
                case PythonExpressionStatement expressionStatement:
                    CompileExpression(expressionStatement.Expression);
                    Emit(PythonOpCode.PopTop, 0, expressionStatement.Span);
                    break;
                case PythonIfStatement ifStatement:
                    CompileIfStatement(ifStatement);
                    break;
                case PythonWhileStatement whileStatement:
                    CompileWhileStatement(whileStatement);
                    break;
                case PythonForStatement forStatement:
                    CompileForStatement(forStatement);
                    break;
                case PythonFunctionDefinitionStatement function:
                    CompileFunctionDefinition(function);
                    break;
                case PythonReturnStatement returnStatement:
                    CompileReturnStatement(returnStatement);
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
                    EmitLoadName(name);
                    break;
                case PythonParenthesizedExpression parenthesized:
                    CompileExpression(parenthesized.Expression);
                    break;
                case PythonUnaryExpression unary:
                    CompileExpression(unary.Operand);
                    Emit(GetUnaryOpCode(unary.Operator), 0, unary.Span);
                    break;
                case PythonBinaryExpression binary:
                    CompileBinaryExpression(binary);
                    break;
                case PythonComparisonExpression comparison:
                    CompileComparisonExpression(comparison);
                    break;
                case PythonCallExpression call:
                    CompileExpression(call.Target);
                    foreach (var argument in call.Arguments)
                    {
                        CompileExpression(argument);
                    }

                    Emit(PythonOpCode.Call, call.Arguments.Count, call.Span);
                    break;
                case PythonListExpression list:
                    CompileElements(list.Elements);
                    Emit(PythonOpCode.BuildList, list.Elements.Count, list.Span);
                    break;
                case PythonTupleExpression tuple:
                    CompileElements(tuple.Elements);
                    Emit(PythonOpCode.BuildTuple, tuple.Elements.Count, tuple.Span);
                    break;
                case PythonDictionaryExpression dictionary:
                    foreach (var item in dictionary.Items)
                    {
                        CompileExpression(item.Key);
                        CompileExpression(item.Value);
                    }

                    Emit(PythonOpCode.BuildDictionary, dictionary.Items.Count, dictionary.Span);
                    break;
                case PythonSubscriptionExpression subscription:
                    CompileExpression(subscription.Target);
                    CompileExpression(subscription.Index);
                    Emit(PythonOpCode.LoadSubscript, 0, subscription.Span);
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

        private void CompileElements(IReadOnlyList<PythonExpression> elements)
        {
            foreach (var element in elements)
            {
                CompileExpression(element);
            }
        }

        private void CompileAssignmentTarget(PythonExpression target)
        {
            switch (target)
            {
                case PythonNameExpression name:
                    EmitStoreName(name);
                    break;
                case PythonSubscriptionExpression subscription:
                    CompileExpression(subscription.Target);
                    CompileExpression(subscription.Index);
                    Emit(PythonOpCode.StoreSubscript, 0, subscription.Span);
                    break;
                default:
                    Report("DPY3003", "This expression cannot be assigned to.", target.Span);
                    Emit(PythonOpCode.PopTop, 0, target.Span);
                    break;
            }
        }

        private void CompileIfStatement(PythonIfStatement statement)
        {
            var endJumps = new List<int>();
            foreach (var clause in statement.Clauses)
            {
                CompileExpression(clause.Condition);
                var nextClauseJump = Emit(PythonOpCode.JumpIfFalse, 0, clause.Condition.Span);
                CompileStatements(clause.Body);
                endJumps.Add(Emit(PythonOpCode.Jump, 0, clause.Span));
                PatchJump(nextClauseJump, _instructions.Count);
            }

            CompileStatements(statement.ElseBody);
            foreach (var endJump in endJumps)
            {
                PatchJump(endJump, _instructions.Count);
            }
        }

        private void CompileFunctionDefinition(PythonFunctionDefinitionStatement function)
        {
            var childScope = _scope.Children.Single(scope =>
                ReferenceEquals(scope.Definition, function)
            );
            var childCompiler = new Compiler(function.Name.Name, childScope, _diagnostics);
            var childCode = childCompiler.CompileCode(function.Body, function.Span.End);
            var constantIndex = AddConstant(
                new PythonConstant(PythonConstantType.CodeObject, childCode)
            );
            Emit(PythonOpCode.MakeFunction, constantIndex, function.Span);
            EmitStoreName(function.Name);
        }

        private void CompileReturnStatement(PythonReturnStatement statement)
        {
            if (_scope.Kind != PythonScopeKind.Function)
            {
                Report("DPY3103", "'return' outside function.", statement.Span);
                return;
            }

            if (statement.Value is null || IsNoneLiteral(statement.Value))
            {
                Emit(PythonOpCode.ReturnNone, 0, statement.Span);
                return;
            }

            CompileExpression(statement.Value);
            Emit(PythonOpCode.ReturnValue, 0, statement.Span);
        }

        private static bool IsNoneLiteral(PythonExpression expression) =>
            expression switch
            {
                PythonConstantExpression { ConstantKind: PythonConstantKind.NoneLiteral } => true,
                PythonParenthesizedExpression parenthesized => IsNoneLiteral(
                    parenthesized.Expression
                ),
                _ => false,
            };

        private void CompileWhileStatement(PythonWhileStatement statement)
        {
            var loopStart = _instructions.Count;
            CompileExpression(statement.Condition);
            var exitJump = Emit(PythonOpCode.JumpIfFalse, 0, statement.Condition.Span);
            CompileStatements(statement.Body);
            Emit(PythonOpCode.Jump, loopStart, statement.Span);
            PatchJump(exitJump, _instructions.Count);
            CompileStatements(statement.ElseBody);
        }

        private void CompileForStatement(PythonForStatement statement)
        {
            CompileExpression(statement.Iterable);
            Emit(PythonOpCode.GetIterator, 0, statement.Iterable.Span);
            var loopStart = _instructions.Count;
            var exitJump = Emit(PythonOpCode.ForIter, 0, statement.Iterable.Span);
            EmitStoreName(statement.Target);
            CompileStatements(statement.Body);
            Emit(PythonOpCode.Jump, loopStart, statement.Span);
            PatchJump(exitJump, _instructions.Count);
            CompileStatements(statement.ElseBody);
        }

        private void CompileStatements(IReadOnlyList<PythonStatement> statements)
        {
            foreach (var statement in statements)
            {
                CompileStatement(statement);
            }
        }

        private void CompileBinaryExpression(PythonBinaryExpression expression)
        {
            CompileExpression(expression.Left);
            if (expression.Operator is PythonBinaryOperator.And or PythonBinaryOperator.Or)
            {
                var jump = Emit(
                    expression.Operator == PythonBinaryOperator.And
                        ? PythonOpCode.JumpIfFalseOrPop
                        : PythonOpCode.JumpIfTrueOrPop,
                    0,
                    expression.Left.Span
                );
                CompileExpression(expression.Right);
                PatchJump(jump, _instructions.Count);
                return;
            }

            CompileExpression(expression.Right);
            Emit(GetBinaryOpCode(expression.Operator), 0, expression.Span);
        }

        private void CompileComparisonExpression(PythonComparisonExpression expression)
        {
            CompileExpression(expression.Left);
            if (expression.Comparisons.Count == 1)
            {
                var comparison = expression.Comparisons[0];
                CompileExpression(comparison.Right);
                Emit(GetComparisonOpCode(comparison.Operator), 0, comparison.Span);
                return;
            }

            var failureJumps = new List<int>();
            for (var index = 0; index < expression.Comparisons.Count; index++)
            {
                var comparison = expression.Comparisons[index];
                CompileExpression(comparison.Right);

                if (index < expression.Comparisons.Count - 1)
                {
                    Emit(PythonOpCode.CopyTop, 0, comparison.Span);
                    Emit(PythonOpCode.RotateThree, 0, comparison.Span);
                    Emit(GetComparisonOpCode(comparison.Operator), 0, comparison.Span);
                    failureJumps.Add(Emit(PythonOpCode.JumpIfFalseOrPop, 0, comparison.Span));
                }
                else
                {
                    Emit(GetComparisonOpCode(comparison.Operator), 0, comparison.Span);
                }
            }

            var endJump = Emit(PythonOpCode.Jump, 0, expression.Span);
            var failureTarget = _instructions.Count;
            Emit(PythonOpCode.RotateTwo, 0, expression.Span);
            Emit(PythonOpCode.PopTop, 0, expression.Span);
            var endTarget = _instructions.Count;

            foreach (var failureJump in failureJumps)
            {
                PatchJump(failureJump, failureTarget);
            }

            PatchJump(endJump, endTarget);
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

        private int GetVariableIndex(string name)
        {
            if (_scope.IsLocal(name))
            {
                return _scope.GetLocalIndex(name);
            }

            throw new InvalidOperationException($"The local variable '{name}' was not bound.");
        }

        private int GetCellIndex(string name)
        {
            if (_scope.IsCellVariable(name))
            {
                return _scope.GetCellVariableIndex(name);
            }

            if (_scope.IsFreeVariable(name))
            {
                return _scope.CellVariableNames.Count + _scope.GetFreeVariableIndex(name);
            }

            throw new InvalidOperationException($"The closure variable '{name}' was not bound.");
        }

        private void EmitLoadName(PythonNameExpression name)
        {
            if (_scope.Kind == PythonScopeKind.Function && _scope.IsLocal(name.Name))
            {
                Emit(
                    _scope.IsCellVariable(name.Name)
                        ? PythonOpCode.LoadCell
                        : PythonOpCode.LoadLocal,
                    _scope.IsCellVariable(name.Name)
                        ? GetCellIndex(name.Name)
                        : GetVariableIndex(name.Name),
                    name.Span
                );
                return;
            }

            if (_scope.Kind == PythonScopeKind.Function && _scope.IsFreeVariable(name.Name))
            {
                Emit(PythonOpCode.LoadCell, GetCellIndex(name.Name), name.Span);
                return;
            }

            Emit(PythonOpCode.LoadName, GetNameIndex(name.Name), name.Span);
        }

        private void EmitStoreName(PythonNameExpression name)
        {
            if (_scope.Kind == PythonScopeKind.Function)
            {
                Emit(
                    _scope.IsCellVariable(name.Name)
                        ? PythonOpCode.StoreCell
                        : PythonOpCode.StoreLocal,
                    _scope.IsCellVariable(name.Name)
                        ? GetCellIndex(name.Name)
                        : GetVariableIndex(name.Name),
                    name.Span
                );
                return;
            }

            Emit(PythonOpCode.StoreName, GetNameIndex(name.Name), name.Span);
        }

        private int Emit(PythonOpCode opCode, int operand, TextSpan span)
        {
            _instructions.Add(new PythonInstruction(opCode, operand, span));
            return _instructions.Count - 1;
        }

        private void PatchJump(int instructionIndex, int target) =>
            _instructions[instructionIndex] = _instructions[instructionIndex] with
            {
                Operand = target,
            };

        private void Report(string code, string message, TextSpan span) =>
            _diagnostics.Add(new Diagnostic(code, message, DiagnosticSeverity.Error, span));

        private static PythonOpCode GetUnaryOpCode(PythonUnaryOperator @operator) =>
            @operator switch
            {
                PythonUnaryOperator.Positive => PythonOpCode.UnaryPositive,
                PythonUnaryOperator.Negative => PythonOpCode.UnaryNegative,
                PythonUnaryOperator.Invert => PythonOpCode.UnaryInvert,
                PythonUnaryOperator.Not => PythonOpCode.UnaryNot,
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

        private static PythonOpCode GetComparisonOpCode(PythonComparisonOperator @operator) =>
            @operator switch
            {
                PythonComparisonOperator.Equal => PythonOpCode.CompareEqual,
                PythonComparisonOperator.NotEqual => PythonOpCode.CompareNotEqual,
                PythonComparisonOperator.LessThan => PythonOpCode.CompareLessThan,
                PythonComparisonOperator.LessThanOrEqual => PythonOpCode.CompareLessThanOrEqual,
                PythonComparisonOperator.GreaterThan => PythonOpCode.CompareGreaterThan,
                PythonComparisonOperator.GreaterThanOrEqual =>
                    PythonOpCode.CompareGreaterThanOrEqual,
                _ => throw new ArgumentOutOfRangeException(nameof(@operator)),
            };
    }
}
