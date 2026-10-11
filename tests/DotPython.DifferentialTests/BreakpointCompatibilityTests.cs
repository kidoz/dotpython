using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BreakpointCompatibilityTests
{
    [Fact]
    public Task BreakpointForwardsToTheHook() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import sys
            seen = []
            def hook(*arguments, **keywords):
                seen.append((arguments, keywords))
            saved = sys.breakpointhook
            sys.breakpointhook = hook
            breakpoint()
            breakpoint(1, x=2)
            print("forwarded", seen)
            sys.breakpointhook = None
            try:
                breakpoint()
            except TypeError as error:
                print("none", error)
            sys.breakpointhook = hook
            breakpoint(3)
            print("again", seen[-1])
            sys.breakpointhook = saved
            print("restored", sys.breakpointhook is saved, saved is getattr(sys, "__" + "breakpointhook" + "__"))
            """
        );
}
