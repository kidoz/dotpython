using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SortingExecutionTests
{
    private static readonly string[] Phases = ["reverse", "key", "compare", "truth"];

    [Fact]
    public void SavedSortAndCallbacksUseTheCurrentExecutionAndDetachedReceiver()
    {
        var engine = PrepareEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            "print(saved_sort(key=key, reverse=reverse))\nprint([item.rank for item in values])",
            "sorting_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("None", "[1, 2, 3]"), output.ToString());
        var events = Assert.IsType<PythonListValue>(Global(engine, "events"));
        var first = Assert.IsType<PythonTupleValue>(events.Elements[0]);
        Assert.Equal("reverse", Assert.IsType<PythonTextValue>(first.Elements[0]).Value);
        Assert.Equal(3, Assert.IsType<PythonWholeNumberValue>(first.Elements[1]).Value);
        foreach (var entry in events.Elements.Skip(1))
        {
            var pair = Assert.IsType<PythonTupleValue>(entry);
            Assert.Equal(0, Assert.IsType<PythonWholeNumberValue>(pair.Elements[1]).Value);
        }
        Assert.Equal(
            3,
            events.Elements.Count(entry =>
                Assert
                    .IsType<PythonTextValue>(Assert.IsType<PythonTupleValue>(entry).Elements[0])
                    .Value == "key"
            )
        );
    }

    [Fact]
    public void CancellationRestoresDetachedStorageAndDoesNotPublishTheSortResult()
    {
        foreach (var phase in Phases)
        {
            var engine = PrepareEngine();
            var originalTarget = Global(engine, "target");
            var receiver = Assert.IsType<PythonListValue>(Global(engine, "values"));
            var storage = receiver.Elements;
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelAfterLineWriter(cancellation);
            Assert.Throws<OperationCanceledException>(() =>
                engine.Execute(
                    $"phase = '{phase}'\ntarget = saved_sort(key=key, reverse=reverse)",
                    "sorting_cancel.py",
                    output,
                    cancellationToken: cancellation.Token
                )
            );
            Assert.Equal(Lines("callback"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(receiver, Global(engine, "values"));
            Assert.Same(storage, receiver.Elements);
            AssertStoppedState(engine, phase);
            AssertRecovery(engine, phase);
        }
    }

    [Fact]
    public void CallbackBudgetsRunFinallyAndRestoreReceiverWithTheCorrectMutationEffects()
    {
        foreach (var phase in Phases)
        {
            var engine = PrepareEngine();
            var originalTarget = Global(engine, "target");
            using var output = new StringWriter();
            var result = engine.Execute(
                $"phase = '{phase}'\ntry:\n    target = saved_sort(key=key, reverse=reverse)"
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "sorting_callback_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 1000 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("callback", "cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertStoppedState(engine, phase);
            AssertRecovery(engine, phase);
        }
    }

    [Fact]
    public void CallbackFreeSortsChargeWorkAndPreserveThePermutationOnBudgetFailure()
    {
        foreach (var reverse in new[] { false, true })
        foreach (var budget in new[] { 300, 700, 1100 })
        {
            var engine = new ManagedPythonEngine();
            using var output = new StringWriter();
            Assert.True(
                engine
                    .Execute(
                        "values = [(index * 37 + index // 7) % 257 for index in range(512)]\ntarget = 'original'",
                        "sorting_scan_setup.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            var receiver = Assert.IsType<PythonListValue>(Global(engine, "values"));
            var original = receiver.Elements.ToArray();
            var originalTarget = Global(engine, "target");
            var result = engine.Execute(
                $"try:\n    target = values.sort(reverse={(reverse ? "True" : "False")})\nfinally:\n    print('cleanup')",
                "sorting_scan_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = budget },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertPermutation(original, receiver.Elements);
            Assert.True(
                engine
                    .Execute(
                        "values.sort()\nprint(len(values))",
                        "sorting_scan_recovery.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            Assert.Equal(Lines("cleanup", "512"), output.ToString());
        }
    }

    [Fact]
    public void FailuresDuringMergesRestoreEveryOriginalItemWithSeparateKeyStorage()
    {
        foreach (var reverse in new[] { false, true })
        foreach (var stop in new[] { 100, 200, 400, 800 })
        {
            var engine = new ManagedPythonEngine();
            using var output = new StringWriter();
            Assert.True(
                engine
                    .Execute(
                        """
                        calls = 0
                        stop = 0
                        class Key:
                            def __init__(self, value): self.value = value
                            def __lt__(self, other):
                                global calls
                                calls += 1
                                if calls == stop: raise LookupError('merge failed')
                                return self.value < other.value
                        values = [(index * 37 + index // 7) % 257 for index in range(512)]
                        target = 'original'
                        def key(value): return Key(value)
                        """,
                        "sorting_merge_setup.py",
                        output,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                    .Success
            );
            var receiver = Assert.IsType<PythonListValue>(Global(engine, "values"));
            var original = receiver.Elements.ToArray();
            var originalTarget = Global(engine, "target");
            var result = engine.Execute(
                $"stop = {stop}\ntry:\n    target = values.sort(key=key, reverse={(reverse ? "True" : "False")})"
                    + "\nexcept LookupError as error:\n    print(str(error))",
                "sorting_merge_failure.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Equal(Lines("merge failed"), output.ToString());
            Assert.Equal(
                stop,
                Assert.IsType<PythonWholeNumberValue>(Global(engine, "calls")).Value
            );
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertPermutation(original, receiver.Elements);
        }
    }

    [Fact]
    public void PositiveEmptyExtendHintsRemainObservableAfterNestedSortAndRestoration()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    """
                    values = [3, 1, 2]
                    class Empty:
                        def __iter__(self): return iter(())
                        def __length_hint__(self): return 1
                    def key(value):
                        if value == 3:
                            values.extend(Empty())
                            values.sort()
                        return value
                    try: values.sort(key=key)
                    except ValueError as error: print(str(error), values)
                    values.sort(reverse=True)
                    print(values)
                    """,
                    "sorting_hint_mutation.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(Lines("list modified during sort [1, 2, 3]", "[3, 2, 1]"), output.ToString());
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
                    phase = ''
                    events = []
                    effects = []
                    def visit(name):
                        events.append((name, len(values)))
                        if phase == name:
                            effects.append((name, len(values)))
                            values.append(Item(99))
                            print('callback')
                            for counter in range(5000): pass
                    class Truth:
                        def __init__(self, answer): self.answer = answer
                        def __bool__(self): visit('truth'); return self.answer
                    class Item:
                        def __init__(self, rank): self.rank = rank
                        def __lt__(self, other): visit('compare'); return Truth(self.rank < other.rank)
                    class Reverse:
                        def __bool__(self): visit('reverse'); return False
                    def key(value): visit('key'); return value
                    values = [Item(3), Item(1), Item(2)]
                    saved_sort = values.sort
                    reverse = Reverse()
                    target = 'original'
                    """,
                    "sorting_callback_setup.py",
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
        var effect = Assert.IsType<PythonTupleValue>(
            Assert.Single(Assert.IsType<PythonListValue>(Global(engine, "effects")).Elements)
        );
        Assert.Equal(phase, Assert.IsType<PythonTextValue>(effect.Elements[0]).Value);
        Assert.Equal(
            phase == "reverse" ? 3 : 0,
            Assert.IsType<PythonWholeNumberValue>(effect.Elements[1]).Value
        );
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "print(sorted(item.rank for item in values))",
                    "sorting_state.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(Lines(phase == "reverse" ? "[1, 2, 3, 99]" : "[1, 2, 3]"), output.ToString());
    }

    private static void AssertRecovery(ManagedPythonEngine engine, string phase)
    {
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "phase = ''\nsaved_sort(key=key, reverse=reverse)\nprint([item.rank for item in values])",
                    "sorting_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(Lines(phase == "reverse" ? "[1, 2, 3, 99]" : "[1, 2, 3]"), output.ToString());
    }

    private static void AssertPermutation(PythonValue[] original, List<PythonValue> actual)
    {
        Assert.Equal(original.Length, actual.Count);
        var remaining = actual.ToList();
        foreach (var item in original)
        {
            var index = remaining.FindIndex(candidate => ReferenceEquals(candidate, item));
            Assert.True(index >= 0, "A sorting failure lost or duplicated an original item.");
            remaining.RemoveAt(index);
        }
        Assert.Empty(remaining);
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
