using System.Collections.ObjectModel;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Diagnostics;

namespace DotPython.Compiler;

public sealed class PythonCompilationResult
{
    internal PythonCompilationResult(PythonCodeObject code, IList<Diagnostic> diagnostics)
    {
        Code = code;
        Diagnostics = new ReadOnlyCollection<Diagnostic>(diagnostics);
    }

    public PythonCodeObject Code { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool Success => Diagnostics.Count == 0;
}
