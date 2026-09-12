using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SequenceCountRemoveExecutionTests
{
    private static readonly string[] Phases = ["equal", "truth"];
    private static readonly string[] Searches = ["list_count", "tuple_count", "list_remove"];

    [Fact]
    public void SavedMethodsUseCurrentExecutionForEqualityAndTruthCallbacks()
    {
        var engine = PrepareEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "print(list_count(needle), tuple_count(needle), list_remove(needle))\nprint(events)",
            "count_remove_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines("1 1 None", "['equal', 'truth', 'equal', 'truth', 'equal', 'truth']"),
            output.ToString()
        );
    }

    [Fact]
    public void CancellationRetainsCallbackMutationWithoutPublishingMethodResults()
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
                    $"phase = '{phase}'\ntarget = {search}(needle)",
                    "count_remove_cancel.py",
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
                $"phase = '{phase}'\ntry:\n    target = {search}(needle)"
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "count_remove_callback_budget.py",
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
    public void MethodsWithoutUserCallbacksChargeTheInstructionBudget()
    {
        foreach (var operation in new[] { "values.count(0)", "fixed.count(0)", "values.remove(1)" })
        {
            var engine = new ManagedPythonEngine();
            using var output = new StringWriter();
            Assert.True(
                engine
                    .Execute(
                        "values = [0] * 2000\nfixed = tuple(values)\ntarget = 'original'",
                        "count_remove_scan_setup.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            var originalTarget = Global(engine, "target");
            var result = engine.Execute(
                "try:\n    target = " + operation + "\nfinally:\n    print('cleanup')",
                "count_remove_scan_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Equal(
                2000,
                Assert.IsType<PythonListValue>(Global(engine, "values")).Elements.Count
            );
            Assert.True(
                engine
                    .Execute(
                        "print(values.count(0), fixed.count(0), values.remove(0), len(values))",
                        "count_remove_scan_recovery.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            Assert.Equal(Lines("cleanup", "2000 2000 None 1999"), output.ToString());
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
            class Truth:
                def __bool__(self): visit('truth'); return True
            class Candidate:
                def __eq__(self, other): visit('equal'); return Truth()
            candidate = Candidate()
            values = [candidate]
            alias = values
            fixed = (candidate,)
            list_count = values.count
            tuple_count = fixed.count
            list_remove = values.remove
            needle = object()
            target = 'original'
            """,
            "count_remove_callback_setup.py",
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
                    $"phase = ''\nprint({search}(needle))",
                    "count_remove_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(Lines(search == "list_remove" ? "None" : "1"), output.ToString());
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
