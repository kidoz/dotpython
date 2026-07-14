namespace DotPython.Compiler.Bytecode;

public sealed record PythonConstant(PythonConstantType Type, object? Value);

public enum PythonConstantType
{
    NoneValue = 0,
    TruthValue = 1,
    WholeNumber = 2,
    FloatingPoint = 3,
    ComplexNumber = 4,
    TextValue = 5,
    ByteSequence = 6,
    CodeObject = 7,
}
