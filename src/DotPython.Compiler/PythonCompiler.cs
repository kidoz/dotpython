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
        private readonly string? _enclosingClassName;
        private bool _isCoroutine;
        private bool _isAsyncGenerator;
        private int _exceptStarBodyDepth;

        internal Compiler(
            string codeName,
            PythonBoundScope scope,
            List<Diagnostic> diagnostics,
            bool enableReturnLocal,
            bool enableCallLocal,
            string? enclosingClassName = null
        )
        {
            _codeName = codeName;
            _scope = scope;
            _diagnostics = diagnostics;
            _enableReturnLocal = enableReturnLocal;
            _enableCallLocal = enableCallLocal;
            _enclosingClassName = enclosingClassName;
        }

        internal PythonCompilationResult Compile(PythonModule module)
        {
            var code = CompileCode(module.Statements, module.Span.End);
            return new PythonCompilationResult(code, _diagnostics);
        }

        private PythonCodeObject CompileCode(
            IReadOnlyList<PythonStatement> statements,
            int endPosition,
            IReadOnlyList<PythonParameter>? signature = null,
            bool isGenerator = false,
            bool isCoroutine = false
        )
        {
            _isCoroutine = isCoroutine;
            _isAsyncGenerator = isGenerator && isCoroutine;
            CompileStatements(statements);
            Emit(PythonOpCode.ReturnNone, 0, new TextSpan(endPosition, 0));
            return CreateCodeObject(signature, isGenerator, isCoroutine);
        }

        private PythonCodeObject CreateCodeObject(
            IReadOnlyList<PythonParameter>? signature,
            bool isGenerator = false,
            bool isCoroutine = false
        )
        {
            var keywordOnlyCount = 0;
            var positionalOnlyCount = 0;
            var hasVariadicPositional = false;
            var hasVariadicKeywords = false;
            foreach (var parameter in signature ?? [])
            {
                switch (parameter.Kind)
                {
                    case PythonParameterKind.PositionalOnly:
                        positionalOnlyCount++;
                        break;
                    case PythonParameterKind.KeywordOnly:
                        keywordOnlyCount++;
                        break;
                    case PythonParameterKind.VariadicPositional:
                        hasVariadicPositional = true;
                        break;
                    case PythonParameterKind.VariadicKeywords:
                        hasVariadicKeywords = true;
                        break;
                }
            }

            return new PythonCodeObject(
                _codeName,
                _instructions,
                _constants,
                _names,
                [.. _scope.LocalNames],
                [.. _scope.CellVariableNames],
                [.. _scope.FreeVariableNames],
                _scope.Parameters.Count,
                keywordOnlyCount,
                positionalOnlyCount,
                hasVariadicPositional,
                hasVariadicKeywords,
                isGenerator,
                isCoroutine
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
                case PythonAnnotatedAssignmentStatement annotated:
                    // PEP 649 default: the annotation is never evaluated. A bare
                    // annotation binds nothing; one with a value assigns normally.
                    if (annotated.Value is not null)
                    {
                        CompileExpression(annotated.Value);
                        CompileAssignmentTarget(annotated.Target);
                    }

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
                case PythonMatchStatement matchStatement:
                    CompileMatchStatement(matchStatement);
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
                    if (
                        call.Target is PythonNameExpression { Name: "super" }
                        && call.Arguments.Count == 0
                        && call.KeywordArguments.Count == 0
                        && _enclosingClassName is not null
                        && _scope.Parameters.Count > 0
                    )
                    {
                        CompileExpression(call.Target);
                        CompileExpression(new PythonNameExpression(_enclosingClassName, call.Span));
                        CompileExpression(
                            new PythonNameExpression(_scope.Parameters[0], call.Span)
                        );
                        Emit(PythonOpCode.Call, 2, call.Span);
                        break;
                    }

                    if (
                        call.Arguments.Any(argument => argument is PythonStarredExpression)
                        || call.KeywordArguments.Any(keyword => keyword.Name is null)
                    )
                    {
                        CompileCallWithUnpacking(call);
                        break;
                    }

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
                    if (list.Elements.Any(element => element is PythonStarredExpression))
                    {
                        CompileUnpackedElements(list.Elements, list.Span);
                    }
                    else
                    {
                        CompileElements(list.Elements);
                        Emit(PythonOpCode.BuildList, list.Elements.Count, list.Span);
                    }

                    break;
                case PythonTupleExpression tuple:
                    if (tuple.Elements.Any(element => element is PythonStarredExpression))
                    {
                        CompileUnpackedElements(tuple.Elements, tuple.Span);
                        Emit(PythonOpCode.ListToTuple, 0, tuple.Span);
                    }
                    else
                    {
                        CompileElements(tuple.Elements);
                        Emit(PythonOpCode.BuildTuple, tuple.Elements.Count, tuple.Span);
                    }

                    break;
                case PythonDictionaryExpression dictionary:
                    if (dictionary.Items.Any(item => item.Key is null))
                    {
                        Emit(PythonOpCode.BuildDictionary, 0, dictionary.Span);
                        foreach (var item in dictionary.Items)
                        {
                            if (item.Key is null)
                            {
                                CompileExpression(item.Value);
                                Emit(PythonOpCode.DictionaryUpdate, 0, item.Span);
                            }
                            else
                            {
                                CompileExpression(item.Key);
                                CompileExpression(item.Value);
                                Emit(PythonOpCode.DictionaryAdd, 0, item.Span);
                            }
                        }
                    }
                    else
                    {
                        foreach (var item in dictionary.Items)
                        {
                            CompileExpression(item.Key!);
                            CompileExpression(item.Value);
                        }

                        Emit(PythonOpCode.BuildDictionary, dictionary.Items.Count, dictionary.Span);
                    }

                    break;
                case PythonSubscriptionExpression subscription:
                    CompileExpression(subscription.Target);
                    CompileExpression(subscription.Index);
                    Emit(PythonOpCode.LoadSubscript, 0, subscription.Span);
                    break;
                case PythonSetExpression setExpression:
                    if (setExpression.Elements.Any(element => element is PythonStarredExpression))
                    {
                        Emit(PythonOpCode.BuildSet, 0, setExpression.Span);
                        foreach (var element in setExpression.Elements)
                        {
                            if (element is PythonStarredExpression starredElement)
                            {
                                CompileExpression(starredElement.Operand);
                                Emit(PythonOpCode.SetUpdate, 0, starredElement.Span);
                            }
                            else
                            {
                                CompileExpression(element);
                                Emit(PythonOpCode.SetAdd, 0, element.Span);
                            }
                        }
                    }
                    else
                    {
                        CompileElements(setExpression.Elements);
                        Emit(
                            PythonOpCode.BuildSet,
                            setExpression.Elements.Count,
                            setExpression.Span
                        );
                    }

                    break;
                case PythonFormattedStringExpression formatted:
                    CompileFormattedString(formatted);
                    break;
                case PythonTemplateStringExpression template:
                    CompileTemplateString(template);
                    break;
                case PythonLambdaExpression lambdaExpression:
                    CompileLambdaExpression(lambdaExpression);
                    break;
                case PythonYieldExpression yieldExpression:
                    if (yieldExpression.Value is null)
                    {
                        Emit(
                            PythonOpCode.LoadConstant,
                            AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                            yieldExpression.Span
                        );
                    }
                    else
                    {
                        CompileExpression(yieldExpression.Value);
                    }

                    // The resume path pushes the yield expression's result (None in
                    // phase 1) after the suspension point. Async-generator yields wrap
                    // the value so the __anext__ drive can tell a produced value from
                    // an inner-await suspension passing through.
                    if (_isAsyncGenerator)
                    {
                        Emit(PythonOpCode.AsyncYieldWrap, 0, yieldExpression.Span);
                    }

                    Emit(PythonOpCode.Yield, 0, yieldExpression.Span);
                    break;
                case PythonYieldFromExpression yieldFrom:
                {
                    // result = yield from e:
                    //   iter; None; loop: step -> (value,True)|(return,False);
                    //   yielded values re-yield and the sent value feeds the next step;
                    //   on completion the delegate's return value is the result.
                    CompileExpression(yieldFrom.Source);
                    Emit(PythonOpCode.GetIterator, 0, yieldFrom.Span);
                    Emit(
                        PythonOpCode.LoadConstant,
                        AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                        yieldFrom.Span
                    );
                    var loopStart = _instructions.Count;
                    Emit(PythonOpCode.YieldFromStep, 0, yieldFrom.Span);
                    var finished = Emit(PythonOpCode.JumpIfFalse, 0, yieldFrom.Span);
                    Emit(PythonOpCode.Yield, 0, yieldFrom.Span);
                    Emit(PythonOpCode.Jump, loopStart, yieldFrom.Span);
                    PatchJump(finished, _instructions.Count);
                    Emit(PythonOpCode.RotateTwo, 0, yieldFrom.Span);
                    Emit(PythonOpCode.PopTop, 0, yieldFrom.Span);
                    break;
                }
                case PythonAwaitExpression awaitExpression:
                {
                    // result = await e: same delegation loop as `yield from`, but
                    // GetAwaitable resolves the awaitable instead of GetIterator.
                    if (!_isCoroutine)
                    {
                        Report(
                            "DPY3116",
                            "'await' is only allowed directly inside an async function body.",
                            awaitExpression.Span
                        );
                    }

                    CompileExpression(awaitExpression.Operand);
                    Emit(PythonOpCode.GetAwaitable, 0, awaitExpression.Span);
                    EmitAwaitLoop(awaitExpression.Span);
                    break;
                }
                case PythonAssignmentExpression assignmentExpression:
                    CompileExpression(assignmentExpression.Value);
                    Emit(PythonOpCode.CopyTop, 0, assignmentExpression.Span);
                    EmitStoreName(assignmentExpression.Target);
                    break;
                case PythonConditionalExpression conditional:
                    CompileExpression(conditional.Condition);
                    var falseJump = Emit(PythonOpCode.JumpIfFalse, 0, conditional.Condition.Span);
                    CompileExpression(conditional.TrueResult);
                    var endJump = Emit(PythonOpCode.Jump, 0, conditional.Span);
                    PatchJump(falseJump, _instructions.Count);
                    CompileExpression(conditional.FalseResult);
                    PatchJump(endJump, _instructions.Count);
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
                case PythonSetComprehensionExpression setComprehension:
                    CompileComprehension(setComprehension, "<setcomp>", setComprehension.Clauses);
                    break;
                case PythonGeneratorExpression generatorExpression:
                    CompileComprehension(
                        generatorExpression,
                        "<genexpr>",
                        generatorExpression.Clauses
                    );
                    break;
                case PythonStarredExpression starred:
                    Report(
                        "DPY3113",
                        "A starred expression is only valid in call arguments and "
                            + "assignment targets.",
                        starred.Span
                    );
                    Emit(
                        PythonOpCode.LoadConstant,
                        AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                        starred.Span
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

        private void CompileTemplateString(PythonTemplateStringExpression template)
        {
            var statics = new List<string>();
            var interpolations = new List<PythonFormattedStringInterpolationPart>();
            var current = new System.Text.StringBuilder();
            foreach (var part in template.Parts)
            {
                switch (part)
                {
                    case PythonFormattedStringLiteralPart literal:
                        current.Append(
                            template.IsRaw
                                ? literal.RawText
                                : PythonLiteralDecoder.DecodeEscapes(literal.RawText)
                        );
                        break;
                    case PythonFormattedStringInterpolationPart interpolation:
                        statics.Add(current.ToString());
                        current.Clear();
                        interpolations.Add(interpolation);
                        break;
                }
            }

            statics.Add(current.ToString());
            foreach (var text in statics)
            {
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(new PythonConstant(PythonConstantType.TextValue, text)),
                    template.Span
                );
            }

            foreach (var interpolation in interpolations)
            {
                CompileExpression(interpolation.Expression);
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(
                        new PythonConstant(
                            PythonConstantType.TextValue,
                            interpolation.RawExpression ?? string.Empty
                        )
                    ),
                    interpolation.Span
                );
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(
                        interpolation.Conversion is { } conversion
                            ? new PythonConstant(
                                PythonConstantType.TextValue,
                                conversion.ToString()
                            )
                            : new PythonConstant(PythonConstantType.NoneValue, null)
                    ),
                    interpolation.Span
                );
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(
                        new PythonConstant(
                            PythonConstantType.TextValue,
                            interpolation.FormatSpecification ?? string.Empty
                        )
                    ),
                    interpolation.Span
                );
                Emit(PythonOpCode.MakeInterpolation, 0, interpolation.Span);
            }

            Emit(PythonOpCode.MakeTemplate, interpolations.Count, template.Span);
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

        private void EmitFunctionCreation(
            IReadOnlyList<PythonParameter> parameters,
            int constantIndex,
            TextSpan span
        )
        {
            var simpleSignature = parameters.All(parameter =>
                parameter.Kind == PythonParameterKind.Positional
            );
            var positionalDefaultCount = 0;
            foreach (var parameter in parameters)
            {
                if (parameter.Kind != PythonParameterKind.Positional || parameter.Default is null)
                {
                    continue;
                }

                CompileExpression(parameter.Default);
                positionalDefaultCount++;
            }

            if (simpleSignature)
            {
                if (positionalDefaultCount == 0)
                {
                    Emit(PythonOpCode.MakeFunction, constantIndex, span);
                }
                else
                {
                    Emit(PythonOpCode.BuildTuple, positionalDefaultCount, span);
                    Emit(PythonOpCode.MakeFunctionWithDefaults, constantIndex, span);
                }

                return;
            }

            Emit(PythonOpCode.BuildTuple, positionalDefaultCount, span);
            var keywordDefaultCount = 0;
            foreach (var parameter in parameters)
            {
                if (parameter.Kind != PythonParameterKind.KeywordOnly || parameter.Default is null)
                {
                    continue;
                }

                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(new PythonConstant(PythonConstantType.TextValue, parameter.Name)),
                    parameter.Span
                );
                CompileExpression(parameter.Default);
                keywordDefaultCount++;
            }

            Emit(PythonOpCode.BuildDictionary, keywordDefaultCount, span);
            Emit(PythonOpCode.MakeFunctionWithSignature, constantIndex, span);
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
            EmitFunctionCreation(lambdaExpression.Parameters, constantIndex, lambdaExpression.Span);
        }

        private PythonCodeObject CompileLambdaCode(PythonLambdaExpression lambdaExpression)
        {
            CompileExpression(lambdaExpression.Body);
            Emit(PythonOpCode.ReturnValue, 0, lambdaExpression.Body.Span);
            return CreateCodeObject(lambdaExpression.Parameters);
        }

        private void CompileUnpackedElements(
            IReadOnlyList<PythonExpression> elements,
            TextSpan span
        )
        {
            Emit(PythonOpCode.BuildList, 0, span);
            foreach (var element in elements)
            {
                if (element is PythonStarredExpression starred)
                {
                    CompileExpression(starred.Operand);
                    Emit(PythonOpCode.ListExtend, 0, starred.Span);
                }
                else
                {
                    CompileExpression(element);
                    Emit(PythonOpCode.ListAppend, 0, element.Span);
                }
            }
        }

        private void CompileCallWithUnpacking(PythonCallExpression call)
        {
            CompileExpression(call.Target);
            Emit(PythonOpCode.BuildList, 0, call.Span);
            foreach (var argument in call.Arguments)
            {
                if (argument is PythonStarredExpression starred)
                {
                    CompileExpression(starred.Operand);
                    Emit(PythonOpCode.ListExtend, 0, starred.Span);
                }
                else
                {
                    CompileExpression(argument);
                    Emit(PythonOpCode.ListAppend, 0, argument.Span);
                }
            }

            if (call.KeywordArguments.Count == 0)
            {
                Emit(PythonOpCode.CallUnpacked, 0, call.Span);
                return;
            }

            Emit(PythonOpCode.BuildDictionary, 0, call.Span);
            foreach (var keywordArgument in call.KeywordArguments)
            {
                if (keywordArgument.Name is null)
                {
                    CompileExpression(keywordArgument.Value);
                    Emit(PythonOpCode.DictionaryMerge, 0, keywordArgument.Span);
                }
                else
                {
                    Emit(
                        PythonOpCode.LoadConstant,
                        AddConstant(
                            new PythonConstant(PythonConstantType.TextValue, keywordArgument.Name)
                        ),
                        keywordArgument.Span
                    );
                    CompileExpression(keywordArgument.Value);
                    Emit(PythonOpCode.DictionaryAdd, 0, keywordArgument.Span);
                }
            }

            Emit(PythonOpCode.CallUnpacked, 1, call.Span);
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

            // A generator expression with async clauses or awaits is an async generator
            // expression, legal even in sync scopes; other comprehensions only become
            // async inside a coroutine (the parser rejects the remaining placements).
            var isAsync =
                (_isCoroutine || comprehension is PythonGeneratorExpression)
                && (
                    clauses.OfType<PythonComprehensionForClause>().Any(clause => clause.IsAsync)
                    || ComprehensionContainsAwait(comprehension, clauses)
                );

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
            var childCode = childCompiler.CompileComprehensionCode(comprehension, clauses, isAsync);
            var constantIndex = AddConstant(
                new PythonConstant(PythonConstantType.CodeObject, childCode)
            );
            Emit(PythonOpCode.MakeFunction, constantIndex, comprehension.Span);
            CompileExpression(firstClause.Iterable);
            if (!firstClause.IsAsync)
            {
                Emit(PythonOpCode.GetIterator, 0, firstClause.Iterable.Span);
            }

            Emit(PythonOpCode.Call, 1, comprehension.Span);
            if (isAsync && comprehension is not PythonGeneratorExpression)
            {
                // The comprehension body is an implicit coroutine; await it in place.
                // An async generator expression instead returns the generator itself.
                Emit(PythonOpCode.GetAwaitable, 0, comprehension.Span);
                EmitAwaitLoop(comprehension.Span);
            }
        }

        private static bool ComprehensionContainsAwait(
            PythonExpression comprehension,
            IReadOnlyList<PythonComprehensionClause> clauses
        )
        {
            var elements = comprehension switch
            {
                PythonListComprehensionExpression list => new[] { list.Element },
                PythonSetComprehensionExpression set => [set.Element],
                PythonDictionaryComprehensionExpression dictionary =>
                [
                    dictionary.Key,
                    dictionary.Value,
                ],
                PythonGeneratorExpression generator => [generator.Element],
                _ => Array.Empty<PythonExpression>(),
            };
            if (elements.Any(ContainsAwait))
            {
                return true;
            }

            for (var index = 0; index < clauses.Count; index++)
            {
                switch (clauses[index])
                {
                    // The first iterable evaluates in the enclosing scope; its awaits
                    // belong to the enclosing coroutine, not the comprehension.
                    case PythonComprehensionForClause forClause
                        when index != 0 && ContainsAwait(forClause.Iterable):
                    case PythonComprehensionIfClause ifClause
                        when ContainsAwait(ifClause.Condition):
                        return true;
                }
            }

            return false;
        }

        private static bool ContainsAwait(PythonExpression expression) =>
            expression switch
            {
                PythonAwaitExpression => true,
                PythonUnaryExpression unary => ContainsAwait(unary.Operand),
                PythonBinaryExpression binary => ContainsAwait(binary.Left)
                    || ContainsAwait(binary.Right),
                PythonComparisonExpression comparison => ContainsAwait(comparison.Left)
                    || comparison.Comparisons.Any(part => ContainsAwait(part.Right)),
                PythonConditionalExpression conditional => ContainsAwait(conditional.Condition)
                    || ContainsAwait(conditional.TrueResult)
                    || ContainsAwait(conditional.FalseResult),
                PythonCallExpression call => ContainsAwait(call.Target)
                    || call.Arguments.Any(ContainsAwait)
                    || call.KeywordArguments.Any(keyword => ContainsAwait(keyword.Value)),
                PythonSubscriptionExpression subscription => ContainsAwait(subscription.Target)
                    || ContainsAwait(subscription.Index),
                PythonSliceExpression slice => (
                    slice.Start is not null && ContainsAwait(slice.Start)
                )
                    || (slice.Stop is not null && ContainsAwait(slice.Stop))
                    || (slice.Step is not null && ContainsAwait(slice.Step)),
                PythonAttributeExpression attribute => ContainsAwait(attribute.Target),
                PythonParenthesizedExpression parenthesized => ContainsAwait(
                    parenthesized.Expression
                ),
                PythonStarredExpression starred => ContainsAwait(starred.Operand),
                PythonListExpression list => list.Elements.Any(ContainsAwait),
                PythonSetExpression set => set.Elements.Any(ContainsAwait),
                PythonTupleExpression tuple => tuple.Elements.Any(ContainsAwait),
                PythonDictionaryExpression dictionary => dictionary.Items.Any(item =>
                    (item.Key is not null && ContainsAwait(item.Key)) || ContainsAwait(item.Value)
                ),
                PythonAssignmentExpression assignment => ContainsAwait(assignment.Value),
                PythonFormattedStringExpression formatted => formatted
                    .Parts.OfType<PythonFormattedStringInterpolationPart>()
                    .Any(part => ContainsAwait(part.Expression)),
                PythonTemplateStringExpression template => template
                    .Parts.OfType<PythonFormattedStringInterpolationPart>()
                    .Any(part => ContainsAwait(part.Expression)),
                // A nested async comprehension makes the enclosing one async too.
                PythonListComprehensionExpression nestedList => ComprehensionContainsAwait(
                    nestedList,
                    nestedList.Clauses
                ) || nestedList.Clauses.OfType<PythonComprehensionForClause>().Any(c => c.IsAsync),
                PythonSetComprehensionExpression nestedSet => ComprehensionContainsAwait(
                    nestedSet,
                    nestedSet.Clauses
                ) || nestedSet.Clauses.OfType<PythonComprehensionForClause>().Any(c => c.IsAsync),
                PythonDictionaryComprehensionExpression nestedDictionary =>
                    ComprehensionContainsAwait(nestedDictionary, nestedDictionary.Clauses)
                        || nestedDictionary
                            .Clauses.OfType<PythonComprehensionForClause>()
                            .Any(c => c.IsAsync),
                _ => false,
            };

        private PythonCodeObject CompileComprehensionCode(
            PythonExpression comprehension,
            IReadOnlyList<PythonComprehensionClause> clauses,
            bool isAsync = false
        )
        {
            _isCoroutine = isAsync;
            _isAsyncGenerator = isAsync && comprehension is PythonGeneratorExpression;
            if (comprehension is not PythonGeneratorExpression)
            {
                Emit(
                    comprehension switch
                    {
                        PythonDictionaryComprehensionExpression => PythonOpCode.BuildDictionary,
                        PythonSetComprehensionExpression => PythonOpCode.BuildSet,
                        _ => PythonOpCode.BuildList,
                    },
                    0,
                    comprehension.Span
                );
            }

            Emit(PythonOpCode.LoadLocal, GetVariableIndex(".0"), comprehension.Span);
            if (clauses is [PythonComprehensionForClause { IsAsync: true }, ..])
            {
                Emit(PythonOpCode.GetAsyncIterator, 0, comprehension.Span);
            }
            else
            {
                Emit(PythonOpCode.GetIterator, 0, comprehension.Span);
            }

            CompileComprehensionClauses(
                comprehension,
                clauses,
                clauseIndex: 0,
                iteratorDepth: 0,
                innermostLoopStart: 0
            );
            if (comprehension is PythonGeneratorExpression)
            {
                Emit(PythonOpCode.ReturnNone, 0, comprehension.Span);
            }
            else
            {
                Emit(PythonOpCode.ReturnValue, 0, comprehension.Span);
            }

            return CreateCodeObject(
                null,
                comprehension is PythonGeneratorExpression,
                isCoroutine: isAsync
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
                    case PythonSetComprehensionExpression setComprehension:
                        CompileExpression(setComprehension.Element);
                        Emit(PythonOpCode.SetAdd, iteratorDepth, setComprehension.Element.Span);
                        break;
                    case PythonGeneratorExpression generatorExpression:
                        CompileExpression(generatorExpression.Element);
                        if (_isAsyncGenerator)
                        {
                            Emit(PythonOpCode.AsyncYieldWrap, 0, generatorExpression.Element.Span);
                        }

                        Emit(PythonOpCode.Yield, 0, generatorExpression.Element.Span);
                        Emit(PythonOpCode.PopTop, 0, generatorExpression.Element.Span);
                        break;
                }

                return;
            }

            switch (clauses[clauseIndex])
            {
                case PythonComprehensionForClause { IsAsync: true } asyncClause:
                {
                    if (clauseIndex != 0)
                    {
                        CompileExpression(asyncClause.Iterable);
                        Emit(PythonOpCode.GetAsyncIterator, 0, asyncClause.Iterable.Span);
                    }

                    var asyncLoopStart = _instructions.Count;
                    var setupExcept = Emit(PythonOpCode.SetupExcept, 0, asyncClause.Span);
                    Emit(PythonOpCode.CopyTop, 0, asyncClause.Span);
                    Emit(PythonOpCode.GetAsyncNext, 0, asyncClause.Span);
                    EmitAwaitLoop(asyncClause.Span);
                    Emit(PythonOpCode.PopExceptionBlock, 0, asyncClause.Span);
                    CompileAssignmentTarget(asyncClause.Target);
                    CompileComprehensionClauses(
                        comprehension,
                        clauses,
                        clauseIndex + 1,
                        iteratorDepth + 1,
                        asyncLoopStart
                    );
                    Emit(PythonOpCode.Jump, asyncLoopStart, asyncClause.Span);
                    PatchJump(setupExcept, _instructions.Count);
                    var asyncExit = Emit(PythonOpCode.EndAsyncFor, 0, asyncClause.Span);
                    PatchJump(asyncExit, _instructions.Count);
                    break;
                }
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
                    var starIndex = -1;
                    for (var index = 0; index < tuple.Elements.Count; index++)
                    {
                        if (tuple.Elements[index] is PythonStarredExpression)
                        {
                            starIndex = index;
                            break;
                        }
                    }

                    if (starIndex < 0)
                    {
                        Emit(PythonOpCode.UnpackSequence, tuple.Elements.Count, tuple.Span);
                    }
                    else
                    {
                        var afterCount = tuple.Elements.Count - starIndex - 1;
                        if (starIndex > byte.MaxValue || afterCount > byte.MaxValue)
                        {
                            Report(
                                "DPY3113",
                                "Too many assignment targets around the starred target.",
                                tuple.Span
                            );
                        }

                        Emit(
                            PythonOpCode.UnpackSequenceStarred,
                            (starIndex << 8) | afterCount,
                            tuple.Span
                        );
                    }

                    foreach (var element in tuple.Elements)
                    {
                        CompileAssignmentTarget(
                            element is PythonStarredExpression starredElement
                                ? starredElement.Operand
                                : element
                        );
                    }

                    break;
                default:
                    Report("DPY3003", "This expression cannot be assigned to.", target.Span);
                    Emit(PythonOpCode.PopTop, 0, target.Span);
                    break;
            }
        }

        private void CompileMatchStatement(PythonMatchStatement statement)
        {
            // The subject is evaluated once and stays on the stack across case tests;
            // it is popped when a case is selected (before its body) or after the last
            // failed test.
            CompileExpression(statement.Subject);
            var endJumps = new List<int>();
            foreach (var matchCase in statement.Cases)
            {
                var failJumps = new List<int>();
                Emit(PythonOpCode.CopyTop, 0, matchCase.Pattern.Span);
                CompilePatternTest(matchCase.Pattern);
                failJumps.Add(Emit(PythonOpCode.JumpIfFalse, 0, matchCase.Pattern.Span));
                if (matchCase.Guard is not null)
                {
                    CompileExpression(matchCase.Guard);
                    failJumps.Add(Emit(PythonOpCode.JumpIfFalse, 0, matchCase.Guard.Span));
                }

                Emit(PythonOpCode.PopTop, 0, matchCase.Span);
                CompileStatements(matchCase.Body);
                endJumps.Add(Emit(PythonOpCode.Jump, 0, matchCase.Span));
                foreach (var failJump in failJumps)
                {
                    PatchJump(failJump, _instructions.Count);
                }
            }

            Emit(PythonOpCode.PopTop, 0, statement.Span);
            foreach (var endJump in endJumps)
            {
                PatchJump(endJump, _instructions.Count);
            }
        }

        /// <summary>
        /// Emits a pattern test with the contract: the subject copy on the stack top is
        /// consumed and a boolean match result is left in its place. Capture bindings
        /// are applied eagerly, matching CPython's visible partial-binding semantics.
        /// </summary>
        private void CompilePatternTest(PythonPattern pattern)
        {
            switch (pattern)
            {
                case PythonLiteralPattern literalPattern:
                    CompileExpression(literalPattern.Literal);
                    Emit(
                        literalPattern.UseIdentity
                            ? PythonOpCode.CompareIs
                            : PythonOpCode.CompareEqual,
                        0,
                        literalPattern.Span
                    );
                    break;
                case PythonValuePattern valuePattern:
                    CompileExpression(valuePattern.DottedName);
                    Emit(PythonOpCode.CompareEqual, 0, valuePattern.Span);
                    break;
                case PythonCapturePattern capturePattern:
                    if (capturePattern.Name is null)
                    {
                        Emit(PythonOpCode.PopTop, 0, capturePattern.Span);
                    }
                    else
                    {
                        EmitStoreName(
                            new PythonNameExpression(capturePattern.Name, capturePattern.Span)
                        );
                    }

                    EmitLoadTrue(capturePattern.Span);
                    break;
                case PythonAsPattern asPattern:
                {
                    Emit(PythonOpCode.CopyTop, 0, asPattern.Span);
                    CompilePatternTest(asPattern.Inner);
                    var innerFail = Emit(PythonOpCode.JumpIfFalse, 0, asPattern.Span);
                    EmitStoreName(new PythonNameExpression(asPattern.Name, asPattern.Span));
                    EmitLoadTrue(asPattern.Span);
                    var done = Emit(PythonOpCode.Jump, 0, asPattern.Span);
                    PatchJump(innerFail, _instructions.Count);
                    Emit(PythonOpCode.PopTop, 0, asPattern.Span);
                    EmitLoadFalse(asPattern.Span);
                    PatchJump(done, _instructions.Count);
                    break;
                }
                case PythonOrPattern orPattern:
                {
                    var doneJumps = new List<int>();
                    for (var index = 0; index < orPattern.Alternatives.Count - 1; index++)
                    {
                        var alternative = orPattern.Alternatives[index];
                        Emit(PythonOpCode.CopyTop, 0, alternative.Span);
                        CompilePatternTest(alternative);
                        var nextAlternative = Emit(PythonOpCode.JumpIfFalse, 0, alternative.Span);
                        Emit(PythonOpCode.PopTop, 0, alternative.Span);
                        EmitLoadTrue(alternative.Span);
                        doneJumps.Add(Emit(PythonOpCode.Jump, 0, alternative.Span));
                        PatchJump(nextAlternative, _instructions.Count);
                    }

                    CompilePatternTest(orPattern.Alternatives[^1]);
                    foreach (var doneJump in doneJumps)
                    {
                        PatchJump(doneJump, _instructions.Count);
                    }

                    break;
                }
                case PythonSequencePattern sequencePattern:
                    CompileSequencePatternTest(sequencePattern);
                    break;
                case PythonMappingPattern mappingPattern:
                    CompileMappingPatternTest(mappingPattern);
                    break;
                case PythonClassPattern classPattern:
                    CompileClassPatternTest(classPattern);
                    break;
                default:
                    Report(
                        "DPY3115",
                        $"Pattern type '{pattern.GetType().Name}' is not supported in this "
                            + "runtime slice.",
                        pattern.Span
                    );
                    Emit(PythonOpCode.PopTop, 0, pattern.Span);
                    EmitLoadFalse(pattern.Span);
                    break;
            }
        }

        private void CompileSequencePatternTest(PythonSequencePattern pattern)
        {
            var span = pattern.Span;
            var hasStar = pattern.StarIndex >= 0;
            var fixedCount = pattern.Patterns.Count;
            Emit(PythonOpCode.MatchSequenceCheck, 0, span);
            var notSequence = Emit(PythonOpCode.JumpIfFalse, 0, span);
            Emit(PythonOpCode.GetLength, 0, span);
            Emit(
                PythonOpCode.LoadConstant,
                AddConstant(
                    new PythonConstant(
                        PythonConstantType.WholeNumber,
                        new System.Numerics.BigInteger(fixedCount)
                    )
                ),
                span
            );
            Emit(
                hasStar ? PythonOpCode.CompareGreaterThanOrEqual : PythonOpCode.CompareEqual,
                0,
                span
            );
            var lengthMismatch = Emit(PythonOpCode.JumpIfFalse, 0, span);

            // Extraction pops the subject copy; the element order below matches the
            // push order of the unpack opcodes (first element on top).
            var elements = new List<PythonPattern>();
            if (hasStar)
            {
                var afterCount = fixedCount - pattern.StarIndex;
                Emit(
                    PythonOpCode.UnpackSequenceStarred,
                    (pattern.StarIndex << 8) | afterCount,
                    span
                );
                elements.AddRange(pattern.Patterns.Take(pattern.StarIndex));
                elements.Add(new PythonCapturePattern(pattern.StarName, span));
                elements.AddRange(pattern.Patterns.Skip(pattern.StarIndex));
            }
            else
            {
                if (fixedCount == 0)
                {
                    Emit(PythonOpCode.PopTop, 0, span);
                    EmitLoadTrue(span);
                    var emptyDone = Emit(PythonOpCode.Jump, 0, span);
                    PatchJump(notSequence, _instructions.Count);
                    PatchJump(lengthMismatch, _instructions.Count);
                    Emit(PythonOpCode.PopTop, 0, span);
                    EmitLoadFalse(span);
                    PatchJump(emptyDone, _instructions.Count);
                    return;
                }

                Emit(PythonOpCode.UnpackSequence, fixedCount, span);
                elements.AddRange(pattern.Patterns);
            }

            CompileExtractedElementTests(
                elements,
                span,
                [notSequence, lengthMismatch],
                trailingValues: 0
            );
        }

        private void CompileMappingPatternTest(PythonMappingPattern pattern)
        {
            var span = pattern.Span;
            var wantRest = pattern.RestName is not null;
            foreach (var item in pattern.Items)
            {
                CompileExpression(item.Key);
            }

            Emit(PythonOpCode.BuildTuple, pattern.Items.Count, span);
            Emit(PythonOpCode.MatchKeys, wantRest ? 1 : 0, span);
            var noMatch = Emit(PythonOpCode.JumpIfFalse, 0, span);

            // Stack on success: subject, [rest], valuesTuple.
            var elements = pattern.Items.Select(item => item.Pattern).ToList();
            if (elements.Count == 0)
            {
                Emit(PythonOpCode.PopTop, 0, span);
            }
            else
            {
                Emit(PythonOpCode.UnpackSequence, elements.Count, span);
            }

            var failJumps = new List<int>();
            foreach (var element in elements)
            {
                CompilePatternTest(element);
                failJumps.Add(Emit(PythonOpCode.JumpIfFalse, 0, element.Span));
            }

            if (wantRest)
            {
                EmitStoreName(new PythonNameExpression(pattern.RestName!, span));
            }

            Emit(PythonOpCode.PopTop, 0, span);
            EmitLoadTrue(span);
            var done = Emit(PythonOpCode.Jump, 0, span);

            // Element-failure cleanups: pop the remaining values, the optional rest
            // dictionary, and the retained subject copy.
            for (var index = 0; index < failJumps.Count; index++)
            {
                PatchJump(failJumps[index], _instructions.Count);
                var remaining = elements.Count - 1 - index + (wantRest ? 1 : 0) + 1;
                for (var pop = 0; pop < remaining; pop++)
                {
                    Emit(PythonOpCode.PopTop, 0, span);
                }

                EmitLoadFalse(span);
                failJumps[index] = Emit(PythonOpCode.Jump, 0, span);
            }

            // MatchKeys no-match: stack is subject, [None], None.
            PatchJump(noMatch, _instructions.Count);
            Emit(PythonOpCode.PopTop, 0, span);
            if (wantRest)
            {
                Emit(PythonOpCode.PopTop, 0, span);
            }

            Emit(PythonOpCode.PopTop, 0, span);
            EmitLoadFalse(span);
            PatchJump(done, _instructions.Count);
            foreach (var jump in failJumps)
            {
                PatchJump(jump, _instructions.Count);
            }
        }

        private void CompileClassPatternTest(PythonClassPattern pattern)
        {
            var span = pattern.Span;
            CompileExpression(pattern.Cls);
            foreach (var keywordPattern in pattern.Keyword)
            {
                Emit(
                    PythonOpCode.LoadConstant,
                    AddConstant(
                        new PythonConstant(PythonConstantType.TextValue, keywordPattern.Name)
                    ),
                    keywordPattern.Span
                );
            }

            Emit(PythonOpCode.BuildTuple, pattern.Keyword.Count, span);
            Emit(PythonOpCode.MatchClass, pattern.Positional.Count, span);
            var noMatch = Emit(PythonOpCode.JumpIfFalse, 0, span);

            // Stack on success: subject, extractedTuple.
            var elements = new List<PythonPattern>();
            elements.AddRange(pattern.Positional);
            elements.AddRange(pattern.Keyword.Select(keywordPattern => keywordPattern.Pattern));
            if (elements.Count == 0)
            {
                Emit(PythonOpCode.PopTop, 0, span);
            }
            else
            {
                Emit(PythonOpCode.UnpackSequence, elements.Count, span);
            }

            CompileExtractedElementTests(elements, span, [noMatch], trailingValues: 1);
        }

        /// <summary>
        /// Emits sequential sub-pattern tests over extracted values (first value on the
        /// stack top), with per-index failure cleanup that pops the remaining values plus
        /// <paramref name="trailingValues"/> stack slots (e.g. a retained subject copy),
        /// then pushes the boolean result. The <paramref name="preFailJumps"/> land on a
        /// cleanup that pops one slot (the retained subject) before pushing False.
        /// </summary>
        private void CompileExtractedElementTests(
            List<PythonPattern> elements,
            TextSpan span,
            List<int> preFailJumps,
            int trailingValues
        )
        {
            var failJumps = new List<int>();
            foreach (var element in elements)
            {
                CompilePatternTest(element);
                failJumps.Add(Emit(PythonOpCode.JumpIfFalse, 0, element.Span));
            }

            for (var pop = 0; pop < trailingValues; pop++)
            {
                Emit(PythonOpCode.PopTop, 0, span);
            }

            EmitLoadTrue(span);
            var done = Emit(PythonOpCode.Jump, 0, span);
            for (var index = 0; index < failJumps.Count; index++)
            {
                PatchJump(failJumps[index], _instructions.Count);
                var remaining = elements.Count - 1 - index + trailingValues;
                for (var pop = 0; pop < remaining; pop++)
                {
                    Emit(PythonOpCode.PopTop, 0, span);
                }

                EmitLoadFalse(span);
                failJumps[index] = Emit(PythonOpCode.Jump, 0, span);
            }

            foreach (var preFail in preFailJumps)
            {
                PatchJump(preFail, _instructions.Count);
            }

            Emit(PythonOpCode.PopTop, 0, span);
            EmitLoadFalse(span);
            PatchJump(done, _instructions.Count);
            foreach (var jump in failJumps)
            {
                PatchJump(jump, _instructions.Count);
            }
        }

        private void EmitLoadTrue(TextSpan span) =>
            Emit(
                PythonOpCode.LoadConstant,
                AddConstant(new PythonConstant(PythonConstantType.TruthValue, true)),
                span
            );

        private void EmitLoadFalse(TextSpan span) =>
            Emit(
                PythonOpCode.LoadConstant,
                AddConstant(new PythonConstant(PythonConstantType.TruthValue, false)),
                span
            );

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
            foreach (var decorator in function.Decorators)
            {
                CompileExpression(decorator);
            }

            var childScope = _scope.Children.Single(scope =>
                ReferenceEquals(scope.Definition, function)
            );
            var childCompiler = new Compiler(
                function.Name.Name,
                childScope,
                _diagnostics,
                _enableReturnLocal,
                _enableCallLocal,
                _scope.Kind == PythonScopeKind.Class ? _codeName : null
            );
            var childCode = childCompiler.CompileCode(
                function.Body,
                function.Span.End,
                function.Parameters,
                function.IsGenerator,
                function.IsCoroutine
            );
            var constantIndex = AddConstant(
                new PythonConstant(PythonConstantType.CodeObject, childCode)
            );
            EmitFunctionCreation(function.Parameters, constantIndex, function.Span);

            for (var index = 0; index < function.Decorators.Count; index++)
            {
                Emit(PythonOpCode.Call, 1, function.Span);
            }

            EmitStoreName(function.Name);
        }

        private void CompileClassDefinition(PythonClassDefinitionStatement @class)
        {
            foreach (var decorator in @class.Decorators)
            {
                CompileExpression(decorator);
            }

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
            if (@class.Bases.Count == 0)
            {
                Emit(PythonOpCode.MakeClass, constantIndex, @class.Span);
            }
            else
            {
                foreach (var baseExpression in @class.Bases)
                {
                    CompileExpression(baseExpression);
                }

                Emit(PythonOpCode.BuildTuple, @class.Bases.Count, @class.Span);
                Emit(PythonOpCode.MakeClassWithBases, constantIndex, @class.Span);
            }

            for (var index = 0; index < @class.Decorators.Count; index++)
            {
                Emit(PythonOpCode.Call, 1, @class.Span);
            }

            EmitStoreName(@class.Name);
        }

        private void CompileReturnStatement(PythonReturnStatement statement)
        {
            if (_scope.Kind != PythonScopeKind.Function)
            {
                Report("DPY3103", "'return' outside function.", statement.Span);
                return;
            }

            if (ReportExceptStarControlFlow(statement.Span))
            {
                return;
            }

            if (statement.Value is null || IsNoneLiteral(statement.Value))
            {
                Emit(PythonOpCode.ReturnNone, 0, statement.Span);
                return;
            }

            if (_isAsyncGenerator)
            {
                Report("DPY3117", "'return' with value in async generator.", statement.Span);
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

        /// <summary>
        /// Drives an awaitable-iterator already on the stack to completion: re-yields
        /// every suspension to this frame's consumer and leaves the delegate's result.
        /// </summary>
        private void EmitAwaitLoop(TextSpan span)
        {
            Emit(
                PythonOpCode.LoadConstant,
                AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                span
            );
            var loopStart = _instructions.Count;
            Emit(PythonOpCode.YieldFromStep, 0, span);
            var finished = Emit(PythonOpCode.JumpIfFalse, 0, span);
            Emit(PythonOpCode.Yield, 0, span);
            Emit(PythonOpCode.Jump, loopStart, span);
            PatchJump(finished, _instructions.Count);
            Emit(PythonOpCode.RotateTwo, 0, span);
            Emit(PythonOpCode.PopTop, 0, span);
        }

        private void CompileWithStatement(PythonWithStatement statement, int itemIndex)
        {
            var item = statement.Items[itemIndex];
            CompileExpression(item.Context);
            if (statement.IsAsync)
            {
                Emit(PythonOpCode.AsyncWithSetup, 0, item.Context.Span);
                Emit(PythonOpCode.Call, 0, item.Context.Span);
                Emit(PythonOpCode.GetAwaitable, 0, item.Context.Span);
                EmitAwaitLoop(item.Context.Span);
            }
            else
            {
                Emit(PythonOpCode.CopyTop, 0, item.Context.Span);
                Emit(PythonOpCode.LoadAttribute, GetNameIndex("__exit__"), item.Context.Span);
                Emit(PythonOpCode.RotateTwo, 0, item.Context.Span);
                Emit(PythonOpCode.LoadAttribute, GetNameIndex("__enter__"), item.Context.Span);
                Emit(PythonOpCode.Call, 0, item.Context.Span);
            }

            if (item.Target is not null)
            {
                CompileAssignmentTarget(item.Target);
            }
            else
            {
                Emit(PythonOpCode.PopTop, 0, item.Span);
            }

            _protectionScopes.Add(
                statement.IsAsync
                    ? AsyncWithProtection.Instance
                    : (ProtectionScope)WithProtection.Instance
            );
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
            if (statement.IsAsync)
            {
                Emit(PythonOpCode.GetAwaitable, 0, statement.Span);
                EmitAwaitLoop(statement.Span);
            }

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

            if (statement.Handlers[0].IsStar)
            {
                CompileTryExceptStar(statement);
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

        private void CompileTryExceptStar(PythonTryStatement statement)
        {
            // PEP 654: every clause splits the remaining exception by its type, runs
            // with the matched subgroup, and the remainder plus any exceptions raised
            // by clause bodies re-raise (combined) after the chain. The chain threads
            // a single state value on the evaluation stack.
            _protectionScopes.Add(ExceptProtection.Instance);
            var setupExcept = Emit(PythonOpCode.SetupExcept, 0, statement.Span);
            CompileStatements(statement.Body);
            Emit(PythonOpCode.PopExceptionBlock, 0, statement.Span);
            _protectionScopes.RemoveAt(_protectionScopes.Count - 1);
            CompileStatements(statement.ElseBody);
            var normalExit = Emit(PythonOpCode.Jump, 0, statement.Span);
            PatchJump(setupExcept, _instructions.Count);

            Emit(PythonOpCode.ExceptStarInit, 0, statement.Span);
            foreach (var handler in statement.Handlers)
            {
                if (handler.Type is null)
                {
                    continue;
                }

                CompileExpression(handler.Type);
                Emit(PythonOpCode.ExceptStarMatch, 0, handler.Type.Span);
                var skipClause = Emit(PythonOpCode.JumpIfFalse, 0, handler.Type.Span);
                if (handler.Target is not null)
                {
                    EmitStoreName(handler.Target);
                }
                else
                {
                    Emit(PythonOpCode.PopTop, 0, handler.Span);
                }

                var clauseCatch = Emit(PythonOpCode.SetupExcept, 0, handler.Span);
                _exceptStarBodyDepth++;
                CompileStatements(handler.Body);
                _exceptStarBodyDepth--;
                Emit(PythonOpCode.PopExceptionBlock, 0, handler.Span);
                EmitExceptStarTargetCleanup(handler);
                var bodyDone = Emit(PythonOpCode.Jump, 0, handler.Span);
                PatchJump(clauseCatch, _instructions.Count);
                Emit(PythonOpCode.ExceptStarCollect, 0, handler.Span);
                EmitExceptStarTargetCleanup(handler);
                var collectDone = Emit(PythonOpCode.Jump, 0, handler.Span);
                PatchJump(skipClause, _instructions.Count);
                Emit(PythonOpCode.PopTop, 0, handler.Span);
                PatchJump(bodyDone, _instructions.Count);
                PatchJump(collectDone, _instructions.Count);
            }

            Emit(PythonOpCode.ExceptStarFinish, 0, statement.Span);
            PatchJump(normalExit, _instructions.Count);
        }

        private void EmitExceptStarTargetCleanup(PythonExceptHandler handler)
        {
            if (handler.Target is null)
            {
                return;
            }

            Emit(
                PythonOpCode.LoadConstant,
                AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                handler.Target.Span
            );
            EmitStoreName(handler.Target);
            EmitDeleteName(handler.Target);
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
            if (statement.IsAsync)
            {
                CompileAsyncForStatement(statement);
                return;
            }

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

        private void CompileAsyncForStatement(PythonForStatement statement)
        {
            // The async iterator stays on the stack across the loop (like ForIter's
            // iterator). Each step protects only the __anext__ call and its await
            // loop; EndAsyncFor consumes StopAsyncIteration and exits, or re-raises.
            CompileExpression(statement.Iterable);
            Emit(PythonOpCode.GetAsyncIterator, 0, statement.Iterable.Span);
            var loopStart = _instructions.Count;
            var setupExcept = Emit(PythonOpCode.SetupExcept, 0, statement.Iterable.Span);
            Emit(PythonOpCode.CopyTop, 0, statement.Iterable.Span);
            Emit(PythonOpCode.GetAsyncNext, 0, statement.Iterable.Span);
            EmitAwaitLoop(statement.Iterable.Span);
            Emit(PythonOpCode.PopExceptionBlock, 0, statement.Iterable.Span);
            CompileAssignmentTarget(statement.Target);
            var loop = PushLoopScope(isForLoop: true, continueTarget: loopStart);
            CompileStatements(statement.Body);
            PopLoopScope();
            Emit(PythonOpCode.Jump, loopStart, statement.Span);
            PatchJump(setupExcept, _instructions.Count);
            var exitJump = Emit(PythonOpCode.EndAsyncFor, 0, statement.Span);
            PatchJump(exitJump, _instructions.Count);
            CompileStatements(statement.ElseBody);
            PatchBreakJumps(loop);
        }

        private bool ReportExceptStarControlFlow(TextSpan span)
        {
            if (_exceptStarBodyDepth == 0)
            {
                return false;
            }

            Report(
                "DPY3118",
                "'break', 'continue' and 'return' cannot appear in an except* block.",
                span
            );
            return true;
        }

        private void CompileBreakStatement(PythonBreakStatement statement)
        {
            if (ReportExceptStarControlFlow(statement.Span))
            {
                return;
            }

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
            if (ReportExceptStarControlFlow(statement.Span))
            {
                return;
            }

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
                    case WithProtection or AsyncWithProtection:
                        for (var argument = 0; argument < 3; argument++)
                        {
                            Emit(
                                PythonOpCode.LoadConstant,
                                AddConstant(new PythonConstant(PythonConstantType.NoneValue, null)),
                                span
                            );
                        }

                        Emit(PythonOpCode.Call, 3, span);
                        if (_protectionScopes[index] is AsyncWithProtection)
                        {
                            Emit(PythonOpCode.GetAwaitable, 0, span);
                            EmitAwaitLoop(span);
                        }

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

    private sealed record AsyncWithProtection : ProtectionScope
    {
        internal static readonly AsyncWithProtection Instance = new();
    }

    private sealed record HandlerCleanupProtection(PythonNameExpression? Target) : ProtectionScope;
}
