using System.Buffers.Binary;
using System.Security.Cryptography;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class ClassHeaderCompilationTests
{
    [Fact]
    public void ClassHeaderEvaluatesBasesBeforeKeywordsAndMergesEveryKeywordEntry()
    {
        var code = Compile(
            "class C(flag=keyword(), *bases(), metaclass=meta(), **options()): pass"
        );
        var loadedNames = code
            .Instructions.Where(instruction => instruction.OpCode == PythonOpCode.LoadName)
            .Select(instruction => code.Names[instruction.Operand]);
        Assert.Equal(["bases", "keyword", "meta", "options"], loadedNames);
        Assert.Contains(
            code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ListExtend
        );
        Assert.Contains(
            code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ListToTuple
        );
        Assert.Equal(
            3,
            code.Instructions.Count(instruction =>
                instruction.OpCode == PythonOpCode.DictionaryMerge
            )
        );
        var creation = Assert.Single(
            code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.MakeClassWithKeywords
        );
        Assert.Equal(PythonConstantType.CodeObject, code.Constants[creation.Operand].Type);
    }

    [Fact]
    public void HeaderKeywordsCaptureEnclosingCellsAndBindComprehensionScopes()
    {
        var code = Compile(
            """
            def outer(meta, flags):
                def inner():
                    class C(metaclass=meta, flags=[value for value in flags]): pass
                    return C
                return inner
            """
        );
        var outer = Assert.Single(
            code.Constants.Select(constant => constant.Value).OfType<PythonCodeObject>()
        );
        Assert.Equal(["meta", "flags"], outer.CellVariableNames);
        var inner = Assert.Single(
            outer.Constants.Select(constant => constant.Value).OfType<PythonCodeObject>()
        );
        Assert.Contains("meta", inner.FreeVariableNames);
        Assert.Contains("flags", inner.FreeVariableNames);
    }

    [Fact]
    public void ClassHeaderArtifactsRoundTripDeterministically()
    {
        const string source = "class C(*bases, metaclass=Meta, **options): pass";
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("classes", Compile(source))
        );
        var restored = DotPythonModuleArtifactSerializer.Deserialize(bytes);
        Assert.Contains(
            restored.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.MakeClassWithKeywords
        );
        Assert.Equal(bytes, DotPythonModuleArtifactSerializer.Serialize(restored));
        Assert.Equal(32, restored.Code.FormatVersion);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ArtifactReaderRejectsClassInstructionsWithInvalidCodeConstants(
        bool keywords,
        bool nonCodeConstant
    )
    {
        var code = Compile(
            keywords
                ? "marker = 42\nclass C(metaclass=Meta): pass"
                : "marker = 42\nclass C(Base): pass"
        );
        var instruction = Assert.Single(
            code.Instructions,
            item =>
                item.OpCode
                == (keywords ? PythonOpCode.MakeClassWithKeywords : PythonOpCode.MakeClassWithBases)
        );
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("invalid", code)
        );
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10, 4));
        var payload = bytes.AsSpan(14, payloadLength);
        var encoded = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(encoded, (ushort)instruction.OpCode);
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(2), instruction.Operand);
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(6), instruction.Span.Start);
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(10), instruction.Span.Length);
        var offset = payload.IndexOf(encoded);
        Assert.True(offset >= 0);
        Assert.NotEqual(PythonConstantType.CodeObject, code.Constants[0].Type);
        BinaryPrimitives.WriteInt32LittleEndian(
            payload[(offset + 2)..],
            nonCodeConstant ? 0 : int.MaxValue
        );
        SHA256.HashData(payload).CopyTo(bytes.AsSpan(14 + payloadLength));
        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleArtifactSerializer.Deserialize(bytes)
        );
    }

    private static PythonCodeObject Compile(string source)
    {
        var parsed = PythonParser.Parse(new SourceText(source));
        Assert.Empty(parsed.Diagnostics);
        var compiled = PythonCompiler.Compile(parsed.Module);
        Assert.Empty(compiled.Diagnostics);
        return compiled.Code;
    }
}
