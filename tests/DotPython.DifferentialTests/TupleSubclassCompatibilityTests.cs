using Xunit;

namespace DotPython.DifferentialTests;

public sealed class TupleSubclassCompatibilityTests
{
    [Fact]
    public Task TupleSubclassSurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import copy


            class P(tuple):
                pass


            p = P((1, 2))
            print("made", p, repr(p), len(p), p[0], p[-1], p[0:1], tuple(p), p == (1, 2), hash(p) == hash((1, 2)))
            print("iter", list(p), [x for x in p], 1 in p, 9 in p, bool(p), bool(P()))
            a, b = p
            print("unpack", a, b)
            print("concat", p + (3,), (0,) + p, p * 2, 2 * p, type(p + (3,)).__name__, type(p[0:1]).__name__, type(p * 2).__name__)
            print("methods", p.count(1), p.index(2), p.__class__.__name__, type(p).__name__, p.__class__ is P)
            print("isinstance", isinstance(p, tuple), isinstance(p, P), issubclass(P, tuple), isinstance(P, type))
            print("bases", P.__bases__, P.__mro__, P.__name__, P.__qualname__)
            print("init", P(), P([7]), P("ab"), P(range(2)), len(P()), P(None if False else []))
            print("attrs", hasattr(p, "__dict__"), p.__dict__)
            p.tag = 5
            print("attribute", p.tag, p.__dict__)
            try:
                p[0] = 9
            except TypeError as error:
                print("setitem", error)
            class Q(P):
                def __new__(cls, value):
                    return tuple.__new__(cls, (value, value * 2))
            print("new", Q(3), Q(3).__class__.__name__)
            print("tuple-of-type", type(p).__name__, tuple.__new__(P, (4, 5)), tuple.__new__(P, (4, 5)).__class__.__name__)
            class R(tuple):
                pass
            print("cross", P((1, 2)) == R((1, 2)), P((1, 2)) == (1, 2), (1, 2) == P((1, 2)), (1, 2) in {P((1, 2))}, P((1, 2)) in [(1, 2)])
            print("copy", copy.copy(p), type(copy.copy(p)).__name__, copy.deepcopy(p))


            def probe(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)


            probe("two-args", lambda: P(1, 2))
            probe("add-list", lambda: P((1,)) + [1])
            probe("radd-list", lambda: [1] + P((1,)))
            probe("add-str", lambda: P((1,)) + "ab")
            probe("mul-float", lambda: P((1,)) * 1.5)
            probe("radd-empty", lambda: () + P((1,)))
            probe("add-empty", lambda: P(()) + (1,))
            probe("sort", lambda: sorted([P((2,)), P((1,))]))
            probe("dict-key", lambda: {P((1,)): "x"}[(1,)])
            probe("index-err", lambda: P((1,))["a"])
            probe("abs", lambda: abs(P((1,))))
            probe("no-delitem", lambda: P((1,)).__delitem__)
            probe("eq-list", lambda: P((1,)) == [1])
            probe("lt", lambda: P((1,)) < (2,))
            """
        );
}
