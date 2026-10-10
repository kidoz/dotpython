using Xunit;

namespace DotPython.DifferentialTests;

public sealed class NamedTupleCompatibilityTests
{
    [Fact]
    public Task NamedTupleSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy
            import collections
            from collections import namedtuple

            Point = namedtuple('Point', ['x', 'y'])
            p = Point(11, y=22)
            print("class", Point, type(Point).__name__, Point.__name__, Point.__bases__, Point.__module__)
            print("doc", Point.__doc__, Point.__slots__, Point._fields, Point._field_defaults, Point.__match_args__)
            print("getter", repr(Point.x), type(Point.x).__name__, Point.x.__doc__, Point.x.__get__(p), Point.x.__get__(p, Point))
            print("member", p.x, p.y, p[0], p[1], len(p), tuple(p), list(p))
            print("repr", repr(p), str(p))
            print("fields", p._fields is Point._fields)
            print("methods", p._make([1, 2]), p._replace(x=100), p.__replace__(x=5), p._asdict(), p.__getnewargs__())
            print("index", p.index(22), p.count(11))
            print("eq", p == (11, 22), p == Point(11, 22), p != Point(1, 2), hash(p) == hash((11, 22)))
            print("unpack", (lambda a, b: (a, b))(*p))
            print("new", Point.__new__.__name__, Point.__new__.__doc__, Point.__new__.__qualname__, Point.__new__.__defaults__)
            D = namedtuple('D', 'a b', defaults=[1, 2])
            print("defaults", D(), D(5), D(5, 6), D._field_defaults, D.__new__.__defaults__)
            E = namedtuple('E', 'a')
            print("one", E(1), repr(E(1)), E.__doc__)
            R = namedtuple('R', ['a', 'b', 'a'], rename=True)
            print("rename", R._fields, R(1, 2, 3))
            S = namedtuple('S', 'class if x_', rename=True)
            print("rename2", S._fields)
            print("module", namedtuple('M', 'a').__module__, namedtuple('M2', 'a', module='custom').__module__)
            class Sub(Point):
                pass
            print("subclass", Sub(1, 2), type(Sub(1, 2)).__name__, isinstance(p, tuple), issubclass(Point, tuple))
            print("copy", copy.copy(p), type(copy.copy(p)).__name__, copy.deepcopy(p))
            print("catalogue", "namedtuple" in collections.__all__, all(hasattr(collections, name) for name in collections.__all__))
            print("fields-forms", namedtuple('T1', ('a', 'b'))._fields, namedtuple('T2', 'a, b')._fields, namedtuple('T3', 'a,b')._fields)
            print("set-class", (setattr(Point, 'z', 5), Point.z)[1])


            def probe(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)


            probe("set-instance", lambda: setattr(Point(1, 2), "x", 5))
            probe("delete-instance", lambda: delattr(Point(1, 2), "x"))
            probe("missing", lambda: Point(1, 2).z)
            probe("missing-args", lambda: Point())
            probe("missing-one", lambda: Point(1))
            probe("extra", lambda: Point(1, 2, 3))
            probe("kw-extra", lambda: Point(1, 2, z=3))
            probe("kw-duplicate", lambda: Point(1, x=2))
            probe("replace-extra", lambda: Point(1, 2)._replace(z=3))
            probe("replace-pos", lambda: Point(1, 2)._replace(1, 2, 3))
            probe("make-short", lambda: Point._make([1]))
            probe("make-long", lambda: Point._make([1, 2, 3]))
            probe("type-bad", lambda: namedtuple('X', [1]))
            probe("bad-ident", lambda: namedtuple('X', ['a-b']))
            probe("keyword", lambda: namedtuple('X', ['class']))
            probe("keyword-type", lambda: namedtuple('class', ['a']))
            probe("underscore", lambda: namedtuple('X', ['_a']))
            probe("duplicate", lambda: namedtuple('X', ['a', 'a']))
            probe("defaults-many", lambda: namedtuple('X', 'a', defaults=[1, 2]))
            probe("typename-int", lambda: namedtuple(123, 'a'))
            probe("nonstr-field", lambda: namedtuple('X', [b'a']))
            """
        );
}
