using Xunit;

namespace DotPython.DifferentialTests;

public sealed class RandomModuleCompatibilityTests
{
    [Fact]
    public Task TheRandomModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
}
