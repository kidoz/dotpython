using System.Numerics;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonVirtualMachine
{
    private readonly Dictionary<string, PythonValue> _builtins;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<string, PythonValue> _globals;
    private readonly long _instructionLimit;
    private readonly TextWriter _output;
    private readonly Stack<PythonValue> _stack = [];
    private long _instructionsExecuted;

    internal PythonVirtualMachine(
        Dictionary<string, PythonValue> globals,
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

    internal void Execute(PythonCodeObject code)
    {
        ArgumentNullException.ThrowIfNull(code);

        for (
            var instructionPointer = 0;
            instructionPointer < code.Instructions.Count;
            instructionPointer++
        )
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var instruction = code.Instructions[instructionPointer];
            if (_instructionsExecuted++ >= _instructionLimit)
            {
                throw Fault(
                    "DPY4001",
                    "The managed instruction limit was exceeded.",
                    instruction.Span
                );
            }

            switch (instruction.OpCode)
            {
                case PythonOpCode.LoadConstant:
                    _stack.Push(ConvertConstant(code.Constants[instruction.Operand]));
                    break;
                case PythonOpCode.LoadName:
                    _stack.Push(LoadName(code.Names[instruction.Operand], instruction.Span));
                    break;
                case PythonOpCode.StoreName:
                    _globals[code.Names[instruction.Operand]] = Pop(instruction.Span);
                    break;
                case PythonOpCode.PopTop:
                    Pop(instruction.Span);
                    break;
                case PythonOpCode.UnaryPositive:
                case PythonOpCode.UnaryNegative:
                case PythonOpCode.UnaryInvert:
                    _stack.Push(
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
                case PythonOpCode.Call:
                    ApplyCall(instruction);
                    break;
                case PythonOpCode.ReturnNone:
                    return;
                default:
                    throw Fault("DPY4007", "Unknown DotPython instruction.", instruction.Span);
            }
        }
    }

    private PythonValue LoadName(string name, TextSpan span)
    {
        if (_globals.TryGetValue(name, out var value) || _builtins.TryGetValue(name, out value))
        {
            return value;
        }

        throw Fault("DPY4002", $"Name '{name}' is not defined.", span);
    }

    private void ApplyBinary(PythonInstruction instruction)
    {
        var right = Pop(instruction.Span);
        var left = Pop(instruction.Span);
        _stack.Push(ApplyBinary(instruction.OpCode, left, right, instruction.Span));
    }

    private void ApplyCall(PythonInstruction instruction)
    {
        var arguments = new PythonValue[instruction.Operand];
        for (var index = arguments.Length - 1; index >= 0; index--)
        {
            arguments[index] = Pop(instruction.Span);
        }

        var target = Pop(instruction.Span);
        if (target is not PythonBuiltinFunctionValue function)
        {
            throw Fault("DPY4003", "The selected value is not callable.", instruction.Span);
        }

        _stack.Push(function.Invoke(arguments));
    }

    private PythonNoneValue Print(IReadOnlyList<PythonValue> arguments)
    {
        _output.WriteLine(string.Join(" ", arguments.Select(value => value.ToDisplayString())));
        return PythonNoneValue.Instance;
    }

    private PythonValue Pop(TextSpan span)
    {
        if (_stack.TryPop(out var value))
        {
            return value;
        }

        throw Fault("DPY4007", "The DotPython evaluation stack is empty.", span);
    }

    private static PythonValue ConvertConstant(PythonConstant constant) =>
        constant.Type switch
        {
            PythonConstantType.NoneValue => PythonNoneValue.Instance,
            PythonConstantType.TruthValue => new PythonTruthValue((bool)constant.Value!),
            PythonConstantType.WholeNumber => new PythonWholeNumberValue(
                (BigInteger)constant.Value!
            ),
            PythonConstantType.FloatingPoint => new PythonFloatingPointValue(
                (double)constant.Value!
            ),
            PythonConstantType.ComplexNumber => new PythonComplexValue((Complex)constant.Value!),
            PythonConstantType.TextValue => new PythonTextValue((string)constant.Value!),
            PythonConstantType.ByteSequence => new PythonByteSequenceValue((byte[])constant.Value!),
            _ => throw new ArgumentOutOfRangeException(nameof(constant)),
        };

    private static PythonValue ApplyUnary(PythonOpCode opCode, PythonValue operand, TextSpan span)
    {
        operand = PromoteTruthValue(operand);
        return (opCode, operand) switch
        {
            (PythonOpCode.UnaryPositive, PythonWholeNumberValue value) => value,
            (PythonOpCode.UnaryPositive, PythonFloatingPointValue value) => value,
            (PythonOpCode.UnaryPositive, PythonComplexValue value) => value,
            (PythonOpCode.UnaryNegative, PythonWholeNumberValue value) =>
                new PythonWholeNumberValue(-value.Value),
            (PythonOpCode.UnaryNegative, PythonFloatingPointValue value) =>
                new PythonFloatingPointValue(-value.Value),
            (PythonOpCode.UnaryNegative, PythonComplexValue value) => new PythonComplexValue(
                -value.Value
            ),
            (PythonOpCode.UnaryInvert, PythonWholeNumberValue value) => new PythonWholeNumberValue(
                ~value.Value
            ),
            _ => throw Fault("DPY4005", "Unsupported operand for unary operator.", span),
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
            PythonOpCode.BinaryAdd => new PythonWholeNumberValue(left + right),
            PythonOpCode.BinarySubtract => new PythonWholeNumberValue(left - right),
            PythonOpCode.BinaryMultiply => new PythonWholeNumberValue(left * right),
            PythonOpCode.BinaryTrueDivide => new PythonFloatingPointValue(
                (double)left / (double)right
            ),
            PythonOpCode.BinaryFloorDivide => new PythonWholeNumberValue(FloorDivide(left, right)),
            PythonOpCode.BinaryModulo => new PythonWholeNumberValue(
                left - FloorDivide(left, right) * right
            ),
            PythonOpCode.BinaryPower when right >= 0 && right <= int.MaxValue =>
                new PythonWholeNumberValue(BigInteger.Pow(left, (int)right)),
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

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? new PythonWholeNumberValue(truth.Value ? BigInteger.One : BigInteger.Zero)
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

    private static PythonRuntimeException Fault(string code, string message, TextSpan span) =>
        new(code, message, span);
}
