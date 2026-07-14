using System.Numerics;
using DotPython.Compiler.Bytecode;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PreparedPythonCode
{
    private readonly PythonValue?[] _constants;
    private readonly PreparedPythonCode?[] _functionCodes;

    private PreparedPythonCode(PythonCodeObject definition)
    {
        Definition = definition;
        _constants = new PythonValue?[definition.Constants.Count];
        _functionCodes = new PreparedPythonCode?[definition.Constants.Count];

        for (var index = 0; index < definition.Constants.Count; index++)
        {
            var constant = definition.Constants[index];
            if (
                constant.Type == PythonConstantType.CodeObject
                && constant.Value is PythonCodeObject functionCode
            )
            {
                _functionCodes[index] = Create(functionCode);
            }
            else
            {
                _constants[index] = ConvertConstant(constant);
            }
        }
    }

    internal PythonCodeObject Definition { get; }

    internal static PreparedPythonCode Create(PythonCodeObject definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new PreparedPythonCode(definition);
    }

    internal PythonValue GetConstant(int index)
    {
        if ((uint)index >= (uint)_constants.Length || _constants[index] is not { } value)
        {
            throw new InvalidOperationException("The prepared constant index is invalid.");
        }

        return value;
    }

    internal PreparedPythonCode GetFunctionCode(int index)
    {
        if ((uint)index >= (uint)_functionCodes.Length || _functionCodes[index] is not { } code)
        {
            throw new InvalidOperationException("The prepared function-code index is invalid.");
        }

        return code;
    }

    private static PythonValue ConvertConstant(PythonConstant constant) =>
        constant.Type switch
        {
            PythonConstantType.NoneValue => PythonNoneValue.Instance,
            PythonConstantType.TruthValue => PythonTruthValue.FromBoolean((bool)constant.Value!),
            PythonConstantType.WholeNumber => new PythonWholeNumberValue(
                (BigInteger)constant.Value!
            ),
            PythonConstantType.FloatingPoint => new PythonFloatingPointValue(
                (double)constant.Value!
            ),
            PythonConstantType.ComplexNumber => new PythonComplexValue((Complex)constant.Value!),
            PythonConstantType.TextValue => new PythonTextValue((string)constant.Value!),
            PythonConstantType.ByteSequence => new PythonByteSequenceValue((byte[])constant.Value!),
            PythonConstantType.CodeObject => throw new InvalidOperationException(
                "Code-object constants must be prepared as function code."
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(constant)),
        };
}
