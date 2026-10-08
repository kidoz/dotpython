using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ItertoolsCompatibilityTests
{
    [Fact]
    public Task CountIsInfiniteAndAdvancesByItsStep() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task RepeatIsBoundedAndRejectsBadTimes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print(list(itertools.repeat("x", 3)))
            print(len(list(itertools.repeat("x", 0))))
            print(len(list(itertools.repeat("x", -3))))
            print(list(itertools.islice(itertools.repeat("x"), 4)))
            print(repr(itertools.repeat("a")), repr(itertools.repeat("a", 3)))

            for bad in (1.5, "x", None):
                try:
                    itertools.repeat(1, bad)
                except Exception as error:
                    print(type(error).__name__, error)

            try:
                itertools.repeat(1, 10**30)
            except Exception as error:
                print(type(error).__name__, error)
            """
        );

    [Fact]
    public Task CycleRestartsTheInputWithoutConsumingItUpFront() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task ChainIsLazyAcrossItsArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print(list(itertools.chain([1, 2], [3], [], (4, 5))))
            print(list(itertools.chain.from_iterable([[1], [2, 3], []])))
            print(list(itertools.chain.from_iterable("ab")))
            print(list(itertools.chain(range(2), "cd")))
            print(list(itertools.chain()))

            log = []

            class Probe:
                def __init__(self, name):
                    self.name = name

                def __iter__(self):
                    log.append("iter:" + self.name)
                    return iter([1, 2])

            chained = itertools.chain(Probe("p"), Probe("q"))
            print(next(chained), log)
            print(list(chained), log)
            """
        );

    [Fact]
    public Task IsliceConsumesExactlyTheIndicesItNeeds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print(list(itertools.islice([1, 2, 3, 4, 5], 3)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 1, 3)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 0, 5, 2)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 1, None, 2)))
            print(list(itertools.islice([1, 2, 3, 4, 5], None, 2)))
            print(list(itertools.islice([1, 2, 3, 4, 5], 2, 2)))
            print(list(itertools.islice([], 3)))

            log = []

            def source(count):
                index = 0
                while index < count:
                    log.append(index)
                    yield index
                    index += 1

            sliced = itertools.islice(source(10), 2)
            print(next(sliced), next(sliced), log)
            print(list(itertools.islice(source(10), 2, 4)), log)

            for bad in (-1, "x", 1.5):
                try:
                    list(itertools.islice([1, 2, 3], bad))
                except Exception as error:
                    print(type(error).__name__, error)

            try:
                itertools.islice([1], 1, 2, 3, 4)
            except Exception as error:
                print(type(error).__name__, error)
            """
        );

    [Fact]
    public Task PredicateIteratorsFollowTheirStoppingRules() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            print(list(itertools.starmap(bool, [])))
            print(list(itertools.pairwise([1, 2, 3])))
            print(list(itertools.pairwise([1])), list(itertools.pairwise([])))
            print(list(itertools.pairwise("abc")))
            """
        );

    [Fact]
    public Task ProductCombinesPoolsInTupleOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            print(list(itertools.product([1, 2], repeat=False)))
            """
        );

    [Fact]
    public Task CombinatoricsEnumerateInCpythonsOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
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

            for call in (
                lambda: itertools.permutations([1, 2], -1),
                lambda: itertools.combinations([1, 2], -1),
                lambda: itertools.combinations_with_replacement([1, 2], -1),
            ):
                try:
                    call()
                except Exception as error:
                    print(type(error).__name__, error)
            """
        );

    [Fact]
    public Task ZipLongestFillsTheShortIterables() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print(list(itertools.zip_longest([1, 2, 3], "ab")))
            print(list(itertools.zip_longest([1, 2], [], [3], fillvalue="-")))
            print(list(itertools.zip_longest()))
            print(list(itertools.zip_longest("ab", "cd", fillvalue=0)))
            print(list(itertools.zip_longest([1], [2], fillvalue=None)))
            """
        );

    [Fact]
    public Task GroupBySharesTheIteratorAndKeysEachElementOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print([(key, list(group)) for key, group in itertools.groupby([1, 1, 2, 2, 3, 1])])
            print([(key, list(group)) for key, group in itertools.groupby("aabbbc")])
            print([(key, list(group)) for key, group in itertools.groupby([1, 2, 3], key=lambda x: x % 2)])
            print(list(itertools.groupby([])))
            print([(key, list(group)) for key, group in itertools.groupby([])])

            calls = []

            def key(value):
                calls.append(value)
                return value // 2

            grouped = itertools.groupby(iter([0, 1, 2, 3]), key=key)
            print([(group_key, list(group)) for group_key, group in grouped])
            print(calls)
            """
        );

    [Fact]
    public Task AccumulateAppliesTheOperatorWithAnOptionalInitial() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print(list(itertools.accumulate([1, 2, 3, 4])))
            print(list(itertools.accumulate([1, 2, 3], initial=10)))
            print(list(itertools.accumulate([1, 2, 3], lambda a, b: a * b)))
            print(list(itertools.accumulate(["a", "b"], initial="")))
            print(list(itertools.accumulate([], initial=5)))
            print(list(itertools.accumulate([1, 2], lambda a, b: a - b, initial=0)))
            print(list(itertools.accumulate([1, 2], initial=0, func=lambda a, b: a + b)))
            """
        );

    [Fact]
    public Task TeeBuffersEachCopyIndependently() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            first, second = itertools.tee([1, 2, 3])
            print(list(first))
            print(list(second))
            left, middle, right = itertools.tee("xy", 3)
            print(list(left), list(middle), list(right))
            print(itertools.tee([1], 0))
            shared_left, shared_right = itertools.tee([1, 2, 3])
            print(next(shared_left), next(shared_left), next(shared_right))
            print(list(shared_left), list(shared_right))
            """
        );

    [Fact]
    public Task BatchedChunksTheInputAndHonoursStrict() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print(list(itertools.batched("ABCDEFG", 3)))
            print(list(itertools.batched([1, 2, 3, 4], 2)))
            print(list(itertools.batched([1, 2, 3, 4], 2, strict=True)))
            print(list(itertools.batched([], 3)))
            print(list(itertools.batched([1, 2, 3], 10)))

            for call in (
                lambda: list(itertools.batched([1, 2, 3], 2, strict=True)),
                lambda: list(itertools.batched([1, 2, 3], 10, strict=True)),
            ):
                try:
                    call()
                except Exception as error:
                    print(type(error).__name__, error)

            for bad in (0, -1):
                try:
                    list(itertools.batched([1], bad))
                except Exception as error:
                    print(type(error).__name__, error)
            """
        );

    [Fact]
    public Task IteratorTypesAreTheirOwnIterators() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            print(type(itertools.count()))
            print(itertools.count().__class__.__name__, itertools.count.__name__)
            print(isinstance(itertools.count(), itertools.count))
            print(isinstance(itertools.repeat(1), itertools.count))
            print(isinstance(itertools.chain([1]), itertools.chain))
            chained = itertools.chain([1])
            print(iter(chained) is chained)
            print(next(chained), next(chained, "default"))
            print(list(chained), next(chained, "default"))
            print(next(iter([1, 2])), type(iter([])).__name__)
            """
        );

    [Fact]
    public Task LazinessIsPreservedPerIterator() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            log = []

            def source(count, tag="s"):
                index = 0
                while index < count:
                    log.append(tag + str(index))
                    yield index
                    index += 1

            class Probe:
                def __init__(self, count, tag):
                    self.count = count
                    self.tag = tag

                def __iter__(self):
                    log.append("iter:" + self.tag)
                    return source(self.count, self.tag)

            def reset():
                del log[:]

            reset()
            chained = itertools.chain(Probe(2, "p"), Probe(2, "q"))
            print("chain", next(chained), log)
            print("chain-rest", list(chained), log)

            reset()
            sliced = itertools.islice(source(10), 2)
            print("islice", next(sliced), next(sliced), log)
            print("islice-rest", list(sliced), log)

            reset()
            print("takewhile", list(itertools.takewhile(lambda value: value < 2, source(10))), log)

            reset()
            print("dropwhile", list(itertools.dropwhile(lambda value: value < 3, source(5))), log)

            reset()
            print("filterfalse", list(itertools.filterfalse(lambda value: value < 2, source(4))), log)

            reset()
            left, right = itertools.tee(source(3))
            print("tee", next(left), next(right), next(right), list(left), list(right), log)

            reset()
            paired = itertools.pairwise(source(3))
            print("pairwise", next(paired), log)

            reset()
            grouped = itertools.groupby(source(4), key=lambda value: value // 2)
            print("groupby", [(key, list(group)) for key, group in grouped], log)

            reset()
            print("cycle", list(itertools.islice(itertools.cycle(Probe(2, "c")), 3)), log)

            reset()
            repeated = itertools.repeat("x", 2)
            print("repeat", next(repeated, "done"), next(repeated, "done"), next(repeated, "done"), log)
            """
        );

    [Fact]
    public Task ArgumentErrorsFollowCpythonsMessages() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            calls = [
                lambda: itertools.count(1, 2, 3),
                lambda: itertools.cycle(),
                lambda: itertools.chain(1),
                lambda: itertools.compress([1]),
                lambda: itertools.pairwise(),
                lambda: itertools.takewhile(lambda x: x),
                lambda: itertools.starmap(bool),
                lambda: itertools.tee([1], 2, 3),
                lambda: itertools.accumulate([1], lambda a, b: a, 0),
                lambda: itertools.islice([1]),
                lambda: itertools.groupby(1),
                lambda: itertools.zip_longest([1], fillvalue=0, fill=1),
            ]
            for call in calls:
                try:
                    call()
                except Exception as error:
                    print(type(error).__name__, error)

            keywords = [
                lambda: itertools.count(stat=1),
                lambda: itertools.count(starting=1),
                lambda: itertools.repeat(1, objects=2),
                lambda: itertools.repeat(1, objectvalue=1),
                lambda: itertools.accumulate([1], fn=1),
                lambda: itertools.accumulate([1], function=1),
                lambda: itertools.accumulate([1], inital=1),
                lambda: itertools.accumulate([1], initialvalue=1),
                lambda: itertools.permutations([1], rr=1),
                lambda: itertools.permutations([1], r=1.5),
                lambda: itertools.product([1], rep=1),
                lambda: itertools.product([1], repetitions=1),
                lambda: itertools.batched([1], size=1),
                lambda: itertools.batched([1], stric=1),
                lambda: itertools.groupby([1], keyf=1),
                lambda: itertools.compress([1], selector=1),
            ]
            for call in keywords:
                try:
                    call()
                except Exception as error:
                    print(type(error).__name__, error)
            """
        );
}
