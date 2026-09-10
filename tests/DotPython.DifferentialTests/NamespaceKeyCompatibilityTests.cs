using Xunit;

namespace DotPython.DifferentialTests;

public sealed class NamespaceKeyCompatibilityTests
{
    [Fact]
    public Task EqualNonStringGlobalKeysShareReadWriteAndDeleteStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            hash_calls = []
            class Key:
                def __hash__(self):
                    hash_calls.append('hash')
                    return hash('injected')
                def __eq__(self, other):
                    return other == 'injected'
            key = Key()
            namespace = globals()
            namespace[key] = 42
            def read():
                return injected
            for i in range(20):
                read()
            print(read(), namespace['injected'], len(hash_calls))
            injected = 7
            print(read(), namespace['injected'], len(hash_calls))
            del injected
            try:
                read()
            except NameError:
                print('deleted')
            namespace[key] = 99
            print(read(), len(hash_calls))
            """
        );

    [Fact]
    public Task MutableKeyEqualityInvalidatesGlobalAndBuiltinLookupCaches() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self, name):
                    self.name = name
                    self.enabled = True
                def __hash__(self):
                    return hash(self.name)
                def __eq__(self, other):
                    return self.enabled and other == self.name
            namespace = globals()
            value_key = Key('injected')
            namespace[value_key] = 42
            def read():
                return injected
            for i in range(20):
                read()
            value_key.enabled = False
            try:
                read()
            except NameError:
                print('equality changed')
            value_key.enabled = True
            print(read())
            builtin_key = Key('len')
            builtin_key.enabled = False
            namespace[builtin_key] = lambda value: 99
            def measure():
                return len([1, 2])
            for i in range(20):
                measure()
            print(measure())
            builtin_key.enabled = True
            print(measure())
            builtin_key.enabled = False
            print(measure())
            """
        );

    [Fact]
    public Task EqualNonStringInstanceKeysShareAttributeStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __hash__(self): return hash('value')
                def __eq__(self, other): return other == 'value'
            class Item:
                pass
            item = Item()
            key = Key()
            item.__dict__[key] = 5
            print(item.value)
            item.value = 7
            print(item.__dict__['value'], len(item.__dict__))
            del item.value
            print(item.__dict__)
            """
        );

    [Fact]
    public Task KeyEqualityCanMutateTheNamespaceDuringLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self): self.changed = False
                def __hash__(self): return hash('injected')
                def __eq__(self, other):
                    if not self.changed:
                        self.changed = True
                        globals()['side_effect'] = 7
                    return other == 'injected'
            namespace = globals()
            namespace[Key()] = 42
            print(injected, side_effect)
            """
        );

    [Fact]
    public Task ShallowDictionaryCopiesPreserveInsertionHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            calls = []
            class Key:
                def __hash__(self):
                    calls.append('hash')
                    return hash('value')
                def __eq__(self, other): return other == 'value'
            key = Key()
            original = {key: 42}
            method_copy = original.copy()
            module_copy = copy.copy(original)
            print(original['value'], method_copy['value'], module_copy['value'])
            print(len(calls))
            method_copy['value'] = 7
            print(original['value'], method_copy['value'], module_copy['value'])
            print(len(calls))
            """
        );
}
