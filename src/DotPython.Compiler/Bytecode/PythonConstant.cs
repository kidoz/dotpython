namespace DotPython.Compiler.Bytecode;

public sealed record PythonConstant(PythonConstantType Type, object? Value);

public enum PythonConstantType
{
    NoneValue,
    TruthValue,
    WholeNumber,
    FloatingPoint,
    ComplexNumber,
    TextValue,
    ByteSequence,
}
