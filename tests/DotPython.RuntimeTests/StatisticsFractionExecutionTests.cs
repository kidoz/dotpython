using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class StatisticsFractionExecutionTests
{
    /// <summary>
    /// `statistics` over `Fraction` inputs: the exact rational sums feed every measure,
    /// and `_coerce` decides which measure keeps the exact type — `mean`, `variance`,
    /// `pvariance`, `harmonic_mean`, the even `median` and the `quantiles` keep
    /// `Fraction`, while `fmean`, `stdev`, `pstdev`, `covariance`, `correlation`,
    /// `linear_regression`, `geometric_mean` and `NormalDist` stay float. Mixed inputs
    /// follow CPython's coercion (ints join the other type, fractions and floats meet at
    /// float), the Counter behind `mode` groups numerically equal keys (`1`, `1.0` and
    /// `Fraction(1, 1)` share a count), and the exact comparisons sort Fractions without
    /// a float round trip. The decimal module is absent, so `Decimal` inputs report the
    /// missing module; that local answer is pinned at the end.
    /// </summary>
    [Fact]
    public void TheStatisticsModuleCarriesFractionsAsCpythonDoes()
    {
        var output = Run(
            """"
            import statistics as st
            from fractions import Fraction as F
            def show(label, *thunks):
                results = []
                for thunk in thunks:
                    try:
                        results.append(repr(thunk()))
                    except Exception as error:
                        results.append(type(error).__name__ + ": " + str(error))
                print(label, *results)
            data = [F(1, 2), F(1, 3), F(3, 4), F(1, 6)]
            show("mean", lambda: st.mean(data), lambda: st.mean([F(1), F(2), F(3)]), lambda: st.mean([F(1, 2), 1]), lambda: st.mean([F(1, 2), 0.5]), lambda: st.mean([True, F(1, 2)]), lambda: st.mean([F(1,2), "x"]))
            show("fmean", lambda: st.fmean(data), lambda: st.fmean(data, [1, 2, 3, 4]), lambda: st.fmean([F(1, 2), 1]))
            show("geo", lambda: st.geometric_mean([F(1), F(4), F(16)]), lambda: st.geometric_mean([F(1, 2), F(1, 8)]))
            show("harm", lambda: st.harmonic_mean([F(1), F(2), F(4)]), lambda: st.harmonic_mean([F(1), F(2)], [F(1), F(2)]))
            show("median", lambda: st.median([F(1), F(3), F(5)]), lambda: st.median([F(1), F(3)]), lambda: st.median([F(1), F(2), F(3), F(4)]), lambda: st.median_low([F(1), F(3)]), lambda: st.median_high([F(1), F(3)]), lambda: st.median_grouped([F(1), F(3), F(3), F(5)]), lambda: st.median_grouped([F(1), F(3), F(3), F(5)], 2))
            show("mode", lambda: st.mode([F(1, 2), F(1, 2), F(1, 3)]), lambda: st.multimode([F(1), F(1), F(2)]))
            show("var", lambda: st.variance(data), lambda: st.pvariance(data), lambda: st.variance([F(1), F(2), F(3)]), lambda: st.variance([F(1,2), 0.5]), lambda: st.variance([F(1), 2]))
            show("stdev", lambda: st.stdev(data), lambda: st.pstdev(data), lambda: st.stdev([F(1), F(2), F(3), F(5)]))
            show("cov", lambda: st.covariance([F(1), F(2), F(3)], [F(3), F(2), F(1)]), lambda: st.covariance(data, data))
            show("corr", lambda: st.correlation([F(1), F(2), F(3)], [F(3), F(2), F(1)]), lambda: st.correlation([F(1), F(2), F(3)], [F(1), F(2), F(3)]), lambda: st.correlation([F(1), F(2), F(3)], [F(3), F(2), F(1)], method='ranked'))
            show("linreg", lambda: st.linear_regression([F(1), F(2), F(3), F(4)], [F(2), F(4), F(6), F(8)]), lambda: st.linear_regression([F(1), F(2), F(3)], [F(1), F(3), F(7)], proportional=True))
            show("quant", lambda: st.quantiles([F(1), F(2), F(3), F(4)], n=2), lambda: st.quantiles([F(1), F(2), F(3), F(4)], n=4), lambda: st.quantiles([F(1), F(2), F(3), F(4)], n=2, method='inclusive'), lambda: st.quantiles([F(1)], n=3), lambda: st.quantiles([1, F(2), 3.0], n=2))
            show("nd", lambda: st.NormalDist.from_samples(data), lambda: st.NormalDist.from_samples([F(1), F(2), F(3), F(4)]))
            show("ndm", lambda: st.NormalDist(1, 2).samples(1, seed=1))
            show("mixederr", lambda: st.variance([F(1), F(2), "x"]), lambda: st.mean([F(1), F(2), b"x"]))
            show("coerce", lambda: st.mean([F(1, 2), 1, True]), lambda: st.variance([F(1), F(2), F(3), F(4)]), lambda: st.mean([F(1), 1.0]), lambda: st.pvariance([F(1, 2), 0.5]))
            show("mixmode", lambda: st.mode([1, F(1), F(2)]), lambda: st.multimode([1, F(1), F(2)]), lambda: st.multimode([1, 1.0, 2]), lambda: st.mode([F(1), 1.0, 2]), lambda: st.multimode(["a", "a", "b"]))
            show("complex", lambda: st.mean([F(1), 1j]), lambda: st.variance([F(1), 1j]))
            show("quant2", lambda: st.quantiles([F(1,3), F(2,3), F(1), F(4,3)], n=4), lambda: st.quantiles([1, 2, 3, 4], n=4), lambda: st.quantiles([F(1), F(2), F(3)], n=2, method='inclusive'))
            show("median-mixed", lambda: st.median([1, F(3)]), lambda: st.median([1.0, F(3)]), lambda: st.median([F(1), 3]), lambda: st.median_grouped([F(1), F(3), F(3), F(5)], F(1)))
            show("fmean-big", lambda: st.fmean([F(10**20), F(1, 3)]))
            show("stdev-frac", lambda: st.stdev([F(1,3), F(2,3)]), lambda: st.pstdev([F(1,3), F(2,3)]), lambda: st.stdev([F(1), F(2)]))
            show("corr-ranked-frac", lambda: st.correlation([F(1), F(2), F(2), F(3)], [F(1), F(3), F(2), F(4)], method='ranked'))
            show("nd-frac", lambda: st.NormalDist.from_samples([F(1,3), F(2,3)]), lambda: st.NormalDist.from_samples([F(1), F(2), F(3), F(4)]))
            show("harm-extra", lambda: st.harmonic_mean([F(0)]), lambda: st.harmonic_mean([F(0), F(1)]), lambda: st.harmonic_mean([F(1,2)], [0]), lambda: st.harmonic_mean([1,2],[F(1),F(2)]), lambda: st.harmonic_mean([F(1),F(2)],[1.0,2.0]), lambda: st.harmonic_mean(["a"]), lambda: st.harmonic_mean(["a","b"]), lambda: st.harmonic_mean([1,2], ["a","b"]))
            show("sumprod", lambda: st.covariance([F(1,3), F(2,3), F(1)], [F(1), F(1,2), F(1,3)]))
            show("local-decimal", lambda: st.mean([__import__("decimal").Decimal("0.5")]))
            """"
        );
        Assert.Equal(
            Lines(
                "mean Fraction(7, 16) Fraction(2, 1) Fraction(3, 4) 0.5 Fraction(3, 4) TypeError: can't convert type 'str' to numerator/denominator",
                "fmean 0.4375 0.4083333333333333 0.75",
                "geo 4.0 0.25",
                "harm Fraction(12, 7) Fraction(3, 2)",
                "median Fraction(3, 1) Fraction(2, 1) Fraction(5, 2) Fraction(1, 1) Fraction(3, 1) 3.0 3.0",
                "mode Fraction(1, 2) [Fraction(1, 1)]",
                "var Fraction(107, 1728) Fraction(107, 2304) Fraction(1, 1) 0.0 Fraction(1, 2)",
                "stdev 0.24883990093290162 0.21550167568309583 1.707825127659933",
                "cov -1.0 0.06192129629629631",
                "corr -1.0 1.0 -1.0",
                "linreg LinearRegression(slope=2.0, intercept=0.0) LinearRegression(slope=2.0, intercept=0.0)",
                "quant [Fraction(5, 2)] [Fraction(5, 4), Fraction(5, 2), Fraction(15, 4)] [Fraction(5, 2)] [Fraction(1, 1), Fraction(1, 1)] [2.0]",
                "nd NormalDist(mu=0.4375, sigma=0.24883990093290162) NormalDist(mu=2.5, sigma=1.2909944487358056)",
                "ndm [-1.2119909024619617]",
                "mixederr TypeError: can't convert type 'str' to numerator/denominator TypeError: can't convert type 'bytes' to numerator/denominator",
                "coerce Fraction(5, 6) Fraction(5, 3) 1.0 0.0",
                "mixmode 1 [1] [1] Fraction(1, 1) ['a']",
                "complex TypeError: can't convert type 'complex' to numerator/denominator TypeError: can't convert type 'complex' to numerator/denominator",
                "quant2 [Fraction(5, 12), Fraction(5, 6), Fraction(5, 4)] [1.25, 2.5, 3.75] [Fraction(2, 1)]",
                "median-mixed Fraction(2, 1) 2.0 Fraction(2, 1) 3.0",
                "fmean-big 5e+19",
                "stdev-frac 0.23570226039551584 0.16666666666666666 0.7071067811865476",
                "corr-ranked-frac 0.9486832980505138",
                "nd-frac NormalDist(mu=0.5, sigma=0.23570226039551584) NormalDist(mu=2.5, sigma=1.2909944487358056)",
                "harm-extra Fraction(0, 1) 0 StatisticsError: Weighted sum must be positive Fraction(3, 2) 1.5 TypeError: unsupported type TypeError: '<' not supported between instances of 'str' and 'int' TypeError: '<' not supported between instances of 'str' and 'int'",
                "sumprod -0.11111111111111112",
                "local-decimal Decimal('0.5')"
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
