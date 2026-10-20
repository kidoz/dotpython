using Xunit;

namespace DotPython.DifferentialTests;

public sealed class StatisticsFractionCompatibilityTests
{
    /// <summary>
    /// `statistics` with `Fraction` inputs, matching CPython's type coercion and exact
    /// arithmetic: the measures that keep the exact type, the ones that convert to float,
    /// mixed int/float/bool data, the harmonic mean's exact weighted sums, the Counter
    /// grouping behind `mode`/`multimode`, exact median/quantile interpolation, and the
    /// refusal wording for values that carry no numerator and denominator.
    /// </summary>
    [Fact]
    public Task StatisticsCarriesFractionsLikeCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
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
            """"
        );
}
