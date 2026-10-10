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
        IList<string> cellVariableNames,
        IList<string> freeVariableNames,
        int argumentCount,
        int keywordOnlyArgumentCount = 0,
        int positionalOnlyArgumentCount = 0,
        bool hasVariadicPositional = false,
        bool hasVariadicKeywords = false,
        bool isGenerator = false,
        bool isCoroutine = false,
        PythonCodeObject? annotateCode = null,
        string? docString = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keywordOnlyArgumentCount);
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
        KeywordOnlyArgumentCount = keywordOnlyArgumentCount;
        PositionalOnlyArgumentCount = positionalOnlyArgumentCount;
        HasVariadicPositional = hasVariadicPositional;
        HasVariadicKeywords = hasVariadicKeywords;
        IsGenerator = isGenerator;
        IsCoroutine = isCoroutine;
        AnnotateCode = annotateCode;
        DocString = docString;
        Instructions = new ReadOnlyCollection<PythonInstruction>(instructions);
        Constants = new ReadOnlyCollection<PythonConstant>(constants);
        Names = new ReadOnlyCollection<string>(names);
        VariableNames = new ReadOnlyCollection<string>(variableNames);
        CellVariableNames = new ReadOnlyCollection<string>(cellVariableNames);
        FreeVariableNames = new ReadOnlyCollection<string>(freeVariableNames);
    }

    public int FormatVersion { get; }

    public string Name { get; }

    public int ArgumentCount { get; }

    /// <summary>Keyword-only parameter slot count within <see cref="ArgumentCount"/>.</summary>
    public int KeywordOnlyArgumentCount { get; }

    /// <summary>Leading parameter slots that reject keyword binding (PEP 570 `/`).</summary>
    public int PositionalOnlyArgumentCount { get; }

    /// <summary>Whether a `*args` slot exists (in source order, before keyword-only slots).</summary>
    public bool HasVariadicPositional { get; }

    /// <summary>Whether a `**kwargs` slot exists (always the last parameter slot).</summary>
    public bool HasVariadicKeywords { get; }

    /// <summary>Plain positional parameter slot count at the start of the parameter slots.</summary>
    public int PositionalParameterCount =>
        ArgumentCount
        - KeywordOnlyArgumentCount
        - (HasVariadicPositional ? 1 : 0)
        - (HasVariadicKeywords ? 1 : 0);

    /// <summary>Whether calling this code creates a generator instead of running it.</summary>
    public bool IsGenerator { get; }

    /// <summary>Whether calling this code creates a coroutine instead of running it.</summary>
    public bool IsCoroutine { get; }

    /// <summary>Whether calling this code creates a suspendable frame object (generator or coroutine) instead of running it.</summary>
    public bool IsSuspendable => IsGenerator || IsCoroutine;

    /// <summary>
    /// The body that evaluates this definition's annotations and returns them as a
    /// mapping, or null when the definition has none. PEP 649 keeps annotations
    /// unevaluated until something asks for them, so this body runs on first access
    /// to `__annotations__` in the globals and closure captured at the def site.
    /// </summary>
    public PythonCodeObject? AnnotateCode { get; }

    /// <summary>
    /// The docstring the body opens with, which is what a function, class or module
    /// reports as `__doc__` — the first statement's text when that statement is a
    /// string, and null when it is anything else.
    /// </summary>
    public string? DocString { get; }

    /// <summary>Whether the signature has no variadic or keyword-only parameters.</summary>
    public bool HasSimpleSignature =>
        KeywordOnlyArgumentCount == 0 && !HasVariadicPositional && !HasVariadicKeywords;

    public IReadOnlyList<PythonInstruction> Instructions { get; }

    public IReadOnlyList<PythonConstant> Constants { get; }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<string> VariableNames { get; }

    public IReadOnlyList<string> CellVariableNames { get; }

    public IReadOnlyList<string> FreeVariableNames { get; }
}
