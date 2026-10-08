using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ItertoolsExecutionTests
{
    [Fact]
    public void CountIsInfiniteAndAdvancesByItsStep()
    {
        var output = Run(
            """
            import itertools

            counter = itertools.count()
            print(next(counter), next(counter), next(counter))
            print(list(itertools.islice(itertools.count(3, 2), 4)))
            print(list(itertools.islice(itertools.count(10, -2), 3)))
            print(list(itertools.islice(itertools.count(2**100), 3)))
            print(list(itertools.islice(itertools.count(0.5, 0.25), 3)))
            print(list(itertools.islice(itertools.count(1j), 2)))
            print(itertools.count(4).__next__(), next(itertools.count(4, 4)))
            """
        );

        Assert.Equal(
            Lines(
                "0 1 2",
                "[3, 5, 7, 9]",
                "[10, 8, 6]",
                "[1267650600228229401496703205376, 1267650600228229401496703205377, 1267650600228229401496703205378]",
                "[0.5, 0.75, 1.0]",
                "[1j, (1+1j)]",
                "4 4"
            ),
            output
        );
    }

    [Fact]
    public void RepeatIsBoundedAndRejectsBadTimes()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.repeat("x", 3)))
            print(len(list(itertools.repeat("x", 0))))
            print(len(list(itertools.repeat("x", -3))))
            print(list(itertools.islice(itertools.repeat("x"), 4)))
            print(repr(itertools.repeat("a")), repr(itertools.repeat("a", 3)))
            try:
                itertools.repeat(1, 1.5)
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            try:
                itertools.repeat(1, None)
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            try:
                itertools.repeat(1, 10**30)
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            """
        );

        Assert.Equal(
            Lines(
                "['x', 'x', 'x']",
                "0",
                "0",
                "['x', 'x', 'x', 'x']",
                "repeat('a') repeat('a', 3)",
                "ERR TypeError 'float' object cannot be interpreted as an integer",
                "ERR TypeError 'NoneType' object cannot be interpreted as an integer",
                "ERR OverflowError Python int too large to convert to C ssize_t"
            ),
            output
        );
    }

    [Fact]
    public void CycleRestartsTheInput()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.islice(itertools.cycle([1, 2, 3]), 7)))
            print(list(itertools.cycle([])))
            print(list(itertools.islice(itertools.cycle(range(3)), 5)))
            seen = []
            for value in itertools.cycle("ab"):
                seen.append(value)
                if len(seen) == 5:
                    break
            print(seen)
            print(list(itertools.islice(itertools.cycle([None, False]), 4)))
            """
        );

        Assert.Equal(
            Lines(
                "[1, 2, 3, 1, 2, 3, 1]",
                "[]",
                "[0, 1, 2, 0, 1]",
                "['a', 'b', 'a', 'b', 'a']",
                "[None, False, None, False]"
            ),
            output
        );
    }

    [Fact]
    public void ChainIsLazyAcrossItsArguments()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.chain([1, 2], [3], [], (4, 5))))
            print(list(itertools.chain.from_iterable([[1], [2, 3], []])))
            print(list(itertools.chain.from_iterable("ab")))
            print(list(itertools.chain(range(2), "cd")))
            print(list(itertools.chain()))
            """
        );

        Assert.Equal(
            Lines("[1, 2, 3, 4, 5]", "[1, 2, 3]", "['a', 'b']", "[0, 1, 'c', 'd']", "[]"),
            output
        );
    }

    [Fact]
    public void IsliceConsumesExactlyTheIndicesItNeeds()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.islice([1, 2, 3, 4, 5], 3)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 1, 3)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 0, 5, 2)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 1, None, 2)))
            print(list(itertools.islice([1, 2, 3, 4, 5], None, 2)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 2, 2)))
            try:
                print(list(itertools.islice([1, 2, 3, 4, 5], -1, 3)))
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            print(list(itertools.islice([], 3)))
            try:
                list(itertools.islice([1], "x"))
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            try:
                itertools.islice([1], 1, 2, 3, 4)
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            """
        );

        Assert.Equal(
            Lines(
                "[1, 2, 3]",
                "[2, 3]",
                "[1, 3, 5]",
                "[2, 4]",
                "[1, 2]",
                "[]",
                "ERR ValueError Indices for islice() must be None or an integer: 0 <= x <= sys.maxsize.",
                "[]",
                "ERR ValueError Stop argument for islice() must be None or an integer: 0 <= x <= sys.maxsize.",
                "ERR TypeError islice expected at most 4 arguments, got 5"
            ),
            output
        );
    }

    [Fact]
    public void PredicateIteratorsFollowTheirStoppingRules()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.takewhile(lambda x: x < 3, [1, 2, 3, 1])))
            print(list(itertools.dropwhile(lambda x: x < 3, [1, 2, 3, 1])))
            print(list(itertools.takewhile(bool, [])))
            print(list(itertools.dropwhile(bool, [False, True, False])))
            print(list(itertools.filterfalse(lambda x: x % 2, range(6))))
            print(list(itertools.filterfalse(None, [0, 1, False, True, ""])))
            print(list(itertools.compress("ABCDEF", [1, 0, 1, 1, 0, 1])))
            print(list(itertools.compress([1, 2], [0, 0])))
            print(list(itertools.compress([1, 2], [])))
            print(list(itertools.compress([1, 2, 3], [1])))
            print(list(itertools.starmap(lambda a, b: a + b, [(1, 2), (3, 4)])))
            print(list(itertools.starmap(lambda a, b: a * b, [(2, 3)])))
            print(list(itertools.starmap(bool, [])))
            print(list(itertools.pairwise([1, 2, 3])))
            print(list(itertools.pairwise([1])))
            print(list(itertools.pairwise([])))
            print(list(itertools.pairwise("abc")))
            """
        );

        Assert.Equal(
            Lines(
                "[1, 2]",
                "[3, 1]",
                "[]",
                "[False, True, False]",
                "[0, 2, 4]",
                "[0, False, '']",
                "['A', 'C', 'D', 'F']",
                "[]",
                "[]",
                "[1]",
                "[3, 7]",
                "[6]",
                "[]",
                "[(1, 2), (2, 3)]",
                "[]",
                "[]",
                "[('a', 'b'), ('b', 'c')]"
            ),
            output
        );
    }

    [Fact]
    public void ProductCombinesPoolsInTupleOrder()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.product([1, 2], "ab")))
            print(list(itertools.product([1, 2])))
            print(list(itertools.product()))
            print(list(itertools.product([], [1])))
            print(list(itertools.product([1, 2], repeat=2)))
            print(list(itertools.product([1], repeat=0)))
            print(list(itertools.product([1, 2], [3], repeat=2)))
            print(list(itertools.product([1, 2], repeat=True)))
            """
        );

        Assert.Equal(
            Lines(
                "[(1, 'a'), (1, 'b'), (2, 'a'), (2, 'b')]",
                "[(1,), (2,)]",
                "[()]",
                "[]",
                "[(1, 1), (1, 2), (2, 1), (2, 2)]",
                "[()]",
                "[(1, 3, 1, 3), (1, 3, 2, 3), (2, 3, 1, 3), (2, 3, 2, 3)]",
                "[(1,), (2,)]"
            ),
            output
        );
    }

    [Fact]
    public void CombinatoricsEnumerateInCpythonsOrder()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.permutations([1, 2, 3])))
            print(list(itertools.permutations([1, 2, 3], 2)))
            print(list(itertools.permutations([1, 2], 0)))
            print(list(itertools.permutations([1, 2], 5)))
            print(list(itertools.permutations("ab")))
            print(list(itertools.combinations([1, 2, 3, 4], 2)))
            print(list(itertools.combinations([1, 2], 0)))
            print(list(itertools.combinations([1, 2], 3)))
            print(list(itertools.combinations_with_replacement([1, 2], 2)))
            print(list(itertools.combinations_with_replacement([], 2)))
            print(list(itertools.combinations_with_replacement([1], 0)))
            """
        );

        Assert.Equal(
            Lines(
                "[(1, 2, 3), (1, 3, 2), (2, 1, 3), (2, 3, 1), (3, 1, 2), (3, 2, 1)]",
                "[(1, 2), (1, 3), (2, 1), (2, 3), (3, 1), (3, 2)]",
                "[()]",
                "[]",
                "[('a', 'b'), ('b', 'a')]",
                "[(1, 2), (1, 3), (1, 4), (2, 3), (2, 4), (3, 4)]",
                "[()]",
                "[]",
                "[(1, 1), (1, 2), (2, 2)]",
                "[]",
                "[()]"
            ),
            output
        );
    }

    [Fact]
    public void ZipLongestFillsTheShortIterables()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.zip_longest([1, 2, 3], "ab")))
            print(list(itertools.zip_longest([1, 2], [], [3], fillvalue="-")))
            print(list(itertools.zip_longest()))
            print(list(itertools.zip_longest("ab", "cd", fillvalue=0)))
            print(list(itertools.zip_longest([1], [2], fillvalue=None)))
            """
        );

        Assert.Equal(
            Lines(
                "[(1, 'a'), (2, 'b'), (3, None)]",
                "[(1, '-', 3), (2, '-', '-')]",
                "[]",
                "[('a', 'c'), ('b', 'd')]",
                "[(1, 2)]"
            ),
            output
        );
    }

    [Fact]
    public void GroupBySharesTheIteratorAndKeysEachElementOnce()
    {
        var output = Run(
            """
            import itertools

            print([(key, list(group)) for key, group in itertools.groupby([1, 1, 2, 2, 3, 1])])
            print([(key, list(group)) for key, group in itertools.groupby("aabbbc")])
            print([(key, list(group)) for key, group in itertools.groupby([1, 2, 3], key=lambda x: x % 2)])
            print(list(itertools.groupby([])))
            print([(key, list(group)) for key, group in itertools.groupby([])])
            """
        );

        Assert.Equal(
            Lines(
                "[(1, [1, 1]), (2, [2, 2]), (3, [3]), (1, [1])]",
                "[('a', ['a', 'a']), ('b', ['b', 'b', 'b']), ('c', ['c'])]",
                "[(1, [1]), (0, [2]), (1, [3])]",
                "[]",
                "[]"
            ),
            output
        );
    }

    [Fact]
    public void AccumulateAppliesTheOperatorWithAnOptionalInitial()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.accumulate([1, 2, 3, 4])))
            print(list(itertools.accumulate([1, 2, 3], initial=10)))
            print(list(itertools.accumulate([1, 2, 3], lambda a, b: a * b)))
            print(list(itertools.accumulate(["a", "b"], initial="")))
            print(list(itertools.accumulate([], initial=5)))
            print(list(itertools.accumulate([1, 2], lambda a, b: a - b, initial=0)))
            """
        );

        Assert.Equal(
            Lines(
                "[1, 3, 6, 10]",
                "[10, 11, 13, 16]",
                "[1, 2, 6]",
                "['', 'a', 'ab']",
                "[5]",
                "[0, -1, -3]"
            ),
            output
        );
    }

    [Fact]
    public void TeeBuffersEachCopyIndependently()
    {
        var output = Run(
            """
            import itertools

            first, second = itertools.tee([1, 2, 3])
            print(list(first))
            print(list(second))
            a, b, c = itertools.tee("xy", 3)
            print(list(a), list(b), list(c))
            print(itertools.tee([1], 0))
            left, right = itertools.tee([1, 2, 3])
            print(next(left), next(left), next(right), list(left), list(right))
            """
        );

        Assert.Equal(
            Lines(
                "[1, 2, 3]",
                "[1, 2, 3]",
                "['x', 'y'] ['x', 'y'] ['x', 'y']",
                "()",
                "1 2 1 [3] [2, 3]"
            ),
            output
        );
    }

    [Fact]
    public void BatchedChunksTheInputAndHonoursStrict()
    {
        var output = Run(
            """
            import itertools

            print(list(itertools.batched("ABCDEFG", 3)))
            print(list(itertools.batched([1, 2, 3, 4], 2)))
            print(list(itertools.batched([1, 2, 3, 4], 2, strict=True)))
            print(list(itertools.batched([], 3)))
            print(list(itertools.batched([1, 2, 3], 10)))
            try:
                list(itertools.batched([1, 2, 3], 2, strict=True))
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            try:
                itertools.batched([1], 0)
            except Exception as exc:
                print("ERR", type(exc).__name__, exc)
            """
        );

        Assert.Equal(
            Lines(
                "[('A', 'B', 'C'), ('D', 'E', 'F'), ('G',)]",
                "[(1, 2), (3, 4)]",
                "[(1, 2), (3, 4)]",
                "[]",
                "[(1, 2, 3)]",
                "ERR ValueError batched(): incomplete batch",
                "ERR ValueError n must be at least one"
            ),
            output
        );
    }

    [Fact]
    public void IteratorTypesAreTheirOwnIterators()
    {
        var output = Run(
            """
            import itertools

            count_type = itertools.count
            print(type(count_type()))
            print(count_type().__class__.__name__, count_type.__name__)
            print(isinstance(itertools.count(), itertools.count))
            print(isinstance(itertools.repeat(1), itertools.count))
            iterator = itertools.chain([1])
            print(iter(iterator) is iterator)
            print(next(iterator), next(iterator, "default"))
            print(list(iterator))
            stream = iter([1, 2])
            print(next(stream), next(stream, "gone"), next(stream, "gone"))
            print(type(iter([])).__name__)
            print([name for name in ["count", "repeat", "cycle", "chain"]])
            """
        );

        Assert.Equal(
            Lines(
                "<class 'itertools.count'>",
                "count count",
                "True",
                "False",
                "True",
                "1 default",
                "[]",
                "1 2 gone",
                "list_iterator",
                "['count', 'repeat', 'cycle', 'chain']"
            ),
            output
        );
    }

    [Fact]
    public void LazinessIsPreservedPerIterator()
    {
        var output = Run(
            """
            import itertools

            LOG = []


            def src(n, tag="s"):
                index = 0
                while index < n:
                    LOG.append(tag + str(index))
                    yield index
                    index += 1


            class Probe:
                def __init__(self, n, tag):
                    self.n = n
                    self.tag = tag

                def __iter__(self):
                    LOG.append("iter:" + self.tag)
                    return src(self.n, self.tag)


            def reset():
                del LOG[:]


            def report(label):
                print(label, LOG)


            reset()
            chain = itertools.chain(Probe(2, "p"), Probe(2, "q"))
            print("chain-step", next(chain))
            report("chain-first")
            print("chain-rest", list(chain))
            report("chain-rest")

            reset()
            sliced = itertools.islice(src(10), 2)
            print("islice-partial", next(sliced), next(sliced))
            report("islice-partial")

            reset()
            print("takewhile", list(itertools.takewhile(lambda value: value < 2, src(10))))
            report("takewhile")

            reset()
            print("dropwhile", list(itertools.dropwhile(lambda value: value < 3, src(5))))
            report("dropwhile")

            reset()
            print("filterfalse", list(itertools.filterfalse(lambda value: value < 2, src(4))))
            report("filterfalse")

            reset()
            left, right = itertools.tee(src(3))
            print("tee", next(left), next(right), next(right), list(left), list(right))
            report("tee")

            reset()
            paired = itertools.pairwise(src(3))
            print("pairwise", next(paired))
            report("pairwise")

            reset()
            grouped = itertools.groupby(src(4), key=lambda value: value // 2)
            print("groupby", [(key, list(group)) for key, group in grouped])
            report("groupby")

            reset()
            print("cycle", list(itertools.islice(itertools.cycle(Probe(2, "c")), 3)))
            report("cycle")
            """
        );

        Assert.Equal(
            Lines(
                "chain-step 0",
                "chain-first ['iter:p', 'p0']",
                "chain-rest [1, 0, 1]",
                "chain-rest ['iter:p', 'p0', 'p1', 'iter:q', 'q0', 'q1']",
                "islice-partial 0 1",
                "islice-partial ['s0', 's1']",
                "takewhile [0, 1]",
                "takewhile ['s0', 's1', 's2']",
                "dropwhile [3, 4]",
                "dropwhile ['s0', 's1', 's2', 's3', 's4']",
                "filterfalse [2, 3]",
                "filterfalse ['s0', 's1', 's2', 's3']",
                "tee 0 0 1 [1, 2] [2]",
                "tee ['s0', 's1', 's2']",
                "pairwise (0, 1)",
                "pairwise ['s0', 's1']",
                "groupby [(0, [0, 1]), (1, [2, 3])]",
                "groupby ['s0', 's1', 's2', 's3']",
                "cycle [0, 1, 0]",
                "cycle ['iter:c', 'c0', 'c1']"
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
