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
        return CompileCore(module, codeName, enableReturnLocal: true, enableCallLocal: true);
    }

    internal static PythonCompilationResult CompileWithoutReturnLocal(
        PythonModule module,
        string codeName = "<module>"
    )
    {
        return CompileCore(module, codeName, enableReturnLocal: false, enableCallLocal: true);
    }

    internal static PythonCompilationResult CompileWithoutCallLocal(
        PythonModule module,
        string codeName = "<module>"
    )
    {
        return CompileCore(module, codeName, enableReturnLocal: true, enableCallLocal: false);
    }

    private static PythonCompilationResult CompileCore(
        PythonModule module,
        string codeName,
        bool enableReturnLocal,
        bool enableCallLocal
    )
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeName);

        var binding = PythonSymbolBinder.Bind(module);
        var diagnostics = new List<Diagnostic>(binding.Diagnostics);
        return new Compiler(
            codeName,
            binding.ModuleScope,
            diagnostics,
            enableReturnLocal,
            enableCallLocal
        ).Compile(module);
    }

    private sealed class Compiler
    {
        private readonly string _codeName;
        private readonly List<PythonConstant> _constants = [];
        private readonly List<Diagnostic> _diagnostics;
        private readonly List<int> _finallyBarriers = [];
        private readonly List<PythonInstruction> _instructions = [];
        private readonly List<LoopScope> _loopScopes = [];
        private readonly List<string> _names = [];
        private readonly List<ProtectionScope> _protectionScopes = [];
        private readonly PythonBoundScope _scope;
        private readonly bool _enableCallLocal;
        private readonly bool _enableReturnLocal;

        internal Compiler(
            string codeName,
            PythonBoundScope scope,
            List<Diagnostic> diagnostics,
            bool enableReturnLocal,
            bool enableCallLocal
        )
        {
            _codeName = codeName;
            _scope = scope;
            _diagnostics = diagnostics;
            _enableReturnLocal = enableReturnLocal;
            _enableCallLocal = enableCallLocal;
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
                case PythonAugmentedAssignmentStatement augmented:
                    CompileAugmentedAssignment(augmented);
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
                case PythonClassDefinitionStatement @class:
                    CompileClassDefinition(@class);
                    break;
                case PythonReturnStatement returnStatement:
                    CompileReturnStatement(returnStatement);
                    break;
                case PythonBreakStatement breakStatement:
                    CompileBreakStatement(breakStatement);
                    break;
                case PythonContinueStatement continueStatement:
                    CompileContinueStatement(continueStatement);
                    break;
                case PythonPassStatement:
                case PythonGlobalStatement:
                case PythonNonlocalStatement:
                    break;
                case PythonAssertStatement assertStatement:
                    CompileAssertStatement(assertStatement);
                    break;
                case PythonDeleteStatement deleteStatement:
                    CompileDeleteStatement(deleteStatement);
                    break;
                case PythonRaiseStatement raiseStatement:
                    CompileRaiseStatement(raiseStatement);
                    break;
                case PythonTryStatement tryStatement:
                    CompileTryStatement(tryStatement);
                    break;
                case PythonWithStatement withStatement:
                    CompileWithStatement(withStatement, itemIndex: 0);
                    break;
                case PythonImportStatement importStatement:
                    CompileImportStatement(importStatement);
                    break;
                case PythonFromImportStatement fromImportStatement:
                    CompileFromImportStatement(fromImportStatement);
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
                    var targetName = GetNameExpression(call.Target);
                    if (
                        _enableCallLocal
                        && call.Arguments.Count == 0
                        && call.KeywordArguments.Count == 0
                        && targetName is not null
                        && _scope.Kind == PythonScopeKind.Function
                        && _scope.IsLocal(targetName.Name)
                        && !_scope.IsCellVariable(targetName.Name)
                    )
                    {
                        Emit(PythonOpCode.CallLocal, GetVariableIndex(targetName.Name), call.Span);
                        break;
                    }

                    CompileExpression(call.Target);
                    foreach (var argument in call.Arguments)
                    {
                        CompileExpression(argument);
                    }

                    if (call.KeywordArguments.Count == 0)
                    {
                        Emit(PythonOpCode.Call, call.Arguments.Count, call.Span);
                        break;
                    }

                    foreach (var keywordArgument in call.KeywordArguments)
                    {
                        CompileExpression(keywordArgument.Value);
                    }

                    foreach (var keywordArgument in call.KeywordArguments)
                    {
                        Emit(
                            PythonOpCode.LoadConstant,
                            AddConstant(
                                new PythonConstant(
                                    PythonConstantType.TextValue,
                                    keywordArgument.Name
                                )
                            ),
                            keywordArgument.Span
                        );
                    }

                    Emit(PythonOpCode.BuildTuple, call.KeywordArguments.Count, call.Span);
                    Emit(
                        PythonOpCode.CallKeyword,
                        call.Arguments.Count + call.KeywordArguments.Count,
                        call.Span
                    );
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
                case PythonSetExpression setExpression:
                    CompileElements(setExpression.Elements);
                    Emit(PythonOpCode.BuildSet, setExpression.Elements.Count, setExpression.Span);
                    break;
                case PythonFormattedStringExpression formatted:
                    CompileFormattedString(formatted);
                    break;
                case PythonLambdaExpression lambdaExpression:
                    CompileLambdaExpression(lambdaExpression);
                    break;
                case PythonSliceExpression slice:
                    CompileOptionalSliceBound(slice.Start, slice.Span);
                    CompileOptionalSliceBound(slice.Stop, slice.Span);
                    CompileOptionalSliceBound(slice.Step, slice.Span);
                    Emit(PythonOpCode.BuildSlice, 0, slice.Span);
                    break;
                case PythonListComprehensionExpression listComprehension:
                    CompileComprehension(
                        listComprehension,
                        "<listcomp>",
                        listComprehension.Clauses
                    );
                    break;
                case PythonDictionaryComprehensionExpression dictionaryComprehension:
                    CompileComprehension(
                        dictionaryComprehension,
                        "<dictcomp>",
                        dictionaryComprehension.Clauses
                    );
                    break;
                case PythonAttributeExpression attribute:
                    CompileExpression(attribute.Target);
                    Emit(
                        PythonOpCode.LoadAttribute,
                        GetNameIndex(attribute.AttributeName),
                        attribute.Span
                    );
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

        private void CompileFormattedString(PythonFormattedStringExpression formatted)
        {
            var partCount = 0;
            foreach (var part in formatted.Parts)
            {
                switch (part)
                {
                    case PythonFormattedStringLiteralPart literal:
                        var decoded = formatted.IsRaw
                            ? literal.RawText
                            : PythonLiteralDecoder.DecodeEscapes(literal.RawText);
                        Emit(
                            PythonOpCode.LoadConstant,
                            AddConstant(new PythonConstant(PythonConstantType.TextValue, decoded)),
                            literal.Span
                        );
                        partCount++;
                        break;
                    case PythonFormattedStringInterpolationPart interpolation:
                        CompileExpression(interpolation.Expression);
                        var flags = interpolation.Conversion switch
                        {
                            's' => 1,
                            'r' => 2,
                            'a' => 3,
                            _ => 0,
                        };
                        if (interpolation.FormatSpecification is not null)
                        {
                            Emit(
                                PythonOpCode.LoadConstant,
                                AddConstant(
                                    new PythonConstant(
                                        PythonConstantType.TextValue,
                                        interpolation.FormatSpecification
                                    )
                                ),
                                interpolation.Span
                            );
                            flags |= 4;
                        }

                        Emit(PythonOpCode.FormatValue, flags, interpolation.Span);
                        partCount++;
                        break;
                }
            }

            if (partCount == 0)
            {
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(new PythonConstant(PythonConstantType.TextValue, string.Empty)),
                    formatted.Span
                );
                return;
            }

            if (partCount > 1)
            {
                Emit(PythonOpCode.BuildString, partCount, formatted.Span);
            }
        }

        private void CompileLambdaExpression(PythonLambdaExpression lambdaExpression)
        {
            var childScope = _scope.Children.Single(scope =>
                ReferenceEquals(scope.Definition, lambdaExpression)
            );
            var childCompiler = new Compiler(
                "<lambda>",
                childScope,
                _diagnostics,
                _enableReturnLocal,
                _enableCallLocal
            );
            var childCode = childCompiler.CompileLambdaCode(lambdaExpression);
            var constantIndex = AddConstant(
                new PythonConstant(PythonConstantType.CodeObject, childCode)
            );
            var defaultCount = 0;
            foreach (var parameter in lambdaExpression.Parameters)
            {
                if (parameter.Default is null)
                {
                    continue;
                }

                CompileExpression(parameter.Default);
                defaultCount++;
            }

            if (defaultCount == 0)
            {
                Emit(PythonOpCode.MakeFunction, constantIndex, lambdaExpression.Span);
            }
            else
            {
                Emit(PythonOpCode.BuildTuple, defaultCount, lambdaExpression.Span);
                Emit(PythonOpCode.MakeFunctionWithDefaults, constantIndex, lambdaExpression.Span);
            }
        }

        private PythonCodeObject CompileLambdaCode(PythonLambdaExpression lambdaExpression)
        {
            CompileExpression(lambdaExpression.Body);
            Emit(PythonOpCode.ReturnValue, 0, lambdaExpression.Body.Span);
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

        private void CompileComprehension(
            PythonExpression comprehension,
            string name,
            IReadOnlyList<PythonComprehensionClause> clauses
        )
        {
            if (clauses.Count == 0 || clauses[0] is not PythonComprehensionForClause firstClause)
            {
                Report(
                    "DPY3002",
                    "The comprehension is missing its leading 'for' clause.",
                    comprehension.Span
                );
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                    comprehension.Span
                );
                return;
            }

            var childScope = _scope.Children.Single(scope =>
                ReferenceEquals(scope.Definition, comprehension)
            );
            var childCompiler = new Compiler(
                name,
                childScope,
                _diagnostics,
                _enableReturnLocal,
                _enableCallLocal
            );
            var childCode = childCompiler.CompileComprehensionCode(comprehension, clauses);
            var constantIndex = AddConstant(
                new PythonConstant(PythonConstantType.CodeObject, childCode)
            );
            Emit(PythonOpCode.MakeFunction, constantIndex, comprehension.Span);
            CompileExpression(firstClause.Iterable);
            Emit(PythonOpCode.GetIterator, 0, firstClause.Iterable.Span);
            Emit(PythonOpCode.Call, 1, comprehension.Span);
        }

        private PythonCodeObject CompileComprehensionCode(
            PythonExpression comprehension,
            IReadOnlyList<PythonComprehensionClause> clauses
        )
        {
            Emit(
                comprehension is PythonDictionaryComprehensionExpression
                    ? PythonOpCode.BuildDictionary
                    : PythonOpCode.BuildList,
                0,
                comprehension.Span
            );
            Emit(PythonOpCode.LoadLocal, GetVariableIndex(".0"), comprehension.Span);
            Emit(PythonOpCode.GetIterator, 0, comprehension.Span);
            CompileComprehensionClauses(
                comprehension,
                clauses,
                clauseIndex: 0,
                iteratorDepth: 0,
                innermostLoopStart: 0
            );
            Emit(PythonOpCode.ReturnValue, 0, comprehension.Span);
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

        private void CompileComprehensionClauses(
            PythonExpression comprehension,
            IReadOnlyList<PythonComprehensionClause> clauses,
            int clauseIndex,
            int iteratorDepth,
            int innermostLoopStart
        )
        {
            if (clauseIndex == clauses.Count)
            {
                switch (comprehension)
                {
                    case PythonListComprehensionExpression listComprehension:
                        CompileExpression(listComprehension.Element);
                        Emit(
                            PythonOpCode.ListAppend,
                            iteratorDepth,
                            listComprehension.Element.Span
                        );
                        break;
                    case PythonDictionaryComprehensionExpression dictionaryComprehension:
                        CompileExpression(dictionaryComprehension.Key);
                        CompileExpression(dictionaryComprehension.Value);
                        Emit(
                            PythonOpCode.DictionaryAdd,
                            iteratorDepth,
                            dictionaryComprehension.Span
                        );
                        break;
                }

                return;
            }

            switch (clauses[clauseIndex])
            {
                case PythonComprehensionForClause forClause:
                    if (clauseIndex != 0)
                    {
                        CompileExpression(forClause.Iterable);
                        Emit(PythonOpCode.GetIterator, 0, forClause.Iterable.Span);
                    }

                    var loopStart = _instructions.Count;
                    var exitJump = Emit(PythonOpCode.ForIter, 0, forClause.Span);
                    CompileAssignmentTarget(forClause.Target);
                    CompileComprehensionClauses(
                        comprehension,
                        clauses,
                        clauseIndex + 1,
                        iteratorDepth + 1,
                        loopStart
                    );
                    Emit(PythonOpCode.Jump, loopStart, forClause.Span);
                    PatchJump(exitJump, _instructions.Count);
                    break;
                case PythonComprehensionIfClause ifClause:
                    CompileExpression(ifClause.Condition);
                    Emit(PythonOpCode.JumpIfFalse, innermostLoopStart, ifClause.Span);
                    CompileComprehensionClauses(
                        comprehension,
                        clauses,
                        clauseIndex + 1,
                        iteratorDepth,
                        innermostLoopStart
                    );
                    break;
            }
        }

        private void CompileOptionalSliceBound(PythonExpression? bound, TextSpan span)
        {
            if (bound is null)
            {
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                    span
                );
                return;
            }

            CompileExpression(bound);
        }

        private void CompileAugmentedAssignment(PythonAugmentedAssignmentStatement statement)
        {
            switch (statement.Target)
            {
                case PythonNameExpression name:
                    EmitLoadName(name);
                    CompileExpression(statement.Value);
                    EmitAugmentedOperator(statement.Operator, statement.Span);
                    EmitStoreName(name);
                    break;
                case PythonSubscriptionExpression subscription:
                    CompileExpression(subscription.Target);
                    CompileExpression(subscription.Index);
                    Emit(PythonOpCode.CopyTopTwo, 0, statement.Span);
                    Emit(PythonOpCode.LoadSubscript, 0, subscription.Span);
                    CompileExpression(statement.Value);
                    EmitAugmentedOperator(statement.Operator, statement.Span);
                    Emit(PythonOpCode.RotateThree, 0, statement.Span);
                    Emit(PythonOpCode.StoreSubscript, 0, statement.Span);
                    break;
                case PythonAttributeExpression attribute:
                    CompileExpression(attribute.Target);
                    Emit(PythonOpCode.CopyTop, 0, attribute.Span);
                    Emit(
                        PythonOpCode.LoadAttribute,
                        GetNameIndex(attribute.AttributeName),
                        attribute.Span
                    );
                    CompileExpression(statement.Value);
                    EmitAugmentedOperator(statement.Operator, statement.Span);
                    Emit(PythonOpCode.RotateTwo, 0, statement.Span);
                    Emit(
                        PythonOpCode.StoreAttribute,
                        GetNameIndex(attribute.AttributeName),
                        attribute.Span
                    );
                    break;
                default:
                    Report("DPY3003", "This expression cannot be assigned to.", statement.Span);
                    break;
            }
        }

        private void EmitAugmentedOperator(PythonBinaryOperator @operator, TextSpan span)
        {
            var opCode = @operator switch
            {
                PythonBinaryOperator.Add => PythonOpCode.InPlaceAdd,
                PythonBinaryOperator.Multiply => PythonOpCode.InPlaceMultiply,
                _ => GetBinaryOpCode(@operator),
            };
            Emit(opCode, 0, span);
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
                case PythonAttributeExpression attribute:
                    CompileExpression(attribute.Target);
                    Emit(
                        PythonOpCode.StoreAttribute,
                        GetNameIndex(attribute.AttributeName),
                        attribute.Span
                    );
                    break;
                case PythonParenthesizedExpression parenthesized:
                    CompileAssignmentTarget(parenthesized.Expression);
                    break;
                case PythonTupleExpression tuple:
                    Emit(PythonOpCode.UnpackSequence, tuple.Elements.Count, tuple.Span);
                    foreach (var element in tuple.Elements)
                    {
                        CompileAssignmentTarget(element);
                    }

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
            var childCompiler = new Compiler(
                function.Name.Name,
                childScope,
                _diagnostics,
                _enableReturnLocal,
                _enableCallLocal
            );
            var childCode = childCompiler.CompileCode(function.Body, function.Span.End);
            var constantIndex = AddConstant(
                new PythonConstant(PythonConstantType.CodeObject, childCode)
            );
            var defaultCount = 0;
            foreach (var parameter in function.Parameters)
            {
                if (parameter.Default is null)
                {
                    continue;
                }

                CompileExpression(parameter.Default);
                defaultCount++;
            }

            if (defaultCount == 0)
            {
                Emit(PythonOpCode.MakeFunction, constantIndex, function.Span);
            }
            else
            {
                Emit(PythonOpCode.BuildTuple, defaultCount, function.Span);
                Emit(PythonOpCode.MakeFunctionWithDefaults, constantIndex, function.Span);
            }

            EmitStoreName(function.Name);
        }

        private void CompileClassDefinition(PythonClassDefinitionStatement @class)
        {
            var childScope = _scope.Children.Single(scope =>
                ReferenceEquals(scope.Definition, @class)
            );
            var childCompiler = new Compiler(
                @class.Name.Name,
                childScope,
                _diagnostics,
                _enableReturnLocal,
                _enableCallLocal
            );
            var childCode = childCompiler.CompileCode(@class.Body, @class.Span.End);
            var constantIndex = AddConstant(
                new PythonConstant(PythonConstantType.CodeObject, childCode)
            );
            Emit(PythonOpCode.MakeClass, constantIndex, @class.Span);
            EmitStoreName(@class.Name);
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

            var returnedName = GetNameExpression(statement.Value);
            if (
                _enableReturnLocal
                && returnedName is not null
                && _scope.IsLocal(returnedName.Name)
                && !_scope.IsCellVariable(returnedName.Name)
            )
            {
                Emit(
                    PythonOpCode.ReturnLocal,
                    GetVariableIndex(returnedName.Name),
                    returnedName.Span
                );
                return;
            }

            CompileExpression(statement.Value);
            Emit(PythonOpCode.ReturnValue, 0, statement.Span);
        }

        private void CompileAssertStatement(PythonAssertStatement statement)
        {
            CompileExpression(statement.Condition);
            var raiseJump = Emit(PythonOpCode.JumpIfFalse, 0, statement.Condition.Span);
            var endJump = Emit(PythonOpCode.Jump, 0, statement.Span);
            PatchJump(raiseJump, _instructions.Count);
            Emit(PythonOpCode.LoadName, GetNameIndex("AssertionError"), statement.Span);
            if (statement.Message is not null)
            {
                CompileExpression(statement.Message);
                Emit(PythonOpCode.Call, 1, statement.Span);
            }

            Emit(PythonOpCode.Raise, 1, statement.Span);
            PatchJump(endJump, _instructions.Count);
        }

        private void CompileDeleteStatement(PythonDeleteStatement statement)
        {
            foreach (var target in statement.Targets)
            {
                switch (target)
                {
                    case PythonNameExpression name:
                        EmitDeleteName(name);
                        break;
                    case PythonSubscriptionExpression subscription:
                        CompileExpression(subscription.Target);
                        CompileExpression(subscription.Index);
                        Emit(PythonOpCode.DeleteSubscript, 0, subscription.Span);
                        break;
                    case PythonAttributeExpression attribute:
                        CompileExpression(attribute.Target);
                        Emit(
                            PythonOpCode.DeleteAttribute,
                            GetNameIndex(attribute.AttributeName),
                            attribute.Span
                        );
                        break;
                    default:
                        Report("DPY3003", "This expression cannot be deleted.", target.Span);
                        break;
                }
            }
        }

        private void EmitDeleteName(PythonNameExpression name)
        {
            if (_scope.Kind == PythonScopeKind.Function && _scope.IsLocal(name.Name))
            {
                Emit(
                    _scope.IsCellVariable(name.Name)
                        ? PythonOpCode.DeleteCell
                        : PythonOpCode.DeleteLocal,
                    _scope.IsCellVariable(name.Name)
                        ? GetCellIndex(name.Name)
                        : GetVariableIndex(name.Name),
                    name.Span
                );
                return;
            }

            if (_scope.Kind == PythonScopeKind.Function && _scope.IsFreeVariable(name.Name))
            {
                Emit(PythonOpCode.DeleteCell, GetCellIndex(name.Name), name.Span);
                return;
            }

            Emit(PythonOpCode.DeleteName, GetNameIndex(name.Name), name.Span);
        }

        private void CompileRaiseStatement(PythonRaiseStatement statement)
        {
            if (statement.Exception is null)
            {
                Emit(PythonOpCode.Raise, 0, statement.Span);
                return;
            }

            CompileExpression(statement.Exception);
            if (statement.Cause is null)
            {
                Emit(PythonOpCode.Raise, 1, statement.Span);
                return;
            }

            CompileExpression(statement.Cause);
            Emit(PythonOpCode.Raise, 2, statement.Span);
        }

        private void CompileWithStatement(PythonWithStatement statement, int itemIndex)
        {
            var item = statement.Items[itemIndex];
            CompileExpression(item.Context);
            Emit(PythonOpCode.CopyTop, 0, item.Context.Span);
            Emit(PythonOpCode.LoadAttribute, GetNameIndex("__exit__"), item.Context.Span);
            Emit(PythonOpCode.RotateTwo, 0, item.Context.Span);
            Emit(PythonOpCode.LoadAttribute, GetNameIndex("__enter__"), item.Context.Span);
            Emit(PythonOpCode.Call, 0, item.Context.Span);
            if (item.Target is not null)
            {
                CompileAssignmentTarget(item.Target);
            }
            else
            {
                Emit(PythonOpCode.PopTop, 0, item.Span);
            }

            _protectionScopes.Add(WithProtection.Instance);
            var setupFinally = Emit(PythonOpCode.SetupFinally, 0, statement.Span);
            if (itemIndex + 1 < statement.Items.Count)
            {
                CompileWithStatement(statement, itemIndex + 1);
            }
            else
            {
                CompileStatements(statement.Body);
            }

            Emit(PythonOpCode.PopExceptionBlock, 0, statement.Span);
            _protectionScopes.RemoveAt(_protectionScopes.Count - 1);
            Emit(PythonOpCode.EnterFinally, 0, statement.Span);
            PatchJump(setupFinally, _instructions.Count);
            Emit(PythonOpCode.LoadExceptionInfo, 0, statement.Span);
            Emit(PythonOpCode.Call, 3, statement.Span);
            Emit(PythonOpCode.EndWith, 0, statement.Span);
        }

        private void CompileTryStatement(PythonTryStatement statement)
        {
            if (statement.FinallyBody.Count == 0)
            {
                CompileTryExcept(statement);
                return;
            }

            _protectionScopes.Add(new FinallyProtection(statement.FinallyBody));
            var setupFinally = Emit(PythonOpCode.SetupFinally, 0, statement.Span);
            CompileTryExcept(statement);
            Emit(PythonOpCode.PopExceptionBlock, 0, statement.Span);
            _protectionScopes.RemoveAt(_protectionScopes.Count - 1);
            Emit(PythonOpCode.EnterFinally, 0, statement.Span);
            PatchJump(setupFinally, _instructions.Count);
            CompileFinallyBody(statement.FinallyBody);
            Emit(PythonOpCode.EndFinally, 0, statement.Span);
        }

        private void CompileTryExcept(PythonTryStatement statement)
        {
            if (statement.Handlers.Count == 0)
            {
                CompileStatements(statement.Body);
                return;
            }

            _protectionScopes.Add(ExceptProtection.Instance);
            var setupExcept = Emit(PythonOpCode.SetupExcept, 0, statement.Span);
            CompileStatements(statement.Body);
            Emit(PythonOpCode.PopExceptionBlock, 0, statement.Span);
            _protectionScopes.RemoveAt(_protectionScopes.Count - 1);
            CompileStatements(statement.ElseBody);
            var normalExit = Emit(PythonOpCode.Jump, 0, statement.Span);
            PatchJump(setupExcept, _instructions.Count);

            var handledExits = new List<int>();
            foreach (var handler in statement.Handlers)
            {
                int? nextHandler = null;
                if (handler.Type is not null)
                {
                    Emit(PythonOpCode.LoadException, 0, handler.Span);
                    CompileExpression(handler.Type);
                    Emit(PythonOpCode.MatchException, 0, handler.Type.Span);
                    nextHandler = Emit(PythonOpCode.JumpIfFalse, 0, handler.Type.Span);
                }

                if (handler.Target is not null)
                {
                    Emit(PythonOpCode.LoadException, 0, handler.Target.Span);
                    EmitStoreName(handler.Target);
                }

                _protectionScopes.Add(new HandlerCleanupProtection(handler.Target));
                var setupCleanup = Emit(PythonOpCode.SetupFinally, 0, handler.Span);
                CompileStatements(handler.Body);
                Emit(PythonOpCode.PopExceptionBlock, 0, handler.Span);
                _protectionScopes.RemoveAt(_protectionScopes.Count - 1);
                Emit(PythonOpCode.EnterFinally, 0, handler.Span);
                PatchJump(setupCleanup, _instructions.Count);
                if (handler.Target is not null)
                {
                    Emit(
                        PythonOpCode.LoadConstant,
                        AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                        handler.Target.Span
                    );
                    EmitStoreName(handler.Target);
                    EmitDeleteName(handler.Target);
                }

                Emit(PythonOpCode.ClearException, 0, handler.Span);
                Emit(PythonOpCode.EndFinally, 0, handler.Span);
                handledExits.Add(Emit(PythonOpCode.Jump, 0, handler.Span));

                if (nextHandler is not null)
                {
                    PatchJump(nextHandler.Value, _instructions.Count);
                }
            }

            Emit(PythonOpCode.Raise, 0, statement.Span);
            var end = _instructions.Count;
            PatchJump(normalExit, end);
            foreach (var handledExit in handledExits)
            {
                PatchJump(handledExit, end);
            }
        }

        private void CompileImportStatement(PythonImportStatement statement)
        {
            foreach (var import in statement.Imports)
            {
                EmitImportChain(import.Name, import.Span);
                if (import.Alias is not null)
                {
                    EmitStoreName(new PythonNameExpression(import.Alias, import.Span));
                    continue;
                }

                var topLevelName = GetTopLevelModuleName(import.Name);
                if (!string.Equals(topLevelName, import.Name, StringComparison.Ordinal))
                {
                    Emit(PythonOpCode.PopTop, 0, import.Span);
                    Emit(PythonOpCode.ImportName, GetNameIndex(topLevelName), import.Span);
                }

                EmitStoreName(new PythonNameExpression(topLevelName, import.Span));
            }
        }

        private void CompileFromImportStatement(PythonFromImportStatement statement)
        {
            foreach (var import in statement.Imports)
            {
                EmitImportChain(statement.ModuleName, statement.Span);
                Emit(PythonOpCode.ImportFrom, GetNameIndex(import.Name), import.Span);
                EmitStoreName(new PythonNameExpression(import.Alias ?? import.Name, import.Span));
            }
        }

        private void EmitImportChain(string name, TextSpan span)
        {
            var leadingDots = 0;
            while (leadingDots < name.Length && name[leadingDots] == '.')
            {
                leadingDots++;
            }

            var prefix = name[..leadingDots];
            var suffix = name[leadingDots..];
            var parts = suffix.Length == 0 ? [] : suffix.Split('.');
            if (leadingDots != 0)
            {
                Emit(PythonOpCode.ImportName, GetNameIndex(prefix), span);
                if (parts.Length != 0)
                {
                    Emit(PythonOpCode.PopTop, 0, span);
                }
            }

            for (var index = 0; index < parts.Length; index++)
            {
                prefix =
                    prefix.Length == leadingDots
                        ? prefix + parts[index]
                        : prefix + "." + parts[index];
                Emit(PythonOpCode.ImportName, GetNameIndex(prefix), span);
                if (index != parts.Length - 1)
                {
                    Emit(PythonOpCode.PopTop, 0, span);
                }
            }
        }

        private static string GetTopLevelModuleName(string name)
        {
            var separator = name.IndexOf('.', StringComparison.Ordinal);
            return separator < 0 ? name : name[..separator];
        }

        private static PythonNameExpression? GetNameExpression(PythonExpression expression) =>
            expression switch
            {
                PythonNameExpression name => name,
                PythonParenthesizedExpression parenthesized => GetNameExpression(
                    parenthesized.Expression
                ),
                _ => null,
            };

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
            var loop = PushLoopScope(isForLoop: false, continueTarget: loopStart);
            CompileStatements(statement.Body);
            PopLoopScope();
            Emit(PythonOpCode.Jump, loopStart, statement.Span);
            PatchJump(exitJump, _instructions.Count);
            CompileStatements(statement.ElseBody);
            PatchBreakJumps(loop);
        }

        private void CompileForStatement(PythonForStatement statement)
        {
            CompileExpression(statement.Iterable);
            Emit(PythonOpCode.GetIterator, 0, statement.Iterable.Span);
            var loopStart = _instructions.Count;
            var exitJump = Emit(PythonOpCode.ForIter, 0, statement.Iterable.Span);
            CompileAssignmentTarget(statement.Target);
            var loop = PushLoopScope(isForLoop: true, continueTarget: loopStart);
            CompileStatements(statement.Body);
            PopLoopScope();
            Emit(PythonOpCode.Jump, loopStart, statement.Span);
            PatchJump(exitJump, _instructions.Count);
            CompileStatements(statement.ElseBody);
            PatchBreakJumps(loop);
        }

        private void CompileBreakStatement(PythonBreakStatement statement)
        {
            var loop = GetTargetLoop("break", "DPY3104", statement.Span);
            if (loop is null)
            {
                return;
            }

            UnwindProtectionScopes(loop.ProtectionDepth, statement.Span);
            if (loop.IsForLoop)
            {
                Emit(PythonOpCode.PopTop, 0, statement.Span);
            }

            loop.BreakJumps.Add(Emit(PythonOpCode.Jump, 0, statement.Span));
        }

        private void CompileContinueStatement(PythonContinueStatement statement)
        {
            var loop = GetTargetLoop("continue", "DPY3105", statement.Span);
            if (loop is null)
            {
                return;
            }

            UnwindProtectionScopes(loop.ProtectionDepth, statement.Span);
            Emit(PythonOpCode.Jump, loop.ContinueTarget, statement.Span);
        }

        private LoopScope? GetTargetLoop(string keyword, string outsideLoopCode, TextSpan span)
        {
            var barrier = _finallyBarriers.Count == 0 ? 0 : _finallyBarriers[^1];
            if (_loopScopes.Count > barrier)
            {
                return _loopScopes[^1];
            }

            if (_loopScopes.Count != 0)
            {
                Report(
                    "DPY3106",
                    $"'{keyword}' inside a 'finally' clause is not supported in this runtime slice.",
                    span
                );
            }
            else
            {
                Report(outsideLoopCode, $"'{keyword}' outside loop.", span);
            }

            return null;
        }

        private void UnwindProtectionScopes(int targetDepth, TextSpan span)
        {
            for (var index = _protectionScopes.Count - 1; index >= targetDepth; index--)
            {
                Emit(PythonOpCode.PopExceptionBlock, 0, span);
                switch (_protectionScopes[index])
                {
                    case HandlerCleanupProtection cleanup:
                        if (cleanup.Target is not null)
                        {
                            Emit(
                                PythonOpCode.LoadConstant,
                                AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                                span
                            );
                            EmitStoreName(cleanup.Target);
                            EmitDeleteName(cleanup.Target);
                        }

                        Emit(PythonOpCode.ClearException, 0, span);
                        break;
                    case FinallyProtection finallyProtection:
                        CompileFinallyBody(finallyProtection.FinallyBody);
                        break;
                    case WithProtection:
                        for (var argument = 0; argument < 3; argument++)
                        {
                            Emit(
                                PythonOpCode.LoadConstant,
                                AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                                span
                            );
                        }

                        Emit(PythonOpCode.Call, 3, span);
                        Emit(PythonOpCode.PopTop, 0, span);
                        break;
                }
            }
        }

        private void CompileFinallyBody(IReadOnlyList<PythonStatement> statements)
        {
            _finallyBarriers.Add(_loopScopes.Count);
            CompileStatements(statements);
            _finallyBarriers.RemoveAt(_finallyBarriers.Count - 1);
        }

        private LoopScope PushLoopScope(bool isForLoop, int continueTarget)
        {
            var loop = new LoopScope(isForLoop, continueTarget, _protectionScopes.Count);
            _loopScopes.Add(loop);
            return loop;
        }

        private void PopLoopScope() => _loopScopes.RemoveAt(_loopScopes.Count - 1);

        private void PatchBreakJumps(LoopScope loop)
        {
            foreach (var breakJump in loop.BreakJumps)
            {
                PatchJump(breakJump, _instructions.Count);
            }
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

            if (
                _scope.Kind == PythonScopeKind.Class
                && _scope.IsFreeVariable(name.Name)
                && !_scope.IsLocal(name.Name)
            )
            {
                Emit(PythonOpCode.LoadCell, GetCellIndex(name.Name), name.Span);
                return;
            }

            Emit(PythonOpCode.LoadName, GetNameIndex(name.Name), name.Span);
        }

        private void EmitStoreName(PythonNameExpression name)
        {
            if (_scope.Kind == PythonScopeKind.Function && _scope.IsLocal(name.Name))
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

            if (_scope.Kind == PythonScopeKind.Function && _scope.IsFreeVariable(name.Name))
            {
                Emit(PythonOpCode.StoreCell, GetCellIndex(name.Name), name.Span);
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
                PythonComparisonOperator.In => PythonOpCode.CompareIn,
                PythonComparisonOperator.NotIn => PythonOpCode.CompareNotIn,
                PythonComparisonOperator.Is => PythonOpCode.CompareIs,
                PythonComparisonOperator.IsNot => PythonOpCode.CompareIsNot,
                _ => throw new ArgumentOutOfRangeException(nameof(@operator)),
            };
    }

    private sealed class LoopScope(bool isForLoop, int continueTarget, int protectionDepth)
    {
        internal List<int> BreakJumps { get; } = [];

        internal int ContinueTarget { get; } = continueTarget;

        internal bool IsForLoop { get; } = isForLoop;

        internal int ProtectionDepth { get; } = protectionDepth;
    }

    private abstract record ProtectionScope;

    private sealed record ExceptProtection : ProtectionScope
    {
        internal static readonly ExceptProtection Instance = new();
    }

    private sealed record FinallyProtection(IReadOnlyList<PythonStatement> FinallyBody)
        : ProtectionScope;

    private sealed record WithProtection : ProtectionScope
    {
        internal static readonly WithProtection Instance = new();
    }

    private sealed record HandlerCleanupProtection(PythonNameExpression? Target) : ProtectionScope;
}
