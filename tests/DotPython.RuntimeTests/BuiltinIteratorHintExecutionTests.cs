using System.Numerics;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BuiltinIteratorHintExecutionTests
{
    [Fact]
    public void BuiltinIteratorMaterializationDoesNotRequireAnActiveVirtualMachine()
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
            var iterator = ManagedObjectProtocols.GetIterator(source);
            var result = Assert.IsType<PythonListValue>(
                ManagedObjectProtocols.Call(PythonBuiltinTypes.List, [iterator])
            );

            Assert.Equal(
                new BigInteger[] { 1, 2 },
                result.Elements.Select(value => Assert.IsType<PythonWholeNumberValue>(value).Value)
            );
            Assert.True(iterator.IsExhausted);
        }
    }

    [Fact]
    public void RangeCursorAndHintAdvanceAcrossTheManagedIntegerBoundary()
    {
        var boundary = new BigInteger(int.MaxValue);
        var iterator = new PythonIteratorValue(new PythonRangeValue(0, boundary + 2, 1), -1)
        {
            RangeIndex = boundary - 1,
        };
        var hint = ManagedObjectProtocols.GetAttribute(iterator, "__length_hint__");

        for (var offset = 0; offset < 3; offset++)
        {
            Assert.Equal(
                new BigInteger(3 - offset),
                Assert.IsType<PythonWholeNumberValue>(ManagedObjectProtocols.Call(hint, [])).Value
            );
            Assert.True(ManagedObjectProtocols.TryGetNext(iterator, out var value));
            Assert.Equal(boundary - 1 + offset, Assert.IsType<PythonWholeNumberValue>(value).Value);
        }

        Assert.Equal(boundary + 2, iterator.RangeIndex);
        Assert.False(ManagedObjectProtocols.TryGetNext(iterator, out _));
        Assert.True(iterator.IsExhausted);
        Assert.Equal(
            BigInteger.Zero,
            Assert.IsType<PythonWholeNumberValue>(ManagedObjectProtocols.Call(hint, [])).Value
        );
    }

    [Fact]
    public void SavedListHintTracksMutationAndPermanentExhaustionAcrossExecutions()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            "values = [10, 20]\nsaved = iter(values)\nhint = saved.__length_hint__\nprint(hint(), next(saved))",
            "builtin_list_hint_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            values.append(30)
            print(hint())
            values.clear()
            print(hint())
            values.extend([40, 50, 60])
            print(hint(), next(saved), next(saved))
            print(next(saved, 'exhausted'))
            values.append(70)
            print(hint(), next(saved, 'still exhausted'))
            """,
            "builtin_list_hint_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines("2 10"), previousOutput.ToString());
        Assert.Equal(
            Lines("2", "0", "2 50 60", "exhausted", "0 still exhausted"),
            output.ToString()
        );
    }

    [Fact]
    public void SavedTupleAndDescendingRangeHintsTrackIndependentCursors()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            tuples = iter(('a', 'b', 'c'))
            ranges = iter(range(9, 0, -2))
            tuple_hint = tuples.__length_hint__
            range_hint = ranges.__length_hint__
            print(tuple_hint(), range_hint())
            print(next(tuples), next(ranges))
            """,
            "builtin_hint_cursor_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(tuple_hint(), range_hint())
            print(list(tuples), list(ranges))
            print(tuple_hint(), range_hint())
            print(next(tuples, 'end'), next(ranges, 'end'))
            """,
            "builtin_hint_cursor_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines("3 5", "a 9"), previousOutput.ToString());
        Assert.Equal(Lines("2 4", "['b', 'c'] [7, 5, 3, 1]", "0 0", "end end"), output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeRangeHintOverflowDoesNotConsumeTheIterator(bool extend)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            "saved = iter(range(1 << 70))\nhint = saved.__length_hint__\nvalues = []",
            "builtin_range_hint_overflow_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        // The hint cannot fit a signed 64-bit size. Conversion must fail before
        // attempting allocation or walking this intentionally enormous range.
        var result = engine.Execute(
            "try:\n    "
                + (extend ? "values.extend(saved)" : "list(saved)")
                + "\nexcept OverflowError as error:\n    print(str(error))\nprint(next(saved), hint() == (1 << 70) - 1, values)",
            "builtin_range_hint_overflow.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(
            Lines("Python int too large to convert to C ssize_t", "0 True []"),
            output.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuiltinIteratorMaterializationUsesCurrentBudgetAndRetainsProgress(bool extend)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            "saved = iter(range(1000000))\nhint = saved.__length_hint__\nvalues = []",
            "builtin_iterator_budget_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            "try:\n    "
                + (extend ? "values.extend(saved)" : "list(saved)")
                + "\nexcept BaseException:\n    print('swallowed budget')\nfinally:\n    print('cleanup')",
            "builtin_iterator_current_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        using var recoveryOutput = new StringWriter();
        var recovery = engine.Execute(
            "next_value = next(saved)\nprint(0 < next_value < 1000, hint() == 1000000 - next_value - 1)\n"
                + (
                    extend
                        ? "print(len(values) == next_value, values[0] == 0, values[-1] == next_value - 1)"
                        : "print(values == [])"
                ),
            "builtin_iterator_budget_recovery.py",
            recoveryOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(recovery.Success);
        Assert.Equal(
            Lines("True True", extend ? "True True True" : "True"),
            recoveryOutput.ToString()
        );
    }

    private static string Lines(params string[] values) =>
        string.Join(Environment.NewLine, values) + Environment.NewLine;
}
