using Xunit;

namespace DotPython.DifferentialTests;

public sealed class StatisticsModuleCompatibilityTests
{
    /// <summary>
    /// The `statistics` module: the measures of central tendency and spread over exact
    /// rational arithmetic, the relations between two inputs, the quantiles, the module's
    /// exception and its `NormalDist` (density, cumulative distribution, inverse through
    /// Wichura's approximation, overlap, samples from the shared Mersenne Twister and the
    /// arithmetic operators), each matching CPython's results and refusal wording.
    /// </summary>
    [Fact]
    public Task TheStatisticsModuleMatchesCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """"
            import statistics as st
            def report(label, thunk):
                try:
                    print(label, repr(thunk()))
                except Exception as error:
                    print(label, type(error).__name__, error)
            print("mean", st.mean([1,2,3,4]), st.mean([1.5,2.5]), st.mean([True, False, True]), st.mean(range(10)), st.mean([0.1,0.2,0.3]))
            report("mean-empty", lambda: st.mean([]))
            report("mean-str", lambda: st.mean(["a"]))
            print("fmean", st.fmean(range(5)), st.fmean([1,2], [1,3]), st.fmean([1.5, 2.5]))
            report("fmean-empty", lambda: st.fmean([]))
            report("fmean-len", lambda: st.fmean([1,2], [1]))
            report("fmean-zero", lambda: st.fmean([1,2], [0,0]))
            print("geo", round(st.geometric_mean([54,24,36]), 9), st.geometric_mean([1,2,4]))
            report("geo-empty", lambda: st.geometric_mean([]))
            report("geo-neg", lambda: st.geometric_mean([1,-1]))
            report("geo-zero", lambda: st.geometric_mean([1,0]))
            print("harm", st.harmonic_mean([40,60]), st.harmonic_mean([40,60], weights=[5,30]), st.harmonic_mean([1,2,4]))
            report("harm-empty", lambda: st.harmonic_mean([]))
            report("harm-neg", lambda: st.harmonic_mean([1,-2]))
            report("harm-weightlen", lambda: st.harmonic_mean([1,2], [1]))
            report("harm-zero", lambda: st.harmonic_mean([1,0]))
            print("median2", st.median([1,3,5]), st.median_low([1,3,5,7]), st.median_high([1,3,5,7]), st.median([2,4]), st.median_grouped([1,3,3,5], 2))
            report("median-empty", lambda: st.median([]))
            report("median-low-empty", lambda: st.median_low([]))
            print("mode2", st.mode("aabbbcc"), st.multimode("aabbbbccddddeeffffgg"), st.multimode(""), st.mode([1.5, 1.5, 2]))
            report("mode-tie", lambda: st.mode([1,1,2,2]))
            report("mode-str", lambda: st.mode([]))
            print("var2", st.pvariance([1,2,3]), st.variance([1,2,3]), st.pstdev([1,2,3]), st.stdev([1,2,3]), st.variance([1,2,3], 2), st.pvariance([1,2,3], 2))
            print("var3", st.variance([2.75, 1.75, 1.25, 0.25, 0.5, 1.25, 3.5]), st.stdev([1.5, 2.5, 2.5, 2.75, 3.25, 4.75]))
            report("var-empty", lambda: st.variance([]))
            report("pv-empty", lambda: st.pvariance([]))
            report("stdev-one", lambda: st.stdev([1]))
            report("pstdev-empty", lambda: st.pstdev([]))
            report("var-inf", lambda: st.variance([1, float('inf')]))
            print("cov2", st.covariance([1,2,3,4,5,6,7,8,9], [1,2,3,1,2,3,1,2,3]), st.covariance([9,8,7,6,5,4,3,2,1], [1,2,3,4,5,6,7,8,9]))
            report("cov-len", lambda: st.covariance([1,2], [1]))
            report("cov-one", lambda: st.covariance([1], [1]))
            print("corr2", st.correlation([1,2,3,4,5,6,7,8,9], [1,2,3,4,5,6,7,8,9]), st.correlation([1,2,3,4,5,6,7,8,9], [9,8,7,6,5,4,3,2,1]), round(st.correlation([1,2,3,4,5],[1,3,2,5,4], method='ranked'), 12))
            report("corr-const", lambda: st.correlation([1,1,1], [1,2,3]))
            report("corr-method", lambda: st.correlation([1,2], [1,2], method='x'))
            print("linreg2", st.linear_regression([1,2,3,4], [2,4,6,8]), st.linear_regression([1,2,3,4], [1,3,5,7], proportional=True), st.linear_regression([0.1,0.2,0.3], [0.2,0.4,0.6]))
            report("linreg-one", lambda: st.linear_regression([1], [1]))
            report("linreg-const", lambda: st.linear_regression([1,1], [1,2]))
            print("quant2", st.quantiles([1,2,3,4], n=2), st.quantiles([1,2,3,4], n=10), st.quantiles([1,2,3,4], n=3, method='inclusive'), st.quantiles([1.5, 2.5, 3.5], n=2))
            report("quant-one", lambda: st.quantiles([1], n=3))
            report("quant-empty", lambda: st.quantiles([]))
            report("quant-n0", lambda: st.quantiles([1,2], n=0))
            print("err", str(st.StatisticsError("x")), isinstance(st.StatisticsError("x"), ValueError), st.StatisticsError.__name__)
            nd = st.NormalDist(100, 15)
            print("nd2", round(nd.pdf(100), 12), round(nd.cdf(85), 12), round(nd.inv_cdf(0.5), 12), round(nd.overlap(st.NormalDist(100, 15)), 12), round(nd.zscore(130), 12))
            print("nd3", st.NormalDist().cdf(0), round(st.NormalDist(2.4,1.6).overlap(st.NormalDist(3.2,2.0)), 12))
            print("nd4", repr(st.NormalDist(1, 2)), st.NormalDist(1,2) == st.NormalDist(1,2), st.NormalDist(1,2) != st.NormalDist(1,3))
            print("nd5", repr(st.NormalDist(1,2) + 3), repr(st.NormalDist(1,2) - 3), repr(3 + st.NormalDist(1,2)), repr(st.NormalDist(1,2) * 2), repr(2 * st.NormalDist(1,2)), repr(st.NormalDist(1,2) / 2), repr(-st.NormalDist(1,2)), repr(+st.NormalDist(1,2)))
            print("nd6", repr(st.NormalDist(1,2) + st.NormalDist(1,2)), repr(st.NormalDist(1,2) - st.NormalDist(1,1)))
            print("nd7", st.NormalDist.from_samples([1,2,3,4]), st.NormalDist.from_samples([1.5, 2.5]))
            print("nd8", [round(x, 12) for x in st.NormalDist(0,1).samples(4, seed=7)])
            print("lr3", st.linear_regression([1,2,3],[2,4,6])._fields, st.linear_regression([1,2,3],[2,4,6])[0], list(st.linear_regression([1,2,3],[2,4,6])))

            print("surface", st.StatisticsError.__name__, st.StatisticsError.__module__, issubclass(st.StatisticsError, ValueError), isinstance(st.StatisticsError("x"), ValueError), str(st.StatisticsError("x")))
            print("ne", st.NormalDist(1,2) != st.NormalDist(1,3), hash(st.NormalDist(1,2)) == hash(st.NormalDist(1,2)), st.NormalDist(1,2) == 5)
            print("nd-q", [round(st.NormalDist().inv_cdf(x), 12) for x in (0.025, 0.5, 0.975)], [round(q, 9) for q in st.NormalDist().quantiles(10)])
            print("lr-named", st.LinearRegression(slope=1.0, intercept=2.0), st.LinearRegression(1.0, 2.0).slope, st.linear_regression([1,2,3],[3,6,9]).intercept)
            print("mean-types", type(st.mean([1,2,3])).__name__, type(st.mean([1,2])).__name__, type(st.median([1,2,3,4])).__name__, type(st.variance([1,2,3,4])).__name__)
            print("stdev-types", type(st.pstdev([1,2])).__name__, type(st.fmean([1,2])).__name__)
            print("edge", st.mean([1]), st.pvariance([5]), st.pstdev([5]), st.quantiles([5], n=3), st.multimode([1]))
            print("weights", st.fmean([1,2,3], [1,0,2]), st.harmonic_mean([2], weights=[1]))
            print("all-len", len(st.__all__), st.__all__[:3], st.__all__[-3:])
            """"
        );
}
