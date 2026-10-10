using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class OperatorModuleExecutionTests
{
    [Fact]
    public void OperatorModuleSurface()
    {
        var output = Run(
            """
            import copy
            import operator

            print("all", operator.__all__)
            print("doc", (operator.__doc__ or "").strip().split("\n")[0])
            print("names", sorted(n for n in dir(operator) if not n.startswith("_")))
            print("cmp", operator.lt(1, 2), operator.le(2, 2), operator.eq(1, 1), operator.ne(1, 2), operator.ge(2, 2), operator.gt(2, 1))
            print("logic", operator.not_(0), operator.not_(1), operator.truth([]), operator.truth([1]), operator.is_(None, None), operator.is_not(1, 1.0))
            print("math", operator.add(1, 2), operator.sub(3, 1), operator.mul(2, 3), operator.truediv(6, 3), operator.floordiv(7, 2), operator.mod(7, 3), operator.pow(2, 3), operator.neg(1), operator.pos(-1), operator.abs(-2))
            print("bit", operator.and_(6, 3), operator.or_(4, 1), operator.xor(5, 3), operator.inv(2), operator.invert(2), operator.lshift(1, 3), operator.rshift(8, 2))
            print("seq", operator.concat([1], [2]), operator.concat("ab", "cd"), operator.contains([1, 2], 2), operator.countOf([1, 1, 2], 1), operator.indexOf([1, 2], 2), operator.length_hint([1, 2, 3]), operator.length_hint(iter([1, 2])))
            print("item", operator.getitem([1, 2], 1), hasattr(operator, "setitem"), hasattr(operator, "delitem"), operator.index(True), operator.index(5))
            print("inplace", operator.iadd(1, 2), operator.iconcat([1], [2]), operator.imul("ab", 2))


            class C:
                def __init__(self):
                    self.x = 5

                def m(self, a, b=1):
                    return a + b


            c = C()
            print("attrgetter", operator.attrgetter("x")(c), operator.attrgetter("x", "m")(c)[0], type(operator.attrgetter("x", "m")(c)).__name__)
            print("itemgetter", operator.itemgetter(1)([1, 2, 3]), operator.itemgetter(0, 2)([1, 2, 3]))
            print("methodcaller", operator.methodcaller("m", 1)(c), operator.methodcaller("m", 1, b=2)(c), operator.methodcaller("upper")("ab"))
            print("getter-types", type(operator.itemgetter(1)).__name__, operator.itemgetter, type(operator.attrgetter("x")).__qualname__, type(operator.methodcaller("m")).__module__)
            g = operator.itemgetter(1)
            print("getter-reprs", repr(g), repr(operator.attrgetter("x")), repr(operator.methodcaller("m", 1)), repr(operator.methodcaller("m", b=2)))
            print("getter-eq", g == operator.itemgetter(1), g is operator.itemgetter(1), [n for n in dir(g) if not n.startswith("_")])
            print("getter-copy", copy.copy(g) is g, repr(copy.copy(g)), repr(copy.deepcopy(g)))
            print("is-none", operator.is_none(None), operator.is_none(0), operator.is_not_none(None), operator.is_not_none(0))
            print("call", operator.call(lambda a, b=1: a + b, 1, b=2))


            def probe(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)


            probe("itemgetter-multi", lambda: operator.itemgetter(0, 2)([1, 2, 3]))
            probe("attrgetter-missing", lambda: operator.attrgetter("zz")(c))
            probe("attrgetter-call", lambda: operator.attrgetter("x")(1))
            probe("methodcaller-missing", lambda: operator.methodcaller("nope")({}))
            probe("itemgetter-none", lambda: operator.itemgetter())
            probe("getitem-err", lambda: operator.getitem([], 0))
            probe("add-err", lambda: operator.add(1, "a"))
            probe("matmul", lambda: operator.matmul(1, 2))
            probe("countOf-err", lambda: operator.countOf(1, 1))
            probe("index-err", lambda: operator.index(1.5))
            probe("indexOf-err", lambda: operator.indexOf([1], 9))
            probe("delitem", lambda: (lambda x: (operator.delitem(x, 0), x)[1])([1, 2]))
            probe("setitem-ret", lambda: operator.setitem([1], 0, 9))
            probe("length-hint-max", lambda: operator.length_hint([1], 5))
            """
        );
        Assert.Equal(
            Lines(
                "all ['abs', 'add', 'and_', 'attrgetter', 'call', 'concat', 'contains', 'countOf', 'delitem', 'eq', 'floordiv', 'ge', 'getitem', 'gt', 'iadd', 'iand', 'iconcat', 'ifloordiv', 'ilshift', 'imatmul', 'imod', 'imul', 'index', 'indexOf', 'inv', 'invert', 'ior', 'ipow', 'irshift', 'is_', 'is_none', 'is_not', 'is_not_none', 'isub', 'itemgetter', 'itruediv', 'ixor', 'le', 'length_hint', 'lshift', 'lt', 'matmul', 'methodcaller', 'mod', 'mul', 'ne', 'neg', 'not_', 'or_', 'pos', 'pow', 'rshift', 'setitem', 'sub', 'truediv', 'truth', 'xor']",
                "doc Operator interface.",
                "names ['abs', 'add', 'and_', 'attrgetter', 'call', 'concat', 'contains', 'countOf', 'delitem', 'eq', 'floordiv', 'ge', 'getitem', 'gt', 'iadd', 'iand', 'iconcat', 'ifloordiv', 'ilshift', 'imatmul', 'imod', 'imul', 'index', 'indexOf', 'inv', 'invert', 'ior', 'ipow', 'irshift', 'is_', 'is_none', 'is_not', 'is_not_none', 'isub', 'itemgetter', 'itruediv', 'ixor', 'le', 'length_hint', 'lshift', 'lt', 'matmul', 'methodcaller', 'mod', 'mul', 'ne', 'neg', 'not_', 'or_', 'pos', 'pow', 'rshift', 'setitem', 'sub', 'truediv', 'truth', 'xor']",
                "cmp True True True True True True",
                "logic True False False True True True",
                "math 3 2 6 2.0 3 1 8 -1 -1 2",
                "bit 2 5 6 -3 -3 8 2",
                "seq [1, 2] abcd True 2 1 3 2",
                "item 2 True True 1 5",
                "inplace 3 [1, 2] abab",
                "attrgetter 5 5 tuple",
                "itemgetter 2 (1, 3)",
                "methodcaller 2 3 AB",
                "getter-types itemgetter <class 'operator.itemgetter'> attrgetter operator",
                "getter-reprs operator.itemgetter(1) operator.attrgetter('x') operator.methodcaller('m', 1) operator.methodcaller('m', b=2)",
                "getter-eq False False []",
                "getter-copy False operator.itemgetter(1) operator.itemgetter(1)",
                "is-none True False False True",
                "call 3",
                "itemgetter-multi (1, 3)",
                "attrgetter-missing AttributeError 'C' object has no attribute 'zz'",
                "attrgetter-call AttributeError 'int' object has no attribute 'x'",
                "methodcaller-missing AttributeError 'dict' object has no attribute 'nope'",
                "itemgetter-none TypeError itemgetter expected 1 argument, got 0",
                "getitem-err IndexError list index out of range",
                "add-err TypeError unsupported operand type(s) for +: 'int' and 'str'",
                "matmul TypeError unsupported operand type(s) for @: 'int' and 'int'",
                "countOf-err TypeError argument of type 'int' is not iterable",
                "index-err TypeError 'float' object cannot be interpreted as an integer",
                "indexOf-err ValueError sequence.index(x): x not in sequence",
                "delitem [2]",
                "setitem-ret None",
                "length-hint-max 1"
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
