using System.Buffers.Binary;
using System.Security.Cryptography;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class ClassNamespaceCompilationTests
{
    private const string Source = """
        def outer(captured, changed):
            class C:
                global published
                nonlocal changed
                observed = captured
                changed = changed + 1
                del changed
                published = published + 1
                del published
            return C
        """;

    [Theory]
    [InlineData("nonlocal value", "DPY3107")]
    [InlineData("value = 1\nclass C:\n    nonlocal value", "DPY3110")]
    [InlineData("def outer():\n    class C:\n        nonlocal value", "DPY3110")]
    [InlineData("class Outer:\n    value = 1\n    class Inner:\n        nonlocal value", "DPY3110")]
    public void NonlocalRequiresAnEnclosingFunctionBinding(string source, string diagnostic)
    {
        var parsed = PythonParser.Parse(new SourceText(source));
        Assert.Empty(parsed.Diagnostics);
        Assert.Contains(
            PythonCompiler.Compile(parsed.Module).Diagnostics,
            item => item.Code == diagnostic
        );
    }

    [Fact]
    public void AssignmentOnlyClassNonlocalStillCapturesItsEnclosingFunctionBinding()
    {
        var code = GetClassCode(
            Compile(
                """
                def outer(value):
                    class C:
                        nonlocal value
                        value = 1
                    return C
                """
            )
        );
        Assert.Equal(["value"], code.FreeVariableNames);
        Assert.Single(code.Instructions, item => item.OpCode == PythonOpCode.StoreCell);
    }

    [Fact]
    public void ClassClosureReadsAndDeclaredScopesUseDistinctInstructions()
    {
        var code = GetClassCode(Compile(Source));
        var closureReads = code.Instructions.Where(item =>
            item.OpCode == PythonOpCode.LoadClassCell
        );
        Assert.Equal(
            ["captured", "changed"],
            closureReads.Select(item =>
                code.FreeVariableNames[item.Operand - code.CellVariableNames.Count]
            )
        );
        Assert.DoesNotContain(code.Instructions, item => item.OpCode == PythonOpCode.LoadCell);
        foreach (var opcode in new[] { PythonOpCode.StoreCell, PythonOpCode.DeleteCell })
        {
            var instruction = Assert.Single(code.Instructions, item => item.OpCode == opcode);
            Assert.Equal(
                "changed",
                code.FreeVariableNames[instruction.Operand - code.CellVariableNames.Count]
            );
        }
        foreach (
            var opcode in new[]
            {
                PythonOpCode.LoadGlobal,
                PythonOpCode.StoreGlobal,
                PythonOpCode.DeleteGlobal,
            }
        )
        {
            var instruction = Assert.Single(code.Instructions, item => item.OpCode == opcode);
            Assert.Equal("published", code.Names[instruction.Operand]);
        }
    }

    [Fact]
    public void ClassNamespaceInstructionsSurviveArtifactRoundTrip()
    {
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("namespace", Compile(Source))
        );
        var restored = DotPythonModuleArtifactSerializer.Deserialize(bytes);
        Assert.Equal(bytes, DotPythonModuleArtifactSerializer.Serialize(restored));
        Assert.Equal(33, restored.Code.FormatVersion);
        Assert.Contains(
            GetClassCode(restored.Code).Instructions,
            item => item.OpCode == PythonOpCode.LoadClassCell
        );
    }

    [Fact]
    public void NonlocalClassNameMutationTargetsOuterCellInsteadOfImplicitMethodCell()
    {
        var code = GetClassCode(
            Compile(
                """
                def outer():
                    __class__ = 1
                    class C:
                        nonlocal __class__
                        __class__ = 2
                        del __class__
                        def method(self): return __class__
                    return C
                """
            )
        );
        Assert.Equal(["__class__"], code.CellVariableNames);
        Assert.Equal(["__class__"], code.FreeVariableNames);
        Assert.Equal(
            1,
            Assert.Single(code.Instructions, item => item.OpCode == PythonOpCode.StoreCell).Operand
        );
        Assert.Equal(
            1,
            Assert.Single(code.Instructions, item => item.OpCode == PythonOpCode.DeleteCell).Operand
        );
    }

    [Theory]
    [InlineData(PythonOpCode.LoadClassCell, -1)]
    [InlineData(PythonOpCode.LoadClassCell, int.MaxValue)]
    [InlineData(PythonOpCode.LoadGlobal, -1)]
    [InlineData(PythonOpCode.LoadGlobal, int.MaxValue)]
    [InlineData(PythonOpCode.StoreGlobal, -1)]
    [InlineData(PythonOpCode.StoreGlobal, int.MaxValue)]
    [InlineData(PythonOpCode.DeleteGlobal, -1)]
    [InlineData(PythonOpCode.DeleteGlobal, int.MaxValue)]
    public void ArtifactReaderRejectsInvalidClassNamespaceOperands(
        PythonOpCode opcode,
        int invalidOperand
    )
    {
        var module = Compile(Source);
        var instruction = GetClassCode(module).Instructions.First(item => item.OpCode == opcode);
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("invalid", module)
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
        BinaryPrimitives.WriteInt32LittleEndian(payload[(offset + 2)..], invalidOperand);
        SHA256.HashData(payload).CopyTo(bytes.AsSpan(14 + payloadLength));
        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleArtifactSerializer.Deserialize(bytes)
        );
    }

    private static PythonCodeObject GetClassCode(PythonCodeObject module)
    {
        var outer = Assert.Single(
            module.Constants.Select(item => item.Value).OfType<PythonCodeObject>()
        );
        return Assert.Single(outer.Constants.Select(item => item.Value).OfType<PythonCodeObject>());
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
