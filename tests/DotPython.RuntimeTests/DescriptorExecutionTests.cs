using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DescriptorExecutionTests
{
    [Theory]
    [InlineData("__get__", "class C: value = D()\nC().value")]
    [InlineData("__set_name__", "class C: value = D()")]
    public void DescriptorCallbacksShareInstructionBudgetAndUnwind(string hook, string operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var source =
            "class D:\n"
            + $"    def {hook}(self, *args):\n"
            + "        try:\n"
            + "            while True: pass\n"
            + "        except Exception:\n"
            + "            print('swallowed')\n"
            + "        finally:\n"
            + "            print('inner cleanup')\n"
            + "try:\n"
            + string.Join('\n', operation.Split('\n').Select(line => "    " + line))
            + "\nexcept Exception:\n"
            + "    print('outer swallowed')\n"
            + "finally:\n"
            + "    print('outer cleanup')\n";
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();

        var result = engine.Execute(
            source,
            "descriptor_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            $"inner cleanup{Environment.NewLine}outer cleanup{Environment.NewLine}",
            output.ToString()
        );
        var subsequent = engine.Execute(
            "print('recovered')",
            "after.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.EndsWith(
            $"recovered{Environment.NewLine}",
            output.ToString(),
            StringComparison.Ordinal
        );
    }
}
