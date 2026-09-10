using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DynamicBasesExecutionTests
{
    [Fact]
    public void SlotDeclarationsRejectBaseMutationWithoutChangingHierarchy()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Base: pass
            class Other: pass
            class WithSlots(Base):
                __slots__ = ()
            class Ordinary(Base): pass
            for target, bases in ((WithSlots, (Other,)), (Ordinary, (WithSlots,))):
                old_bases = target.__bases__
                old_mro = target.__mro__
                try: target.__bases__ = bases
                except TypeError as error: print(str(error))
                print(target.__bases__ is old_bases, target.__mro__ is old_mro)
            """,
            "dynamic_bases_slots_boundary.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Changing bases of classes with __slots__ is not supported in this runtime slice.",
                "True True",
                "Changing bases of classes with __slots__ is not supported in this runtime slice.",
                "True True",
                ""
            ),
            output.ToString()
        );
    }

    [Fact]
    public void CancellationDuringDescendantMroRestoresHierarchyAndAllowsLaterExecution()
    {
        var engine = new ManagedPythonEngine();
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                """
                active = False
                class Before:
                    value = 'before'
                class After:
                    value = 'after'
                class Meta(type):
                    def mro(cls):
                        if active and cls.__name__ == 'Child':
                            try:
                                print('cancel descendant')
                                return type.mro(cls)
                            finally:
                                print('inner cleanup')
                        return type.mro(cls)
                class Example(Before, metaclass=Meta): pass
                class Child(Example): pass
                instance = Child()
                old_bases = Example.__bases__
                old_mro = Example.__mro__
                old_child = Child.__mro__
                active = True
                try:
                    Example.__bases__ = (After,)
                finally:
                    print('outer cleanup')
                """,
                "cancelled_dynamic_bases.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(
            $"cancel descendant{Environment.NewLine}inner cleanup{Environment.NewLine}outer cleanup{Environment.NewLine}",
            output.ToString()
        );
        using var subsequentOutput = new StringWriter();
        var subsequent = engine.Execute(
            """
            active = False
            print(Example.__bases__ is old_bases, Example.__mro__ is old_mro,
                  Child.__mro__ is old_child, instance.value)
            Example.__bases__ = (After,)
            print(instance.value, Child.__mro__ == (Child, Example, After, object))
            """,
            "after.py",
            subsequentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.Equal(
            $"True True True before{Environment.NewLine}after True{Environment.NewLine}",
            subsequentOutput.ToString()
        );
    }

    [Theory]
    [InlineData("Example")]
    [InlineData("Child")]
    public void BaseChangeCallbacksShareBudgetsAndRestoreHierarchyOnUnwind(string target)
    {
        var source =
            "active = False\n"
            + "class Before:\n"
            + "    value = 'before'\n"
            + "class After:\n"
            + "    value = 'after'\n"
            + "class Meta(type):\n"
            + "    def mro(cls):\n"
            + $"        if active and cls.__name__ == '{target}':\n"
            + "            try:\n"
            + "                while True: pass\n"
            + "            except Exception:\n"
            + "                print('swallowed')\n"
            + "            finally:\n"
            + "                print('inner cleanup')\n"
            + "        return type.mro(cls)\n"
            + "class Example(Before, metaclass=Meta): pass\n"
            + "class Child(Example): pass\n"
            + "old_bases = Example.__bases__\n"
            + "old_mro = Example.__mro__\n"
            + "old_child = Child.__mro__\n"
            + "instance = Child()\n"
            + "active = True\n"
            + "try:\n"
            + "    Example.__bases__ = (After,)\n"
            + "except Exception:\n"
            + "    print('outer swallowed')\n"
            + "finally:\n"
            + "    print('outer cleanup')\n";
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();

        var result = engine.Execute(
            source,
            "dynamic_bases_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 1000 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            $"inner cleanup{Environment.NewLine}outer cleanup{Environment.NewLine}",
            output.ToString()
        );
        using var subsequentOutput = new StringWriter();
        var subsequent = engine.Execute(
            """
            active = False
            print(Example.__bases__ is old_bases, Example.__mro__ is old_mro,
                  Child.__mro__ is old_child, instance.value)
            Example.__bases__ = (After,)
            print(instance.value, Child.__mro__ == (Child, Example, After, object))
            """,
            "after.py",
            subsequentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.Equal(
            $"True True True before{Environment.NewLine}after True{Environment.NewLine}",
            subsequentOutput.ToString()
        );
    }

    [Fact]
    public void SavedClassBaseChangesUseCurrentExecutionAndUpdateExistingInstances()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            """
            class Before:
                value = 7
            class After:
                value = 8
            class Meta(type):
                def mro(cls):
                    print('mro', cls.__name__)
                    return super().mro()
            class Example(Before, metaclass=Meta): pass
            class Child(Example): pass
            instance = Child()
            saved_descriptor = type.__dict__['__bases__']
            saved_class = Example
            """,
            "before.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        Assert.Equal(
            $"mro Example{Environment.NewLine}mro Child{Environment.NewLine}",
            firstOutput.ToString()
        );
        var previousOutput = firstOutput.ToString();
        using var secondOutput = new StringWriter();

        var second = engine.Execute(
            """
            saved_descriptor.__set__(saved_class, (After,))
            print(saved_class is Example, instance.value)
            print(Child.__mro__ == (Child, Example, After, object))
            print(isinstance(instance, Before), isinstance(instance, After))
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
                "mro Example",
                "mro Child",
                "True 8",
                "True",
                "False True",
                ""
            ),
            secondOutput.ToString()
        );
    }

    private sealed class CancelAfterFirstLineWriter(CancellationTokenSource cancellation)
        : StringWriter
    {
        private bool _cancelled;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (!_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
        }
    }
}
