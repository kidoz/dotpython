using System.Numerics;
using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SequenceIndexExecutionTests
{
    [Fact]
    public void SavedIndexObjectsUseCurrentOutputAndCancellationForSlicesAndRepetition()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            class Index:
                def __init__(self, name, value):
                    self.name = name
                    self.value = value
                def __index__(self):
                    print(self.name)
                    return self.value
            start = Index('start', 0)
            stop = Index('stop', 3)
            step = Index('step', 1)
            count = Index('count', 2)
            values = [1, 2, 3]
            """,
            "sequence_index_setup.py",
            previousOutput,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(values[start:stop:step])
            print(values * count)
            print(count * 'x')
            """,
            "sequence_index_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Empty(previousOutput.ToString());
        Assert.Equal(
            Lines(
                "step",
                "start",
                "stop",
                "[1, 2, 3]",
                "count",
                "[1, 2, 3, 1, 2, 3]",
                "count",
                "xx"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void IndexCallbacksChargeCurrentBudgetAndRecoverWithoutPublishingResults()
    {
        foreach (
            var operation in new[]
            {
                "target = values[index:]",
                "target = values * index",
                "values *= index",
            }
        )
        {
            var engine = PrepareCallbackEngine();
            var originalTarget = Global(engine, "target");
            var originalValues = Global(engine, "values");
            using var output = new StringWriter();
            var result = engine.Execute(
                "Index.slow = True\ntry:\n    "
                    + operation
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "sequence_index_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalValues, Global(engine, "values"));
            Assert.Equal(3, Assert.IsType<PythonListValue>(originalValues).Elements.Count);
            AssertRecovery(engine, operation);
        }
    }

    [Fact]
    public void CancelledSliceIndexRetainsCallbackMutationWithoutPublishingSlice()
    {
        var engine = PrepareCallbackEngine();
        var originalTarget = Global(engine, "target");
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterLineWriter(cancellation);
        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                "Index.cancel = True\ntarget = values[index:]",
                "sequence_index_slice_cancel.py",
                output,
                cancellationToken: cancellation.Token
            )
        );
        Assert.Equal(Lines("cancel"), output.ToString());
        Assert.Same(originalTarget, Global(engine, "target"));
        AssertValues(engine, 1, 2, 3, 9);
        AssertRecovery(engine, "target = values[index:]");
    }

    [Fact]
    public void CancelledInPlaceRepetitionRetainsOnlyCallbackMutationAndOriginalAliases()
    {
        var engine = PrepareCallbackEngine();
        var originalValues = Global(engine, "values");
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterLineWriter(cancellation);
        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                "Index.cancel = True\nvalues *= index",
                "sequence_index_repeat_cancel.py",
                output,
                cancellationToken: cancellation.Token
            )
        );
        Assert.Equal(Lines("cancel"), output.ToString());
        Assert.Same(originalValues, Global(engine, "values"));
        Assert.Same(originalValues, Global(engine, "alias"));
        AssertValues(engine, 1, 2, 3, 9);
        AssertRecovery(engine, "values *= index");
        Assert.Same(originalValues, Global(engine, "values"));
        AssertValues(engine, 1, 2, 3, 9, 1, 2, 3, 9);
    }

    [Fact]
    public void UserIndexNoOpsPreserveTupleIdentityAndPreviouslyPublishedHash()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Key:
                calls = 0
                def __hash__(self):
                    Key.calls += 1
                    return 123
            class Index:
                calls = 0
                def __index__(self):
                    Index.calls += 1
                    return 1
            value = (Key(),)
            initial = hash(value)
            index = Index()
            sliced = value[::index]
            repeated = value * index
            reflected = index * value
            value *= index
            print(sliced is value, repeated is value, reflected is value)
            print(hash(sliced) == initial, hash(repeated) == initial, hash(reflected) == initial)
            print(Index.calls, Key.calls)
            """,
            "sequence_index_tuple_cache.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("True True True", "True True True", "4 1"), output.ToString());
        var tuple = Assert.IsType<PythonTupleValue>(Global(engine, "value"));
        Assert.NotEqual(-1, tuple.CachedHash);
        Assert.Same(tuple, Global(engine, "sliced"));
        Assert.Same(tuple, Global(engine, "repeated"));
        Assert.Same(tuple, Global(engine, "reflected"));
    }

    [Fact]
    public void EmptyInPlaceRepetitionValidatesSignedWidthBeforeTakingAllocationFreeNoOp()
    {
        var empty = new PythonListValue([]);
        foreach (var count in new BigInteger[] { long.MinValue, 0, 1, long.MaxValue })
        {
            ManagedObjectProtocols.RepeatListInPlace(
                empty,
                PythonWholeNumberValue.Create(count),
                default
            );
            Assert.Empty(empty.Elements);
        }
        foreach (
            var count in new[] { (BigInteger)long.MinValue - 1, (BigInteger)long.MaxValue + 1 }
        )
        {
            var error = Assert.Throws<PythonRuntimeException>(() =>
                ManagedObjectProtocols.RepeatListInPlace(
                    empty,
                    PythonWholeNumberValue.Create(count),
                    default
                )
            );
            Assert.Equal("OverflowError", error.PythonExceptionTypeName);
            Assert.Empty(empty.Elements);
        }
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Index:
                def __index__(self): return 9223372036854775807
            values = []
            alias = values
            values *= Index()
            print(values is alias, len(values), len(() * Index()), len('' * Index()))
            """,
            "sequence_index_empty.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("True 0 0 0"), output.ToString());
    }

    private static ManagedPythonEngine PrepareCallbackEngine()
    {
        var engine = new ManagedPythonEngine();
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var setup = engine.Execute(
            """
            class Index:
                slow = False
                cancel = False
                def __index__(self):
                    if self.cancel:
                        values.append(9)
                        print('cancel')
                    if self.slow:
                        for counter in range(5000): pass
                    return 2
            index = Index()
            values = [1, 2, 3]
            alias = values
            target = 'original'
            """,
            "sequence_index_callback_setup.py",
            output,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        return engine;
    }

    private static void AssertRecovery(ManagedPythonEngine engine, string operation)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "Index.slow = False\nIndex.cancel = False\n" + operation + "\nprint('recovered')",
            "sequence_index_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("recovered"), output.ToString());
    }

    private static void AssertValues(ManagedPythonEngine engine, params int[] expected) =>
        Assert.Equal(
            expected.Select(value => new BigInteger(value)),
            Assert
                .IsType<PythonListValue>(Global(engine, "values"))
                .Elements.Select(value => Assert.IsType<PythonWholeNumberValue>(value).Value)
        );

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
