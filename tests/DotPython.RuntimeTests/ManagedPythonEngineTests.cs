using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ManagedPythonEngineTests
{
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

    [Theory]
    [InlineData("print(missing)", "DPY4002")]
    [InlineData("print(1 / 0)", "DPY4004")]
    [InlineData("print(0 ** -1)", "DPY4004")]
    [InlineData("print('a' - 'b')", "DPY4005")]
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
