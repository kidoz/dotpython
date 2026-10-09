using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TypeObjectCornerCompatibilityTests
{
    [Fact]
    public Task BoundMethodReprs() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def shape(value):
                text = repr(value)
                cut = text.find(' at 0x')
                return text if cut < 0 else text[:cut]
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            print(shape(b'ab'.__len__), shape(b'ab'.count), shape([].append), shape([].__len__))
            print(shape([].__format__), shape([].__init__), shape([].__getstate__), shape({}.get))
            print(shape((1).bit_length), shape('a'.upper), shape(memoryview(b'a').tobytes), shape(memoryview(b'a').__len__))
            print(shape(list.__len__([1])), shape(list.append([], 1)), shape(list.__hash__), shape(list.__eq__))
            print(shape(dict.fromkeys), shape(str.maketrans), shape(bytes.fromhex), shape(int.from_bytes), shape(float.fromhex), shape(bool.from_bytes))
            print(shape(list.__new__), shape(list.__class_getitem__), shape(object.__subclasshook__), shape(list.__subclasshook__), shape([].__subclasshook__))
            print(shape(iter([1]).__next__), shape(iter([1]).__length_hint__), shape([].__reversed__), shape((x for x in []).__next__), shape((x for x in []).send))
            print(shape(reversed([1])), shape(iter([1])), shape(iter((1,))), shape(iter('a')), shape(iter(b'a')), shape(iter({})), shape(iter(set())), shape(iter(range(3))), shape(iter(memoryview(b'a'))))
            print(shape(enumerate([])), shape(zip([])), shape(map(int, [])))
            def gen():
                yield 1
            print(shape(gen()), shape((x for x in [1])))
            class C:
                def method(self):
                    pass
            print(shape(C().method), shape(C.method), C.method.__qualname__)
            print('address' in shape(iter([1])), ' at 0x' in repr(iter([1])), repr(iter([1])).endswith('>'), repr(iter([1]))[:-1].endswith(' at 0x'))
            print(len(repr(iter([1])) ) > len(shape(iter([1]))), shape(lambda: None))
            """
        );

    [Fact]
    public Task TypeObjectMembers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def shape(value):
                text = repr(value)
                cut = text.find(' at 0x')
                return text if cut < 0 else text[:cut]
            def probe(thunk):
                try:
                    print(repr(thunk()))
                except Exception as error:
                    print(type(error).__name__, error)
            r = range(2, 9, 3)
            print(r.start, r.stop, r.step, r.__len__(), r.__getitem__(1), r.__contains__(5), next(r.__iter__()))
            print(repr(range.start), repr(range.stop), repr(range.step), repr(int.real), repr(memoryview.obj))
            print(shape(r.__iter__), shape(r.__len__), shape(r.__getitem__), shape(r.__contains__), shape(r.__eq__), shape(r.__hash__), shape(r.__reversed__))
            print(range(3) == range(0, 3), range(3) == range(4), range(5, 6) == range(5, 7, 5))
            print(range(0, 4, 2) == range(0, 3, 2), range(0, 4, 2) == range(0, 4, 3), range(0) == range(7, 7), range(3) == 3, range(3) == [0, 1, 2])
            print(hash(range(3)) == hash(range(0, 3)), hash(range(3)) == hash((3, 0, 1)), hash(range(1, 10, 3)) == hash((3, 1, 3)))
            print(hash(range(0)) == hash(range(7, 7)), hash(range(0, 4, 2)) == hash(range(0, 3, 2)))
            probe(lambda: range(3) < range(4))
            probe(lambda: range(3).__lt__(range(4)))
            print([x for x in r], list(reversed(range(3))), 4 in range(9), range(5)[1:3] == range(1, 2), len(range(9)))
            print(repr(object.__reduce__), repr(set.__reduce__), repr(frozenset.__reduce__), repr(bytearray.__reduce__), repr(range.__reduce__), repr(slice.__reduce__), repr(type(...).__reduce__), repr(list.__reduce__), repr(int.__reduce__))
            print(set([3]).__reduce__(), frozenset([7]).__reduce__(), bytearray(b'ab').__reduce__(), range(2, 9, 3).__reduce__(), slice(1).__reduce__(), (...).__reduce__())
            print(set([3]).__reduce__()[0] is set, set([3]).__reduce__()[1][0] == [3])
            probe(lambda: (3).__reduce__())
            probe(lambda: 'a'.__reduce__())
            probe(lambda: b'a'.__reduce__())
            probe(lambda: [1].__reduce__())
            probe(lambda: {}.__reduce__())
            probe(lambda: None.__reduce__())
            probe(lambda: memoryview(b'a').__reduce__())
            probe(lambda: {}.keys().__reduce__())
            probe(lambda: set([1]).__reduce__(1))
            probe(lambda: (1).__reduce__(2))
            print(shape(list.__subclasshook__), shape([].__subclasshook__), shape(object.__subclasshook__))
            probe(lambda: list.__subclasshook__(int))
            probe(lambda: int.__subclasshook__(3))
            probe(lambda: object.__subclasshook__((int,)))
            probe(lambda: list.__subclasshook__())
            probe(lambda: list.__subclasshook__(int, tuple))
            print(issubclass(bool, int), issubclass(list, object))
            """
        );
}
