using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SequenceSearchBoundsExecutionTests
{
    private static readonly string[] Phases = ["start", "stop", "equal", "truth"];
    private static readonly string[] Searches = ["list_search", "tuple_search"];

    [Fact]
    public void SavedSearchesUseCurrentExecutionForBoundsEqualityAndTruthCallbacks()
    {
        var engine = PrepareEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "print(list_search(needle, start, stop), tuple_search(needle, start, stop))\nprint(events)",
            "search_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines("0 0", "['start', 'stop', 'equal', 'truth', 'start', 'stop', 'equal', 'truth']"),
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
                    $"phase = '{phase}'\ntarget = {search}(needle, start, stop)",
                    "search_cancel.py",
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
                $"phase = '{phase}'\ntry:\n    target = {search}(needle, start, stop)"
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "search_callback_budget.py",
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
    public void SearchesWithoutUserCallbacksChargeTheInstructionBudget()
    {
        foreach (var expression in new[] { "[0] * 2000", "(0,) * 2000" })
        {
            var engine = new ManagedPythonEngine();
            using var output = new StringWriter();
            Assert.True(
                engine
                    .Execute(
                        "values = " + expression + "\ntarget = 'original'",
                        "search_scan_setup.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            var originalTarget = Global(engine, "target");
            var result = engine.Execute(
                "try:\n    target = values.index(1)\nfinally:\n    print('cleanup')",
                "search_scan_budget.py",
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
                        "target = values.index(0, 1999)\nprint(target)",
                        "search_scan_recovery.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            Assert.Equal(Lines("cleanup", "1999"), output.ToString());
        }
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
            class Bound:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): visit(self.name); return self.value
            class Truth:
                def __bool__(self): visit('truth'); return True
            class Candidate:
                def __eq__(self, other): visit('equal'); return Truth()
            candidate = Candidate()
            values = [candidate]
            alias = values
            fixed = (candidate,)
            list_search = values.index
            tuple_search = fixed.index
            start = Bound('start', 0)
            stop = Bound('stop', 999)
            needle = object()
            target = 'original'
            """,
            "search_callback_setup.py",
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
                    $"phase = ''\nprint({search}(needle, start, stop))",
                    "search_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(Lines("0"), output.ToString());
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
