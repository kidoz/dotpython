using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BytesSequenceExecutionTests
{
    [Fact]
    public void SavedIndexObjectsUseCurrentOutputAndCancellationForByteOperations()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): print(self.name); return self.value
            start = Index('start', 1)
            stop = Index('stop', 5)
            step = Index('step', 2)
            count = Index('count', 2)
            values = b'abcde'
            """,
            "bytes_sequence_setup.py",
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
            print(count * values)
            values *= count
            print(values)
            """,
            "bytes_sequence_current.py",
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
                "b'bd'",
                "count",
                "b'abcdeabcde'",
                "count",
                "b'abcdeabcde'",
                "count",
                "b'abcdeabcde'"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void IndexCallbacksChargeCurrentBudgetWithoutPublishingByteResults()
    {
        foreach (var operation in CallbackOperations())
        {
            var engine = PrepareCallbackEngine();
            var originalTarget = Global(engine, "target");
            var originalValues = Global(engine, "values");
            using var output = new StringWriter();
            var result = engine.Execute(
                "Index.slow = True\ntry:\n    "
                    + operation
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "bytes_sequence_callback_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalValues, Global(engine, "values"));
            Assert.Same(originalValues, Global(engine, "alias"));
            Assert.Equal(
                new byte[] { 97, 98, 99, 100 },
                Assert.IsType<PythonByteSequenceValue>(originalValues).Value
            );
            AssertRecovery(engine, operation);
        }
    }

    [Fact]
    public void CancelledIndexCallbacksPreserveOriginalByteBindingsAndAliases()
    {
        foreach (var operation in CallbackOperations())
        {
            var engine = PrepareCallbackEngine();
            var originalTarget = Global(engine, "target");
            var originalValues = Global(engine, "values");
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelAfterLineWriter(cancellation);
            Assert.Throws<OperationCanceledException>(() =>
                engine.Execute(
                    "Index.cancel = True\n" + operation,
                    "bytes_sequence_callback_cancel.py",
                    output,
                    cancellationToken: cancellation.Token
                )
            );
            Assert.Equal(Lines("cancel"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalValues, Global(engine, "values"));
            Assert.Same(originalValues, Global(engine, "alias"));
            Assert.Equal(
                new byte[] { 97, 98, 99, 100 },
                Assert.IsType<PythonByteSequenceValue>(originalValues).Value
            );
            AssertRecovery(engine, operation);
        }
    }

    [Fact]
    public void LargeByteCopiesChargeWorkAndRecoverWithoutPublishingPartialResults()
    {
        foreach (
            var operation in new[] { "target = values[::-1]", "target = values * 3", "values *= 3" }
        )
        {
            var engine = PrepareLargeByteEngine();
            var originalTarget = Global(engine, "target");
            var originalValues = Assert.IsType<PythonByteSequenceValue>(Global(engine, "values"));
            using var output = new StringWriter();
            var result = engine.Execute(
                "try:\n    " + operation + "\nfinally:\n    print('cleanup')",
                "bytes_sequence_copy_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 100 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalValues, Global(engine, "values"));
            Assert.Same(originalValues, Global(engine, "alias"));
            Assert.Equal(200_000, originalValues.Value.Length);
            Assert.Equal((byte)'a', originalValues.Value[0]);
            Assert.Equal((byte)'b', originalValues.Value[^1]);
            AssertRecovery(engine, operation, hasIndex: false);
            var copied = Assert.IsType<PythonByteSequenceValue>(
                Global(
                    engine,
                    operation.StartsWith("target", StringComparison.Ordinal) ? "target" : "values"
                )
            );
            Assert.NotSame(originalValues, copied);
            Assert.Equal(
                operation.Contains("[::-1]", StringComparison.Ordinal) ? 200_000 : 600_000,
                copied.Value.Length
            );
            Assert.Equal(
                operation.Contains("[::-1]", StringComparison.Ordinal) ? (byte)'b' : (byte)'a',
                copied.Value[0]
            );
            Assert.Same(originalValues, Global(engine, "alias"));
        }
    }

    [Fact]
    public void FullSlicesAndRepetitionByOnePreserveLargeByteIdentityWithinSmallBudget()
    {
        var engine = PrepareLargeByteEngine();
        var original = Global(engine, "values");
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            sliced = values[:]
            bounded = values[-(2 ** 100):2 ** 100:1]
            repeated = values * 1
            reflected = 1 * values
            values *= 1
            print(sliced is values, bounded is values, repeated is values, reflected is values)
            """,
            "bytes_sequence_identity.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("True True True True"), output.ToString());
        foreach (
            var name in new[] { "values", "alias", "sliced", "bounded", "repeated", "reflected" }
        )
            Assert.Same(original, Global(engine, name));
    }

    [Fact]
    public void RepetitionRejectsNativeProductOverflowAndManagedAllocationLimitBeforeCopying()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            for count in (2 ** 62, 5000001):
                values = b'ab'
                alias = values
                try: values *= count
                except OverflowError as error: print(str(error), values is alias, len(values))
            class Index:
                def __index__(self): return 2 ** 63
            try: b'' * Index()
            except OverflowError as error: print(str(error))
            """,
            "bytes_sequence_repeat_limits.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "repeated bytes are too long True 2",
                "The repeated sequence exceeds the supported size. True 2",
                "cannot fit 'Index' into an index-sized integer"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void FailedInPlaceIndexConversionKeepsImmutableBytesAndAllowsLaterRecovery()
    {
        var engine = PrepareCallbackEngine();
        var original = Global(engine, "values");
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            problem = ValueError('repeat failed')
            def fail(self): raise problem
            previous = Index.__index__
            Index.__index__ = fail
            try: values *= index
            except ValueError as error: print(error is problem, values is alias)
            Index.__index__ = previous
            values *= index
            print(values, alias, values is alias)
            """,
            "bytes_sequence_repeat_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("True True", "b'abcdabcd' b'abcd' False"), output.ToString());
        Assert.Same(original, Global(engine, "alias"));
        Assert.NotSame(original, Global(engine, "values"));
    }

    private static string[] CallbackOperations() =>
        [
            "target = values[index:]",
            "target = values[:index]",
            "target = values[::index]",
            "target = values * index",
            "target = index * values",
            "values *= index",
        ];

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
                    if self.cancel: print('cancel')
                    if self.slow:
                        for counter in range(5000): pass
                    return 2
            index = Index()
            values = b'abcd'
            alias = values
            target = 'original'
            """,
            "bytes_sequence_callback_setup.py",
            output,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        return engine;
    }

    private static ManagedPythonEngine PrepareLargeByteEngine()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var setup = engine.Execute(
            "values = b'ab' * 100000\nalias = values\ntarget = 'original'",
            "bytes_sequence_large_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        return engine;
    }

    private static void AssertRecovery(
        ManagedPythonEngine engine,
        string operation,
        bool hasIndex = true
    )
    {
        using var output = new StringWriter();
        var source = hasIndex
            ? "Index.slow = False\nIndex.cancel = False\n" + operation
            : operation;
        var result = engine.Execute(
            source + "\nprint('recovered')",
            "bytes_sequence_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("recovered"), output.ToString());
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
