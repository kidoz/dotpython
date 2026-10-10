using Xunit;

namespace DotPython.DifferentialTests;

public sealed class AbcMixinCompatibilityTests
{
    [Fact]
    public Task AbcMixinSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import collections.abc as abc

            print("class-dict", abc.Set.__hash__ is None, abc.Mapping.__hash__ is None, abc.Mapping.__reversed__ is None, abc.MappingView.__slots__)
            print("abstract", sorted(abc.Sequence.__abstractmethods__), sorted(abc.Set.__abstractmethods__), sorted(abc.MutableSet.__abstractmethods__), sorted(abc.ValuesView.__abstractmethods__))


            class Seq(abc.Sequence):
                def __init__(self, items):
                    self.items = list(items)

                def __getitem__(self, index):
                    return self.items[index]

                def __len__(self):
                    return len(self.items)


            class MSeq(abc.MutableSequence, Seq):
                def __setitem__(self, index, value):
                    self.items[index] = value

                def __delitem__(self, index):
                    del self.items[index]

                def insert(self, index, value):
                    self.items.insert(index, value)

            s = Seq([10, 20, 30])
            print("seq", list(s), list(reversed(s)), 20 in s, 99 in s, s.index(20), s.index(30, -2), s.count(20), s.count(9))
            try:
                s.index(99)
            except ValueError as error:
                print("seq-miss", repr(error), error.args)
            print("seq-reversed-empty", list(reversed(Seq([]))), list(reversed(Seq([1]))))
            walk = iter(s)
            print("seq-lazy", next(walk))
            s.items.append(40)
            print("seq-lazy2", list(walk))
            print("seq-kw", s.index(value=30), s.count(value=10), s.__contains__(value=20))

            m = MSeq([1, 2, 3])
            m.append(4)
            m.extend([5])
            m.remove(5)
            print("mseq", m.items, m.pop(), m.pop(0), m.items)
            m.reverse()
            print("mseq-reverse", m.items)
            m += [9]
            print("mseq-iadd", m.items, type(m).__name__, isinstance(m, abc.MutableSequence))
            m.extend(m)
            print("mseq-self", m.items)
            m.clear()
            print("mseq-clear", m.items, len(m))


            class Bag(abc.Set):
                def __init__(self, items=()):
                    self.items = set(items)

                def __contains__(self, value):
                    return value in self.items

                def __iter__(self):
                    return iter(sorted(self.items))

                def __len__(self):
                    return len(self.items)


            class HashBag(Bag):
                __hash__ = abc.Set._hash


            a = Bag([1, 2, 3])
            b = Bag([2, 3, 4])
            print("set-order", a <= b, a >= b, Bag([1, 2]) <= a, Bag([1, 2]) < a, a < a, a > Bag([1, 2]), a == Bag([3, 2, 1]), a == {1, 2, 3}, a == 5, a != Bag([1]))
            print("set-ops", sorted(a & b), sorted(a & [3, 4]), sorted({3, 9} & a), sorted(a | b), sorted(a | [9]), sorted([9] | a))
            print("set-ops2", sorted(a - b), sorted(a - [2]), sorted([9, 2, 3] - a), sorted(a ^ b), sorted([1, 9] ^ a))
            print("set-disjoint", a.isdisjoint([9]), a.isdisjoint([1]), a.isdisjoint(b))
            print("set-build", type(a._from_iterable([1, 2])).__name__, sorted(a._from_iterable([1, 2, 1])))
            print("set-hash", hash(HashBag([1, 2, 3])) == hash(frozenset([1, 2, 3])), HashBag([1, 2]) == HashBag([2, 1]))
            try:
                hash(a)
            except TypeError as error:
                print("set-unhashable", error)


            class MBag(abc.MutableSet, Bag):
                def add(self, value):
                    self.items.add(value)

                def discard(self, value):
                    self.items.discard(value)
            mb = MBag([1, 2, 3])
            mb.remove(2)
            print("mset", sorted(mb), mb.pop(), sorted(mb))
            try:
                mb.remove(99)
            except KeyError as error:
                print("mset-miss", repr(error), error.args)
            mb |= [7, 8]
            mb &= [7, 3]
            mb ^= [3, 5]
            mb -= [7]
            print("mset-inplace", sorted(mb))
            mb -= mb
            print("mset-self", sorted(mb))
            empty = MBag()
            try:
                empty.pop()
            except KeyError as error:
                print("mset-empty", repr(error), error.args)
            empty.clear()
            print("mset-clear", len(empty), empty.isdisjoint([]))


            class Table(abc.Mapping):
                def __init__(self, pairs=()):
                    self.data = dict(pairs)

                def __getitem__(self, key):
                    return self.data[key]

                def __iter__(self):
                    return iter(self.data)

                def __len__(self):
                    return len(self.data)


            t = Table([("a", 1), ("b", 2)])
            print("map", t.get("a"), t.get("z"), t.get("z", 9), "a" in t, "z" in t, len(t))
            print("map-views", sorted(t.keys()), sorted(t.items()), sorted(t.values()), len(t.keys()), len(t.values()))
            print("map-view-in", "a" in t.keys(), ("a", 1) in t.items(), 1 in t.values(), ("a", 9) in t.items(), 9 in t.values())
            print("map-view-mapping", t.keys()._mapping is t, t.values()._mapping is t)
            print("map-view-set", sorted(t.keys() & {"b", "z"}), sorted(t.keys() | {"z"}), sorted(t.keys() - {"a"}), t.keys().isdisjoint({"z"}))
            print("map-eq", t == {"a": 1, "b": 2}, t == {"a": 1}, t != {"a": 1})
            try:
                hash(t)
            except TypeError as error:
                print("map-unhashable", error)
            try:
                reversed(t)
            except TypeError as error:
                print("map-reversed", error)
            print("map-kw", t.get(key="a"), t.get(key="z", default=4))


            class TableMut(abc.MutableMapping, Table):
                def __setitem__(self, key, value):
                    self.data[key] = value

                def __delitem__(self, key):
                    del self.data[key]


            d = TableMut([("a", 1)])
            d.update({"x": 1}, y=2)
            d.update([("z", 3)])
            d.update(Table([("w", 4)]))
            print("mmap-update", sorted(d.data.items()))
            print("mmap", d.pop("a"), d.pop("n", 5), d.setdefault("s", 7), d.setdefault("s", 8))
            print("mmap-popitem", d.popitem(), sorted(d.data.items()))
            d.clear()
            print("mmap-clear", d.data)
            try:
                d.popitem()
            except KeyError as error:
                print("mmap-empty", repr(error), error.args)
            try:
                d.pop("n")
            except KeyError as error:
                print("mmap-pop-miss", repr(error), error.args)


            class Countdown(abc.Iterator):
                def __init__(self, start):
                    self.left = start

                def __next__(self):
                    if self.left <= 0:
                        raise StopIteration
                    self.left -= 1
                    return self.left + 1


            c = Countdown(3)
            print("iterator", iter(c) is c, list(c), [value for value in Countdown(2)])
            print("iterator-abc", isinstance(Countdown(1), abc.Iterator), issubclass(Countdown, abc.Iterable))


            class Machine(abc.Generator):
                def __init__(self, items):
                    self.items = list(items)
                    self.index = 0
                    self.closed = False

                def send(self, value):
                    if self.index >= len(self.items):
                        raise StopIteration
                    value = self.items[self.index]
                    self.index += 1
                    return value

                def throw(self, typ, val=None, tb=None):
                    self.closed = True
                    if val is None:
                        raise typ
                    raise typ()


            g = Machine([1, 2])
            print("generator", next(g), next(g), list(Machine([3, 4])))
            closed = Machine([1])
            print("generator-close", closed.close(), closed.closed)


            class Swallow(abc.Generator):
                def send(self, value):
                    return 1

                def throw(self, typ, val=None, tb=None):
                    return "swallowed"


            try:
                Swallow().close()
            except RuntimeError as error:
                print("generator-swallow", error)
            print("bases", isinstance(t.keys(), abc.KeysView), isinstance(t.keys(), abc.Set), isinstance(t.values(), abc.ValuesView), isinstance(t.items(), abc.ItemsView))


            class Partial(abc.Mapping):
                def __getitem__(self, key):
                    return key


            class Plain:
                pass


            print("subclass-abstract", sorted(Partial.__abstractmethods__), sorted(Table.__abstractmethods__), sorted(MSeq.__abstractmethods__), hasattr(Plain, "__abstractmethods__"), hasattr(Partial, "__abstractmethods__"))
            """
        );
}
