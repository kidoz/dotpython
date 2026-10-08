using Xunit;

namespace DotPython.DifferentialTests;

public sealed class FloatReprCompatibilityTests
{
    [Fact]
    public Task ReprSwitchesToScientificOneDecadeEarlierThanRoundTrip() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for value in (1e15, 9.9e15, 1e16, 1.5e16, 2e16, 5e16, 9.999e16,
                          1e17, 1.2345678901234568e17, -1e16, -1.5e16,
                          12345678901234567.0, -12345678901234567.0, 1e16 + 2.0):
                print(repr(value), str(value))
            print(1e-5, 1e-4, 1e-3, 0.0001, 0.0, -0.0)
            print(1.7976931348623157e308, 5e-324)
            """
        );

    [Fact]
    public Task ReprFeedsFStringsPercentFormattingAndJson() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import json
            w = 1e16
            print(f"{w}", f"{w!r}", f"{w:.3f}")
            print("%s" % w, "%r" % w)
            print(json.dumps(w), json.dumps([w, -1.5e16]), json.dumps({"a": w}))
            print([w], (w,), {w: 1})
            """
        );
}
