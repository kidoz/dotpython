using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class LoopAndWithTargetExecutionTests
{
    [Fact]
    public void ParenthesizedContextManagersBindEveryItem()
    {
        var output = Run(
            """
            class C:
                def __init__(self, n): self.n = n
                def __enter__(self): print('enter', self.n); return self
                def __exit__(self, *a): print('exit', self.n)

            with (C(1) as a, C(2) as b):
                print('body', a.n, b.n)

            with (C(3)):
                print('single item')

            with (C(4) as x,):
                print('trailing comma', x.n)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "enter 1",
                "enter 2",
                "body 1 2",
                "exit 2",
                "exit 1",
                "enter 3",
                "single item",
                "exit 3",
                "enter 4",
                "trailing comma 4",
                "exit 4",
                ""
            ),
            output
        );
    }

    [Fact]
    public void AttributeAndSubscriptLoopTargetsAssign()
    {
        var output = Run(
            """
            class C: pass
            holder = C()
            sequence = [0, 0]

            for holder.x in [1, 2]:
                pass
            for sequence[1] in [9]:
                pass
            for holder.x, sequence[0] in [(8, 7)]:
                pass

            print(holder.x, sequence)
            print([0 for holder.x in [1, 2]])
            print(holder.x)
            """
        );

        Assert.Equal(string.Join(Environment.NewLine, "8 [7, 9]", "[0, 0]", "2", ""), output);
    }

    [Theory]
    [InlineData("for f() in [1]:\n    pass\n", "DPY2001")]
    [InlineData("for c.m() in [1]:\n    pass\n", "DPY2001")]
    public void CallTargetsAreStillRejected(string source, string code)
    {
        // CPython reports its own SyntaxError here; the point is that a call is not
        // silently accepted as a loop target now that the target rule is wider.
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "class C: pass\nc = C()\ndef f(): return [1]\n" + source,
            "loop_and_with_target_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "loop_and_with_target_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
