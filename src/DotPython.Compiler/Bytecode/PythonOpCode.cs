namespace DotPython.Compiler.Bytecode;

public enum PythonOpCode
{
    LoadConstant,
    LoadName,
    StoreName,
    PopTop,
    UnaryPositive,
    UnaryNegative,
    UnaryInvert,
    BinaryAdd,
    BinarySubtract,
    BinaryMultiply,
    BinaryTrueDivide,
    BinaryFloorDivide,
    BinaryModulo,
    BinaryPower,
    Call,
    ReturnNone,
}
