using DotPython.Language.Text;

namespace DotPython.Compiler.Bytecode;

public readonly record struct PythonInstruction(PythonOpCode OpCode, int Operand, TextSpan Span);
