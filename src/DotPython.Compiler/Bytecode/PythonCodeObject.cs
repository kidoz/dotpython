using System.Collections.ObjectModel;

namespace DotPython.Compiler.Bytecode;

public sealed class PythonCodeObject
{
    internal PythonCodeObject(
        string name,
        IList<PythonInstruction> instructions,
        IList<PythonConstant> constants,
        IList<string> names
    )
    {
        FormatVersion = DotPythonBytecodeFormat.CurrentVersion;
        Name = name;
        Instructions = new ReadOnlyCollection<PythonInstruction>(instructions);
        Constants = new ReadOnlyCollection<PythonConstant>(constants);
        Names = new ReadOnlyCollection<string>(names);
    }

    public int FormatVersion { get; }

    public string Name { get; }

    public IReadOnlyList<PythonInstruction> Instructions { get; }

    public IReadOnlyList<PythonConstant> Constants { get; }

    public IReadOnlyList<string> Names { get; }
}
