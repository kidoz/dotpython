using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TupleSubclassExecutionTests
{
    [Fact]
    public void TupleSubclassSurface()
    {
        var output = Run(
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
        Assert.Equal(
            Lines(
                "made (1, 2) (1, 2) 2 1 2 (1,) (1, 2) True True",
                "iter [1, 2] [1, 2] True False True False",
                "unpack 1 2",
                "concat (1, 2, 3) (0, 1, 2) (1, 2, 1, 2) (1, 2, 1, 2) tuple tuple tuple",
                "methods 1 1 P P True",
                "isinstance True True True True",
                "bases (<class 'tuple'>,) (<class '__main__.P'>, <class 'tuple'>, <class 'object'>) P P",
                "init () (7,) ('a', 'b') (0, 1) 0 ()",
                "attrs True {}",
                "attribute 5 {'tag': 5}",
                "setitem 'P' object does not support item assignment",
                "new (3, 6) Q",
                "tuple-of-type P (4, 5) P",
                "cross True True True True True",
                "copy (1, 2) P (1, 2)",
                "two-args TypeError tuple expected at most 1 argument, got 2",
                "add-list TypeError can only concatenate tuple (not \"list\") to tuple",
                "radd-list TypeError can only concatenate list (not \"P\") to list",
                "add-str TypeError can only concatenate tuple (not \"str\") to tuple",
                "mul-float TypeError can't multiply sequence by non-int of type 'float'",
                "radd-empty (1,)",
                "add-empty (1,)",
                "sort [(1,), (2,)]",
                "dict-key 'x'",
                "index-err TypeError tuple indices must be integers or slices, not str",
                "abs TypeError bad operand type for abs(): 'P'",
                "no-delitem AttributeError 'P' object has no attribute '__delitem__'",
                "eq-list False",
                "lt True"
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
