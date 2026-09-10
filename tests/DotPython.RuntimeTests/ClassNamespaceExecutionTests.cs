using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ClassNamespaceExecutionTests
{
    [Theory]
    [InlineData("__getitem__", "value = blocked")]
    [InlineData("__setitem__", "blocked = 1")]
    [InlineData("__delitem__", "del blocked")]
    public void NamespaceCallbacksShareInstructionBudgetAndDoNotMaskHostFailures(
        string hook,
        string operation
    )
    {
        var source =
            "class Namespace:\n"
            + "    def __init__(self): self.data = {}\n"
            + (
                hook == "__getitem__"
                    ? ""
                    : "    def __getitem__(self, key): return self.data[key]\n"
            )
            + (
                hook == "__setitem__"
                    ? ""
                    : "    def __setitem__(self, key, value): self.data[key] = value\n"
            )
            + $"    def {hook}(self, key, *args):\n"
            + "        if key != 'blocked':\n"
            + (
                hook == "__getitem__"
                    ? "            return self.data[key]\n"
                    : "            self.data[key] = args[0]\n            return\n"
            )
            + "        try:\n"
            + "            while True: pass\n"
            + "        except Exception:\n"
            + "            print('swallowed')\n"
            + "        finally:\n"
            + "            print('inner cleanup')\n"
            + "class Factory:\n"
            + "    def __prepare__(self, name, bases): return Namespace()\n"
            + "    def __call__(self, name, bases, namespace): return 42\n"
            + "try:\n"
            + "    class Example(metaclass=Factory()):\n"
            + $"        {operation}\n"
            + "except Exception:\n"
            + "    print('outer swallowed')\n"
            + "finally:\n"
            + "    print('outer cleanup')\n";
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "namespace_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            $"inner cleanup{Environment.NewLine}outer cleanup{Environment.NewLine}",
            output.ToString()
        );
        var subsequent = engine.Execute(
            "class Recovered: pass\nprint(Recovered.__name__, 'Example' in globals())",
            "after.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.EndsWith(
            $"Recovered False{Environment.NewLine}",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void SavedCallableMetaclassAndNamespaceUseCurrentExecution()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            """
            class Namespace:
                def __init__(self): self.data = {}
                def __getitem__(self, key): return self.data[key]
                def __setitem__(self, key, value):
                    if key == 'value': print('store', value)
                    self.data[key] = value
            class Factory:
                def __init__(self): self.namespace = Namespace()
                def __prepare__(self, name, bases): return self.namespace
                def __call__(self, name, bases, namespace): return namespace.data['value']
            saved_factory = Factory()
            saved_namespace = saved_factory.namespace
            class First(metaclass=saved_factory): value = 1
            """,
            "before.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        Assert.Equal($"store 1{Environment.NewLine}", firstOutput.ToString());
        using var secondOutput = new StringWriter();
        var second = engine.Execute(
            """
            class Second(metaclass=saved_factory): value = 2
            print(First, Second, saved_factory.namespace is saved_namespace)
            """,
            "after.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(second.Success);
        Assert.Equal($"store 1{Environment.NewLine}", firstOutput.ToString());
        Assert.Equal(
            $"store 2{Environment.NewLine}1 2 True{Environment.NewLine}",
            secondOutput.ToString()
        );
    }
}
