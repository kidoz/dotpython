using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ComprehensionWalrusExecutionTests
{
    [Fact]
    public void ComprehensionWalrusBindsInTheEnclosingModule()
    {
        var output = Run(
            """
            x = 0
            [(x := i) for i in range(3)]
            print(x)
            """
        );

        Assert.Equal($"2{Environment.NewLine}", output);
    }

    [Fact]
    public void ComprehensionWalrusBindsInTheEnclosingFunction()
    {
        var output = Run(
            """
            def f():
                [(w := i) for i in range(3)]
                return w
            print(f())
            """
        );

        Assert.Equal($"2{Environment.NewLine}", output);
    }

    [Fact]
    public void WalrusInAConditionAndNestedClausesIsVisibleAfterwards()
    {
        var output = Run(
            """
            print([y for i in range(3) if (y := i) >= 1])
            print(y)
            print([a for x in [1, 2] for a in [x] if (b := a) > 0], b)
            """
        );

        Assert.Equal(string.Join(Environment.NewLine, "[1, 2]", "2", "[1, 2] 2", ""), output);
    }

    [Fact]
    public void ComprehensionKindsAndNestingShareTheBinding()
    {
        var output = Run(
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
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "1",
                "(0, 0)",
                "[0, 1, 2] 2",
                "{0, 1, 2} 2",
                "{0: 0, 1: 1, 2: 2} 2",
                ""
            ),
            output
        );
    }

    [Fact]
    public void DeclaredGlobalAndParameterTargetsKeepTheirRouting()
    {
        var output = Run(
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

        Assert.Equal(string.Join(Environment.NewLine, "2 2", "2", ""), output);
    }

    [Fact]
    public void AnUnfilledComprehensionLeavesTheTargetUnbound()
    {
        var output = Run(
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

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "unbound local in function",
                "unbound global at module scope",
                ""
            ),
            output
        );
    }

    [Theory]
    [InlineData("[(i := 1) for i in range(2)]\n", "DPY3119")]
    [InlineData("[[(i := 1) for j in range(2)] for i in range(2)]\n", "DPY3119")]
    [InlineData("[x for x in (n := range(3))]\n", "DPY3120")]
    [InlineData("[x for x in [(q := 1) for _ in range(1)]]\n", "DPY3120")]
    [InlineData("[x for x in (lambda: (n := range(3)))()]\n", "DPY3120")]
    [InlineData("class C:\n    [(y := 1) for i in range(2)]\n", "DPY3121")]
    [InlineData("x := 1\n", "DPY3122")]
    public void RejectedPlacementsReportTheScopeDiagnostic(string source, string code)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "comprehension_walrus_rejection.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "comprehension_walrus_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
