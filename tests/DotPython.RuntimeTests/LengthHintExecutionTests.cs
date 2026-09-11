using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class LengthHintExecutionTests
{
    private const string Setup = """
        blocking = False
        failing = False
        failure = RuntimeError('length failed')
        class Sequence:
            def __len__(self):
                try:
                    print('length')
                    while blocking: pass
                    if failing: raise failure
                    return 2
                finally:
                    print('length cleanup')
            def __getitem__(self, index):
                print('getitem', index)
                if index >= 2: raise IndexError
                return index
        sequence = Sequence()
        saved = iter(sequence)
        saved_hint = saved.__length_hint__
        """;

    [Fact]
    public void SavedBoundLengthHintUsesCurrentExecutionAndCurrentIteratorIndex()
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        Prepare(engine, setupOutput);
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            "print(saved_hint())",
            "length_hint_first.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        using var output = new StringWriter();
        var second = engine.Execute(
            "print(next(saved))\nprint(saved_hint())",
            "length_hint_second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(second.Success);
        Assert.Equal(string.Empty, setupOutput.ToString());
        Assert.Equal(Lines("length", "length cleanup", "2"), firstOutput.ToString());
        Assert.Equal(Lines("getitem 0", "0", "length", "length cleanup", "1"), output.ToString());
    }

    [Fact]
    public void ListAndExceptionNotesLengthCallbacksUseCurrentOutput()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(list(sequence))
            root = ExceptionGroup('root', [ValueError(), TypeError()])
            root.__notes__ = sequence
            result = root.subgroup(ValueError)
            print(result.__notes__)
            """,
            "length_hint_consumers.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(
            Lines(
                "length",
                "length cleanup",
                "getitem 0",
                "getitem 1",
                "getitem 2",
                "[0, 1]",
                "length",
                "length cleanup",
                "getitem 0",
                "getitem 1",
                "getitem 2",
                "[0, 1]"
            ),
            output.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LengthCallbackInstructionLimitEscapesAndIteratorRemainsUsable(bool materialize)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            GuardedOperation(materialize),
            "length_hint_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("length", "length cleanup", "outer cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LengthCallbackCancellationEscapesAndIteratorRemainsUsable(bool materialize)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                GuardedOperation(materialize),
                "length_hint_cancellation.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(Lines("length", "length cleanup", "outer cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void MaterializationHintFailurePreservesExceptionIdentityAndUnconsumedIndex()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            "failing = True\ntry: list(saved)\nexcept RuntimeError as error: print(error is failure)",
            "length_hint_failure.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines("length", "length cleanup", "True"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void HugeValidHintsDoNotAllocateUntrustedCapacity()
    {
        // Deliberately managed-only: CPython may try the advertised allocation.
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Iterator:
                def __init__(self): self.done = False
                def __iter__(self): return self
                def __length_hint__(self):
                    print('iterator hint')
                    return 1 << 40
                def __next__(self):
                    if self.done: raise StopIteration
                    self.done = True
                    return 'one'
            class Sequence:
                def __len__(self):
                    print('sequence length')
                    return 1 << 40
                def __getitem__(self, index):
                    if index: raise IndexError
                    return 'one'
            print(list(Iterator()))
            print(list(Sequence()))
            """,
            "length_hint_untrusted_capacity.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 1000 },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            Lines("iterator hint", "['one']", "sequence length", "['one']"),
            output.ToString()
        );
    }

    private static string GuardedOperation(bool materialize) =>
        "blocking = True\ntry:\n    "
        + (materialize ? "list(saved)" : "saved_hint()")
        + "\nexcept BaseException:\n    print('outer swallowed')\nfinally:\n    print('outer cleanup')";

    private static void Prepare(ManagedPythonEngine engine, StringWriter output)
    {
        var result = engine.Execute(
            Setup,
            "length_hint_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, output.ToString());
    }

    private static void AssertRecovery(ManagedPythonEngine engine)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "blocking = False\nfailing = False\nprint(next(saved))\nprint(saved_hint())",
            "length_hint_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("getitem 0", "0", "length", "length cleanup", "1"), output.ToString());
    }

    private static string Lines(params string[] values) =>
        string.Join(Environment.NewLine, values) + Environment.NewLine;

    private sealed class CancelAfterFirstLineWriter(CancellationTokenSource cancellation)
        : StringWriter
    {
        private bool _cancelled;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (!_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
        }
    }
}
