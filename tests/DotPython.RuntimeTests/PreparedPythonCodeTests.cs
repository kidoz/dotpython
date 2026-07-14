using DotPython.Compiler;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PreparedPythonCodeTests
{
    [Fact]
    public void PrepareCode_ReusesPreparedCodeAndImmutableConstants()
    {
        var code = Compile("first = 42\nsecond = 42\n");
        var engine = new ManagedPythonEngine();

        var first = engine.PrepareCode(code);
        var second = engine.PrepareCode(code);

        Assert.Same(first, second);
        Assert.Same(first.GetConstant(0), second.GetConstant(0));
    }

    [Fact]
    public void PrepareCode_PreparesNestedFunctionConstants()
    {
        var code = Compile("def answer(): return 42\nresult = answer()\n");
        var prepared = PreparedPythonCode.Create(code);
        var functionCodeIndex = Assert
            .Single(
                code.Constants.Select((constant, index) => (constant, index)),
                item =>
                    item.constant.Type == DotPython.Compiler.Bytecode.PythonConstantType.CodeObject
            )
            .index;

        var function = prepared.GetFunctionCode(functionCodeIndex);

        Assert.Equal("answer", function.Definition.Name);
        Assert.Same(function.GetConstant(0), function.GetConstant(0));
    }

    [Fact]
    public void TruthValues_AreCanonicalSingletons()
    {
        Assert.Same(PythonTruthValue.True, PythonTruthValue.FromBoolean(true));
        Assert.Same(PythonTruthValue.False, PythonTruthValue.FromBoolean(false));
    }

    private static DotPython.Compiler.Bytecode.PythonCodeObject Compile(string code)
    {
        var source = new SourceText(code, "prepared-code-test.py");
        var parse = PythonParser.Parse(source);
        var compilation = PythonCompiler.Compile(parse.Module, source.FilePath!);

        Assert.Empty(parse.Diagnostics);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Code;
    }
}
