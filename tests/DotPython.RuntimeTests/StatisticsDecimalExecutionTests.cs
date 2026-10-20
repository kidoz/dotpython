using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class StatisticsDecimalExecutionTests
{
    /// <summary>
    /// `statistics` over `Decimal` inputs: the exact sums feed every measure and
    /// `_coerce` keeps `Decimal` where CPython does (`mean`, `variance`, `pvariance`,
    /// `harmonic_mean`, the even `median`, `quantiles`), while `fmean`, `geometric_mean`,
    /// the relation measures and `NormalDist` convert to float; mixed int/bool data
    /// joins exactly, mixing a float or a `Fraction` raises CPython's refusal, a
    /// `Decimal` in the inputs makes the covariance/regression float subtraction fail as
    /// CPython's does, the specials carry through, and `Fraction.from_decimal` reads
    /// these values back exactly.
    /// </summary>
    [Fact]
    public void TheStatisticsModuleCarriesDecimalsAsCpythonDoes()
    {
        var output = Run(
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
        Assert.Equal(
            Lines(
                "mean Decimal('2') Decimal('2') Decimal('1.25') Decimal('0.75')",
                "meanerr TypeError: don't know how to coerce Decimal and float TypeError: don't know how to coerce Decimal and Fraction TypeError: don't know how to coerce Fraction and Decimal TypeError: can't convert type 'str' to numerator/denominator",
                "fmean 2.0 TypeError: unsupported operand type(s) for /: 'decimal.Decimal' and 'float'",
                "geo 4.0",
                "harm Decimal('1.714285714285714285714285714') Decimal('1.5')",
                "median Decimal('3') Decimal('2') Decimal('2.5') Decimal('1') Decimal('3') 3.0",
                "var Decimal('1.875') Decimal('1.40625') Decimal('1') Decimal('0.125')",
                "stdev Decimal('1.369306393762915283642424457') Decimal('1.185854122563142249499585079') Decimal('1.707825127659933063870173113') Decimal('0.5')",
                "quant [Decimal('2.5')] [Decimal('1.25'), Decimal('2.5'), Decimal('3.75')] [Decimal('1'), Decimal('1')] TypeError: unsupported operand type(s) for +: 'decimal.Decimal' and 'float'",
                "cov TypeError: unsupported operand type(s) for -: 'decimal.Decimal' and 'float'",
                "corr TypeError: unsupported operand type(s) for -: 'decimal.Decimal' and 'float' TypeError: unsupported operand type(s) for -: 'decimal.Decimal' and 'float'",
                "linreg TypeError: unsupported operand type(s) for -: 'decimal.Decimal' and 'float'",
                "mode Decimal('1') [Decimal('1')] Decimal('1')",
                "nd NormalDist(mu=2.0, sigma=1.3693063937629153) NormalDist(mu=2.5, sigma=1.2909944487358056)",
                "special Decimal('Infinity') Decimal('NaN') Decimal('Infinity') ValueError: inf or nan encountered in data",
                "fracdec Fraction(3, 2) Fraction(-1, 4) Fraction(3, 1) OverflowError: cannot convert Infinity to integer ratio ValueError: cannot convert NaN to integer ratio",
                "sump Decimal('0.15') 0.15000000000000002 Decimal('0.01')",
                "ctxdep 28 Decimal('2') Decimal('1.666666666666666666666666667')"
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
