using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DictionaryConstructionStorageCompatibilityTests
{
    [Fact]
    public Task LiteralPresizingCountsDuplicatePairsBeforeKeysAreCombined() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for target in (
                {'a': 0, 'b': 1, 'c': 2},
                {'a': 0, 'b': 1, 'c': 2, 'a': 3, 'b': 4},
                {'a': 0, 'b': 1, 'c': 2, 'a': 3, 'b': 4, 'c': 5},
                {'a': 0, 'b': 1, 'c': 2, 'a': 3, 'b': 4, 'c': 5, 'a': 6, 'b': 7, 'c': 8, 'a': 9},
                {'a': 0, 'b': 1, 'c': 2, 'a': 3, 'b': 4, 'c': 5, 'a': 6, 'b': 7, 'c': 8, 'a': 9, 'b': 10},
            ):
                for index in range(9):
                    iterator = iter(target)
                    key = next(iterator)
                    del target[key]
                    target['new' + str(index)] = index
                    print(index, key, list(target.items()))
                    for attempt in range(4):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task MixedLiteralKeysSelectGeneralStorageBeforeInsertingAnyPair() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for target in (
                {'a': 0, 'b': 1, 'c': 2, 'd': 3, 'e': 4, 42: 5},
                {'a': 0, 'b': 1, 'c': 2, 'd': 3, 'e': 4, 'f': 5, 'g': 6, 'h': 7, 'i': 8, 'j': 9, 42: 10},
            ):
                for index in range(13):
                    iterator = reversed(target)
                    print(index, next(iterator))
                    del target[next(iter(target))]
                    target['new' + str(index)] = index
                    print(list(iterator), list(target.items()))
            """
        );

    [Fact]
    public Task EqualGeneralLiteralKeysStillSelectGeneralPresizedStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class EqualKey:
                def __hash__(self): return hash('b')
                def __eq__(self, other): return other == 'b'
            for target in (
                {'a': 0, 'b': 1, 'c': 2, EqualKey(): 3},
                {'a': 0, 'b': 1, 'c': 2, 'a': 3, 'b': 4, EqualKey(): 5},
            ):
                iterator = iter(target)
                print(next(iterator))
                del target['a']
                target[42] = 9
                print(list(target.items()))
                for attempt in range(4):
                    try: print(next(iterator))
                    except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task LiteralExpressionsFinishBeforeHashingSmallDictionaryPairs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name): self.name = name
                def __hash__(self): events.append('hash ' + self.name); return int(self.name)
                def __repr__(self): return self.name
            def key(name): events.append('key ' + name); return Key(name)
            def value(name): events.append('value ' + name); return name
            target = {key('1'): value('1'), key('2'): value('2'), key('3'): value('3'), key('4'): value('4'), key('5'): value('5'), key('6'): value('6')}
            print(events, list(target.items()))
            """
        );

    [Fact]
    public Task LiteralHashFailureOccursAfterAllSmallLiteralExpressions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name): self.name = name
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if self.name == '2': raise ValueError('bad hash')
                    return int(self.name)
            def key(name): events.append('key ' + name); return Key(name)
            def value(name): events.append('value ' + name); return name
            try: target = {key('1'): value('1'), key('2'): value('2'), key('3'): value('3'), key('4'): value('4'), key('5'): value('5'), key('6'): value('6')}
            except ValueError as error: print(str(error))
            print(events)
            """
        );

    [Fact]
    public Task FromDictionaryKeysReusesCachedHashesAndOriginalKeyObjects() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append(self.name)
                    if fail: raise ValueError('hash called')
                    return self.code
                def __repr__(self): return self.name
            fail = False
            keys = [Key('first', 1), Key('second', 2), Key('third', 3)]
            source = {}
            for key in keys: source[key] = key.name
            fail = True
            events.clear()
            shared = []
            target = dict.fromkeys(source, shared)
            print(events, list(target.items()), next(iter(target)) is keys[0])
            print(all(value is shared for value in target.values()), list(source.values()))
            """
        );

    [Fact]
    public Task FromDictionaryKeysPresizesLiveEntriesWithoutCopyingSparseHistory() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (5, 6, 10, 11, 21):
                for removed in (1, count // 2, count - 2):
                    for backward in (False, True):
                        source = {}
                        for index in range(count): source[str(index)] = index
                        for index in range(removed): del source[str(index)]
                        target = dict.fromkeys(source, 7)
                        iterator = reversed(target) if backward else iter(target)
                        print(count, removed, backward, next(iterator))
                        del target[next(iter(target))]
                        target['new'] = 9
                        for attempt in range(len(target) + 1):
                            try: print(next(iterator))
                            except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task FromDictionaryKeysDiscardsPopitemSpentInsertionCapacity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (5, 10, 11, 21):
                for removed in (1, count // 2, count - 2):
                    source = {}
                    for index in range(count): source[str(index)] = index
                    for index in range(removed): source.popitem()
                    target = dict.fromkeys(source)
                    for index in range(4):
                        iterator = iter(target)
                        key = next(iterator)
                        print(count, removed, index, key)
                        del target[key]
                        target['new' + str(index)] = index
                        for attempt in range(len(target) + 1):
                            try: print(next(iterator))
                            except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task FromDictionaryKeysRetainsGeneralKeyHistoryEvenForEmptySources() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for empty in (False, True):
                for cleared in (False, True):
                    source = {42: 1, 'a': 2, 'b': 3, 'c': 4}
                    del source[42]
                    if empty:
                        if cleared: source.clear()
                        else:
                            for key in list(source): del source[key]
                    target = dict.fromkeys(source)
                    if empty:
                        target['a'] = 2
                        target['b'] = 3
                        target['c'] = 4
                    iterator = iter(target)
                    print(empty, cleared, next(iterator))
                    del target['a']
                    target[42] = 5
                    print(list(target.items()))
                    for attempt in range(4):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task GenericFromkeysSourcesInsertIncrementallyWithoutDictionaryPresizing() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            for count in (6, 11):
                for kind in ('dict', 'list', 'tuple', 'iterator', 'view', 'proxy'):
                    source = {}
                    for index in range(count): source[str(index)] = index
                    if kind == 'list': incoming = list(source)
                    elif kind == 'tuple': incoming = tuple(source)
                    elif kind == 'iterator': incoming = iter(source)
                    elif kind == 'view': incoming = source.keys()
                    elif kind == 'proxy': incoming = MappingProxyType(source)
                    else: incoming = source
                    target = dict.fromkeys(incoming, 7)
                    for index in range(12):
                        iterator = reversed(target)
                        print(count, kind, index, next(iterator))
                        del target[next(iter(target))]
                        target['new' + str(index)] = index
                        print(list(iterator))
            """
        );

    [Fact]
    public Task GenericFromkeysInterleavesIterationAndHashingWithoutLengthHints() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, index): self.index = index
                def __hash__(self): events.append('hash ' + str(self.index)); return self.index
                def __repr__(self): return str(self.index)
            class Keys:
                def __init__(self): self.index = 0
                def __iter__(self): events.append('iter'); return self
                def __len__(self): events.append('len'); raise ValueError('length called')
                def __length_hint__(self): events.append('hint'); raise ValueError('hint called')
                def __next__(self):
                    events.append('next ' + str(self.index))
                    if self.index == 3: raise StopIteration
                    self.index += 1
                    return Key(self.index)
            target = dict.fromkeys(Keys(), 'fill')
            print(events, list(target.items()))
            """
        );

    [Fact]
    public Task GenericFromkeysHashFailureLeavesTheIteratorAtTheFailingKey() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, index): self.index = index
                def __hash__(self):
                    events.append('hash ' + str(self.index))
                    if self.index == 2: raise ValueError('bad key')
                    return self.index
                def __repr__(self): return str(self.index)
            keys = iter([Key(1), Key(2), Key(3), Key(4)])
            try: dict.fromkeys(keys)
            except ValueError as error: print(str(error))
            print(events, list(keys))
            """
        );

    [Fact]
    public Task GenericFromkeysProcessesEarlierHashesBeforeIteratorFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __hash__(self): events.append('hash'); return 7
            class Keys:
                def __iter__(self):
                    events.append('first')
                    yield Key()
                    events.append('failure')
                    raise ValueError('iterator failed')
            try: dict.fromkeys(Keys())
            except ValueError as error: print(str(error))
            print(events)
            """
        );

    [Fact]
    public Task GenericFromkeysSeesSourceGrowthCausedByAKeyHash() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, index): self.index = index
                def __hash__(self):
                    events.append(self.index)
                    if self.index == 1: keys.append(Key(3))
                    return self.index
                def __repr__(self): return str(self.index)
            keys = [Key(1), Key(2)]
            target = dict.fromkeys(keys, 'fill')
            print(events, list(target.items()), keys)
            """
        );

    [Fact]
    public Task FromkeysEqualKeysKeepTheFirstKeyAndShareTheFillObject() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, name): self.name = name
                def __hash__(self): events.append('hash ' + self.name); return 7
                def __eq__(self, other): events.append('equal ' + self.name + ' ' + other.name); return True
                def __repr__(self): return self.name
            first = Key('first')
            second = Key('second')
            fill = []
            target = dict.fromkeys([first, second, 'other'], fill)
            print(events, next(iter(target)) is first, list(target.items()))
            fill.append(7)
            print(list(target.values()), all(value is fill for value in target.values()))
            print({}.fromkeys(['a', 'a', 'b']), dict.fromkeys([]))
            """
        );

    [Fact]
    public Task LargeLiteralGroupsInterleaveEvaluationAndHashingAtCompilerBoundaries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, index): self.index = index
                def __hash__(self): events.append('hash ' + str(self.index)); return self.index
                def __repr__(self): return str(self.index)
            def key(index): events.append('key ' + str(index)); return Key(index)
            def value(index): events.append('value ' + str(index)); return index
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14)}
            print(15, events, list(target.items()))
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15)}
            print(16, events, list(target.items()))
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16)}
            print(17, events, list(target.items()))
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16), key(17): value(17)}
            print(18, events, list(target.items()))
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16), key(17): value(17), key(18): value(18), key(19): value(19), key(20): value(20), key(21): value(21), key(22): value(22), key(23): value(23), key(24): value(24), key(25): value(25), key(26): value(26), key(27): value(27), key(28): value(28), key(29): value(29), key(30): value(30), key(31): value(31)}
            print(32, events, list(target.items()))
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16), key(17): value(17), key(18): value(18), key(19): value(19), key(20): value(20), key(21): value(21), key(22): value(22), key(23): value(23), key(24): value(24), key(25): value(25), key(26): value(26), key(27): value(27), key(28): value(28), key(29): value(29), key(30): value(30), key(31): value(31), key(32): value(32)}
            print(33, events, list(target.items()))
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16), key(17): value(17), key(18): value(18), key(19): value(19), key(20): value(20), key(21): value(21), key(22): value(22), key(23): value(23), key(24): value(24), key(25): value(25), key(26): value(26), key(27): value(27), key(28): value(28), key(29): value(29), key(30): value(30), key(31): value(31), key(32): value(32), key(33): value(33)}
            print(34, events, list(target.items()))
            events.clear()
            target = {key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16), key(17): value(17), key(18): value(18), key(19): value(19), key(20): value(20), key(21): value(21), key(22): value(22), key(23): value(23), key(24): value(24), key(25): value(25), key(26): value(26), key(27): value(27), key(28): value(28), key(29): value(29), key(30): value(30), key(31): value(31), key(32): value(32), key(33): value(33), key(34): value(34)}
            print(35, events, list(target.items()))
            """
        );

    [Fact]
    public Task LiteralUnpackingFlushesPendingGroupsWithoutChangingCallbackOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Key:
                def __init__(self, index): self.index = index
                def __hash__(self): events.append('hash ' + str(self.index)); return self.index
                def __repr__(self): return str(self.index)
            def key(index): events.append('key ' + str(index)); return Key(index)
            def value(index): events.append('value ' + str(index)); return index
            source = {'seed': 99}
            events.clear()
            target = {key(99): value(99), **source, key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), **source, key(100): value(100)}
            print(15, events, list(target.items()))
            events.clear()
            target = {key(99): value(99), **source, key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), **source, key(100): value(100)}
            print(16, events, list(target.items()))
            events.clear()
            target = {key(99): value(99), **source, key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16), **source, key(100): value(100)}
            print(17, events, list(target.items()))
            events.clear()
            target = {key(99): value(99), **source, key(0): value(0), key(1): value(1), key(2): value(2), key(3): value(3), key(4): value(4), key(5): value(5), key(6): value(6), key(7): value(7), key(8): value(8), key(9): value(9), key(10): value(10), key(11): value(11), key(12): value(12), key(13): value(13), key(14): value(14), key(15): value(15), key(16): value(16), key(17): value(17), **source, key(100): value(100)}
            print(18, events, list(target.items()))
            """
        );

    [Fact]
    public Task LargeDuplicateLiteralGroupsPreserveTheirIncrementalAndMergedCapacities() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count, target in (
                (15, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14}),
                (16, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14, '0': 15}),
                (17, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14, '0': 15, '1': 16}),
                (18, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14, '0': 15, '1': 16, '2': 17}),
                (32, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14, '0': 15, '1': 16, '2': 17, '0': 18, '1': 19, '2': 20, '0': 21, '1': 22, '2': 23, '0': 24, '1': 25, '2': 26, '0': 27, '1': 28, '2': 29, '0': 30, '1': 31}),
                (33, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14, '0': 15, '1': 16, '2': 17, '0': 18, '1': 19, '2': 20, '0': 21, '1': 22, '2': 23, '0': 24, '1': 25, '2': 26, '0': 27, '1': 28, '2': 29, '0': 30, '1': 31, '2': 32}),
                (34, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14, '0': 15, '1': 16, '2': 17, '0': 18, '1': 19, '2': 20, '0': 21, '1': 22, '2': 23, '0': 24, '1': 25, '2': 26, '0': 27, '1': 28, '2': 29, '0': 30, '1': 31, '2': 32, '0': 33}),
                (35, {'0': 0, '1': 1, '2': 2, '0': 3, '1': 4, '2': 5, '0': 6, '1': 7, '2': 8, '0': 9, '1': 10, '2': 11, '0': 12, '1': 13, '2': 14, '0': 15, '1': 16, '2': 17, '0': 18, '1': 19, '2': 20, '0': 21, '1': 22, '2': 23, '0': 24, '1': 25, '2': 26, '0': 27, '1': 28, '2': 29, '0': 30, '1': 31, '2': 32, '0': 33, '1': 34}),

            ):
                for index in range(18):
                    iterator = iter(target)
                    key = next(iterator)
                    del target[key]
                    target['new' + str(index)] = index
                    print(count, index, key, list(target.items()))
                    for attempt in range(4):
                        try: print(next(iterator))
                        except Exception as error: print(type(error).__name__, str(error))
            """
        );
}
