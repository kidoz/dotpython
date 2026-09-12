using System.Numerics;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ReverseIteratorExecutionTests
{
    private const string Setup = """
        blocking_length = False
        blocking_item = False
        class Sequence:
            def __len__(self):
                try:
                    print('length')
                    while blocking_length: pass
                    return 3
                finally:
                    print('length cleanup')
            def __getitem__(self, index):
                try:
                    print('getitem', index)
                    while blocking_item: pass
                    return index
                finally:
                    print('getitem cleanup', index)
        saved = reversed(Sequence())
        saved_next = saved.__next__
        saved_hint = saved.__length_hint__
        """;

    [Fact]
    public void SavedListMethodsObserveMutationsAndPermanentExhaustionAcrossExecutions()
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        var setup = engine.Execute(
            "values = [10, 20, 30]\nsaved = reversed(values)\nadvance = saved.__next__\nhint = saved.__length_hint__\nprint(hint(), advance())",
            "reverse_list_setup.py",
            setupOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            values.clear()
            print(hint())
            values.extend([40, 50, 60, 70])
            print(hint(), advance())
            values[0] = 80
            print(hint(), advance())
            print(next(saved, 'end'))
            values.append(90)
            print(hint(), next(saved, 'still exhausted'))
            """,
            "reverse_list_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines("3 30"), setupOutput.ToString());
        Assert.Equal(Lines("0", "2 50", "1 80", "end", "0 still exhausted"), output.ToString());
    }

    [Fact]
    public void SavedGenericMethodsUseCurrentOutputAndPythonFailureExhaustsTheIterator()
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        Prepare(engine, setupOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(saved_next(), saved_hint())
            failure = RuntimeError('getitem failed')
            def replacement(self, index):
                print('replacement', index)
                raise failure
            Sequence.__getitem__ = replacement
            try: saved_next()
            except RuntimeError as error: print(error is failure)
            print(next(saved, 'exhausted'), saved_hint())
            """,
            "reverse_generic_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines("length", "length cleanup"), setupOutput.ToString());
        Assert.Equal(
            Lines(
                "getitem 2",
                "getitem cleanup 2",
                "length",
                "length cleanup",
                "2 2",
                "replacement 1",
                "True",
                "exhausted 0"
            ),
            output.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedGenericCallbackUsesCurrentBudgetAndHostFailurePreservesPosition(bool hint)
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        Prepare(engine, setupOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            GuardedOperation(hint),
            "reverse_generic_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(InterruptedOutput(hint), output.ToString());
        Assert.Equal(Lines("length", "length cleanup"), setupOutput.ToString());
        AssertRecovery(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedGenericCallbackUsesCurrentCancellationAndHostFailurePreservesPosition(
        bool hint
    )
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        Prepare(engine, setupOutput);
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                GuardedOperation(hint),
                "reverse_generic_cancellation.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(InterruptedOutput(hint), output.ToString());
        Assert.Equal(Lines("length", "length cleanup"), setupOutput.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void HugeRangeConstructionAndFirstStepDoNotMaterializeOrNarrowItsLength()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            count = 1 << 100
            saved = reversed(range(count))
            hint = saved.__length_hint__
            print(hint() == count, next(saved) == count - 1, hint() == count - 1)
            try: list(saved)
            except OverflowError: print('hint overflow')
            print(next(saved) == count - 2, hint() == count - 2)
            """,
            "reverse_huge_range.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines("True True True", "hint overflow", "True True"), output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedReverseRangeMaterializationUsesCurrentBudgetAndRetainsProgress(bool extend)
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        var setup = engine.Execute(
            "saved = reversed(range(1000000))\nhint = saved.__length_hint__\nvalues = []",
            "reverse_range_budget_setup.py",
            setupOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            "try:\n    "
                + (extend ? "values.extend(saved)" : "list(saved)")
                + "\nexcept BaseException:\n    print('swallowed budget')\nfinally:\n    print('cleanup')",
            "reverse_range_current_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.Equal(string.Empty, setupOutput.ToString());
        using var recoveryOutput = new StringWriter();
        var recovery = engine.Execute(
            "value = next(saved)\nprint(999000 < value < 999999, hint() == value)\n"
                + (
                    extend
                        ? "print(len(values) == 999999 - value, values[0] == 999999, values[-1] == value + 1)"
                        : "print(values == [])"
                ),
            "reverse_range_budget_recovery.py",
            recoveryOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(recovery.Success);
        Assert.Equal(
            Lines("True True", extend ? "True True True" : "True"),
            recoveryOutput.ToString()
        );
    }

    [Fact]
    public void BuiltinReverseIteratorsWorkWithoutAVirtualMachineAndReleaseExhaustedSources()
    {
        PythonValue[] sources =
        [
            new PythonListValue([
                PythonWholeNumberValue.Create(1),
                PythonWholeNumberValue.Create(2),
            ]),
            new PythonTupleValue([
                PythonWholeNumberValue.Create(1),
                PythonWholeNumberValue.Create(2),
            ]),
            new PythonRangeValue(1, 3, 1),
        ];
        foreach (var source in sources)
        {
            var iterator = Assert.IsType<PythonIteratorValue>(
                PythonReverseIterators.Create(source, default)
            );
            var state = Assert.IsType<PythonReverseIteratorSourceValue>(iterator.Iterable);
            var hint = ManagedObjectProtocols.GetAttribute(iterator, "__length_hint__");
            var identity = ManagedObjectProtocols.GetAttribute(iterator, "__iter__");
            Assert.Same(iterator, ManagedObjectProtocols.Call(identity, []));
            Assert.Same(source, state.Sequence);
            Assert.Equal(
                new BigInteger(2),
                Assert.IsType<PythonWholeNumberValue>(ManagedObjectProtocols.Call(hint, [])).Value
            );

            var values = Assert.IsType<PythonListValue>(
                ManagedObjectProtocols.Call(PythonBuiltinTypes.List, [iterator])
            );

            Assert.Equal(
                new BigInteger[] { 2, 1 },
                values.Elements.Select(value => Assert.IsType<PythonWholeNumberValue>(value).Value)
            );
            Assert.True(iterator.IsExhausted);
            Assert.Null(state.Sequence);
            Assert.Equal(
                BigInteger.Zero,
                Assert.IsType<PythonWholeNumberValue>(ManagedObjectProtocols.Call(hint, [])).Value
            );
        }
    }

    private static string GuardedOperation(bool hint) =>
        (hint ? "blocking_length = True" : "blocking_item = True")
        + "\ntry:\n    "
        + (hint ? "saved_hint()" : "saved_next()")
        + "\nexcept BaseException:\n    print('outer swallowed')\nfinally:\n    print('outer cleanup')";

    private static string InterruptedOutput(bool hint) =>
        hint
            ? Lines("length", "length cleanup", "outer cleanup")
            : Lines("getitem 2", "getitem cleanup 2", "outer cleanup");

    private static void Prepare(ManagedPythonEngine engine, StringWriter output)
    {
        var result = engine.Execute(
            Setup,
            "reverse_generic_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("length", "length cleanup"), output.ToString());
    }

    private static void AssertRecovery(ManagedPythonEngine engine)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "blocking_length = False\nblocking_item = False\nprint(saved_next(), saved_hint())",
            "reverse_generic_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines("getitem 2", "getitem cleanup 2", "length", "length cleanup", "2 2"),
            output.ToString()
        );
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
