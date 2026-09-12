using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SequenceComparisonExecutionTests
{
    private static readonly string[] Phases = ["equal", "equal_truth", "order", "order_truth"];
    private static readonly string[] Searches =
    [
        "list_left < list_right",
        "tuple_left < tuple_right",
    ];

    [Fact]
    public void SavedComparisonsUseCurrentExecutionForEqualityOrderingAndTruthCallbacks()
    {
        var engine = PrepareEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "print(bool(list_left < list_right), bool(tuple_left < tuple_right))\nprint(events)",
            "sequence_comparison_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "True True",
                "['equal', 'equal_truth', 'order', 'order_truth', 'equal', 'equal_truth', 'order', 'order_truth']"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void CancellationRetainsCallbackMutationWithoutPublishingSearchResults()
    {
        foreach (var search in Searches)
        foreach (var phase in Phases)
        {
            var engine = PrepareEngine();
            var originalTarget = Global(engine, "target");
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelAfterLineWriter(cancellation);
            Assert.Throws<OperationCanceledException>(() =>
                engine.Execute(
                    $"phase = '{phase}'\ntarget = bool({search})",
                    "sequence_comparison_cancel.py",
                    output,
                    cancellationToken: cancellation.Token
                )
            );
            Assert.Equal(Lines("callback"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertCallbackState(engine, phase);
            AssertRecovery(engine, search);
        }
    }

    [Fact]
    public void CallbackBudgetFailuresPreserveMutationRunFinallyAndAllowRecovery()
    {
        foreach (var search in Searches)
        foreach (var phase in Phases)
        {
            var engine = PrepareEngine();
            var originalTarget = Global(engine, "target");
            using var output = new StringWriter();
            var result = engine.Execute(
                $"phase = '{phase}'\ntry:\n    target = bool({search})"
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "sequence_comparison_callback_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("callback", "cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertCallbackState(engine, phase);
            AssertRecovery(engine, search);
        }
    }

    [Fact]
    public void ComparisonsWithoutUserCallbacksChargeTheInstructionBudget()
    {
        foreach (var expression in new[] { "[0] * 2000", "(0,) * 2000" })
        foreach (var operation in new[] { "==", "<" })
        {
            var engine = new ManagedPythonEngine();
            using var output = new StringWriter();
            Assert.True(
                engine
                    .Execute(
                        "left = "
                            + expression
                            + "\nright = "
                            + expression
                            + "\ntarget = 'original'",
                        "sequence_comparison_scan_setup.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            var originalTarget = Global(engine, "target");
            var result = engine.Execute(
                "try:\n    target = left " + operation + " right\nfinally:\n    print('cleanup')",
                "sequence_comparison_scan_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.True(
                engine
                    .Execute(
                        "print(left == right)",
                        "sequence_comparison_scan_recovery.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            Assert.Equal(Lines("cleanup", "True"), output.ToString());
        }
    }

    [Fact]
    public void RawOrderingResultsRemainUnconvertedUntilTheCallerTestsTruth()
    {
        var engine = PrepareEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "target = list_left < list_right\nprint(target is order_result, events)",
                    "sequence_comparison_raw.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Same(Global(engine, "order_result"), Global(engine, "target"));
        Assert.Equal(Lines("True ['equal', 'equal_truth', 'order']"), output.ToString());
    }

    [Fact]
    public void RecursiveSequenceComparisonsFailSafelyAndReleaseDepthOnRecovery()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            left = []
            left.append(left)
            right = []
            right.append(right)
            for repeat in range(3):
                try: print(left == right)
                except RecursionError: print('recursion')
                try: print(left < right)
                except RecursionError: print('recursion')
                print([1] == [1], [1] < [2], left == left)
            """,
            "sequence_comparison_recursion.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "recursion",
                "recursion",
                "True True True",
                "recursion",
                "recursion",
                "True True True",
                "recursion",
                "recursion",
                "True True True"
            ),
            output.ToString()
        );
    }

    private static ManagedPythonEngine PrepareEngine()
    {
        var engine = new ManagedPythonEngine();
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var setup = engine.Execute(
            """
            events = []
            phase = ''
            def visit(name):
                events.append(name)
                if phase == name:
                    values.append(9)
                    print('callback')
                    for counter in range(5000): pass
            class EqualityTruth:
                def __bool__(self): visit('equal_truth'); return False
            class OrderingTruth:
                def __bool__(self): visit('order_truth'); return True
            order_result = OrderingTruth()
            class Candidate:
                def __eq__(self, other): visit('equal'); return EqualityTruth()
                def __lt__(self, other): visit('order'); return order_result
            candidate = Candidate()
            values = [candidate]
            alias = values
            fixed = (candidate,)
            list_left = [candidate]
            list_right = [object()]
            tuple_left = (candidate,)
            tuple_right = (object(),)
            target = 'original'
            """,
            "sequence_comparison_callback_setup.py",
            output,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        Assert.Empty(output.ToString());
        cancellation.Cancel();
        return engine;
    }

    private static void AssertCallbackState(ManagedPythonEngine engine, string phase)
    {
        var values = Assert.IsType<PythonListValue>(Global(engine, "values"));
        Assert.Same(values, Global(engine, "alias"));
        Assert.Equal(2, values.Elements.Count);
        Assert.Equal(9, Assert.IsType<PythonWholeNumberValue>(values.Elements[1]).Value);
        var events = Assert.IsType<PythonListValue>(Global(engine, "events"));
        Assert.Equal(
            Phases.Take(Array.IndexOf(Phases, phase) + 1),
            events.Elements.Select(value => Assert.IsType<PythonTextValue>(value).Value)
        );
    }

    private static void AssertRecovery(ManagedPythonEngine engine, string search)
    {
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    $"phase = ''\nprint(bool({search}))",
                    "sequence_comparison_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(Lines("True"), output.ToString());
    }

    private static PythonValue Global(ManagedPythonEngine engine, string name)
    {
        var globals = Assert.IsType<PythonGlobalNamespace>(
            typeof(ManagedPythonEngine)
                .GetField("_globals", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine)
        );
        Assert.True(globals.TryGetValue(name, out var value));
        return value;
    }

    private static string Lines(params string[] values) =>
        string.Join(Environment.NewLine, values) + Environment.NewLine;

    private sealed class CancelAfterLineWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            cancellation.Cancel();
        }
    }
}
