using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class DotPythonModuleArtifactTests
{
    [Fact]
    public void Serialize_IsDeterministicAcrossCompilationsAndExportOrder()
    {
        const string source =
            "factor = 2\n"
            + "def calculate(value):\n"
            + "    print(None, True, 123456789012345678901234567890, 1.5, 2j, 'text', b'bytes')\n"
            + "    return value * factor\n";
        var firstCode = Compile(source);
        var secondCode = Compile(source);
        var first = DotPythonModuleArtifact.Create(
            "pricing",
            firstCode,
            [
                new DotPythonModuleExport("calculate", "Calculate", DotPythonExportKind.Function),
                new DotPythonModuleExport("helper", "Helper", DotPythonExportKind.Function),
            ]
        );
        var second = DotPythonModuleArtifact.Create(
            "pricing",
            secondCode,
            [
                new DotPythonModuleExport("helper", "Helper", DotPythonExportKind.Function),
                new DotPythonModuleExport("calculate", "Calculate", DotPythonExportKind.Function),
            ]
        );

        var firstBytes = DotPythonModuleArtifactSerializer.Serialize(first);
        var secondBytes = DotPythonModuleArtifactSerializer.Serialize(second);

        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal("DPYMOD\r\n", System.Text.Encoding.ASCII.GetString(firstBytes[..8]));
        Assert.Equal(".dpyc", DotPythonModuleArtifactFormat.FileExtension);
    }

    [Fact]
    public void ManifestJson_UsesCanonicalSchemaAndOrdering()
    {
        var artifact = DotPythonModuleArtifact.Create(
            "pricing",
            Compile("def calculate(value): return value * 2"),
            [new DotPythonModuleExport("calculate", "Calculate", DotPythonExportKind.Function)]
        );

        var json = DotPythonModuleManifestJson.Serialize(artifact.Manifest);
        var restored = DotPythonModuleManifestJson.Deserialize(json);

        Assert.Equal(
            "{\"formatVersion\":4,\"moduleName\":\"pricing\",\"languageVersion\":\"3.14\","
                + "\"bytecodeFormatVersion\":7,\"exports\":[{\"pythonName\":\"calculate\","
                + "\"contractName\":\"Calculate\",\"kind\":\"function\"}]}",
            json
        );
        Assert.Equal(json, DotPythonModuleManifestJson.Serialize(restored));
    }

    [Fact]
    public void Deserialize_RoundTripsNestedCodeAndEveryConstantRepresentation()
    {
        const string source =
            "def values(argument):\n"
            + "    def read(): return argument\n"
            + "    print(None, False, 123456789012345678901234567890, 1.25, 3j, 'text', b'bytes')\n"
            + "    mapping = {'argument': argument}\n"
            + "    for key in mapping: print(mapping[key])\n"
            + "    return [read, (argument,)]\n";
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("values", Compile(source))
        );

        var restored = DotPythonModuleArtifactSerializer.Deserialize(bytes);

        Assert.Equal("values", restored.Manifest.ModuleName);
        Assert.Equal(DotPythonBytecodeFormat.CurrentVersion, restored.Code.FormatVersion);
        var nested = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    restored.Code.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );
        Assert.Equal("values", nested.Name);
        Assert.Equal(["argument"], nested.CellVariableNames);
        var read = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    nested.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );
        Assert.Equal(["argument"], read.FreeVariableNames);
        Assert.Contains(
            nested.Constants,
            constant => constant.Type == PythonConstantType.ByteSequence
        );
        Assert.Contains(
            nested.Instructions,
            instruction => instruction.OpCode == PythonOpCode.BuildList
        );
        Assert.Contains(
            nested.Instructions,
            instruction => instruction.OpCode == PythonOpCode.BuildTuple
        );
        Assert.Contains(
            nested.Instructions,
            instruction => instruction.OpCode == PythonOpCode.BuildDictionary
        );
        Assert.Contains(
            nested.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ForIter
        );
        Assert.Equal(bytes, DotPythonModuleArtifactSerializer.Serialize(restored));
    }

    [Fact]
    public void Deserialize_RoundTripsReturnLocalInstruction()
    {
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create(
                "identity",
                Compile("def identity(value): return value\n")
            )
        );

        var restored = DotPythonModuleArtifactSerializer.Deserialize(bytes);
        var function = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    restored.Code.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );

        Assert.Contains(
            function.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ReturnLocal
        );
        Assert.Equal(bytes, DotPythonModuleArtifactSerializer.Serialize(restored));
    }

    [Fact]
    public void Deserialize_RoundTripsCallLocalInstruction()
    {
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create(
                "invoke",
                Compile("def invoke(target): return target()\n")
            )
        );

        var restored = DotPythonModuleArtifactSerializer.Deserialize(bytes);
        var function = Assert.IsType<PythonCodeObject>(
            Assert
                .Single(
                    restored.Code.Constants,
                    constant => constant.Type == PythonConstantType.CodeObject
                )
                .Value
        );

        Assert.Contains(
            function.Instructions,
            instruction => instruction.OpCode == PythonOpCode.CallLocal
        );
        Assert.Equal(bytes, DotPythonModuleArtifactSerializer.Serialize(restored));
    }

    [Fact]
    public void Deserialize_RoundTripsManagedImportInstructions()
    {
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create(
                "consumer",
                Compile("import helper\nprint(helper.answer)")
            )
        );

        var restored = DotPythonModuleArtifactSerializer.Deserialize(bytes);

        Assert.Contains(
            restored.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.ImportName
        );
        Assert.Contains(
            restored.Code.Instructions,
            instruction => instruction.OpCode == PythonOpCode.LoadAttribute
        );
        Assert.Equal(bytes, DotPythonModuleArtifactSerializer.Serialize(restored));
    }

    [Fact]
    public void Deserialize_RejectsCorruptionTruncationAndTrailingData()
    {
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("sample", Compile("print(42)"))
        );
        var corrupted = (byte[])bytes.Clone();
        corrupted[14] ^= 0x01;
        var truncated = bytes[..^1];
        var trailing = new byte[bytes.Length + 1];
        bytes.CopyTo(trailing, 0);

        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleArtifactSerializer.Deserialize(corrupted)
        );
        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleArtifactSerializer.Deserialize(truncated)
        );
        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleArtifactSerializer.Deserialize(trailing)
        );
    }

    [Fact]
    public void ManifestJson_RejectsUnsupportedVersionsAndDuplicateExports()
    {
        const string unsupported =
            "{\"formatVersion\":5,\"moduleName\":\"sample\",\"languageVersion\":\"3.14\","
            + "\"bytecodeFormatVersion\":4,\"exports\":[]}";
        const string unsupportedBytecode =
            "{\"formatVersion\":4,\"moduleName\":\"sample\",\"languageVersion\":\"3.14\","
            + "\"bytecodeFormatVersion\":4,\"exports\":[]}";

        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleManifestJson.Deserialize(unsupported)
        );
        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleManifestJson.Deserialize(unsupportedBytecode)
        );
        Assert.Throws<ArgumentException>(() =>
            DotPythonModuleArtifact.Create(
                "sample",
                Compile("print(42)"),
                [
                    new DotPythonModuleExport("run", "Run", DotPythonExportKind.Function),
                    new DotPythonModuleExport("run", "RunAgain", DotPythonExportKind.Function),
                ]
            )
        );
    }

    [Fact]
    public void ManifestJson_RejectsLanguageVersionsOutsideTheSupportedArtifactSet()
    {
        const string unsupportedLanguage =
            "{\"formatVersion\":4,\"moduleName\":\"sample\",\"languageVersion\":\"3.13\","
            + "\"bytecodeFormatVersion\":7,\"exports\":[]}";
        const string nonCanonicalLanguage =
            "{\"formatVersion\":4,\"moduleName\":\"sample\",\"languageVersion\":\"3.14.0\","
            + "\"bytecodeFormatVersion\":7,\"exports\":[]}";

        var unsupportedFailure = Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleManifestJson.Deserialize(unsupportedLanguage)
        );
        Assert.Contains(
            "Supported artifact versions: 3.14",
            unsupportedFailure.Message,
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            DotPythonModuleManifestJson.Deserialize(nonCanonicalLanguage)
        );
        Assert.Throws<ArgumentException>(() =>
            DotPythonModuleArtifact.Create(
                "sample",
                Compile("print(42)"),
                exports: null,
                languageVersion: new Version(3, 15)
            )
        );
    }

    [Fact]
    public void Create_StampsExplicitSupportedLanguageVersion()
    {
        var artifact = DotPythonModuleArtifact.Create(
            "sample",
            Compile("print(42)"),
            exports: null,
            languageVersion: new Version(3, 14)
        );

        Assert.Equal("3.14", artifact.Manifest.LanguageVersion);
        var restored = DotPythonModuleManifestJson.Deserialize(
            DotPythonModuleManifestJson.Serialize(artifact.Manifest)
        );
        Assert.Equal("3.14", restored.LanguageVersion);
    }

    [Fact]
    public void WireEnums_HaveStableVersionedValues()
    {
        Assert.Equal(4, DotPythonModuleArtifactFormat.CurrentVersion);
        Assert.Equal(0, (int)PythonOpCode.LoadConstant);
        Assert.Equal(30, (int)PythonOpCode.MakeFunction);
        Assert.Equal(33, (int)PythonOpCode.ReturnNone);
        Assert.Equal(34, (int)PythonOpCode.LoadCell);
        Assert.Equal(35, (int)PythonOpCode.StoreCell);
        Assert.Equal(36, (int)PythonOpCode.BuildList);
        Assert.Equal(37, (int)PythonOpCode.BuildTuple);
        Assert.Equal(38, (int)PythonOpCode.BuildDictionary);
        Assert.Equal(39, (int)PythonOpCode.LoadSubscript);
        Assert.Equal(40, (int)PythonOpCode.StoreSubscript);
        Assert.Equal(41, (int)PythonOpCode.GetIterator);
        Assert.Equal(42, (int)PythonOpCode.ForIter);
        Assert.Equal(43, (int)PythonOpCode.ReturnLocal);
        Assert.Equal(44, (int)PythonOpCode.CallLocal);
        Assert.Equal(0, (int)PythonConstantType.NoneValue);
        Assert.Equal(7, (int)PythonConstantType.CodeObject);
    }

    private static PythonCodeObject Compile(string source)
    {
        var parseResult = PythonParser.Parse(new SourceText(source, "module.py"));
        Assert.Empty(parseResult.Diagnostics);
        var compilation = PythonCompiler.Compile(parseResult.Module, "module.py");
        Assert.Empty(compilation.Diagnostics);
        return compilation.Code;
    }
}
