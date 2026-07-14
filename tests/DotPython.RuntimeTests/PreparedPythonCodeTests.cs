using System.Numerics;
using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Bytecode;
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

    [Fact]
    public void GlobalLoadCache_ReusesSlotValuesAndInvalidatesOnNamespaceShapeChanges()
    {
        var code = Compile("global_value = 41\ndef get_value(): return global_value\n");
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("global_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "get_value");
        var loadIndex = GetLoadNameInstructionIndex(function);

        for (var invocation = 0; invocation < 8; invocation++)
        {
            Assert.Equal(new BigInteger(41), Invoke(engine, "get_value").Value);
        }

        Assert.True(initialization.Success);
        Assert.Equal(GlobalLoadCacheState.Global, function.GetGlobalLoadCacheState(loadIndex));

        var reassignment = engine.Execute(
            "global_value = 42",
            "reassignment.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(reassignment.Success);
        Assert.Equal(new BigInteger(42), Invoke(engine, "get_value").Value);
        Assert.Equal(GlobalLoadCacheState.Global, function.GetGlobalLoadCacheState(loadIndex));

        var shapeChange = engine.Execute(
            "other_value = 1",
            "shape-change.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(shapeChange.Success);
        Assert.Equal(new BigInteger(42), Invoke(engine, "get_value").Value);
        Assert.Equal(GlobalLoadCacheState.Adaptive, function.GetGlobalLoadCacheState(loadIndex));
    }

    [Fact]
    public void GlobalLoadCache_SpecializesBuiltinsAndAllowsGlobalShadowing()
    {
        var code = Compile("def get_builtin(): return print\n");
        var engine = new ManagedPythonEngine();
        var initialization = engine.Execute(
            DotPythonModuleArtifact.Create("builtin_cache", code),
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var function = GetPreparedFunction(engine, code, "get_builtin");
        var loadIndex = GetLoadNameInstructionIndex(function);

        for (var invocation = 0; invocation < 16; invocation++)
        {
            Assert.IsType<PythonBuiltinFunctionValue>(
                engine.Invoke(
                    "get_builtin",
                    [],
                    TextWriter.Null,
                    new ManagedExecutionOptions(),
                    TestContext.Current.CancellationToken
                )
            );
        }

        Assert.True(initialization.Success);
        Assert.Equal(GlobalLoadCacheState.Builtin, function.GetGlobalLoadCacheState(loadIndex));

        var shadow = engine.Execute(
            "print = 42",
            "shadow.py",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(shadow.Success);
        Assert.Equal(new BigInteger(42), Invoke(engine, "get_builtin").Value);
        Assert.Equal(GlobalLoadCacheState.Adaptive, function.GetGlobalLoadCacheState(loadIndex));
    }

    private static PythonWholeNumberValue Invoke(ManagedPythonEngine engine, string functionName) =>
        Assert.IsType<PythonWholeNumberValue>(
            engine.Invoke(
                functionName,
                [],
                TextWriter.Null,
                new ManagedExecutionOptions(),
                TestContext.Current.CancellationToken
            )
        );

    private static PreparedPythonCode GetPreparedFunction(
        ManagedPythonEngine engine,
        PythonCodeObject code,
        string functionName
    )
    {
        var functionCodeIndex = Assert
            .Single(
                code.Constants.Select((constant, index) => (constant, index)),
                item =>
                    item.constant.Type == PythonConstantType.CodeObject
                    && item.constant.Value is PythonCodeObject function
                    && function.Name == functionName
            )
            .index;
        return engine.PrepareCode(code).GetFunctionCode(functionCodeIndex);
    }

    private static int GetLoadNameInstructionIndex(PreparedPythonCode code) =>
        Assert
            .Single(
                code.Definition.Instructions.Select((instruction, index) => (instruction, index)),
                item => item.instruction.OpCode == PythonOpCode.LoadName
            )
            .index;

    private static PythonCodeObject Compile(string code)
    {
        var source = new SourceText(code, "prepared-code-test.py");
        var parse = PythonParser.Parse(source);
        var compilation = PythonCompiler.Compile(parse.Module, source.FilePath!);

        Assert.Empty(parse.Diagnostics);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Code;
    }
}
