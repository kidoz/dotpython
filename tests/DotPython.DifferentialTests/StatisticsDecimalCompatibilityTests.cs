using Xunit;

namespace DotPython.DifferentialTests;

public sealed class StatisticsDecimalCompatibilityTests
{
    /// <summary>
    /// `statistics` with `Decimal` inputs: the measures that keep the exact type, the
    /// float measures, the `_coerce` refusals, the special values, the Counter grouping
    /// behind `mode`, the relation measures' float subtraction refusal for Decimal data,
    /// and `Fraction.from_decimal`'s exact conversion.
    /// </summary>
    [Fact]
    public Task StatisticsCarriesDecimalsLikeCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            import statistics as st
            from decimal import Decimal as D
            from fractions import Fraction as F
            def show(label, *thunks):
                results = []
                for thunk in thunks:
                    try:
                        results.append(repr(thunk()))
                    except Exception as error:
                        results.append(type(error).__name__ + ": " + str(error))
                print(label, *results)
            data = [D("1.5"), D("2.25"), D("3.75"), D("0.5")]
            show("mean", lambda: st.mean(data), lambda: st.mean([D("1"), D("2"), D("3")]), lambda: st.mean([D("1.5"), 1]), lambda: st.mean([True, D("0.5")]))
            show("meanerr", lambda: st.mean([D("1"), 1.0]), lambda: st.mean([D("1"), F(1,2)]), lambda: st.mean([F(1,2), D("1")]), lambda: st.mean([D("1"), "x"]))
            show("fmean", lambda: st.fmean(data), lambda: st.fmean(data, [1, 2, 3, 4]))
            show("geo", lambda: st.geometric_mean([D("1"), D("4"), D("16")]))
            show("harm", lambda: st.harmonic_mean([D("1"), D("2"), D("4")]), lambda: st.harmonic_mean([D("1"), D("2")], [1, 2]))
            show("median", lambda: st.median([D("1"), D("3"), D("5")]), lambda: st.median([D("1"), D("3")]), lambda: st.median([D("1"), D("2"), D("3"), D("4")]), lambda: st.median_low([D("1"), D("3")]), lambda: st.median_high([D("1"), D("3")]), lambda: st.median_grouped([D("1"), D("3"), D("3"), D("5")]))
            show("var", lambda: st.variance(data), lambda: st.pvariance(data), lambda: st.variance([D("1"), D("2"), D("3")]), lambda: st.variance([D("1.5"), 2]))
            show("stdev", lambda: st.stdev(data), lambda: st.pstdev(data), lambda: st.stdev([D("1"), D("2"), D("3"), D("5")]), lambda: st.pstdev([D("1.5"), D("2.5")]))
            show("quant", lambda: st.quantiles([D("1"), D("2"), D("3"), D("4")], n=2), lambda: st.quantiles([D("1"), D("2"), D("3"), D("4")], n=4), lambda: st.quantiles([D("1")], n=3), lambda: st.quantiles([1, D("2"), 3.0], n=2))
            show("cov", lambda: st.covariance([D("1"), D("2"), D("3")], [D("3"), D("2"), D("1")]))
            show("corr", lambda: st.correlation([D("1"), D("2"), D("3")], [D("3"), D("2"), D("1")]), lambda: st.correlation([D("1"), D("2"), D("3")], [D("1"), D("2"), D("3")]))
            show("linreg", lambda: st.linear_regression([D("1"), D("2"), D("3")], [D("2"), D("4"), D("6")]))
            show("mode", lambda: st.mode([D("1"), D("1.0"), D("2")]), lambda: st.multimode([D("1"), D("1.0"), D("2")]), lambda: st.mode([D("1"), 1, 1.0]))
            show("nd", lambda: st.NormalDist.from_samples(data), lambda: st.NormalDist.from_samples([D("1"), D("2"), D("3"), D("4")]))
            show("special", lambda: st.mean([D("1"), D("Inf")]), lambda: st.mean([D("1"), D("NaN")]), lambda: st.variance([D("1"), D("Inf")]), lambda: st.stdev([D("1"), D("NaN")]))
            show("fracdec", lambda: F.from_decimal(D("1.5")), lambda: F.from_decimal(D("-0.25")), lambda: F.from_decimal(D(3)), lambda: F.from_decimal(D("Inf")), lambda: F.from_decimal(D("NaN")))
            show("sump", lambda: st.mean([D("0.1"), D("0.2")]), lambda: st.fmean([D("0.1"), D("0.2")]), lambda: st.variance([D("0.1"), D("0.2"), D("0.3")]))
            show("ctxdep", lambda: (__import__("decimal").getcontext().prec), lambda: st.mean([D(1), D(2), D(3)]), lambda: st.variance([D(1), D(2), D(3), D(4)]))
            """"
        );
}
