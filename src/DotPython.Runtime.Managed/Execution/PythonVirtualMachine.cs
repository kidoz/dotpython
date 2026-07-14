using System.Numerics;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonVirtualMachine
{
    private readonly Dictionary<string, PythonValue> _builtins;
    private readonly CancellationToken _cancellationToken;
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
        CancellationToken cancellationToken
    )
    {
        _globals = globals;
        _output = output;
        _instructionLimit = instructionLimit;
        _cancellationToken = cancellationToken;
        _builtins = new Dictionary<string, PythonValue>(StringComparer.Ordinal)
        {
            ["print"] = new PythonBuiltinFunctionValue("print", Print),
        };
    }

    internal PythonValue Execute(PreparedPythonCode code)
    {
        ArgumentNullException.ThrowIfNull(code);

        PushFrame(code, _globals, 0, 0);
        return Run();
    }

    internal PythonValue Invoke(string functionName, IReadOnlyList<PythonValue> arguments)
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
        return Run();
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
            case PythonOpCode.CompareLessThan:
            case PythonOpCode.CompareLessThanOrEqual:
            case PythonOpCode.CompareGreaterThan:
            case PythonOpCode.CompareGreaterThanOrEqual:
                ApplyComparison(instruction);
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
                ApplyCall(instruction);
                break;
            case PythonOpCode.ReturnValue:
                ReturnFromFrame(Pop(instruction.Span));
                break;
            case PythonOpCode.ReturnNone:
                ReturnFromFrame(PythonNoneValue.Instance);
                break;
            default:
                throw Fault("DPY4007", "Unknown DotPython instruction.", instruction.Span);
        }
    }

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

    private void ApplyBinary(PythonInstruction instruction)
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        _evaluationStack.Push(ApplyBinary(instruction.OpCode, left, right, instruction.Span));
    }

    private void ApplyComparison(PythonInstruction instruction)
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        _evaluationStack.Push(ApplyComparison(instruction.OpCode, left, right, instruction.Span));
    }

    private void ApplyCall(PythonInstruction instruction)
    {
        var target = Peek(instruction.Operand, instruction.Span);
        if (target is PythonBuiltinFunctionValue builtin)
        {
            var arguments = PopArguments(instruction.Operand, instruction.Span);
            Pop(instruction.Span);
            _evaluationStack.Push(builtin.Invoke(arguments));
            return;
        }

        if (target is not PythonFunctionValue function)
        {
            throw Fault("DPY4003", "The selected value is not callable.", instruction.Span);
        }

        PushFunctionFrame(function, instruction.Operand, instruction.Span);
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

        var localsBase = ReserveLocals(function.Code.Definition.VariableNames.Count);
        for (var index = argumentCount - 1; index >= 0; index--)
        {
            _locals[localsBase + index] = Pop(span);
        }

        Pop(span);
        PushFrame(
            function.Code,
            function.Globals,
            localsBase,
            function.Code.Definition.VariableNames.Count
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
        for (var index = 0; index < arguments.Count; index++)
        {
            _locals[localsBase + index] = arguments[index];
        }

        PushFrame(
            function.Code,
            function.Globals,
            localsBase,
            function.Code.Definition.VariableNames.Count
        );
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
        int localsCount
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
            _evaluationStack.Count
        );
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

        _evaluationStack.Push(
            new PythonFunctionValue(code.Definition.Name, code, CurrentFrame.Globals)
        );
    }

    private void ReturnFromFrame(PythonValue value)
    {
        var evaluationStackBase = CurrentFrame.EvaluationStackBase;
        var localsBase = CurrentFrame.LocalsBase;
        var localsCount = CurrentFrame.LocalsCount;
        while (_evaluationStack.Count > evaluationStackBase)
        {
            _evaluationStack.Pop();
        }

        Array.Clear(_locals, localsBase, localsCount);
        _localsCount = localsBase;
        _frames[--_frameCount] = default;
        if (_frameCount != 0)
        {
            _evaluationStack.Push(value);
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
            (PythonBuiltinFunctionValue leftFunction, PythonBuiltinFunctionValue rightFunction) =>
                ReferenceEquals(leftFunction, rightFunction),
            (PythonFunctionValue leftFunction, PythonFunctionValue rightFunction) =>
                ReferenceEquals(leftFunction, rightFunction),
            _ => false,
        };
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

    private static PythonRuntimeException Fault(string code, string message, TextSpan span) =>
        new(code, message, span);

    private struct PythonFrame
    {
        internal PythonFrame(
            PreparedPythonCode code,
            PythonGlobalNamespace globals,
            int localsBase,
            int localsCount,
            int evaluationStackBase
        )
        {
            Code = code;
            EvaluationStackBase = evaluationStackBase;
            Globals = globals;
            LocalsBase = localsBase;
            LocalsCount = localsCount;
        }

        internal PreparedPythonCode Code { get; }

        internal int EvaluationStackBase { get; }

        internal PythonGlobalNamespace Globals { get; }

        internal int InstructionPointer { get; set; }

        internal int LocalsBase { get; }

        internal int LocalsCount { get; }
    }
}
