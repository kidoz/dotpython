using System.Numerics;
using System.Runtime.CompilerServices;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonVirtualMachine
{
    [ThreadStatic]
    private static HashSet<PythonValuePair>? _activeEqualityPairs;

    private static readonly PythonCell[] NoCells = [];
    private readonly Dictionary<string, PythonValue> _builtins;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _enableReturnLocalContinuation;
    private readonly Stack<PythonValue> _evaluationStack = [];
    private readonly PythonGlobalNamespace _globals;
    private readonly long _instructionLimit;
    private readonly TextWriter _output;
    private PythonFrame[] _frames = new PythonFrame[4];
    private int _frameCount;
    private long _instructionsExecuted;
    private PythonValue?[] _locals = [];
    private int _localsCount;
    private PythonValue _result = PythonNoneValue.Instance;

    private ref PythonFrame CurrentFrame => ref _frames[_frameCount - 1];

    internal PythonVirtualMachine(
        PythonGlobalNamespace globals,
        TextWriter output,
        long instructionLimit,
        bool enableReturnLocalContinuation,
        CancellationToken cancellationToken
    )
    {
        _globals = globals;
        _output = output;
        _instructionLimit = instructionLimit;
        _enableReturnLocalContinuation = enableReturnLocalContinuation;
        _cancellationToken = cancellationToken;
        _builtins = new Dictionary<string, PythonValue>(StringComparer.Ordinal)
        {
            ["print"] = new PythonBuiltinFunctionValue("print", Print),
        };
    }

    internal PythonValue Execute(PreparedPythonCode code)
    {
        ArgumentNullException.ThrowIfNull(code);

        PushFrame(code, _globals, 0, 0, CreateCells(code, [], new TextSpan(0, 0)));
        return Run();
    }

    internal PythonValue Invoke(string functionName, IReadOnlyList<PythonValue> arguments)
    {
        PushNamedFunctionFrame(functionName, arguments);
        return Run();
    }

    internal PythonValue InvokeProfiled(
        string functionName,
        IReadOnlyList<PythonValue> arguments,
        PythonExecutionProfile profile
    )
    {
        ArgumentNullException.ThrowIfNull(profile);
        PushNamedFunctionFrame(functionName, arguments);
        return RunProfiled(profile);
    }

    private PythonValue Run()
    {
        _result = PythonNoneValue.Instance;
        try
        {
            while (_frameCount != 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ref var frame = ref CurrentFrame;
                if (frame.InstructionPointer >= frame.Code.Definition.Instructions.Count)
                {
                    ReturnFromFrame(PythonNoneValue.Instance);
                    continue;
                }

                var instructionIndex = frame.InstructionPointer;
                var instruction = frame.Code.Definition.Instructions[instructionIndex];
                frame.InstructionPointer++;
                if (_instructionsExecuted++ >= _instructionLimit)
                {
                    throw Fault(
                        "DPY4001",
                        "The managed instruction limit was exceeded.",
                        instruction.Span
                    );
                }

                ExecuteInstruction(ref frame, instruction, instructionIndex);
            }

            return _result;
        }
        finally
        {
            Array.Clear(_frames, 0, _frameCount);
            _frameCount = 0;
            _evaluationStack.Clear();
            Array.Clear(_locals, 0, _localsCount);
            _localsCount = 0;
        }
    }

    private PythonValue RunProfiled(PythonExecutionProfile profile)
    {
        _result = PythonNoneValue.Instance;
        try
        {
            while (_frameCount != 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ref var frame = ref CurrentFrame;
                if (frame.InstructionPointer >= frame.Code.Definition.Instructions.Count)
                {
                    ReturnFromFrame(PythonNoneValue.Instance);
                    continue;
                }

                var instructionIndex = frame.InstructionPointer;
                var instruction = frame.Code.Definition.Instructions[instructionIndex];
                frame.InstructionPointer++;
                if (_instructionsExecuted++ >= _instructionLimit)
                {
                    throw Fault(
                        "DPY4001",
                        "The managed instruction limit was exceeded.",
                        instruction.Span
                    );
                }

                profile.Record(_frameCount - 1, frame.Code, instructionIndex, instruction.OpCode);
                ExecuteInstruction(ref frame, instruction, instructionIndex);
            }

            return _result;
        }
        finally
        {
            Array.Clear(_frames, 0, _frameCount);
            _frameCount = 0;
            _evaluationStack.Clear();
            Array.Clear(_locals, 0, _localsCount);
            _localsCount = 0;
        }
    }

    private void PushNamedFunctionFrame(string functionName, IReadOnlyList<PythonValue> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!_globals.TryGetValue(functionName, out var value))
        {
            throw Fault("DPY4002", $"Name '{functionName}' is not defined.", new TextSpan(0, 0));
        }

        if (value is not PythonFunctionValue function)
        {
            throw Fault("DPY4003", $"Export '{functionName}' is not callable.", new TextSpan(0, 0));
        }

        PushFunctionFrame(function, arguments, new TextSpan(0, 0));
    }

    private void ExecuteInstruction(
        ref PythonFrame frame,
        PythonInstruction instruction,
        int instructionIndex
    )
    {
        switch (instruction.OpCode)
        {
            case PythonOpCode.LoadConstant:
                _evaluationStack.Push(frame.Code.GetConstant(instruction.Operand));
                break;
            case PythonOpCode.LoadName:
                _evaluationStack.Push(
                    LoadName(
                        frame.Code,
                        instructionIndex,
                        frame.Code.Definition.Names[instruction.Operand],
                        instruction.Span
                    )
                );
                break;
            case PythonOpCode.StoreName:
                frame.Globals.SetValue(
                    frame.Code.Definition.Names[instruction.Operand],
                    Pop(instruction.Span)
                );
                break;
            case PythonOpCode.LoadLocal:
                _evaluationStack.Push(LoadLocal(instruction.Operand, instruction.Span));
                break;
            case PythonOpCode.StoreLocal:
                StoreLocal(instruction.Operand, Pop(instruction.Span), instruction.Span);
                break;
            case PythonOpCode.LoadCell:
                _evaluationStack.Push(LoadCell(instruction.Operand, instruction.Span));
                break;
            case PythonOpCode.StoreCell:
                StoreCell(instruction.Operand, Pop(instruction.Span), instruction.Span);
                break;
            case PythonOpCode.PopTop:
                Pop(instruction.Span);
                break;
            case PythonOpCode.CopyTop:
                _evaluationStack.Push(Peek(instruction.Span));
                break;
            case PythonOpCode.RotateTwo:
                RotateTwo(instruction.Span);
                break;
            case PythonOpCode.RotateThree:
                RotateThree(instruction.Span);
                break;
            case PythonOpCode.UnaryPositive:
            case PythonOpCode.UnaryNegative:
            case PythonOpCode.UnaryInvert:
            case PythonOpCode.UnaryNot:
                _evaluationStack.Push(
                    ApplyUnary(instruction.OpCode, Pop(instruction.Span), instruction.Span)
                );
                break;
            case PythonOpCode.BinaryAdd:
                ApplyBinaryAdd(frame.Code, instructionIndex, instruction.Span);
                break;
            case PythonOpCode.BinarySubtract:
            case PythonOpCode.BinaryMultiply:
            case PythonOpCode.BinaryTrueDivide:
            case PythonOpCode.BinaryFloorDivide:
            case PythonOpCode.BinaryModulo:
            case PythonOpCode.BinaryPower:
                ApplyBinary(instruction);
                break;
            case PythonOpCode.CompareEqual:
            case PythonOpCode.CompareNotEqual:
                ApplyComparison(instruction);
                break;
            case PythonOpCode.CompareLessThan:
            case PythonOpCode.CompareLessThanOrEqual:
            case PythonOpCode.CompareGreaterThan:
            case PythonOpCode.CompareGreaterThanOrEqual:
                ApplyOrderedComparison(frame.Code, instructionIndex, instruction);
                break;
            case PythonOpCode.Jump:
                frame.InstructionPointer = GetJumpTarget(
                    instruction,
                    frame.Code.Definition.Instructions.Count
                );
                break;
            case PythonOpCode.JumpIfFalse:
                if (!IsTruthy(Pop(instruction.Span)))
                {
                    frame.InstructionPointer = GetJumpTarget(
                        instruction,
                        frame.Code.Definition.Instructions.Count
                    );
                }

                break;
            case PythonOpCode.JumpIfFalseOrPop:
                if (!IsTruthy(Peek(instruction.Span)))
                {
                    frame.InstructionPointer = GetJumpTarget(
                        instruction,
                        frame.Code.Definition.Instructions.Count
                    );
                }
                else
                {
                    Pop(instruction.Span);
                }

                break;
            case PythonOpCode.JumpIfTrueOrPop:
                if (IsTruthy(Peek(instruction.Span)))
                {
                    frame.InstructionPointer = GetJumpTarget(
                        instruction,
                        frame.Code.Definition.Instructions.Count
                    );
                }
                else
                {
                    Pop(instruction.Span);
                }

                break;
            case PythonOpCode.MakeFunction:
                MakeFunction(instruction);
                break;
            case PythonOpCode.Call:
                ApplyCall(frame.Code, instructionIndex, instruction);
                break;
            case PythonOpCode.CallLocal:
                ApplyLocalCall(
                    frame.Code,
                    instructionIndex,
                    LoadLocal(instruction.Operand, instruction.Span),
                    instruction.Span
                );
                break;
            case PythonOpCode.BuildList:
                BuildCollection(instruction.Operand, buildTuple: false, instruction.Span);
                break;
            case PythonOpCode.BuildTuple:
                BuildCollection(instruction.Operand, buildTuple: true, instruction.Span);
                break;
            case PythonOpCode.BuildDictionary:
                BuildDictionary(instruction.Operand, instruction.Span);
                break;
            case PythonOpCode.LoadSubscript:
                LoadSubscript(instruction.Span);
                break;
            case PythonOpCode.StoreSubscript:
                StoreSubscript(instruction.Span);
                break;
            case PythonOpCode.GetIterator:
                GetIterator(instruction.Span);
                break;
            case PythonOpCode.ForIter:
                ForIter(ref frame, instruction);
                break;
            case PythonOpCode.ReturnValue:
                ReturnFromFrame(Pop(instruction.Span));
                break;
            case PythonOpCode.ReturnLocal:
                ReturnFromFrame(LoadLocal(instruction.Operand, instruction.Span));
                break;
            case PythonOpCode.ReturnNone:
                ReturnFromFrame(PythonNoneValue.Instance);
                break;
            default:
                throw Fault("DPY4007", "Unknown DotPython instruction.", instruction.Span);
        }
    }

    private void BuildCollection(int elementCount, bool buildTuple, TextSpan span)
    {
        if (
            elementCount < 0
            || elementCount > _evaluationStack.Count - CurrentFrame.EvaluationStackBase
        )
        {
            throw Fault("DPY4007", "Invalid collection element count.", span);
        }

        var elements = new PythonValue[elementCount];
        for (var index = elementCount - 1; index >= 0; index--)
        {
            elements[index] = Pop(span);
        }

        _evaluationStack.Push(
            buildTuple
                ? new PythonTupleValue(elements)
                : new PythonListValue(new List<PythonValue>(elements))
        );
    }

    private void BuildDictionary(int itemCount, TextSpan span)
    {
        if (
            itemCount < 0
            || itemCount > (_evaluationStack.Count - CurrentFrame.EvaluationStackBase) / 2
        )
        {
            throw Fault("DPY4007", "Invalid dictionary item count.", span);
        }

        var keys = new PythonValue[itemCount];
        var values = new PythonValue[itemCount];
        for (var index = itemCount - 1; index >= 0; index--)
        {
            values[index] = Pop(span);
            keys[index] = Pop(span);
        }

        var dictionary = new PythonDictionaryValue([]);
        for (var index = 0; index < itemCount; index++)
        {
            SetDictionaryItem(dictionary, keys[index], values[index], span);
        }

        _evaluationStack.Push(dictionary);
    }

    private void LoadSubscript(TextSpan span)
    {
        var index = Pop(span);
        var target = Pop(span);
        _evaluationStack.Push(GetSubscript(target, index, span));
    }

    private void StoreSubscript(TextSpan span)
    {
        var index = Pop(span);
        var target = Pop(span);
        var value = Pop(span);

        switch (target)
        {
            case PythonListValue list:
                list.Elements[GetSequenceIndex(index, list.Elements.Count, span)] = value;
                break;
            case PythonDictionaryValue dictionary:
                SetDictionaryItem(dictionary, index, value, span);
                break;
            default:
                throw Fault("DPY4011", "This value does not support item assignment.", span);
        }
    }

    private void GetIterator(TextSpan span)
    {
        var iterable = Pop(span);
        if (iterable is PythonIteratorValue iterator)
        {
            _evaluationStack.Push(iterator);
            return;
        }

        if (
            iterable
            is not (
                PythonListValue
                or PythonTupleValue
                or PythonDictionaryValue
                or PythonTextValue
                or PythonByteSequenceValue
            )
        )
        {
            throw Fault("DPY4015", "This value is not iterable.", span);
        }

        var dictionaryVersion = iterable is PythonDictionaryValue dictionary
            ? dictionary.SizeVersion
            : -1;
        _evaluationStack.Push(new PythonIteratorValue(iterable, dictionaryVersion));
    }

    private void ForIter(ref PythonFrame frame, PythonInstruction instruction)
    {
        if (Peek(instruction.Span) is not PythonIteratorValue iterator)
        {
            throw Fault("DPY4007", "The for-loop iterator is invalid.", instruction.Span);
        }

        if (TryGetNext(iterator, instruction.Span, out var value))
        {
            _evaluationStack.Push(value);
            return;
        }

        Pop(instruction.Span);
        frame.InstructionPointer = GetJumpTarget(
            instruction,
            frame.Code.Definition.Instructions.Count
        );
    }

    private static bool TryGetNext(
        PythonIteratorValue iterator,
        TextSpan span,
        out PythonValue value
    )
    {
        switch (iterator.Iterable)
        {
            case PythonListValue list when iterator.Index < list.Elements.Count:
                value = list.Elements[iterator.Index++];
                return true;
            case PythonTupleValue tuple when iterator.Index < tuple.Elements.Length:
                value = tuple.Elements[iterator.Index++];
                return true;
            case PythonDictionaryValue dictionary:
                if (dictionary.SizeVersion != iterator.ExpectedDictionarySizeVersion)
                {
                    throw Fault("DPY4016", "Dictionary size changed during iteration.", span);
                }

                if (iterator.Index < dictionary.Items.Count)
                {
                    value = dictionary.Items[iterator.Index++].Key;
                    return true;
                }

                break;
            case PythonTextValue text:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                if (iterator.Index < runes.Length)
                {
                    value = new PythonTextValue(runes[iterator.Index++].ToString());
                    return true;
                }

                break;
            }
            case PythonByteSequenceValue bytes when iterator.Index < bytes.Value.Length:
                value = PythonWholeNumberValue.Create(bytes.Value[iterator.Index++]);
                return true;
        }

        value = PythonNoneValue.Instance;
        return false;
    }

    private static PythonValue GetSubscript(PythonValue target, PythonValue index, TextSpan span)
    {
        switch (target)
        {
            case PythonListValue list:
                return list.Elements[GetSequenceIndex(index, list.Elements.Count, span)];
            case PythonTupleValue tuple:
                return tuple.Elements[GetSequenceIndex(index, tuple.Elements.Length, span)];
            case PythonTextValue text:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                return new PythonTextValue(
                    runes[GetSequenceIndex(index, runes.Length, span)].ToString()
                );
            }
            case PythonByteSequenceValue bytes:
                return PythonWholeNumberValue.Create(
                    bytes.Value[GetSequenceIndex(index, bytes.Value.Length, span)]
                );
            case PythonDictionaryValue dictionary:
                if (TryFindDictionaryItem(dictionary, index, out var item))
                {
                    return item.Value;
                }

                throw Fault("DPY4013", "The dictionary key was not found.", span);
            default:
                throw Fault("DPY4011", "This value is not subscriptable.", span);
        }
    }

    private static int GetSequenceIndex(PythonValue index, int count, TextSpan span)
    {
        index = PromoteTruthValue(index);
        if (index is not PythonWholeNumberValue wholeNumber)
        {
            throw Fault("DPY4011", "Sequence indices must be integers.", span);
        }

        var value = wholeNumber.Value;
        if (value < 0)
        {
            value += count;
        }

        if (value < 0 || value >= count)
        {
            throw Fault("DPY4012", "The sequence index is out of range.", span);
        }

        return (int)value;
    }

    private static void SetDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        PythonValue value,
        TextSpan span
    )
    {
        if (!IsHashable(key))
        {
            throw Fault("DPY4014", "The dictionary key is not hashable.", span);
        }

        if (TryFindDictionaryItem(dictionary, key, out var item))
        {
            item.Value = value;
            return;
        }

        dictionary.Items.Add(new PythonDictionaryItemValue(key, value));
        dictionary.SizeVersion++;
    }

    private static bool TryFindDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        out PythonDictionaryItemValue item
    )
    {
        foreach (var candidate in dictionary.Items)
        {
            if (ReferenceEquals(candidate.Key, key) || AreEqual(candidate.Key, key))
            {
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private static bool IsHashable(PythonValue value) =>
        value switch
        {
            PythonListValue or PythonDictionaryValue => false,
            PythonTupleValue tuple => tuple.Elements.All(IsHashable),
            _ => true,
        };

    private PythonValue LoadName(
        PreparedPythonCode code,
        int instructionIndex,
        string name,
        TextSpan span
    )
    {
        var globals = CurrentFrame.Globals;
        if (code.TryGetCachedName(instructionIndex, globals, out var value))
        {
            return value;
        }

        if (globals.TryGetSlot(name, out var slot))
        {
            code.RecordGlobalLoad(instructionIndex, globals, slot);
            return slot.Value;
        }

        if (_builtins.TryGetValue(name, out value))
        {
            code.RecordBuiltinLoad(instructionIndex, globals, value);
            return value;
        }

        throw Fault("DPY4002", $"Name '{name}' is not defined.", span);
    }

    private PythonValue LoadLocal(int index, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.LocalsCount)
        {
            throw Fault("DPY4007", "The DotPython local index is invalid.", span);
        }

        var value = _locals[frame.LocalsBase + index];
        if (value is not null)
        {
            return value;
        }

        var name = frame.Code.Definition.VariableNames[index];
        throw Fault("DPY4008", $"Local variable '{name}' was referenced before assignment.", span);
    }

    private void StoreLocal(int index, PythonValue value, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.LocalsCount)
        {
            throw Fault("DPY4007", "The DotPython local index is invalid.", span);
        }

        _locals[frame.LocalsBase + index] = value;
    }

    private PythonValue LoadCell(int index, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.Cells.Length)
        {
            throw Fault("DPY4007", "The DotPython closure-cell index is invalid.", span);
        }

        var value = frame.Cells[index].Value;
        if (value is not null)
        {
            return value;
        }

        var definition = frame.Code.Definition;
        if (index < definition.CellVariableNames.Count)
        {
            throw Fault(
                "DPY4008",
                $"Local variable '{definition.CellVariableNames[index]}' was referenced before assignment.",
                span
            );
        }

        var freeVariableIndex = index - definition.CellVariableNames.Count;
        throw Fault(
            "DPY4010",
            $"Free variable '{definition.FreeVariableNames[freeVariableIndex]}' was referenced before assignment in an enclosing scope.",
            span
        );
    }

    private void StoreCell(int index, PythonValue value, TextSpan span)
    {
        ref var frame = ref CurrentFrame;
        if ((uint)index >= (uint)frame.Cells.Length)
        {
            throw Fault("DPY4007", "The DotPython closure-cell index is invalid.", span);
        }

        frame.Cells[index].Value = value;
    }

    private void ApplyBinary(PythonInstruction instruction)
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        _evaluationStack.Push(ApplyBinary(instruction.OpCode, left, right, instruction.Span));
    }

    private void ApplyBinaryAdd(PreparedPythonCode code, int instructionIndex, TextSpan span)
    {
        var right = Pop(span);
        var left = Pop(span);
        var cacheState = code.GetBinaryAddCacheState(instructionIndex);
        if (
            cacheState == AdaptiveNumericCacheState.WholeNumber
            && left is PythonWholeNumberValue leftWholeNumber
            && right is PythonWholeNumberValue rightWholeNumber
        )
        {
            _evaluationStack.Push(
                PythonWholeNumberValue.Create(leftWholeNumber.Value + rightWholeNumber.Value)
            );
            return;
        }

        if (
            cacheState == AdaptiveNumericCacheState.FloatingPoint
            && left is PythonFloatingPointValue leftFloatingPoint
            && right is PythonFloatingPointValue rightFloatingPoint
        )
        {
            _evaluationStack.Push(
                new PythonFloatingPointValue(leftFloatingPoint.Value + rightFloatingPoint.Value)
            );
            return;
        }

        var operandKind = GetAdaptiveNumericOperandKind(left, right);
        if (
            cacheState
            is AdaptiveNumericCacheState.WholeNumber
                or AdaptiveNumericCacheState.FloatingPoint
        )
        {
            code.RecordBinaryAddObservation(instructionIndex, operandKind);
        }

        var result = ApplyBinary(PythonOpCode.BinaryAdd, left, right, span);
        if (cacheState == AdaptiveNumericCacheState.Adaptive)
        {
            code.RecordBinaryAddObservation(instructionIndex, operandKind);
        }

        _evaluationStack.Push(result);
    }

    private void ApplyOrderedComparison(
        PreparedPythonCode code,
        int instructionIndex,
        PythonInstruction instruction
    )
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        var cacheState = code.GetOrderedComparisonCacheState(instructionIndex);
        if (
            cacheState == AdaptiveNumericCacheState.WholeNumber
            && left is PythonWholeNumberValue leftWholeNumber
            && right is PythonWholeNumberValue rightWholeNumber
        )
        {
            _evaluationStack.Push(
                PythonTruthValue.FromBoolean(
                    ApplyWholeNumberComparison(
                        instruction.OpCode,
                        leftWholeNumber.Value,
                        rightWholeNumber.Value
                    )
                )
            );
            return;
        }

        if (
            cacheState == AdaptiveNumericCacheState.FloatingPoint
            && left is PythonFloatingPointValue leftFloatingPoint
            && right is PythonFloatingPointValue rightFloatingPoint
        )
        {
            _evaluationStack.Push(
                PythonTruthValue.FromBoolean(
                    ApplyFloatingPointComparison(
                        instruction.OpCode,
                        leftFloatingPoint.Value,
                        rightFloatingPoint.Value
                    )
                )
            );
            return;
        }

        var operandKind = GetAdaptiveNumericOperandKind(left, right);
        if (
            cacheState
            is AdaptiveNumericCacheState.WholeNumber
                or AdaptiveNumericCacheState.FloatingPoint
        )
        {
            code.RecordOrderedComparisonObservation(instructionIndex, operandKind);
        }

        var result = ApplyComparison(instruction.OpCode, left, right, instruction.Span);
        if (cacheState == AdaptiveNumericCacheState.Adaptive)
        {
            code.RecordOrderedComparisonObservation(instructionIndex, operandKind);
        }

        _evaluationStack.Push(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyWholeNumberComparison(
        PythonOpCode opCode,
        BigInteger left,
        BigInteger right
    ) =>
        opCode switch
        {
            PythonOpCode.CompareLessThan => left < right,
            PythonOpCode.CompareLessThanOrEqual => left <= right,
            PythonOpCode.CompareGreaterThan => left > right,
            PythonOpCode.CompareGreaterThanOrEqual => left >= right,
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyFloatingPointComparison(
        PythonOpCode opCode,
        double left,
        double right
    ) =>
        opCode switch
        {
            PythonOpCode.CompareLessThan => left < right,
            PythonOpCode.CompareLessThanOrEqual => left <= right,
            PythonOpCode.CompareGreaterThan => left > right,
            PythonOpCode.CompareGreaterThanOrEqual => left >= right,
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };

    private static AdaptiveNumericOperandKind GetAdaptiveNumericOperandKind(
        PythonValue left,
        PythonValue right
    ) =>
        (left, right) switch
        {
            (PythonWholeNumberValue, PythonWholeNumberValue) =>
                AdaptiveNumericOperandKind.WholeNumber,
            (PythonFloatingPointValue, PythonFloatingPointValue) =>
                AdaptiveNumericOperandKind.FloatingPoint,
            _ => AdaptiveNumericOperandKind.Other,
        };

    private void ApplyComparison(PythonInstruction instruction)
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        _evaluationStack.Push(ApplyComparison(instruction.OpCode, left, right, instruction.Span));
    }

    private void ApplyCall(
        PreparedPythonCode code,
        int instructionIndex,
        PythonInstruction instruction
    )
    {
        var target = Peek(instruction.Operand, instruction.Span);
        if (
            code.TryGetCachedManagedCall(
                instructionIndex,
                target,
                out var cachedFunction,
                out var useEmptyFrame
            )
        )
        {
            if (useEmptyFrame)
            {
                PushEmptyFunctionFrame(cachedFunction, instruction.Span);
            }
            else
            {
                PushFunctionFrameUnchecked(cachedFunction, instruction.Operand, instruction.Span);
            }

            return;
        }

        if (target is PythonBuiltinFunctionValue builtin)
        {
            var arguments = PopArguments(instruction.Operand, instruction.Span);
            Pop(instruction.Span);
            _evaluationStack.Push(builtin.Invoke(arguments));
            code.RecordBuiltinCall(instructionIndex);
            return;
        }

        if (target is not PythonFunctionValue function)
        {
            throw Fault("DPY4003", "The selected value is not callable.", instruction.Span);
        }

        ValidateArgumentCount(function, instruction.Operand, instruction.Span);
        code.RecordManagedCall(instructionIndex, function);
        PushFunctionFrameUnchecked(function, instruction.Operand, instruction.Span);
    }

    private void ApplyLocalCall(
        PreparedPythonCode code,
        int instructionIndex,
        PythonValue target,
        TextSpan span
    )
    {
        if (
            code.TryGetCachedManagedCall(
                instructionIndex,
                target,
                out var cachedFunction,
                out var useEmptyFrame
            )
        )
        {
            if (useEmptyFrame)
            {
                PushEmptyFunctionFrame(cachedFunction, span, popTarget: false);
            }
            else
            {
                PushFunctionFrameUnchecked(cachedFunction, 0, span, popTarget: false);
            }

            return;
        }

        if (target is PythonBuiltinFunctionValue builtin)
        {
            _evaluationStack.Push(builtin.Invoke(Array.Empty<PythonValue>()));
            code.RecordBuiltinCall(instructionIndex);
            return;
        }

        if (target is not PythonFunctionValue function)
        {
            throw Fault("DPY4003", "The selected value is not callable.", span);
        }

        ValidateArgumentCount(function, 0, span);
        code.RecordManagedCall(instructionIndex, function);
        PushFunctionFrameUnchecked(function, 0, span, popTarget: false);
    }

    private void PushEmptyFunctionFrame(
        PythonFunctionValue function,
        TextSpan span,
        bool popTarget = true
    )
    {
        var hasReturnLocalContinuation = CaptureReturnLocalContinuation();
        if (popTarget)
        {
            Pop(span);
        }

        PushFrame(
            function.Code,
            function.Globals,
            _localsCount,
            0,
            NoCells,
            hasReturnLocalContinuation
        );
    }

    private PythonValue[] PopArguments(int argumentCount, TextSpan span)
    {
        var arguments = new PythonValue[argumentCount];
        for (var index = arguments.Length - 1; index >= 0; index--)
        {
            arguments[index] = Pop(span);
        }

        return arguments;
    }

    private void PushFunctionFrame(PythonFunctionValue function, int argumentCount, TextSpan span)
    {
        ValidateArgumentCount(function, argumentCount, span);
        PushFunctionFrameUnchecked(function, argumentCount, span);
    }

    private void PushFunctionFrameUnchecked(
        PythonFunctionValue function,
        int argumentCount,
        TextSpan span,
        bool popTarget = true
    )
    {
        var hasReturnLocalContinuation = CaptureReturnLocalContinuation();
        var localsBase = ReserveLocals(function.Code.Definition.VariableNames.Count);
        var cells = CreateCells(function.Code, function.Closure, span);
        for (var index = argumentCount - 1; index >= 0; index--)
        {
            StoreArgument(function.Code, cells, localsBase, index, Pop(span));
        }

        if (popTarget)
        {
            Pop(span);
        }

        PushFrame(
            function.Code,
            function.Globals,
            localsBase,
            function.Code.Definition.VariableNames.Count,
            cells,
            hasReturnLocalContinuation
        );
    }

    private void PushFunctionFrame(
        PythonFunctionValue function,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        ValidateArgumentCount(function, arguments.Count, span);

        var localsBase = ReserveLocals(function.Code.Definition.VariableNames.Count);
        var cells = CreateCells(function.Code, function.Closure, span);
        for (var index = 0; index < arguments.Count; index++)
        {
            StoreArgument(function.Code, cells, localsBase, index, arguments[index]);
        }

        PushFrame(
            function.Code,
            function.Globals,
            localsBase,
            function.Code.Definition.VariableNames.Count,
            cells
        );
    }

    private void StoreArgument(
        PreparedPythonCode code,
        PythonCell[] cells,
        int localsBase,
        int argumentIndex,
        PythonValue value
    )
    {
        var cellIndex = code.GetLocalCellIndex(argumentIndex);
        if (cellIndex >= 0)
        {
            cells[cellIndex].Value = value;
        }
        else
        {
            _locals[localsBase + argumentIndex] = value;
        }
    }

    private int ReserveLocals(int count)
    {
        var localsBase = _localsCount;
        var requiredCapacity = checked(localsBase + count);
        if (requiredCapacity > _locals.Length)
        {
            var doubledCapacity = _locals.Length == 0 ? 4 : checked(_locals.Length * 2);
            Array.Resize(ref _locals, Math.Max(requiredCapacity, doubledCapacity));
        }

        _localsCount = requiredCapacity;
        return localsBase;
    }

    private static void ValidateArgumentCount(
        PythonFunctionValue function,
        int argumentCount,
        TextSpan span
    )
    {
        if (argumentCount == function.Code.Definition.ArgumentCount)
        {
            return;
        }

        throw Fault(
            "DPY4009",
            $"Function '{function.Name}' expected {function.Code.Definition.ArgumentCount} positional "
                + $"argument(s), but received {argumentCount}.",
            span
        );
    }

    private void PushFrame(
        PreparedPythonCode code,
        PythonGlobalNamespace globals,
        int localsBase,
        int localsCount,
        PythonCell[] cells,
        bool hasReturnLocalContinuation = false
    )
    {
        if (_frameCount == _frames.Length)
        {
            Array.Resize(ref _frames, checked(_frameCount * 2));
        }

        _frames[_frameCount++] = new PythonFrame(
            code,
            globals,
            localsBase,
            localsCount,
            cells,
            _evaluationStack.Count,
            hasReturnLocalContinuation
        );
    }

    private bool CaptureReturnLocalContinuation()
    {
        if (!_enableReturnLocalContinuation)
        {
            return false;
        }

        ref var caller = ref CurrentFrame;
        var instructionIndex = caller.InstructionPointer;
        if ((uint)instructionIndex >= (uint)caller.Code.Definition.Instructions.Count)
        {
            return false;
        }

        var instruction = caller.Code.Definition.Instructions[instructionIndex];
        if (instruction.OpCode != PythonOpCode.StoreLocal)
        {
            return false;
        }

        caller.InstructionPointer++;
        return true;
    }

    private void MakeFunction(PythonInstruction instruction)
    {
        PreparedPythonCode code;
        try
        {
            code = CurrentFrame.Code.GetFunctionCode(instruction.Operand);
        }
        catch (InvalidOperationException)
        {
            throw Fault("DPY4007", "The function code object is invalid.", instruction.Span);
        }

        var closure = new PythonCell[code.Definition.FreeVariableNames.Count];
        for (var index = 0; index < closure.Length; index++)
        {
            var name = code.Definition.FreeVariableNames[index];
            var cellIndex = CurrentFrame.Code.GetClosureCellIndex(name);
            if ((uint)cellIndex >= (uint)CurrentFrame.Cells.Length)
            {
                throw Fault(
                    "DPY4007",
                    $"Closure variable '{name}' cannot be resolved in the enclosing frame.",
                    instruction.Span
                );
            }

            closure[index] = CurrentFrame.Cells[cellIndex];
        }

        _evaluationStack.Push(
            new PythonFunctionValue(code.Definition.Name, code, CurrentFrame.Globals, closure)
        );
    }

    private static PythonCell[] CreateCells(
        PreparedPythonCode code,
        PythonCell[] closure,
        TextSpan span
    )
    {
        var definition = code.Definition;
        if (closure.Length != definition.FreeVariableNames.Count)
        {
            throw Fault("DPY4007", "The function closure does not match its code object.", span);
        }

        if (definition.CellVariableNames.Count == 0 && closure.Length == 0)
        {
            return NoCells;
        }

        var cells = new PythonCell[
            definition.CellVariableNames.Count + definition.FreeVariableNames.Count
        ];
        for (var index = 0; index < definition.CellVariableNames.Count; index++)
        {
            cells[index] = new PythonCell();
        }

        for (var index = 0; index < closure.Length; index++)
        {
            cells[definition.CellVariableNames.Count + index] = closure[index];
        }

        return cells;
    }

    private void ReturnFromFrame(PythonValue value)
    {
        var evaluationStackBase = CurrentFrame.EvaluationStackBase;
        var localsBase = CurrentFrame.LocalsBase;
        var localsCount = CurrentFrame.LocalsCount;
        var hasReturnLocalContinuation = CurrentFrame.HasReturnLocalContinuation;
        while (_evaluationStack.Count > evaluationStackBase)
        {
            _evaluationStack.Pop();
        }

        Array.Clear(_locals, localsBase, localsCount);
        _localsCount = localsBase;
        _frames[--_frameCount] = default;
        if (_frameCount != 0)
        {
            if (hasReturnLocalContinuation)
            {
                ref var caller = ref CurrentFrame;
                var instructionIndex = caller.InstructionPointer - 1;
                var instruction = caller.Code.Definition.Instructions[instructionIndex];
                if (instruction.OpCode != PythonOpCode.StoreLocal)
                {
                    throw Fault(
                        "DPY4007",
                        "The managed return continuation is invalid.",
                        instruction.Span
                    );
                }

                _cancellationToken.ThrowIfCancellationRequested();
                if (_instructionsExecuted++ >= _instructionLimit)
                {
                    throw Fault(
                        "DPY4001",
                        "The managed instruction limit was exceeded.",
                        instruction.Span
                    );
                }

                StoreLocal(instruction.Operand, value, instruction.Span);
            }
            else
            {
                _evaluationStack.Push(value);
            }
        }
        else
        {
            _result = value;
        }
    }

    private PythonNoneValue Print(IReadOnlyList<PythonValue> arguments)
    {
        _output.WriteLine(string.Join(" ", arguments.Select(value => value.ToDisplayString())));
        return PythonNoneValue.Instance;
    }

    private PythonValue Pop(TextSpan span)
    {
        if (_evaluationStack.Count > CurrentFrame.EvaluationStackBase)
        {
            return _evaluationStack.Pop();
        }

        throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
    }

    private PythonValue Peek(TextSpan span)
    {
        if (_evaluationStack.Count > CurrentFrame.EvaluationStackBase)
        {
            return _evaluationStack.Peek();
        }

        throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
    }

    private PythonValue Peek(int depth, TextSpan span)
    {
        if (depth < 0 || _evaluationStack.Count - depth <= CurrentFrame.EvaluationStackBase)
        {
            throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
        }

        var index = 0;
        foreach (var value in _evaluationStack)
        {
            if (index++ == depth)
            {
                return value;
            }
        }

        throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
    }

    private void RotateTwo(TextSpan span)
    {
        var top = Pop(span);
        var second = Pop(span);
        _evaluationStack.Push(top);
        _evaluationStack.Push(second);
    }

    private void RotateThree(TextSpan span)
    {
        var top = Pop(span);
        var second = Pop(span);
        var third = Pop(span);
        _evaluationStack.Push(top);
        _evaluationStack.Push(third);
        _evaluationStack.Push(second);
    }

    private static PythonValue ApplyUnary(PythonOpCode opCode, PythonValue operand, TextSpan span)
    {
        if (opCode == PythonOpCode.UnaryNot)
        {
            return PythonTruthValue.FromBoolean(!IsTruthy(operand));
        }

        operand = PromoteTruthValue(operand);
        return (opCode, operand) switch
        {
            (PythonOpCode.UnaryPositive, PythonWholeNumberValue value) => value,
            (PythonOpCode.UnaryPositive, PythonFloatingPointValue value) => value,
            (PythonOpCode.UnaryPositive, PythonComplexValue value) => value,
            (PythonOpCode.UnaryNegative, PythonWholeNumberValue value) =>
                PythonWholeNumberValue.Create(-value.Value),
            (PythonOpCode.UnaryNegative, PythonFloatingPointValue value) =>
                new PythonFloatingPointValue(-value.Value),
            (PythonOpCode.UnaryNegative, PythonComplexValue value) => new PythonComplexValue(
                -value.Value
            ),
            (PythonOpCode.UnaryInvert, PythonWholeNumberValue value) =>
                PythonWholeNumberValue.Create(~value.Value),
            _ => throw Fault("DPY4005", "Unsupported operand for unary operator.", span),
        };
    }

    private static PythonTruthValue ApplyComparison(
        PythonOpCode opCode,
        PythonValue left,
        PythonValue right,
        TextSpan span
    )
    {
        if (opCode is PythonOpCode.CompareEqual or PythonOpCode.CompareNotEqual)
        {
            var equal = AreEqual(left, right);
            return PythonTruthValue.FromBoolean(
                opCode == PythonOpCode.CompareEqual ? equal : !equal
            );
        }

        var promotedLeft = PromoteTruthValue(left);
        var promotedRight = PromoteTruthValue(right);
        if (
            IsNumeric(promotedLeft)
            && IsNumeric(promotedRight)
            && (
                promotedLeft is PythonFloatingPointValue
                || promotedRight is PythonFloatingPointValue
            )
        )
        {
            if (promotedLeft is PythonComplexValue || promotedRight is PythonComplexValue)
            {
                throw Fault("DPY4005", "Complex numbers cannot be ordered.", span);
            }

            var leftFloatingPoint = ToDouble(promotedLeft);
            var rightFloatingPoint = ToDouble(promotedRight);
            return PythonTruthValue.FromBoolean(
                opCode switch
                {
                    PythonOpCode.CompareLessThan => leftFloatingPoint < rightFloatingPoint,
                    PythonOpCode.CompareLessThanOrEqual => leftFloatingPoint <= rightFloatingPoint,
                    PythonOpCode.CompareGreaterThan => leftFloatingPoint > rightFloatingPoint,
                    PythonOpCode.CompareGreaterThanOrEqual => leftFloatingPoint
                        >= rightFloatingPoint,
                    _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
                }
            );
        }

        var comparison = CompareOrdered(left, right, span);
        return PythonTruthValue.FromBoolean(
            opCode switch
            {
                PythonOpCode.CompareLessThan => comparison < 0,
                PythonOpCode.CompareLessThanOrEqual => comparison <= 0,
                PythonOpCode.CompareGreaterThan => comparison > 0,
                PythonOpCode.CompareGreaterThanOrEqual => comparison >= 0,
                _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
            }
        );
    }

    private static bool AreEqual(PythonValue left, PythonValue right)
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        var tracksRecursion =
            left is PythonListValue or PythonTupleValue or PythonDictionaryValue
            && right is PythonListValue or PythonTupleValue or PythonDictionaryValue;
        if (tracksRecursion)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            _activeEqualityPairs ??= new HashSet<PythonValuePair>(PythonValuePairComparer.Instance);
            var pair = new PythonValuePair(left, right);
            if (!_activeEqualityPairs.Add(pair))
            {
                return true;
            }

            try
            {
                return AreEqualCore(left, right);
            }
            finally
            {
                _activeEqualityPairs.Remove(pair);
                if (_activeEqualityPairs.Count == 0)
                {
                    _activeEqualityPairs = null;
                }
            }
        }

        return AreEqualCore(left, right);
    }

    private static bool AreEqualCore(PythonValue left, PythonValue right)
    {
        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                return ToComplex(left) == ToComplex(right);
            }

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                return ToDouble(left) == ToDouble(right);
            }

            return ((PythonWholeNumberValue)left).Value == ((PythonWholeNumberValue)right).Value;
        }

        return (left, right) switch
        {
            (PythonNoneValue, PythonNoneValue) => true,
            (PythonTextValue leftText, PythonTextValue rightText) => string.Equals(
                leftText.Value,
                rightText.Value,
                StringComparison.Ordinal
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceEqual(rightBytes.Value),
            (PythonListValue leftList, PythonListValue rightList) => AreSequencesEqual(
                leftList.Elements,
                rightList.Elements
            ),
            (PythonTupleValue leftTuple, PythonTupleValue rightTuple) => AreSequencesEqual(
                leftTuple.Elements,
                rightTuple.Elements
            ),
            (PythonDictionaryValue leftDictionary, PythonDictionaryValue rightDictionary) =>
                AreDictionariesEqual(leftDictionary, rightDictionary),
            (PythonBuiltinFunctionValue leftFunction, PythonBuiltinFunctionValue rightFunction) =>
                ReferenceEquals(leftFunction, rightFunction),
            (PythonFunctionValue leftFunction, PythonFunctionValue rightFunction) =>
                ReferenceEquals(leftFunction, rightFunction),
            _ => false,
        };
    }

    private static bool AreSequencesEqual(
        IReadOnlyList<PythonValue> left,
        IReadOnlyList<PythonValue> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!AreEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreDictionariesEqual(
        PythonDictionaryValue left,
        PythonDictionaryValue right
    )
    {
        if (left.Items.Count != right.Items.Count)
        {
            return false;
        }

        foreach (var item in left.Items)
        {
            if (
                !TryFindDictionaryItem(right, item.Key, out var rightItem)
                || !AreEqual(item.Value, rightItem.Value)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareOrdered(PythonValue left, PythonValue right, TextSpan span)
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                throw Fault("DPY4005", "Complex numbers cannot be ordered.", span);
            }

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                return ToDouble(left).CompareTo(ToDouble(right));
            }

            return ((PythonWholeNumberValue)left).Value.CompareTo(
                ((PythonWholeNumberValue)right).Value
            );
        }

        return (left, right) switch
        {
            (PythonTextValue leftText, PythonTextValue rightText) => string.CompareOrdinal(
                leftText.Value,
                rightText.Value
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceCompareTo(rightBytes.Value),
            _ => throw Fault("DPY4005", "Values of these types cannot be ordered.", span),
        };
    }

    private static PythonValue ApplyBinary(
        PythonOpCode opCode,
        PythonValue left,
        PythonValue right,
        TextSpan span
    )
    {
        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (
            opCode == PythonOpCode.BinaryAdd
            && left is PythonTextValue leftText
            && right is PythonTextValue rightText
        )
        {
            return new PythonTextValue(leftText.Value + rightText.Value);
        }

        if (
            opCode == PythonOpCode.BinaryAdd
            && left is PythonByteSequenceValue leftBytes
            && right is PythonByteSequenceValue rightBytes
        )
        {
            return new PythonByteSequenceValue([.. leftBytes.Value, .. rightBytes.Value]);
        }

        if (opCode == PythonOpCode.BinaryMultiply)
        {
            if (left is PythonTextValue text && right is PythonWholeNumberValue count)
            {
                return Repeat(text, count, span);
            }

            if (right is PythonTextValue reverseText && left is PythonWholeNumberValue reverseCount)
            {
                return Repeat(reverseText, reverseCount, span);
            }
        }

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            throw Fault("DPY4005", "Unsupported operands for binary operator.", span);
        }

        if (left is PythonComplexValue || right is PythonComplexValue)
        {
            return ApplyComplex(opCode, ToComplex(left), ToComplex(right), span);
        }

        if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
        {
            return ApplyFloatingPoint(opCode, ToDouble(left), ToDouble(right), span);
        }

        return ApplyWholeNumber(
            opCode,
            ((PythonWholeNumberValue)left).Value,
            ((PythonWholeNumberValue)right).Value,
            span
        );
    }

    private static PythonValue ApplyWholeNumber(
        PythonOpCode opCode,
        BigInteger left,
        BigInteger right,
        TextSpan span
    )
    {
        if (
            opCode
                is PythonOpCode.BinaryTrueDivide
                    or PythonOpCode.BinaryFloorDivide
                    or PythonOpCode.BinaryModulo
            && right.IsZero
        )
        {
            throw Fault("DPY4004", "Division by zero.", span);
        }

        return opCode switch
        {
            PythonOpCode.BinaryAdd => PythonWholeNumberValue.Create(left + right),
            PythonOpCode.BinarySubtract => PythonWholeNumberValue.Create(left - right),
            PythonOpCode.BinaryMultiply => PythonWholeNumberValue.Create(left * right),
            PythonOpCode.BinaryTrueDivide => new PythonFloatingPointValue(
                (double)left / (double)right
            ),
            PythonOpCode.BinaryFloorDivide => PythonWholeNumberValue.Create(
                FloorDivide(left, right)
            ),
            PythonOpCode.BinaryModulo => PythonWholeNumberValue.Create(
                left - FloorDivide(left, right) * right
            ),
            PythonOpCode.BinaryPower when right >= 0 && right <= int.MaxValue =>
                PythonWholeNumberValue.Create(BigInteger.Pow(left, (int)right)),
            PythonOpCode.BinaryPower when left.IsZero => throw Fault(
                "DPY4004",
                "Zero cannot be raised to a negative power.",
                span
            ),
            PythonOpCode.BinaryPower => new PythonFloatingPointValue(
                Math.Pow((double)left, (double)right)
            ),
            _ => throw Fault("DPY4005", "Unsupported numeric operator.", span),
        };
    }

    private static PythonFloatingPointValue ApplyFloatingPoint(
        PythonOpCode opCode,
        double left,
        double right,
        TextSpan span
    )
    {
        if (
            opCode
                is PythonOpCode.BinaryTrueDivide
                    or PythonOpCode.BinaryFloorDivide
                    or PythonOpCode.BinaryModulo
            && right == 0
        )
        {
            throw Fault("DPY4004", "Division by zero.", span);
        }

        return opCode switch
        {
            PythonOpCode.BinaryAdd => new PythonFloatingPointValue(left + right),
            PythonOpCode.BinarySubtract => new PythonFloatingPointValue(left - right),
            PythonOpCode.BinaryMultiply => new PythonFloatingPointValue(left * right),
            PythonOpCode.BinaryTrueDivide => new PythonFloatingPointValue(left / right),
            PythonOpCode.BinaryFloorDivide => new PythonFloatingPointValue(
                Math.Floor(left / right)
            ),
            PythonOpCode.BinaryModulo => new PythonFloatingPointValue(
                left - Math.Floor(left / right) * right
            ),
            PythonOpCode.BinaryPower => new PythonFloatingPointValue(Math.Pow(left, right)),
            _ => throw Fault("DPY4005", "Unsupported numeric operator.", span),
        };
    }

    private static PythonComplexValue ApplyComplex(
        PythonOpCode opCode,
        Complex left,
        Complex right,
        TextSpan span
    )
    {
        if (opCode == PythonOpCode.BinaryTrueDivide && right == Complex.Zero)
        {
            throw Fault("DPY4004", "Division by zero.", span);
        }

        return opCode switch
        {
            PythonOpCode.BinaryAdd => new PythonComplexValue(left + right),
            PythonOpCode.BinarySubtract => new PythonComplexValue(left - right),
            PythonOpCode.BinaryMultiply => new PythonComplexValue(left * right),
            PythonOpCode.BinaryTrueDivide => new PythonComplexValue(left / right),
            PythonOpCode.BinaryPower => new PythonComplexValue(Complex.Pow(left, right)),
            _ => throw Fault("DPY4005", "Unsupported complex-number operator.", span),
        };
    }

    private static PythonTextValue Repeat(
        PythonTextValue text,
        PythonWholeNumberValue count,
        TextSpan span
    )
    {
        if (count.Value <= 0)
        {
            return new PythonTextValue(string.Empty);
        }

        const int maximumRepeatedTextLength = 16 * 1024 * 1024;
        if (
            count.Value > int.MaxValue
            || text.Value.Length != 0 && count.Value * text.Value.Length > maximumRepeatedTextLength
        )
        {
            throw Fault("DPY4006", "Repeated string is too large.", span);
        }

        return new PythonTextValue(string.Concat(Enumerable.Repeat(text.Value, (int)count.Value)));
    }

    private static BigInteger FloorDivide(BigInteger left, BigInteger right)
    {
        var quotient = BigInteger.DivRem(left, right, out var remainder);
        if (!remainder.IsZero && remainder.Sign != right.Sign)
        {
            quotient--;
        }

        return quotient;
    }

    private static bool IsNumeric(PythonValue value) =>
        value is PythonWholeNumberValue or PythonFloatingPointValue or PythonComplexValue;

    private static bool IsTruthy(PythonValue value) =>
        value switch
        {
            PythonNoneValue => false,
            PythonTruthValue truth => truth.Value,
            PythonWholeNumberValue whole => !whole.Value.IsZero,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value != 0,
            PythonComplexValue complex => complex.Value != Complex.Zero,
            PythonTextValue text => text.Value.Length != 0,
            PythonByteSequenceValue bytes => bytes.Value.Length != 0,
            PythonListValue list => list.Elements.Count != 0,
            PythonTupleValue tuple => tuple.Elements.Length != 0,
            PythonDictionaryValue dictionary => dictionary.Items.Count != 0,
            _ => true,
        };

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : value;

    private static double ToDouble(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static Complex ToComplex(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => new Complex((double)whole.Value, 0),
            PythonFloatingPointValue floatingPoint => new Complex(floatingPoint.Value, 0),
            PythonComplexValue complex => complex.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static int GetJumpTarget(PythonInstruction instruction, int instructionCount)
    {
        if (instruction.Operand >= 0 && instruction.Operand <= instructionCount)
        {
            return instruction.Operand;
        }

        throw Fault("DPY4007", "The DotPython jump target is invalid.", instruction.Span);
    }

    private readonly record struct PythonValuePair(PythonValue Left, PythonValue Right);

    private sealed class PythonValuePairComparer : IEqualityComparer<PythonValuePair>
    {
        internal static PythonValuePairComparer Instance { get; } = new();

        public bool Equals(PythonValuePair left, PythonValuePair right) =>
            ReferenceEquals(left.Left, right.Left) && ReferenceEquals(left.Right, right.Right);

        public int GetHashCode(PythonValuePair pair) =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(pair.Left),
                RuntimeHelpers.GetHashCode(pair.Right)
            );
    }

    private static PythonRuntimeException Fault(string code, string message, TextSpan span) =>
        new(code, message, span);

    private struct PythonFrame
    {
        internal PythonFrame(
            PreparedPythonCode code,
            PythonGlobalNamespace globals,
            int localsBase,
            int localsCount,
            PythonCell[] cells,
            int evaluationStackBase,
            bool hasReturnLocalContinuation
        )
        {
            Code = code;
            Cells = cells;
            _encodedEvaluationStackBase = hasReturnLocalContinuation
                ? ~evaluationStackBase
                : evaluationStackBase;
            Globals = globals;
            LocalsBase = localsBase;
            LocalsCount = localsCount;
        }

        // A complemented base marks a return-local continuation without growing each frame.
        private readonly int _encodedEvaluationStackBase;

        internal PreparedPythonCode Code { get; }

        internal PythonCell[] Cells { get; }

        internal int EvaluationStackBase =>
            HasReturnLocalContinuation ? ~_encodedEvaluationStackBase : _encodedEvaluationStackBase;

        internal PythonGlobalNamespace Globals { get; }

        internal int InstructionPointer { get; set; }

        internal int LocalsBase { get; }

        internal int LocalsCount { get; }

        internal bool HasReturnLocalContinuation => _encodedEvaluationStackBase < 0;
    }
}
