using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CustomMroExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomMroAndItsReturnedIteratorShareInstructionBudgetAndUnwind(bool iterator)
    {
        var source =
            "class Meta(type):\n"
            + "    def mro(cls):\n"
            + (iterator ? "        yield cls\n" : "")
            + "        try:\n"
            + "            while True: pass\n"
            + "        except Exception:\n"
            + "            print('swallowed')\n"
            + "        finally:\n"
            + "            print('inner cleanup')\n"
            + "try:\n"
            + "    class Example(metaclass=Meta): pass\n"
            + "except Exception:\n"
            + "    print('outer swallowed')\n"
            + "finally:\n"
            + "    print('outer cleanup')\n";
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();

        var result = engine.Execute(
            source,
            "custom_mro_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            $"inner cleanup{Environment.NewLine}outer cleanup{Environment.NewLine}",
            output.ToString()
        );
        using var subsequentOutput = new StringWriter();
        var subsequent = engine.Execute(
            "class Recovered: pass\nprint(Recovered.__name__, 'Example' in globals())",
            "after.py",
            subsequentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.Equal($"Recovered False{Environment.NewLine}", subsequentOutput.ToString());
    }

    [Fact]
    public void SavedCustomMroCallbacksUseCurrentExecutionAndRetainBaseIdentity()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            """
            class Meta(type):
                def mro(cls):
                    print('mro', cls.__name__)
                    return super().mro()
            class Base(metaclass=Meta):
                value = 7
            saved_meta = Meta
            saved_base = Base
            """,
            "before.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        Assert.Equal($"mro Base{Environment.NewLine}", firstOutput.ToString());
        var previousOutput = firstOutput.ToString();
        using var secondOutput = new StringWriter();

        var second = engine.Execute(
            """
            class Child(saved_base): pass
            Dynamic = saved_meta('Dynamic', (saved_base,), {})
            print(Child.__mro__ == (Child, saved_base, object))
            print(Dynamic.__mro__ == (Dynamic, saved_base, object))
            print(type(Child) is saved_meta, type(Dynamic) is saved_meta)
            print(Child().value, Dynamic().value)
            """,
            "after.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(second.Success);
        Assert.Equal(previousOutput, firstOutput.ToString());
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "mro Child",
                "mro Dynamic",
                "True",
                "True",
                "True True",
                "7 7",
                ""
            ),
            secondOutput.ToString()
        );
    }
}
