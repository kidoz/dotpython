using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ExtremumExecutionTests
{
    private static readonly string[] Selections = ["choose_min", "choose_max"];
    private static readonly string[] Phases = ["iter", "next", "key", "compare", "truth"];

    [Fact]
    public void SavedIteratorsKeysAndComparisonsUseCurrentExecutionInStreamingOrder()
    {
        foreach (var selection in Selections)
        {
            var engine = PrepareEngine();
            using var output = new StringWriter();
            var result = engine.Execute(
                $"print({selection}(source, key=key).rank)\nprint(events)",
                "extremum_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Equal(
                Lines(
                    selection == "choose_min" ? "1" : "3",
                    "['iter', 'next', 'key', 'next', 'key', 'compare', 'truth', 'next', 'key', 'compare', 'truth', 'next']"
                ),
                output.ToString()
            );
        }
    }

    [Fact]
    public void CancellationStopsConsumptionAndRetainsEffectsWithoutPublishingResult()
    {
        foreach (var selection in Selections)
        foreach (var phase in Phases)
        {
            var engine = PrepareEngine();
            var originalTarget = Global(engine, "target");
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelAfterLineWriter(cancellation);
            Assert.Throws<OperationCanceledException>(() =>
                engine.Execute(
                    $"phase = '{phase}'\ntarget = {selection}(source, key=key)",
                    "extremum_cancel.py",
                    output,
                    cancellationToken: cancellation.Token
                )
            );
            Assert.Equal(Lines("callback"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertStoppedState(engine, phase);
            AssertRecovery(engine, selection);
        }
    }

    [Fact]
    public void CallbackBudgetFailuresRunFinallyAndStopBeforeFurtherConsumption()
    {
        foreach (var selection in Selections)
        foreach (var phase in Phases)
        {
            var engine = PrepareEngine();
            var originalTarget = Global(engine, "target");
            using var output = new StringWriter();
            var result = engine.Execute(
                $"phase = '{phase}'\ntry:\n    target = {selection}(source, key=key)"
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "extremum_callback_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("callback", "cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertStoppedState(engine, phase);
            AssertRecovery(engine, selection);
        }
    }

    [Fact]
    public void BuiltinIterableAndPositionalScansChargeBudgetsWithoutUserCallbacks()
    {
        foreach (var function in new[] { "min", "max" })
        foreach (var arguments in new[] { "cursor", "*values" })
        {
            var engine = new ManagedPythonEngine();
            using var output = new StringWriter();
            Assert.True(
                engine
                    .Execute(
                        "values = [0] * 2000\ncursor = iter(values)\ntarget = 'original'\ndef ignore(*args): return len(args)",
                        "extremum_scan_setup.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            var budget = arguments == "cursor" ? 500 : 2500;
            if (arguments == "*values")
            {
                // This budget permits argument expansion; selection must charge
                // additional work after receiving the positional candidates.
                Assert.True(
                    engine
                        .Execute(
                            "print(ignore(*values))",
                            "extremum_expand_control.py",
                            output,
                            new ManagedExecutionOptions { InstructionLimit = budget },
                            TestContext.Current.CancellationToken
                        )
                        .Success
                );
                Assert.Equal(Lines("2000"), output.ToString());
                output.GetStringBuilder().Clear();
            }
            var originalTarget = Global(engine, "target");
            var result = engine.Execute(
                $"try:\n    target = {function}({arguments})\nfinally:\n    print('cleanup')",
                "extremum_scan_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = budget },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Equal(Lines("cleanup"), output.ToString());
            if (arguments == "cursor")
            {
                var iterator = Assert.IsType<PythonIteratorValue>(Global(engine, "cursor"));
                Assert.InRange(iterator.Index, 1, 1999);
                Assert.False(iterator.IsExhausted);
            }
            Assert.True(
                engine
                    .Execute(
                        $"print({function}(values))",
                        "extremum_scan_recovery.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            Assert.Equal(Lines("cleanup", "0"), output.ToString());
        }
    }

    private static ManagedPythonEngine PrepareEngine()
    {
        var engine = new ManagedPythonEngine();
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    """
                    events = []
                    effects = []
                    phase = ''
                    consumed = 0
                    def visit(name):
                        events.append(name)
                        if phase == name:
                            effects.append(9)
                            print('callback')
                            for counter in range(5000): pass
                    class Truth:
                        def __init__(self, answer): self.answer = answer
                        def __bool__(self): visit('truth'); return self.answer
                    class Item:
                        def __init__(self, rank): self.rank = rank
                        def __lt__(self, other): visit('compare'); return Truth(self.rank < other.rank)
                        def __gt__(self, other): visit('compare'); return Truth(self.rank > other.rank)
                    class Cursor:
                        def __iter__(self): return self
                        def __next__(self):
                            global consumed
                            visit('next')
                            if consumed == 3: raise StopIteration
                            consumed += 1
                            return Item(4 - consumed)
                    cursor = Cursor()
                    class Source:
                        def __iter__(self): visit('iter'); return cursor
                    source = Source()
                    def key(value): visit('key'); return value
                    choose_min = min
                    choose_max = max
                    target = 'original'
                    """,
                    "extremum_callback_setup.py",
                    output,
                    cancellationToken: cancellation.Token
                )
                .Success
        );
        Assert.Empty(output.ToString());
        cancellation.Cancel();
        return engine;
    }

    private static void AssertStoppedState(ManagedPythonEngine engine, string phase)
    {
        var effects = Assert.IsType<PythonListValue>(Global(engine, "effects"));
        Assert.Equal(
            9,
            Assert.IsType<PythonWholeNumberValue>(Assert.Single(effects.Elements)).Value
        );
        var expectedConsumed =
            phase is "iter" or "next" ? 0
            : phase == "key" ? 1
            : 2;
        Assert.Equal(
            expectedConsumed,
            Assert.IsType<PythonWholeNumberValue>(Global(engine, "consumed")).Value
        );
        string[] expected = phase switch
        {
            "iter" => ["iter"],
            "next" => ["iter", "next"],
            "key" => ["iter", "next", "key"],
            "compare" => ["iter", "next", "key", "next", "key", "compare"],
            _ => ["iter", "next", "key", "next", "key", "compare", "truth"],
        };
        Assert.Equal(
            expected,
            Assert
                .IsType<PythonListValue>(Global(engine, "events"))
                .Elements.Select(value => Assert.IsType<PythonTextValue>(value).Value)
        );
    }

    private static void AssertRecovery(ManagedPythonEngine engine, string selection)
    {
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    $"phase = ''\nconsumed = 0\nprint({selection}(source, key=key).rank)",
                    "extremum_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(Lines(selection == "choose_min" ? "1" : "3"), output.ToString());
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
