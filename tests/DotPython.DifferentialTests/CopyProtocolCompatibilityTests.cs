using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CopyProtocolCompatibilityTests
{
    [Fact]
    public Task CopyUsesTheHookDefinedOnTheType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            class C:
                def __copy__(self):
                    return 42
            print(copy.copy(C()))
            """
        );

    [Fact]
    public Task CopyResolvesHooksThroughTheMroButNotTheInstanceDictionary() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            class Base:
                def __copy__(self):
                    return 'base hook'
            class Derived(Base):
                pass
            class Disabled:
                def __copy__(self): return 1
                __copy__ = None
            class Slotted:
                __slots__ = ('__dict__',)
            instance = Slotted()
            instance.__copy__ = lambda: 99
            print(copy.copy(Derived()))
            print(type(copy.copy(Disabled())).__name__)
            print(type(copy.copy(instance)).__name__, copy.copy(instance) is instance)
            """
        );

    [Fact]
    public Task DeepCopyResolvesTheHookOnTheInstanceAndSharesTheMemo() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            class Slotted:
                __slots__ = ('__dict__',)
            instance = Slotted()
            instance.__deepcopy__ = lambda memo: 77
            print(copy.deepcopy(instance))

            class Node:
                def __init__(self, child=None):
                    self.child = child
                    self.seen = []
                def __deepcopy__(self, memo):
                    if id(self) in memo:
                        return memo[id(self)]
                    result = Node.__new__(Node)
                    memo[id(self)] = result
                    result.child = copy.deepcopy(self.child, memo)
                    result.seen = copy.deepcopy(self.seen, memo)
                    return result
            node = Node()
            node.child = node
            node.seen.append(node)
            copied = copy.deepcopy(node)
            print(copied is copied.child, copied is not node)
            print(copied.seen[0] is copied)
            """
        );

    [Fact]
    public Task DeepCopyAcceptsAnExplicitMemoAndStillCopiesPlainInstances() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            class C:
                def __init__(self):
                    self.value = [1, 2]
            print(copy.deepcopy([1, [2]], None))
            memo = {}
            print(copy.deepcopy([1, [2]], memo), len(memo) > 0)
            original = C()
            shallow = copy.copy(original)
            deep = copy.deepcopy(original)
            print(shallow.value is original.value, deep.value is original.value)
            print(shallow.value == original.value, deep.value == original.value)
            """
        );

    [Fact]
    public Task DeepCopyHookSeesAConsistentMemoAcrossRepeatedCalls() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            class Marker:
                def __init__(self, name):
                    self.name = name
                def __deepcopy__(self, memo):
                    if id(self) in memo:
                        return memo[id(self)]
                    result = Marker(self.name)
                    memo[id(self)] = result
                    return result
            shared = Marker('shared')
            shared.seen = []
            memo = {}
            box = [shared, shared]
            copied_box = copy.deepcopy(box, memo)
            print(copied_box[0] is copied_box[1], copied_box[0] is not shared)
            print(copied_box[0].name)
            """
        );
}
