using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BreakpointExecutionTests
{
    [Fact]
    public void BreakpointForwardsToTheHook()
    {
        var output = Run(
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
        Assert.Equal(
            Lines(
                "forwarded [((), {}), ((1,), {'x': 2})]",
                "none 'NoneType' object is not callable",
                "again ((3,), {})",
                "restored True True"
            ),
            output
        );
    }

    [Fact]
    public void TheShippedHookReportsTheMissingDebugger()
    {
        var output = Run(
            """
            import sys
            assert sys.breakpointhook is sys.__breakpointhook__
            try:
                breakpoint()
            except ModuleNotFoundError as error:
                print(error, sys.breakpointhook.__name__ is not None)
            """
        );
        Assert.Equal(Lines("No module named 'pdb' True"), output);
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
