using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

/// <summary>
/// The instruction limit bounds the program, not the runtime's own tables. Building a
/// module's globals — and the type tables the runtime publishes — is work no source line
/// asked for, so a budget too small for it must neither spend the program's budget nor
/// stop the construction half way, which a type initializer would then cache for the rest
/// of the process. Reproduce the poisoning directly with a fresh process:
/// `DotPython.Cli --instruction-limit 200` on a script whose first statement imports
/// `collections.abc`.
/// </summary>
public sealed class SetupBudgetExecutionTests
{
    [Fact]
    public void NativeModuleInitializationRunsOutsideTheProgramsBudget()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "import collections\nimport collections.abc\nprint('ok')",
            "<setup-budget>",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        Assert.Equal(Lines("ok"), output.ToString());
    }

    /// <summary>The program's own work is still what the budget stops.</summary>
    [Fact]
    public void TheProgramsOwnWorkStillStopsAtTheBudget()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "total = 0\nfor step in range(1000):\n    total = total + step\nprint(total)",
            "<setup-budget>",
            output,
            new ManagedExecutionOptions { InstructionLimit = 20 },
            TestContext.Current.CancellationToken
        );
        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("instruction limit", StringComparison.Ordinal)
        );
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
