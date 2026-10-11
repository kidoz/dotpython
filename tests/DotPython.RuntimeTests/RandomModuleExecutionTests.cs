using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class RandomModuleExecutionTests
{
    /// <summary>
    /// The runtime's own Mersenne Twister on CPython's `random` surface: the seeded
    /// streams are CPython's streams, and the state a `getstate` hands out round-trips
    /// through `setstate`.
    /// </summary>
    [Fact]
    public void TheRandomModuleMatchesCpython()
    {
        var output = Run(
            """
            import random
            print("names", sorted(n for n in dir(random) if not n.startswith("_") and n != "main"))
            print("all", random.__all__)
            print("constants", random.BPF, random.RECIP_BPF == 2 ** -53, round(random.NV_MAGICCONST, 12),
                  round(random.LOG4, 12), round(random.SG_MAGICCONST, 12), round(random.TWOPI, 12))
            print("inst", type(random._inst).__name__, random.random.__self__ is random._inst, random.Random.VERSION)
            random.seed(0)
            print("stream", random.random(), random.random(), random.random())
            random.seed(42)
            print("stream2", [round(random.random(), 12) for _ in range(5)])
            random.seed(0)
            print("bits", random.getrandbits(8), random.getrandbits(32), random.getrandbits(64), random.getrandbits(0))
            print("bits-state", random.getstate()[1][-1])
            random.seed(1)
            print("range", random.randrange(10), random.randrange(5, 15), random.randrange(0, 100, 7), random.randrange(10, 0, -1))
            random.seed(2)
            print("randint", random.randint(1, 6), random.randint(1, 6), random.randint(1, 6))
            random.seed(3)
            print("choice", random.choice("abcdef"), random.choice([1, 2, 3]), random.choices("abc", k=5))
            print("choices", random.choices([1, 2, 3], weights=[1, 1, 1], k=4), random.choices([1, 2, 3], cum_weights=[1, 2, 4], k=3))
            random.seed(4)
            print("sample", random.sample(range(10), 3), random.sample("abcdef", 3), random.sample(range(5), 5))
            print("sample-counts", random.sample(["red", "blue"], counts=[4, 2], k=5), random.sample(range(30), 6))
            random.seed(5)
            items = [1, 2, 3, 4, 5]
            random.shuffle(items)
            print("shuffle", items)
            random.seed(6)
            print("uniform", random.uniform(1, 2), random.uniform(-1, 1), random.triangular(0, 10, 5), random.triangular(0, 10))
            random.seed(7)
            print("gauss", random.gauss(0, 1), random.gauss(0, 1), random.gauss(0, 1), random.normalvariate(0, 1))
            random.seed(8)
            print("dist", random.expovariate(1), random.lognormvariate(0, 1), random.betavariate(2, 3), random.gammavariate(2, 3))
            random.seed(9)
            print("dist2", random.paretovariate(1), random.weibullvariate(1, 1), random.vonmisesvariate(0, 1), random.binomialvariate(10, 0.5))
            random.seed(10)
            print("binomial", [random.binomialvariate(20, 0.3) for _ in range(4)], [random.binomialvariate(2000, 0.2) for _ in range(3)])
            print("gamma-small", random.gammavariate(0.5, 2), random.gammavariate(1.0, 2), random.binomialvariate(1, 0.25))
            print("seed-forms", end=" ")
            for seed in [0, 1, -1, 2**40, 3.5, "abc", b"abc", bytearray(b"abc")]:
                random.seed(seed)
                print(round(random.random(), 12), end=" ")
            print()
            random.seed("abc", version=1)
            print("v1-v2", round(random.random(), 12), end=" ")
            random.seed("abc")
            print(round(random.random(), 12))
            state = random.getstate()
            print("state", type(state).__name__, len(state), state[0], len(state[1]), state[1][:4], state[1][-1], state[2])
            random.seed(99)
            print("after", round(random.random(), 12))
            random.setstate(state)
            print("restore", round(random.random(), 12))
            print("gauss-cache", random.Random(1).gauss(0, 1), random.Random(1).gauss_next)
            r = random.Random(5)
            print("gauss-next", r.gauss(0, 1), r.gauss_next is None, r.gauss(0, 1), r.gauss(0, 1))
            r.gauss_next = 3.0
            print("gauss-set", r.gauss(0, 1), r.gauss_next)
            print("randbytes", random.Random(3).randbytes(4), random.Random(3).randbytes(0), len(random.Random(3).randbytes(10)))
            print("ctor", random.Random(7).random(), random.Random().random() != random.Random().random())
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            report("seed-tuple", lambda: random.seed((1, 2)))
            report("randrange-empty", lambda: random.randrange(0))
            report("randrange-equal", lambda: random.randrange(5, 5))
            report("randrange-zero-step", lambda: random.randrange(1, 2, 0))
            report("randint-empty", lambda: random.randint(5, 4))
            report("choice-empty", lambda: random.choice([]))
            report("sample-big", lambda: random.sample([1, 2], 3))
            report("sample-set", lambda: random.sample({1, 2}, 1))
            report("choices-both", lambda: random.choices([1, 2], weights=[1, 2], cum_weights=[1, 2]))
            report("choices-empty", lambda: random.choices([], k=1))
            report("getrandbits-neg", lambda: random.getrandbits(-1))
            report("shuffle-int", lambda: random.shuffle(5))
            report("setstate-bad", lambda: random.setstate((1, (1,), None)))
            report("gamma-zero", lambda: random.gammavariate(0, 1))
            report("binomial-bad", lambda: random.binomialvariate(1, 1.5))
            report("random-args", lambda: random.random(1))
            report("uniform-str", lambda: random.uniform("a", 1))
            sys_rand = random.SystemRandom()
            print("system", type(sys_rand).__name__, isinstance(sys_rand, random.Random), sys_rand.seed(4),
                   len(sys_rand.randbytes(5)), sys_rand.getrandbits(0), 0 <= sys_rand.random() < 1, 0 <= sys_rand.getrandbits(64) < 2**64)
            report("system-state", lambda: sys_rand.getstate())
            """
        );
        Assert.Equal(
            Lines(
                "names ['BPF', 'LOG4', 'NV_MAGICCONST', 'RECIP_BPF', 'Random', 'SG_MAGICCONST', 'SystemRandom', 'TWOPI', 'betavariate', 'binomialvariate', 'choice', 'choices', 'expovariate', 'gammavariate', 'gauss', 'getrandbits', 'getstate', 'lognormvariate', 'normalvariate', 'paretovariate', 'randbytes', 'randint', 'random', 'randrange', 'sample', 'seed', 'setstate', 'shuffle', 'triangular', 'uniform', 'vonmisesvariate', 'weibullvariate']",
                "all ['Random', 'SystemRandom', 'betavariate', 'binomialvariate', 'choice', 'choices', 'expovariate', 'gammavariate', 'gauss', 'getrandbits', 'getstate', 'lognormvariate', 'normalvariate', 'paretovariate', 'randbytes', 'randint', 'random', 'randrange', 'sample', 'seed', 'setstate', 'shuffle', 'triangular', 'uniform', 'vonmisesvariate', 'weibullvariate']",
                "constants 53 True 1.715527769921 1.38629436112 2.504077396776 6.28318530718",
                "inst Random True 3",
                "stream 0.8444218515250481 0.7579544029403025 0.420571580830845",
                "stream2 [0.639426798458, 0.025010755223, 0.275029318369, 0.223210738149, 0.736471214164]",
                "bits 216 1654615998 16422101724900707500 0",
                "bits-state 4",
                "range 2 14 91 9",
                "randint 1 1 1",
                "choice b 3 ['b', 'b', 'b', 'b', 'a']",
                "choices [1, 3, 1, 1] [3, 2, 3]",
                "sample [3, 4, 1] ['f', 'd', 'e'] [1, 0, 3, 2, 4]",
                "sample-counts ['blue', 'red', 'red', 'red', 'red'] [8, 24, 5, 26, 3, 29]",
                "shuffle [1, 2, 4, 3, 5]",
                "uniform 1.793340083761663 0.6439080846394536 4.924604694444749 3.616776762150644",
                "gauss -0.2558802884476004 0.511431512516514 -0.2260961647831047 0.0970447726200016",
                "dist 0.2570957845082924 1.4682742560312607 0.3885275384772589 5.458488605906913",
                "dist2 1.8622229085506328 0.4673063603802309 6.069137101729387 5",
                "binomial [7, 5, 8, 5] [389, 427, 403]",
                "gamma-small 0.011842354674761529 3.71666921580018 1",
                "seed-forms 0.844421851525 0.134364244112 0.134364244112 0.103639456281 0.303919012483 0.772024631416 0.772024631416 0.772024631416 ",
                "v1-v2 0.096393749788 0.772024631416",
                "state tuple 3 3 625 (1791233174, 2259565249, 421998574, 119748381) 2 None",
                "after 0.403978074944",
                "restore 0.55816913739",
                "gauss-cache 1.2881847531554629 None",
                "gauss-next -1.1788417512306717 False -1.1481606807908016 0.6694689143859696",
                "gauss-set 3.0 None",
                "randbytes b'\\xfd?\\xeb<' b'' 10",
                "ctor 0.32383276483316237 True",
                "seed-tuple TypeError The only supported seed types are:",
                "None, int, float, str, bytes, and bytearray.",
                "randrange-empty ValueError empty range for randrange()",
                "randrange-equal ValueError empty range in randrange(5, 5)",
                "randrange-zero-step ValueError zero step for randrange()",
                "randint-empty ValueError empty range in randint(5, 4)",
                "choice-empty IndexError Cannot choose from an empty sequence",
                "sample-big ValueError Sample larger than population or is negative",
                "sample-set TypeError Population must be a sequence.  For dicts or sets, use sorted(d).",
                "choices-both TypeError Cannot specify both weights and cumulative weights",
                "choices-empty IndexError list index out of range",
                "getrandbits-neg ValueError Cannot convert negative int",
                "shuffle-int TypeError object of type 'int' has no len()",
                "setstate-bad ValueError state with version 1 passed to Random.setstate() of version 3",
                "gamma-zero ValueError gammavariate: alpha and beta must be > 0.0",
                "binomial-bad ValueError p must be in the range 0.0 <= p <= 1.0",
                "random-args TypeError Random.random() takes no arguments (1 given)",
                "uniform-str TypeError unsupported operand type(s) for -: 'int' and 'str'",
                "system SystemRandom True None 5 0 True True",
                "system-state NotImplementedError System entropy source does not have state."
            ),
            output
        );
    }

    /// <summary>
    /// One instance's stream is another's: the same seed, whatever sequence of calls the
    /// two make in between, and `__reduce__` carries the state that restores it.
    /// </summary>
    [Fact]
    public void SeededInstancesReproduceTheirStreams()
    {
        var output = Run(
            """
            import random
            first = random.Random("seed")
            second = random.Random("seed")
            print("same", [first.random() == second.random() for _ in range(3)])
            state = first.getstate()
            expected = [first.random(), first.random()]
            factory, args, restored = first.__reduce__()
            print("reduce", factory is random.Random, args, restored == first.getstate())
            second.setstate(state)
            print("restored", [second.random() for _ in range(2)] == expected)
            print("names", type(first).__name__, isinstance(first, random.Random), isinstance(random.SystemRandom(), random.Random))
            """
        );
        Assert.Equal(
            Lines(
                "same [True, True, True]",
                "reduce True () True",
                "restored True",
                "names Random True True"
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
