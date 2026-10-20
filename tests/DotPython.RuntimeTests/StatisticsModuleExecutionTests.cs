using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class StatisticsModuleExecutionTests
{
    /// <summary>
    /// The runtime's `statistics`: every measure over exact rational arithmetic, with
    /// CPython's own error wording and its float/integer results, `NormalDist` with the
    /// AS241 inverse normal and the Mersenne Twister behind `samples`, and the
    /// `LinearRegression` named tuple. The module publishes only its own names — CPython's
    /// module namespace also leaks the imports it uses — and `kde`/`kde_random` are the
    /// slice's gap; those local answers are pinned at the end.
    /// </summary>
    [Fact]
    public void TheStatisticsModuleAnswersAsCpythonDoes()
    {
        var output = Run(
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
            print("local-names", sorted(n for n in dir(st) if not n.startswith('_')))
            report("local-kde", lambda: st.kde([1,2,3], 0.5))
            report("local-kde-random", lambda: st.kde_random([1,2,3], 0.5))
            print("local-inv", st.NormalDist().inv_cdf(0.25), st.NormalDist().quantiles(4)[0], st.NormalDist().quantiles(4)[2])
            print("local-error", st.StatisticsError("x", 1), st.StatisticsError("a", "b").args if hasattr(st.StatisticsError("a","b"), "args") else "")
            """"
        );
        Assert.Equal(
            Lines(
                "mean 2.5 2.0 0.6666666666666666 4.5 0.2",
                "mean-empty StatisticsError mean requires at least one data point",
                "mean-str TypeError can't convert type 'str' to numerator/denominator",
                "fmean 2.0 1.75 2.0",
                "fmean-empty StatisticsError fmean requires at least one data point",
                "fmean-len StatisticsError data and weights must be the same length",
                "fmean-zero StatisticsError sum of weights must be non-zero",
                "geo 36.0 2.0",
                "geo-empty StatisticsError Must have a non-empty dataset",
                "geo-neg StatisticsError ('No negative inputs allowed', -1)",
                "geo-zero 0.0",
                "harm 48.0 56.0 1.7142857142857142",
                "harm-empty StatisticsError harmonic_mean requires at least one data point",
                "harm-neg StatisticsError harmonic mean does not support negative values",
                "harm-weightlen StatisticsError Number of weights does not match data size",
                "harm-zero 0",
                "median2 3 3 5 3.0 3.0",
                "median-empty StatisticsError no median for empty data",
                "median-low-empty StatisticsError no median for empty data",
                "mode2 b ['b', 'd', 'f'] [] 1.5",
                "mode-tie 1",
                "mode-str StatisticsError no mode for empty data",
                "var2 0.6666666666666666 1 0.816496580927726 1.0 1 0.6666666666666666",
                "var3 1.3720238095238095 1.0810874155219827",
                "var-empty StatisticsError variance requires at least two data points",
                "pv-empty StatisticsError pvariance requires at least one data point",
                "stdev-one StatisticsError stdev requires at least two data points",
                "pstdev-empty StatisticsError pstdev requires at least one data point",
                "var-inf inf",
                "cov2 0.75 -7.5",
                "cov-len StatisticsError covariance requires that both inputs have same number of data points",
                "cov-one StatisticsError covariance requires at least two data points",
                "corr2 1.0 -1.0 0.8",
                "corr-const StatisticsError at least one of the inputs is constant",
                "corr-method ValueError Unknown method: 'x'",
                "linreg2 LinearRegression(slope=2.0, intercept=0.0) LinearRegression(slope=1.6666666666666667, intercept=0.0) LinearRegression(slope=2.0, intercept=0.0)",
                "linreg-one StatisticsError linear regression requires at least two data points",
                "linreg-const StatisticsError x is constant",
                "quant2 [2.5] [0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5] [2.0, 3.0] [2.5]",
                "quant-one [1, 1]",
                "quant-empty StatisticsError must have at least one data point",
                "quant-n0 StatisticsError n must be at least 1",
                "err x True StatisticsError",
                "nd2 0.026596152027 0.158655253931 100.0 1.0 2.0",
                "nd3 0.5 0.803505065733",
                "nd4 NormalDist(mu=1.0, sigma=2.0) True True",
                "nd5 NormalDist(mu=4.0, sigma=2.0) NormalDist(mu=-2.0, sigma=2.0) NormalDist(mu=4.0, sigma=2.0) NormalDist(mu=2.0, sigma=4.0) NormalDist(mu=2.0, sigma=4.0) NormalDist(mu=0.5, sigma=1.0) NormalDist(mu=-1.0, sigma=2.0) NormalDist(mu=1.0, sigma=2.0)",
                "nd6 NormalDist(mu=2.0, sigma=2.8284271247461903) NormalDist(mu=0.0, sigma=2.23606797749979)",
                "nd7 NormalDist(mu=2.5, sigma=1.2909944487358056) NormalDist(mu=2.0, sigma=0.7071067811865476)",
                "nd8 [-0.457007674023, -1.032798196661, 0.387844578312, -1.457883804795]",
                "lr3 ('slope', 'intercept') 2.0 [2.0, 0.0]",
                "surface StatisticsError statistics True True x",
                "ne True True False",
                "nd-q [-1.95996398454, 0.0, 1.95996398454] [-1.281551566, -0.841621234, -0.524400513, -0.253347103, 0.0, 0.253347103, 0.524400513, 0.841621234, 1.281551566]",
                "lr-named LinearRegression(slope=1.0, intercept=2.0) 1.0 0.0",
                "mean-types int float float float",
                "stdev-types float float",
                "edge 1 0 0.0 [5, 5] [1]",
                "weights 2.3333333333333335 2.0",
                "all-len 22 ['NormalDist', 'StatisticsError', 'correlation'] ['quantiles', 'stdev', 'variance']",
                "local-names ['LinearRegression', 'NormalDist', 'StatisticsError', 'correlation', 'covariance', 'fmean', 'geometric_mean', 'harmonic_mean', 'kde', 'kde_random', 'linear_regression', 'mean', 'median', 'median_grouped', 'median_high', 'median_low', 'mode', 'multimode', 'pstdev', 'pvariance', 'quantiles', 'stdev', 'variance']",
                "local-kde NotImplementedError kernel density estimation is not implemented in this runtime",
                "local-kde-random NotImplementedError kernel density estimation is not implemented in this runtime",
                "local-inv -0.6744897501960817 -0.6744897501960817 0.6744897501960817",
                "local-error ('x', 1) ('a', 'b')"
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
