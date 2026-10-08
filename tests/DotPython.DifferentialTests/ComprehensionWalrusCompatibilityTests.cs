using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// Assignment expressions bind in the scope that contains the comprehension. The
/// rejected placements are compile-time errors the oracle harness cannot express, so
/// they are covered by the paired execution and binder tests instead.
/// </summary>
public sealed class ComprehensionWalrusCompatibilityTests
{
    [Fact]
    public Task WalrusInAComprehensionBindsInTheEnclosingScope() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            x = 0
            [(x := i) for i in range(3)]
            print(x)

            def f():
                [(w := i) for i in range(3)]
                return w
            print(f())

            print([y for i in range(3) if (y := i) >= 1])
            print(y)
            print([a for x in [1, 2] for a in [x] if (b := a) > 0], b)
            """
        );

    [Fact]
    public Task EveryComprehensionKindSharesTheEnclosingBinding() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def nested():
                [[(z := j) for j in range(2)] for i in range(2)]
                return z
            def generator():
                lazy = ((s := i) for i in range(3))
                return next(lazy), s
            print(nested())
            print(generator())
            print([(q := i) for i in range(3)], q)
            print({(r := i) for i in range(3)}, r)
            print({(t := i): t for i in range(3)}, t)
            print([(x := i) + x for i in range(3)])
            """
        );

    [Fact]
    public Task DeclaredGlobalAndParameterTargetsKeepTheirRouting() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def declared_global():
                global gx
                [(gx := i) for i in range(3)]
                return gx
            def parameter(x):
                [(x := i) for i in range(3)]
                return x
            print(declared_global(), gx)
            print(parameter(9))
            """
        );

    [Fact]
    public Task AnUnfilledComprehensionLeavesTheTargetUnbound() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def f():
                [(u := i) for i in range(0)]
                return u
            try:
                f()
            except UnboundLocalError:
                print('unbound local in function')
            try:
                [(v := i) for i in range(0)]
                print(v)
            except NameError:
                print('unbound global at module scope')
            """
        );

    [Fact]
    public Task PlacementsCpythonAllowsKeepWorking() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            (x := 1)
            print(x)
            print([i for i in range(3) if (z := i) >= 1])
            print(z)
            print([(lambda: (i := 1)) for i in range(1)][0]())
            print([(q := 1) for _ in range(1)] + [(q := 2) for _ in range(1)])
            """
        );
}
