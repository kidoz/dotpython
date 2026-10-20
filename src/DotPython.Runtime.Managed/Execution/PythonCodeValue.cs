using System.Numerics;
using System.Runtime.CompilerServices;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// A compiled `code` object, as `compile` returns and `exec`/`eval` accept. The
/// definition is the compiler's own code object; the file name is the one the compile
/// was given, which is what a frame built from it reports.
/// </summary>
internal sealed record PythonCodeValue(PythonCodeObject Definition, string FileName) : PythonValue
{
    /// <summary>
    /// The name this view of the code reports. A module code object is compiled under its
    /// file or module name but CPython reports every module body as `<module>`.
    /// </summary>
    internal string? NameOverride { get; init; }

    private string ReportedName => NameOverride ?? Definition.Name;

    /// <summary>
    /// The attributes the runtime can answer for a code object. The ones a compiled
    /// definition does not carry — the byte code itself, the line tables, the stack size
    /// and the flags — are absent rather than guessed at.
    /// </summary>
    internal PythonValue GetAttribute(string name, TextSpan span) =>
        name switch
        {
            "co_name" or "co_qualname" => new PythonTextValue(ReportedName),
            "co_filename" => new PythonTextValue(FileName),
            "co_firstlineno" => PythonWholeNumberValue.Create(1),
            "co_argcount" => Count(Definition.PositionalParameterCount),
            "co_posonlyargcount" => Count(Definition.PositionalOnlyArgumentCount),
            "co_kwonlyargcount" => Count(Definition.KeywordOnlyArgumentCount),
            "co_nlocals" => Count(Definition.VariableNames.Count),
            "co_varnames" => NameTuple(Definition.VariableNames),
            "co_names" => NameTuple(Definition.Names),
            "co_cellvars" => NameTuple(Definition.CellVariableNames),
            "co_freevars" => NameTuple(Definition.FreeVariableNames),
            "co_consts" => new PythonTupleValue([.. Definition.Constants.Select(ConstantValue)]),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'code' object has no attribute '{name}'",
                span,
                "AttributeError"
            ),
        };

    /// <summary>A constant from the compiled pool as the value it stands for.</summary>
    private PythonValue ConstantValue(PythonConstant constant) =>
        constant.Type switch
        {
            PythonConstantType.NoneValue => PythonNoneValue.Instance,
            PythonConstantType.TruthValue => PythonTruthValue.FromBoolean((bool)constant.Value!),
            PythonConstantType.WholeNumber => PythonWholeNumberValue.Create(
                (BigInteger)constant.Value!
            ),
            PythonConstantType.FloatingPoint => new PythonFloatingPointValue(
                (double)constant.Value!
            ),
            PythonConstantType.ComplexNumber => new PythonComplexValue(
                (System.Numerics.Complex)constant.Value!
            ),
            PythonConstantType.TextValue => new PythonTextValue((string)constant.Value!),
            PythonConstantType.ByteSequence => new PythonByteSequenceValue([
                .. (byte[])constant.Value!,
            ]),
            // A nested definition carries the file its unit was compiled under.
            PythonConstantType.CodeObject => new PythonCodeValue(
                (PythonCodeObject)constant.Value!,
                FileName
            ),
            _ => PythonEllipsisValue.Instance,
        };

    private static PythonWholeNumberValue Count(int count) => PythonWholeNumberValue.Create(count);

    private static PythonTupleValue NameTuple(IReadOnlyList<string> names) =>
        new([.. names.Select(name => (PythonValue)new PythonTextValue(name))]);

    /// <summary>
    /// Code objects compare by what they compile to, not by identity: two compiles of the
    /// same source are equal, and the file name they were compiled under is not part of
    /// the comparison, as CPython's own is not.
    /// </summary>
    public bool Equals(PythonCodeValue? other) =>
        other is not null && CodesEqual(Definition, other.Definition);

    public override int GetHashCode() => CodeHash(Definition);

    private static bool CodesEqual(PythonCodeObject left, PythonCodeObject right)
    {
        if (
            left.Instructions.Count != right.Instructions.Count
            || left.Constants.Count != right.Constants.Count
            || left.ArgumentCount != right.ArgumentCount
            || left.KeywordOnlyArgumentCount != right.KeywordOnlyArgumentCount
            || left.PositionalOnlyArgumentCount != right.PositionalOnlyArgumentCount
            || left.HasVariadicPositional != right.HasVariadicPositional
            || left.HasVariadicKeywords != right.HasVariadicKeywords
            || left.IsGenerator != right.IsGenerator
            || left.IsCoroutine != right.IsCoroutine
            || !NamesEqual(left.Names, right.Names)
            || !NamesEqual(left.VariableNames, right.VariableNames)
            || !NamesEqual(left.CellVariableNames, right.CellVariableNames)
            || !NamesEqual(left.FreeVariableNames, right.FreeVariableNames)
        )
        {
            return false;
        }

        for (var index = 0; index < left.Instructions.Count; index++)
        {
            // The span is part of it: CPython's own code objects differ when the
            // source's layout does, even where the bytecode is the same.
            if (left.Instructions[index] != right.Instructions[index])
            {
                return false;
            }
        }

        for (var index = 0; index < left.Constants.Count; index++)
        {
            if (!ConstantsEqual(left.Constants[index], right.Constants[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NamesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && !left.Where(
                (name, index) => !string.Equals(name, right[index], StringComparison.Ordinal)
            )
            .Any();

    private static bool ConstantsEqual(PythonConstant left, PythonConstant right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        if (left.Type == PythonConstantType.CodeObject)
        {
            return CodesEqual((PythonCodeObject)left.Value!, (PythonCodeObject)right.Value!);
        }

        if (left.Type == PythonConstantType.ByteSequence)
        {
            return ((byte[])left.Value!).AsSpan().SequenceEqual((byte[])right.Value!);
        }

        return Equals(left.Value, right.Value);
    }

    private static int CodeHash(PythonCodeObject code)
    {
        var hash = new HashCode();
        hash.Add(code.Instructions.Count);
        hash.Add(code.ArgumentCount);
        hash.Add(code.KeywordOnlyArgumentCount);
        hash.Add(code.PositionalOnlyArgumentCount);
        hash.Add(code.HasVariadicPositional);
        hash.Add(code.HasVariadicKeywords);
        hash.Add(code.IsGenerator);
        hash.Add(code.IsCoroutine);
        for (var index = 0; index < code.Instructions.Count; index++)
        {
            hash.Add(code.Instructions[index].OpCode);
            hash.Add(code.Instructions[index].Operand);
            hash.Add(code.Instructions[index].Span.Start);
            hash.Add(code.Instructions[index].Span.Length);
        }

        for (var index = 0; index < code.Constants.Count; index++)
        {
            var constant = code.Constants[index];
            hash.Add(constant.Type);
            if (constant.Type == PythonConstantType.CodeObject)
            {
                hash.Add(CodeHash((PythonCodeObject)constant.Value!));
            }
            else if (constant.Type == PythonConstantType.ByteSequence)
            {
                foreach (var item in (byte[])constant.Value!)
                {
                    hash.Add(item);
                }
            }
            else
            {
                hash.Add(constant.Value);
            }
        }

        foreach (var name in code.Names)
        {
            hash.Add(name);
        }

        return hash.ToHashCode();
    }

    internal override string ToDisplayString() =>
        $"<code object {ReportedName} at 0x{RuntimeHelpers.GetHashCode(this):x}, "
        + $"file \"{FileName}\", line 1>";
}
