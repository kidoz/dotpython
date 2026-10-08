using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CopyProtocolExecutionTests
{
    [Fact]
    public void CopyUsesTheCopyHookDefinedOnTheType()
    {
        var output = Run(
            """
            import copy
            class C:
                def __copy__(self):
                    return 42
            print(copy.copy(C()))
            """
        );

        Assert.Equal($"42{Environment.NewLine}", output);
    }

    [Fact]
    public void CopyUsesACopyHookInheritedFromABase()
    {
        var output = Run(
            """
            import copy
            class Base:
                def __copy__(self):
                    return 'base hook'
            class Derived(Base):
                pass
            print(copy.copy(Derived()))
            """
        );

        Assert.Equal($"base hook{Environment.NewLine}", output);
    }

    [Fact]
    public void CopyIgnoresACopyHookStoredOnlyOnTheInstance()
    {
        // `copy.copy` resolves `__copy__` on the type, so an instance attribute of the
        // same name never stands in for the hook.
        var output = Run(
            """
            import copy
            class C:
                __slots__ = ('__dict__',)
            instance = C()
            instance.__copy__ = lambda: 99
            result = copy.copy(instance)
            print(type(result).__name__, result is instance)
            """
        );

        Assert.Equal($"C False{Environment.NewLine}", output);
    }

    [Fact]
    public void CopyHookSetToNoneIsTreatedAsAbsent()
    {
        var output = Run(
            """
            import copy
            class C:
                def __copy__(self): return 1
                __copy__ = None
            print(type(copy.copy(C())).__name__)
            """
        );

        Assert.Equal($"C{Environment.NewLine}", output);
    }

    [Fact]
    public void DeepCopyResolvesTheHookOnTheInstanceRatherThanTheType()
    {
        // The counterpart of the shallow rule: `copy.deepcopy` uses a plain
        // `getattr` on the instance, so an instance-dictionary hook does apply.
        var output = Run(
            """
            import copy
            class C:
                __slots__ = ('__dict__',)
            instance = C()
            instance.__deepcopy__ = lambda memo: 77
            print(copy.deepcopy(instance))
            """
        );

        Assert.Equal($"77{Environment.NewLine}", output);
    }

    [Fact]
    public void DeepCopyHookReceivesTheMemoAndCanBreakCycles()
    {
        var output = Run(
            """
            import copy
            class Node:
                def __init__(self, child=None):
                    self.child = child
                def __deepcopy__(self, memo):
                    if id(self) in memo:
                        return memo[id(self)]
                    result = Node.__new__(Node)
                    memo[id(self)] = result
                    result.child = copy.deepcopy(self.child, memo)
                    return result
            node = Node()
            node.child = node
            copy_of_node = copy.deepcopy(node)
            print(copy_of_node is copy_of_node.child, copy_of_node is not node)
            """
        );

        Assert.Equal($"True True{Environment.NewLine}", output);
    }

    [Fact]
    public void DeepCopyAcceptsAnExplicitMemoWhichItPopulates()
    {
        var output = Run(
            """
            import copy
            print(copy.deepcopy([1, [2]], None))
            memo = {}
            print(copy.deepcopy([1, [2]], memo), len(memo) > 0)
            """
        );

        Assert.Equal(string.Join(Environment.NewLine, "[1, [2]]", "[1, [2]] True", ""), output);
    }

    [Fact]
    public void CopyWithoutAHookStillCopiesOrdinaryInstances()
    {
        var output = Run(
            """
            import copy
            class C:
                def __init__(self):
                    self.value = [1, 2]
            original = C()
            shallow = copy.copy(original)
            deep = copy.deepcopy(original)
            print(shallow.value is original.value, deep.value is original.value)
            print(shallow.value == original.value, deep.value == original.value)
            """
        );

        Assert.Equal(string.Join(Environment.NewLine, "True False", "True True", ""), output);
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "copy_protocol_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
