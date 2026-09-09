using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Binding;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class ClassBindingCompatibilityTests
{
    [Theory]
    [InlineData("class C:\n    def method(__arg):\n        global _C__arg\n", "DPY3108")]
    [InlineData("class C:\n    def method(__arg, _C__arg): pass\n", "DPY3102")]
    public void Compile_PrivateNameCollisionsProduceDiagnostics(string source, string code)
    {
        var parsed = PythonParser.Parse(new SourceText(source));
        var result = PythonCompiler.Compile(parsed.Module);
        Assert.Empty(parsed.Diagnostics);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Bind_ClassOwnsImplicitCellAndMethodsCaptureIt()
    {
        var parsed = PythonParser.Parse(
            new SourceText("class C:\n    def method(self):\n        return __class__\n")
        );
        var result = PythonSymbolBinder.Bind(parsed.Module);

        Assert.Empty(parsed.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var classScope = Assert.Single(result.ModuleScope.Children);
        Assert.Contains("__class__", classScope.LocalNames);
        Assert.Equal(["__class__"], classScope.CellVariableNames);
        Assert.Equal(["__class__"], Assert.Single(classScope.Children).FreeVariableNames);
    }

    [Fact]
    public void Artifact_RoundTripsSeparateOwnedAndCapturedClassCells()
    {
        var parsed = PythonParser.Parse(
            new SourceText(
                "def outer():\n    __class__ = 1\n    class C:\n        value = __class__\n        def method(self): return __class__\n    return C\n"
            )
        );
        var result = PythonCompiler.Compile(parsed.Module);
        Assert.Empty(parsed.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var artifact = DotPythonModuleArtifact.Create("class_cells", result.Code, []);
        var serialized = DotPythonModuleArtifactSerializer.Serialize(artifact);
        var restored = DotPythonModuleArtifactSerializer.Deserialize(serialized);
        Assert.Equal(serialized, DotPythonModuleArtifactSerializer.Serialize(restored));
    }

    [Fact]
    public void Compile_PrivateNamesAreMangledButFunctionIdentityIsPreserved()
    {
        var parsed = PythonParser.Parse(
            new SourceText(
                "class _C:\n    def __method(self, __arg):\n        return self.__value + __arg\n"
            )
        );
        var result = PythonCompiler.Compile(parsed.Module);

        Assert.Empty(parsed.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var classCode = Assert.IsType<PythonCodeObject>(Assert.Single(result.Code.Constants).Value);
        var methodCode = Assert.IsType<PythonCodeObject>(Assert.Single(classCode.Constants).Value);
        Assert.Contains("_C__method", classCode.Names);
        Assert.Equal("__method", methodCode.Name);
        Assert.Equal(["self", "_C__arg"], methodCode.VariableNames);
        Assert.Contains("_C__value", methodCode.Names);
    }
}
