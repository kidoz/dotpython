using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TypeMetadataExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DocumentationDescriptorCallbacksShareInstructionBudgetAndUnwind(bool direct)
    {
        var source =
            "class Documentation:\n"
            + "    def __get__(self, instance, owner):\n"
            + "        try:\n"
            + "            while True: pass\n"
            + "        except Exception:\n"
            + "            print('swallowed')\n"
            + "        finally:\n"
            + "            print('inner cleanup')\n"
            + "class Example:\n"
            + "    __doc__ = Documentation()\n"
            + "try:\n"
            + (
                direct
                    ? "    result = type.__dict__['__doc__'].__get__(Example)\n"
                    : "    result = Example.__doc__\n"
            )
            + "except Exception:\n"
            + "    print('outer swallowed')\n"
            + "finally:\n"
            + "    print('outer cleanup')\n";
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();

        var result = engine.Execute(
            source,
            "type_metadata_budget.py",
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
            "Example.__doc__ = 'recovered'\nprint(Example.__doc__, 'result' in globals())",
            "after.py",
            subsequentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.Equal($"recovered False{Environment.NewLine}", subsequentOutput.ToString());
    }

    [Fact]
    public void SavedMetadataDescriptorsUseCurrentExecutionAndPreserveLiveNamespace()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            """
            class Documentation:
                def __get__(self, instance, owner):
                    print('documentation', owner.__name__, instance is None)
                    return owner.value
            class Example:
                __doc__ = Documentation()
                value = 7
            saved_doc = type.__dict__['__doc__']
            saved_name = type.__dict__['__name__']
            namespace = type.__dict__['__dict__'].__get__(Example)
            print(saved_doc.__get__(Example))
            """,
            "before.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        Assert.Equal(
            $"documentation Example True{Environment.NewLine}7{Environment.NewLine}",
            firstOutput.ToString()
        );
        var previousOutput = firstOutput.ToString();
        using var secondOutput = new StringWriter();

        var second = engine.Execute(
            """
            saved_name.__set__(Example, 'Renamed')
            Example.value = 8
            print(saved_doc.__get__(Example))
            print(namespace['value'], saved_name is type.__dict__['__name__'])
            saved_doc.__set__(Example, 'replacement')
            print(saved_doc.__get__(Example), namespace['__doc__'])
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
                "documentation Renamed True",
                "8",
                "8 True",
                "replacement replacement",
                ""
            ),
            secondOutput.ToString()
        );
    }
}
