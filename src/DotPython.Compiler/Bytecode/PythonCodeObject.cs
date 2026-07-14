using System.Collections.ObjectModel;

namespace DotPython.Compiler.Bytecode;

public sealed class PythonCodeObject
{
    internal PythonCodeObject(
        string name,
        IList<PythonInstruction> instructions,
        IList<PythonConstant> constants,
        IList<string> names,
        IList<string> variableNames,
        int argumentCount
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(argumentCount);
        if (argumentCount > variableNames.Count)
        {
            throw new ArgumentException(
                "The argument count cannot exceed the variable count.",
                nameof(argumentCount)
            );
        }

        FormatVersion = DotPythonBytecodeFormat.CurrentVersion;
        Name = name;
        ArgumentCount = argumentCount;
        Instructions = new ReadOnlyCollection<PythonInstruction>(instructions);
        Constants = new ReadOnlyCollection<PythonConstant>(constants);
        Names = new ReadOnlyCollection<string>(names);
        VariableNames = new ReadOnlyCollection<string>(variableNames);
    }

    public int FormatVersion { get; }

    public string Name { get; }

    public int ArgumentCount { get; }

    public IReadOnlyList<PythonInstruction> Instructions { get; }

    public IReadOnlyList<PythonConstant> Constants { get; }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<string> VariableNames { get; }
}
