using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SetCachedHashCompatibilityTests
{
    [Fact]
    public Task QueriesHashOnlyTheQueryAndCompareMatchingStoredHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            values = {a}
            query = Key('a', 1)
            events.clear()
            print(query in values, events)
            events.clear()
            values.add(query)
            print(len(values), next(iter(values)) is a, events)
            events.clear()
            values.remove(query)
            print(len(values), events)
            """
        );

    [Fact]
    public Task DifferentInsertionHashesAvoidEqualityEvenForEqualKeys() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('same', 1)
            b = Key('same', 2)
            values = {a}
            events.clear()
            print(b in values, events)
            events.clear()
            values.add(b)
            print(len(values), sorted(key.code for key in values), events)
            events.clear()
            values.discard(Key('same', 3))
            print(len(values), events)
            """
        );

    [Fact]
    public Task ChangingAKeyHashDoesNotChangeItsStoredInsertionHash() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            values = {a}
            a.code = 2
            events.clear()
            print(a in values, events)
            events.clear()
            values.add(a)
            print(len(values), all(key is a for key in values), events)
            events.clear()
            values.remove(a)
            print(len(values), a in values, events)
            a.code = 1
            events.clear()
            print(a in values, events)
            """
        );

    [Fact]
    public Task StoredHashExceptionsDoNotAffectNewQueries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self, code): self.code = code; self.blocked = False
                def __hash__(self):
                    if self.blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other): return self.code == other.code
            stored = Key(1)
            values = {stored}
            stored.blocked = True
            query = Key(1)
            print(query in values)
            values.discard(query)
            print(len(values))
            values.add(Key(2))
            print(len(values))
            """
        );

    [Fact]
    public Task CopiesAndExactSetConstructorsReuseInsertionHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            mutable = {a, b}
            frozen = frozenset(mutable)
            blocked = True
            events.clear()
            for value in (mutable.copy(), set(mutable), frozenset(mutable), set(frozen), frozenset(frozen), frozen.copy()):
                print(type(value).__name__, sorted(key.name for key in value))
            print(events, frozenset(frozen) is frozen, frozen.copy() is frozen)
            """
        );

    [Fact]
    public Task FromKeysReusesSetHashesAndPreservesKeysAndFillIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            mutable = {a, b}
            frozen = frozenset(mutable)
            blocked = True
            events.clear()
            fill = []
            for source in (mutable, frozen):
                for target in (dict.fromkeys(source, fill), {}.fromkeys(source, fill)):
                    print(sorted(key.name for key in target), all(value is fill for value in target.values()))
                    print(any(key is a for key in target), any(key is b for key in target))
            print(events)
            """
        );

    [Fact]
    public Task FromKeysRetainsDistinctEntriesForAKeyWithTwoInsertionHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            source = {a}
            a.code = 2
            source.add(a)
            blocked = True
            events.clear()
            for values in (source, frozenset(source)):
                target = dict.fromkeys(values, 7)
                print(len(target), all(key is a for key in target), list(target.values()))
                blocked = False
                a.code = 1
                target[a] = 11
                a.code = 2
                target[a] = 22
                blocked = True
                print(list(target.values()))
                events.clear()
            print(events)
            """
        );

    [Fact]
    public Task FromKeysSetSourcesPresizeLiveEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for count in (5, 6, 10, 11, 21):
                for frozen in (False, True):
                    source = set(range(count))
                    source.discard(0)
                    if frozen: source = frozenset(source)
                    target = dict.fromkeys(source, 7)
                    iterator = iter(target)
                    key = next(iterator)
                    print(count, frozen, key)
                    del target[key]
                    target[100] = 9
                    try: print(list(iterator))
                    except RuntimeError as error: print(str(error))
                    print(list(target))
            """
        );

    [Fact]
    public Task FromKeysStringSetSourcesStartWithGeneralDictionaryStorage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for source in ({'a'}, frozenset({'a'})):
                target = dict.fromkeys(source, 0)
                target['b'] = 1
                target['c'] = 2
                target['d'] = 3
                del target['a']
                iterator = iter(target)
                print(next(iterator))
                del target['b']
                target[42] = 4
                for attempt in range(4):
                    try: print(next(iterator))
                    except Exception as error: print(type(error).__name__, str(error))
            """
        );

    [Fact]
    public Task ExactSetAlgebraReusesStoredHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            c = Key('c', 3)
            left = {a, b}
            right = {b, c}
            blocked = True
            events.clear()
            for value in (left | right, left & right, left - right, left ^ right,
                          left.union(right), left.intersection(right), left.difference(right), left.symmetric_difference(right)):
                print(sorted(key.name for key in value))
            print(events)
            """
        );

    [Fact]
    public Task FrozenSetAlgebraReusesStoredHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            c = Key('c', 3)
            left = frozenset({a, b})
            right = frozenset({b, c})
            blocked = True
            events.clear()
            for value in (left | right, left & right, left - right, left ^ right):
                print(type(value).__name__, sorted(key.name for key in value))
            print(events)
            """
        );

    [Fact]
    public Task SetRelationsAndDisjointnessReuseStoredHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            small = {a}
            large = {a, b}
            other = {b}
            blocked = True
            events.clear()
            print(small <= large, small < large, large >= small, large > small)
            print(small == large, small == small.copy(), small != other)
            print(small.issubset(large), large.issuperset(small), small.isdisjoint(other), small.isdisjoint(large))
            print(events)
            """
        );

    [Fact]
    public Task SetUpdateMethodsReuseStoredHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            c = Key('c', 3)
            left = {a, b}
            right = {b, c}
            blocked = True
            events.clear()
            for method in ('update', 'intersection_update', 'difference_update', 'symmetric_difference_update'):
                target = left.copy()
                result = getattr(target, method)(right)
                print(method, result, sorted(key.name for key in target))
            print(events)
            """
        );

    [Fact]
    public Task InPlaceSetAlgebraReusesStoredHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            c = Key('c', 3)
            left = {a, b}
            right = {b, c}
            blocked = True
            events.clear()
            x = left.copy()
            x |= right
            print(sorted(key.name for key in x))
            x = left.copy()
            x &= right
            print(sorted(key.name for key in x))
            x = left.copy()
            x -= right
            print(sorted(key.name for key in x))
            x = left.copy()
            x ^= right
            print(sorted(key.name for key in x))
            print(events)
            """
        );

    [Fact]
    public Task GenericSetConstructionAndUpdatesHashEachYieldedKey() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            for operation in ('set', 'frozenset', 'update', 'fromkeys'):
                events.clear()
                def keys():
                    events.append('yield a')
                    yield a
                    events.append('yield b')
                    yield b
                    events.append('yield a again')
                    yield a
                if operation == 'set': target = set(keys())
                elif operation == 'frozenset': target = frozenset(keys())
                elif operation == 'fromkeys': target = dict.fromkeys(keys())
                else:
                    target = set()
                    target.update(keys())
                print(operation, len(target), events)
            """
        );

    [Fact]
    public Task GenericUpdatePreservesEarlierInsertionsWhenHashingFails() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Key:
                def __init__(self, name, fail): self.name = name; self.fail = fail
                def __hash__(self):
                    print('hash', self.name)
                    if self.fail: raise ValueError('bad key')
                    return 1
            first = Key('first', False)
            bad = Key('bad', True)
            def keys():
                yield first
                yield bad
                print('should not reach')
            values = set()
            try: values.update(keys())
            except ValueError as error: print(str(error))
            print(len(values), next(iter(values)) is first)
            """
        );

    [Fact]
    public Task FrozenSetHashUsesStoredHashesAndCachesTheAggregate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            blocked = False
            class Key:
                def __init__(self, name, code): self.name = name; self.code = code
                def __hash__(self):
                    events.append('hash ' + self.name)
                    if blocked: raise ValueError('stored hash called')
                    return self.code
                def __eq__(self, other):
                    events.append('eq ' + self.name + ' ' + other.name)
                    return self.name == other.name
                def __repr__(self): return self.name

            a = Key('a', 1)
            b = Key('b', 2)
            values = frozenset({a, b})
            blocked = True
            events.clear()
            first = hash(values)
            print(first == hash(values), first == hash(frozenset({1, 2})), events)
            """
        );

    [Fact]
    public Task FrozenSetNumericAndNestedHashesMatchCPythonAndUseInsertionHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for values in ([], [1], [1, 2], [-1, -2, 0, 1], [2 ** 61 - 1, 2 ** 61, 2 ** 100, -(2 ** 100)]):
                value = frozenset(values)
                print(hash(value), hash(value) == hash(frozenset(reversed(values))))
            inner = frozenset({1, 2})
            outer = frozenset({inner, frozenset({-1, 3})})
            print(hash(outer), hash(frozenset({outer})))
            events = []
            class Key:
                def __init__(self, code): self.code = code; self.blocked = False
                def __hash__(self):
                    events.append(self.code)
                    if self.blocked: raise ValueError('stored hash called')
                    return self.code
            for code in (-2, -1, 0, 1, 23):
                key = Key(code)
                value = frozenset({key})
                key.code = 99
                key.blocked = True
                events.clear()
                print(hash(value), hash(value) == hash(frozenset({code})), hash(value) == hash(value), events)
            """
        );

    [Fact]
    public Task IntegralFloatAndRealOnlyComplexFrozenSetsPreserveEqualNumericHashes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for integer in (0, 1, -1, 2 ** 40, -(2 ** 40), 2 ** 61, 2 ** 100):
                real = float(integer)
                imaginary = complex(real, 0)
                print(integer, hash(integer) == hash(real), hash(real) == hash(imaginary))
                values = (frozenset({integer}), frozenset({real}), frozenset({imaginary}))
                print(values[0] == values[1], values[1] == values[2], hash(values[0]) == hash(values[1]), hash(values[1]) == hash(values[2]))
                outer = {values[0]}
                print(values[1] in outer, values[2] in outer, len({integer, real, imaginary}))
            """
        );
}
