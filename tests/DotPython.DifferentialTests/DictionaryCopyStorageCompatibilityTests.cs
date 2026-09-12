using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DictionaryCopyStorageCompatibilityTests
{
    [Fact]
    public Task ShallowCopiesPreserveSparseEntryPositionsAtTheFastCopyDensityBoundary() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            for count in (3, 5, 6, 10, 11):
                for removed in range(1, count):
                    for copier in (lambda source: source.copy(), copy.copy):
                        source = {}
                        for index in range(count): source[str(index)] = index
                        for index in range(removed): del source[str(index)]
                        target = copier(source)
                        iterator = iter(target)
                        key = next(iterator)
                        del target[key]
                        target['new'] = 99
                        print(count, removed, key, list(source))
                        for attempt in range(len(target) + 1):
                            try: print(next(iterator))
                            except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task ReverseCursorsOnCopiesRetainHolesAndCompactionThresholds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            for count in (3, 5, 6, 10, 11):
                for removed in (1, count // 2, count - 1):
                    for copier in (lambda source: source.copy(), copy.copy):
                        source = {}
                        for index in range(count): source[str(index)] = index
                        for index in range(removed): del source[str(index)]
                        target = copier(source)
                        iterator = reversed(target.items())
                        print(count, removed, next(iterator))
                        del target[str(removed)]
                        target['new'] = 99
                        print(iterator.__length_hint__(), list(iterator), list(target.items()))
            """
        );

    [Fact]
    public Task CopiesOfPopitemTrimmedTablesPreserveTheirSpentInsertionCapacity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            for count in (5, 10, 11, 21):
                for removed in (1, count // 2, count - 2):
                    for copier in (lambda source: source.copy(), copy.copy, dict):
                        source = {}
                        for index in range(count): source[str(index)] = index
                        for index in range(removed): source.popitem()
                        target = copier(source)
                        iterator = iter(target)
                        print(count, removed, next(iterator))
                        del target['0']
                        target['new'] = 99
                        for attempt in range(len(target) + 1):
                            try: print(next(iterator))
                            except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task CopyingAnEmptiedDictionaryStartsWithFreshStringKeyStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            for copier in (lambda source: source.copy(), copy.copy, dict):
                for cleared in (False, True):
                    source = {}
                    for index in range(10): source[index] = index
                    if cleared: source.clear()
                    else:
                        for index in range(10): del source[index]
                    target = copier(source)
                    for index in range(3): target[str(index)] = index
                    iterator = iter(target)
                    print(cleared, next(iterator), source)
                    del target['0']
                    target[42] = 42
                    print(list(iterator), list(target.items()))
            """
        );

    [Fact]
    public Task DictionaryConstructionAndCopyUseDifferentSparseStoragePaths() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for backward in (False, True):
                for mode in ('copy', 'construct', 'update'):
                    source = {'a': 1, 'b': 2, 'c': 3, 'd': 4, 'e': 5}
                    del source['a']
                    if mode == 'copy': target = source.copy()
                    elif mode == 'construct': target = dict(source)
                    else:
                        target = {}
                        target.update(source)
                    iterator = reversed(target) if backward else iter(target)
                    print(backward, mode, next(iterator))
                    del target['b']
                    target['z'] = 9
                    for attempt in range(5):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task DirectUpdatesCloneEligibleCleanSourcesButProxyUpdatesInsertIncrementally() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for count in (5, 10, 11, 21):
                for mode in ('direct', 'proxy', 'pairs'):
                    source = {}
                    for index in range(count): source[str(index)] = index
                    source.popitem()
                    target = {}
                    incoming = source if mode == 'direct' else MappingProxyType(source) if mode == 'proxy' else source.items()
                    target.update(incoming)
                    iterator = iter(target)
                    print(count, mode, next(iterator))
                    del target['0']
                    target['new'] = 99
                    for attempt in range(len(target) + 1):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task UpdatingAnEmptyButPreviouslyAllocatedTargetRespectsCloneEligibility() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source_count in (4, 5, 6, 10):
                for emptied in ('clear', 'delete', 'popitem'):
                    source = {}
                    for index in range(source_count): source[str(index)] = index
                    source.popitem()
                    target = {}
                    for index in range(11): target[index] = index
                    if emptied == 'clear': target.clear()
                    elif emptied == 'delete':
                        for index in range(11): del target[index]
                    else:
                        for index in range(11): target.popitem()
                    target.update(source)
                    iterator = iter(target)
                    print(source_count, emptied, next(iterator))
                    del target['0']
                    target['new'] = 99
                    for attempt in range(len(target) + 1):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task OverlapPresizingChangesTheLaterCompactionBoundary() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for mode in ('direct', 'proxy', 'pairs'):
                for churn in (0, 3, 4, 5, 9, 10, 14, 15, 16):
                    target = {}
                    incoming = {}
                    for index in range(5): target[str(index)] = index
                    for index in range(6): incoming[str(index)] = index + 10
                    source = incoming if mode == 'direct' else MappingProxyType(incoming) if mode == 'proxy' else incoming.items()
                    target.update(source)
                    for index in range(churn):
                        del target[next(iter(target))]
                        target['churn' + str(index)] = index
                    iterator = iter(target)
                    first = next(iterator)
                    del target[first]
                    target['probe'] = 99
                    print(mode, churn, first)
                    for attempt in range(7):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task UnionClonesTheLeftOperandWhileUnpackingMergesIntoANewDictionary() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for mode in ('union', 'unpack', 'proxy union', 'proxy unpack'):
                for backward in (False, True):
                    source = {'a': 1, 'b': 2, 'c': 3, 'd': 4, 'e': 5}
                    del source['a']
                    proxy = MappingProxyType(source)
                    if mode == 'union': target = source | {}
                    elif mode == 'unpack': target = {**source}
                    elif mode == 'proxy union': target = proxy | {}
                    else: target = {**proxy}
                    iterator = reversed(target) if backward else iter(target)
                    print(mode, backward, next(iterator))
                    del target['b']
                    target['new'] = 99
                    for attempt in range(5):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task UnionAndUnpackingUseBulkPresizingForLaterOverlappingOperands() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for mode in ('union', 'unpack', 'inplace'):
                for churn in (4, 5, 14, 15, 16):
                    left = {}
                    right = {}
                    for index in range(5): left[str(index)] = index
                    for index in range(6): right[str(index)] = index + 10
                    if mode == 'union': target = left | right
                    elif mode == 'unpack': target = {**left, **right}
                    else:
                        target = left
                        target |= right
                    for index in range(churn):
                        del target[next(iter(target))]
                        target['churn' + str(index)] = index
                    iterator = reversed(target)
                    print(mode, churn, next(iterator))
                    del target[next(iter(target))]
                    target['probe'] = 99
                    print(list(iterator), list(target.items()))
            """
        );

    [Fact]
    public Task EmptyAndSelfUpdatesDoNotCompactExistingCursorHoles() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for backward in (False, True):
                for mode in ('self', 'empty', 'empty proxy'):
                    target = {'a': 1, 'b': 2, 'c': 3}
                    del target['a']
                    iterator = reversed(target.items()) if backward else iter(target.items())
                    print(backward, mode, next(iterator))
                    source = target if mode == 'self' else {} if mode == 'empty' else MappingProxyType({})
                    target.update(source)
                    print(list(iterator), list(target.items()))
            """
        );

    [Fact]
    public Task ShallowCopiesShareValuesAndKeysButOwnTheirEntryValuesAndMutations() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            for copier in (lambda source: source.copy(), copy.copy, dict):
                shared = []
                source = {'a': shared, 'b': shared, 'c': shared, 'd': shared, 'e': shared}
                del source['a']
                target = copier(source)
                print(target is source, target['b'] is source['b'])
                target['b'].append(7)
                target['c'] = [8]
                del target['d']
                target['new'] = [9]
                print(source, target)
            """
        );

    [Fact]
    public Task DirectCopiesAndMergesReuseCachedHashesWithoutCallingUserHash() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self): events.append(self.name); return self.code
                def __repr__(self): return self.name
            keys = [Key('a', 1), Key('b', 2), Key('c', 3), Key('d', 4), Key('e', 5)]
            source = {}
            for key in keys: source[key] = key.name
            for mode in ('copy', 'shallow', 'construct', 'update', 'union', 'unpack'):
                events.clear()
                if mode == 'copy': target = source.copy()
                elif mode == 'shallow': target = copy.copy(source)
                elif mode == 'construct': target = dict(source)
                elif mode == 'update':
                    target = {}
                    target.update(source)
                elif mode == 'union': target = {} | source
                else: target = {**source}
                print(mode, events, list(target.items()), next(iter(target)) is keys[0])
            """
        );

    [Fact]
    public Task ProxyAndPairUpdatesUseTheGenericMappingAndIterableHashCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self): events.append(self.name); return self.code
                def __repr__(self): return self.name
            source = {}
            for index in range(3):
                key = Key('key' + str(index), index)
                source[key] = index
            for mode in ('direct', 'proxy', 'pairs', 'proxy construct', 'proxy unpack'):
                events.clear()
                target = {}
                if mode == 'direct': target.update(source)
                elif mode == 'proxy': target.update(MappingProxyType(source))
                elif mode == 'pairs': target.update(source.items())
                elif mode == 'proxy construct': target = dict(MappingProxyType(source))
                else: target = {**MappingProxyType(source)}
                print(mode, events, list(target.items()))
            """
        );

    [Fact]
    public Task UpdatingEqualKeysRetainsTheOriginalKeyObjectAndUpdatesUnconsumedValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self): events.append('hash ' + self.name); return self.code
                def __eq__(self, other): events.append('equal ' + self.name + ' ' + other.name); return self.code == other.code
                def __repr__(self): return self.name
            old = Key('old', 1)
            new = Key('new', 1)
            for backward in (False, True):
                target = {old: 1, 'tail': 2}
                incoming = {new: 9}
                iterator = reversed(target.items()) if backward else iter(target.items())
                events.clear()
                target.update(incoming)
                print(backward, events, list(iterator), next(iter(target)) is old)
            """
        );

    [Fact]
    public Task InPlaceProxyMergesKeepGenericHashCallbacksAndInsertionHistory() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 7
                def __repr__(self): return 'key'
            key = Key()
            source = {key: 1}
            target = {}
            alias = target
            events.clear()
            target |= MappingProxyType(source)
            print(target is alias, events, list(target.items()))
            for mode in ('direct', 'proxy'):
                source = {}
                for index in range(5): source[str(index)] = index
                source.popitem()
                target = {}
                target |= source if mode == 'direct' else MappingProxyType(source)
                iterator = iter(target)
                print(mode, next(iterator))
                del target['0']
                target['new'] = 99
                for attempt in range(5):
                    try: print(next(iterator))
                    except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task DirectMergeDetectsSourceSizeChangesAfterAnEqualityCallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name): self.name = name
                def __hash__(self): return 7
                def __eq__(self, other):
                    events.append(self.name + ' ' + other.name)
                    incoming.clear()
                    return False
                def __repr__(self): return self.name
            old = Key('old')
            new = Key('new')
            target = {old: 1}
            incoming = {new: 2}
            try: target.update(incoming)
            except RuntimeError as error: print(str(error))
            print(events, list(target.items()), incoming)
            """
        );

    [Fact]
    public Task CustomMappingMergesPreserveLookupOrderAndPartialEffectsOnFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Mapping:
                def keys(self): events.append('keys'); return ['a', 'b', 'c']
                def __getitem__(self, key):
                    events.append('get ' + key)
                    if fail and key == 'b': raise KeyError(key)
                    return key + '!'
            for fail in (False, True):
                for mode in ('update', 'unpack', 'inplace', 'construct'):
                    events.clear()
                    target = {'a': 'old', 'seed': 'original'}
                    source = Mapping()
                    try:
                        if mode == 'update': target.update(source)
                        elif mode == 'unpack': target = {**target, **source}
                        elif mode == 'inplace': target |= source
                        else: target = dict(source)
                    except KeyError as error: print(error.args)
                    print(fail, mode, events, target)
            """
        );
}
