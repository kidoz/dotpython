using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ManagedPythonEngineTests
{
    [Fact]
    public void Execute_RunsDeserializedModuleArtifact()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("value = 40 + 2; print(value)", "answer.py")
        );
        var compilation = PythonCompiler.Compile(parseResult.Module, "answer.py");
        var bytes = DotPythonModuleArtifactSerializer.Serialize(
            DotPythonModuleArtifact.Create("answer", compilation.Code)
        );
        var artifact = DotPythonModuleArtifactSerializer.Deserialize(bytes);
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            artifact,
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(compilation.Diagnostics);
        Assert.True(result.Success);
        Assert.Equal("answer.dpyc", result.Source.FilePath);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_RunsAssignmentArithmeticAndBuiltinCall()
    {
        using var output = new StringWriter();
        var engine = new ManagedPythonEngine();

        var result = engine.Execute(
            "value = 40 + 2; print(value)",
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesGlobalsAcrossCallsOnTheSameEngine()
    {
        using var output = new StringWriter();
        var engine = new ManagedPythonEngine();

        var assignment = engine.Execute(
            "value = 21",
            "first.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var print = engine.Execute(
            "print(value * 2)",
            "second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(assignment.Success);
        Assert.True(print.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("print(2 ** 3 ** 2)", "512")]
    [InlineData("print(-7 // 3, -7 % 3, 7 / 2)", "-3 2 3.5")]
    [InlineData("print('ab' * 3)", "ababab")]
    [InlineData("print(True + 1, +True, ~False, None)", "2 1 -1 None")]
    [InlineData("print(2j, 1 + 2j)", "2j (1+2j)")]
    [InlineData("print(123456789012345678901234567890 + 1)", "123456789012345678901234567891")]
    [InlineData(
        "print(-4 - 1, -5 - 1, 255 + 1, 256 + 1, 10 ** 40)",
        "-5 -6 256 257 10000000000000000000000000000000000000000"
    )]
    [InlineData("print(False and missing, True or missing)", "False True")]
    [InlineData("print('' or 'fallback', 'value' and 42)", "fallback 42")]
    [InlineData("print(not 0, not 'value', 1 < 2 < 3, 1 < 2 > 3)", "True False True False")]
    [InlineData("print(1 == True, None != 0, 'a' < 'b', b'a' <= b'ab')", "True True True True")]
    public void Execute_MatchesSupportedPythonNumericAndTextSemantics(string code, string expected)
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(expected + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Execute_RunsNestedIfWhileAndLoopElse()
    {
        const string code =
            "value = 0\n"
            + "while value < 3:\n"
            + "    if value != 1:\n"
            + "        print(value)\n"
            + "    value = value + 1\n"
            + "else:\n"
            + "    print('done')\n";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            $"0{Environment.NewLine}2{Environment.NewLine}done{Environment.NewLine}",
            output.ToString()
        );
    }

    [Fact]
    public void Execute_RunsFunctionsWithLocalsGlobalsEarlyReturnsAndRecursion()
    {
        const string code =
            "factor = 2\n"
            + "def calculate(value):\n"
            + "    local = value * factor\n"
            + "    if local > 10:\n"
            + "        return local\n"
            + "    return 0\n"
            + "def factorial(value):\n"
            + "    if value <= 1:\n"
            + "        return 1\n"
            + "    return value * factorial(value - 1)\n"
            + "print(calculate(21), calculate(2), factorial(6))\n";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 0 720{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesEachRecursiveFramesLocalSlots()
    {
        const string code =
            "def preserve(value):\n"
            + "    first = value\n"
            + "    second = value + 100\n"
            + "    if value > 0:\n"
            + "        preserve(value - 1)\n"
            + "    return first + second\n"
            + "print(preserve(6))\n";
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"112{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesDefinedFunctionsAcrossEngineCalls()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();

        var definition = engine.Execute(
            "def double(value): return value * 2",
            "definition.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var call = engine.Execute(
            "print(double(21))",
            "call.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(definition.Success);
        Assert.True(call.Success);
        Assert.Equal($"42{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_ReturnsNoneWhenFunctionFallsThrough()
    {
        using var output = new StringWriter();

        var result = new ManagedPythonEngine().Execute(
            "def procedure(value):\n    value = value + 1\nprint(procedure(4))",
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"None{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_TreatsFunctionsAsFirstClassIdentityValues()
    {
        using var output = new StringWriter();
        const string code =
            "def double(value): return value * 2\n"
            + "def apply(function, value): return function(value)\n"
            + "alias = double\n"
            + "print(apply(alias, 21), alias == double, alias != double)";

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 True False{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void Execute_PreservesCallerValuesAcrossNestedFunctionFrames()
    {
        using var output = new StringWriter();
        const string code =
            "def double(value): return value * 2\n"
            + "def add(left, right): return left + right\n"
            + "print(1 + double(20) + 1, add(1, add(20, 21)))";

        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal($"42 42{Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("print(missing)", "DPY4002")]
    [InlineData("print(1 / 0)", "DPY4004")]
    [InlineData("print(0 ** -1)", "DPY4004")]
    [InlineData("print('a' - 'b')", "DPY4005")]
    [InlineData("value = 1\ndef invalid():\n    print(value)\n    value = 2\ninvalid()", "DPY4008")]
    [InlineData("def add(left, right): return left + right\nadd(1)", "DPY4009")]
    [InlineData("def outer(value):\n    def inner(): return value\n    return inner()", "DPY3101")]
    public void Execute_ReturnsRuntimeDiagnostics(string code, string expectedCode)
    {
        var result = new ManagedPythonEngine().Execute(
            code,
            "<test>",
            TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
    }

    [Fact]
    public void Execute_EnforcesInstructionLimit()
    {
        var options = new ManagedExecutionOptions { InstructionLimit = 1 };

        var result = new ManagedPythonEngine().Execute(
            "print(42)",
            "<test>",
            TextWriter.Null,
            options,
            TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4001", diagnostic.Code);
    }

    [Fact]
    public void Execute_StopsAnInfiniteWhileLoopAtTheInstructionLimit()
    {
        var options = new ManagedExecutionOptions { InstructionLimit = 20 };

        var result = new ManagedPythonEngine().Execute(
            "while True:\n    value = 1\n",
            "<test>",
            TextWriter.Null,
            options,
            TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4001", diagnostic.Code);
    }

    [Fact]
    public void Execute_StopsUnboundedRecursionAtTheInstructionLimit()
    {
        var options = new ManagedExecutionOptions { InstructionLimit = 40 };

        var result = new ManagedPythonEngine().Execute(
            "def recurse(): return recurse()\nrecurse()",
            "<test>",
            TextWriter.Null,
            options,
            TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY4001", diagnostic.Code);
    }

    [Fact]
    public void Execute_ObservesCancellationBeforeParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ManagedPythonEngine().Execute(
                "print(42)",
                "<test>",
                TextWriter.Null,
                cancellationToken: cancellation.Token
            )
        );
    }
}
