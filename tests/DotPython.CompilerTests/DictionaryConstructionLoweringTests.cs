using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class DictionaryConstructionLoweringTests
{
    [Fact]
    public void EmptyLiteralBuildsOneEmptyDictionary()
    {
        Assert.Equal(["build:0"], Trace(Compile("result = {}")));
    }

    [Fact]
    public void SmallGroupsEvaluateEveryKeyAndValueBeforeInserting()
    {
        foreach (var count in new[] { 1, 15 })
        {
            var expected = PairNames(0, count).Append($"build:{count}");
            Assert.Equal(expected, Trace(Compile($"result = {{{Pairs(0, count)}}}")));
        }
    }

    [Fact]
    public void SixteenAndSeventeenPairsInsertAfterEachValue()
    {
        foreach (var count in new[] { 16, 17 })
        {
            var expected = new List<string> { "build:0" };
            for (var index = 0; index < count; index++)
            {
                expected.AddRange([$"k{index}", $"v{index}", "add:0"]);
            }

            Assert.Equal(expected, Trace(Compile($"result = {{{Pairs(0, count)}}}")));
        }
    }

    [Fact]
    public void LargeLiteralsMergeSeventeenPairChunksAndTheirFinalRemainder()
    {
        foreach (
            var (count, builds, adds, updates) in new[]
            {
                (18, new[] { 0, 1 }, 17, 1),
                (32, new[] { 0, 15 }, 17, 1),
                (33, new[] { 0, 0 }, 33, 1),
                (34, new[] { 0, 0 }, 34, 1),
                (35, new[] { 0, 0, 1 }, 34, 2),
            }
        )
        {
            var code = Compile($"result = {{{Pairs(0, count)}}}");
            Assert.Equal(
                builds,
                code.Instructions.Where(item => item.OpCode == PythonOpCode.BuildDictionary)
                    .Select(item => item.Operand)
            );
            Assert.Equal(
                adds,
                code.Instructions.Count(item => item.OpCode == PythonOpCode.DictionaryAdd)
            );
            Assert.Equal(
                updates,
                code.Instructions.Count(item => item.OpCode == PythonOpCode.DictionaryUpdate)
            );
            Assert.Equal(
                PairNames(0, count),
                code.Instructions.Where(item => item.OpCode == PythonOpCode.LoadName)
                    .Select(item => code.Names[item.Operand])
            );
            Assert.Equal("update:0", Trace(code)[^1]);
        }
    }

    [Fact]
    public void UnpackingFlushesGroupsBeforeEvaluatingTheMappingAndAfterItsMerge()
    {
        var code = Compile("result = {k0: v0, k1: v1, **mapping, k2: v2, k3: v3}");
        Assert.Equal(
            [
                "k0",
                "v0",
                "k1",
                "v1",
                "build:2",
                "mapping",
                "update:0",
                "k2",
                "v2",
                "k3",
                "v3",
                "build:2",
                "update:0",
            ],
            Trace(code)
        );
    }

    [Fact]
    public void AdjacentUnpacksReuseTheAccumulatorWithoutEmptyIntermediateGroups()
    {
        var code = Compile("result = {**first, **second, key: value, **third}");
        Assert.Equal(
            [
                "build:0",
                "first",
                "update:0",
                "second",
                "update:0",
                "key",
                "value",
                "build:1",
                "update:0",
                "third",
                "update:0",
            ],
            Trace(code)
        );
    }

    [Fact]
    public void GroupedDictionaryArtifactsPreserveInstructionsAndSourceSpansDeterministically()
    {
        var source = $"result = {{**first, {Pairs(0, 18)}, **last}}";
        var code = Compile(source);
        var bytes = Serialize(code);
        var restored = DotPythonModuleArtifactSerializer.Deserialize(bytes);

        Assert.Equal(code.Instructions, restored.Code.Instructions);
        Assert.Equal(bytes, Serialize(Compile(source)));
        Assert.Equal(bytes, DotPythonModuleArtifactSerializer.Serialize(restored));
        Assert.Equal(DotPythonBytecodeFormat.CurrentVersion, restored.Code.FormatVersion);
        Assert.All(
            restored.Code.Instructions.Where(item =>
                item.OpCode
                    is PythonOpCode.BuildDictionary
                        or PythonOpCode.DictionaryAdd
                        or PythonOpCode.DictionaryUpdate
            ),
            item =>
            {
                Assert.True(item.Span.Length > 0);
                Assert.InRange(item.Span.Start, 0, source.Length - item.Span.Length);
            }
        );
    }

    private static byte[] Serialize(PythonCodeObject code) =>
        DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("dictionary_construction", code)
        );

    private static IEnumerable<string> PairNames(int start, int count) =>
        Enumerable.Range(start, count).SelectMany(index => new[] { $"k{index}", $"v{index}" });

    private static string Pairs(int start, int count) =>
        string.Join(", ", Enumerable.Range(start, count).Select(index => $"k{index}: v{index}"));

    private static string[] Trace(PythonCodeObject code) =>
        code
            .Instructions.Where(item =>
                item.OpCode
                    is PythonOpCode.LoadName
                        or PythonOpCode.BuildDictionary
                        or PythonOpCode.DictionaryAdd
                        or PythonOpCode.DictionaryUpdate
            )
            .Select(item =>
                item.OpCode switch
                {
                    PythonOpCode.LoadName => code.Names[item.Operand],
                    PythonOpCode.BuildDictionary => $"build:{item.Operand}",
                    PythonOpCode.DictionaryAdd => $"add:{item.Operand}",
                    _ => $"update:{item.Operand}",
                }
            )
            .ToArray();

    private static PythonCodeObject Compile(string source)
    {
        var parsed = PythonParser.Parse(new SourceText(source));
        Assert.Empty(parsed.Diagnostics);
        var compiled = PythonCompiler.Compile(parsed.Module);
        Assert.Empty(compiled.Diagnostics);
        return compiled.Code;
    }
}
