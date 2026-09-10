using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class MixedMetaclassExecutionTests
{
    [Theory]
    [InlineData("__prepare__")]
    [InlineData("__call__")]
    public void InheritedMixinHooksShareInstructionBudgetAndUnwind(string hook)
    {
        var source =
            "class Mixin:\n"
            + (hook == "__prepare__" ? "    @classmethod\n" : "")
            + $"    def {hook}(self, *args, **kwargs):\n"
            + "        try:\n"
            + "            while True: pass\n"
            + "        except Exception:\n"
            + "            print('swallowed')\n"
            + "        finally:\n"
            + "            print('inner cleanup')\n"
            + "class Meta(Mixin, type): pass\n"
            + "try:\n"
            + "    class Example(metaclass=Meta): pass\n"
            + (hook == "__call__" ? "    instance = Example()\n" : "")
            + "except Exception:\n"
            + "    print('outer swallowed')\n"
            + "finally:\n"
            + "    print('outer cleanup')\n";
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();

        var result = engine.Execute(
            source,
            "mixed_metaclass_budget.py",
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
            "class Recovered: pass\nprint(Recovered.__name__, 'instance' in globals(), 'Example' in globals())",
            "after.py",
            subsequentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.Equal(
            $"Recovered False {(hook == "__call__" ? "True" : "False")}{Environment.NewLine}",
            subsequentOutput.ToString()
        );
    }

    [Fact]
    public void SavedMixedMetaclassCallbacksUseCurrentExecutionAndRetainMroIdentity()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            """
            class Mixin:
                @classmethod
                def __prepare__(meta, name, bases):
                    print('prepare', name)
                    return super().__prepare__(name, bases)
                def __new__(meta, name, bases, namespace):
                    print('new', name)
                    return super().__new__(meta, name, bases, namespace)
                def __call__(cls, value):
                    print('call', cls.__name__)
                    return super().__call__(value)
            class Meta(Mixin, type): pass
            class Base(metaclass=Meta):
                def __init__(self, value): self.value = value
            saved_meta = Meta
            saved_base = Base
            """,
            "before.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        var previousOutput = firstOutput.ToString();
        using var secondOutput = new StringWriter();

        var second = engine.Execute(
            """
            class Child(saved_base): pass
            Dynamic = saved_meta('Dynamic', (saved_base,), {})
            print(type(Child) is saved_meta, type(Dynamic) is saved_meta)
            print(saved_meta.__bases__ == (Mixin, type), saved_meta.__base__ is type)
            print(Child(7).value, Dynamic(8).value)
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
                "prepare Child",
                "new Child",
                "new Dynamic",
                "True True",
                "True True",
                "call Child",
                "call Dynamic",
                "7 8",
                ""
            ),
            secondOutput.ToString()
        );
    }
}
