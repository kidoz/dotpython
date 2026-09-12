using System.Numerics;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ContainerIteratorHintExecutionTests
{
    [Fact]
    public void ContainerHintsAndMaterializationWorkWithoutAnActiveVirtualMachine()
    {
        var dictionary = new PythonDictionaryValue([
            new(new PythonTextValue("a"), PythonWholeNumberValue.Create(1)),
            new(new PythonTextValue("b"), PythonWholeNumberValue.Create(2)),
        ]);
        PythonValue[] sources =
        [
            new PythonTextValue("a😀"),
            new PythonByteSequenceValue([1, 2]),
            dictionary,
            new PythonDictionaryViewValue("dict_values", dictionary),
            new PythonSetValue([
                PythonWholeNumberValue.Create(1),
                PythonWholeNumberValue.Create(2),
            ]),
        ];
        foreach (var source in sources)
        {
            var iterator = ManagedObjectProtocols.GetIterator(source);
            var hint = ManagedObjectProtocols.GetAttribute(iterator, "__length_hint__");
            Assert.Equal(new BigInteger(2), Hint(hint));
            Assert.True(ManagedObjectProtocols.TryGetNext(iterator, out _));
            Assert.Equal(BigInteger.One, Hint(hint));
            var remaining = Assert.IsType<PythonListValue>(
                ManagedObjectProtocols.Call(PythonBuiltinTypes.List, [iterator])
            );
            Assert.Single(remaining.Elements);
            Assert.Equal(BigInteger.Zero, Hint(hint));
            Assert.True(iterator.IsExhausted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedHintsRetainPermanentSizeFailureAcrossExecutions(bool set)
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        using var previousCancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            (set ? "values = {1, 2}" : "values = {1: 10, 2: 20}")
                + "\nsaved = iter(values)\nhint = saved.__length_hint__\nadvance = saved.__next__",
            "container_hint_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            (set ? "values.add(3)" : "values[3] = 30")
                + "\nprint(hint())\ntry: advance()\nexcept RuntimeError as error: print(str(error))\n"
                + (set ? "values.remove(3)" : "del values[3]")
                + "\nprint(hint())\ntry: next(saved)\nexcept RuntimeError as error: print(str(error))",
            "container_hint_invalidated.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        var error = set
            ? "Set changed size during iteration"
            : "dictionary changed size during iteration";
        Assert.Equal(Lines("0", error, "0", error), output.ToString());
        Assert.Equal(string.Empty, previous.ToString());
    }

    [Theory]
    [InlineData("'x' * 10000", false)]
    [InlineData("bytes([120] * 10000)", true)]
    [InlineData("{index: index for index in range(10000)}.values()", false)]
    [InlineData("set(range(10000))", true)]
    public void ContainerMaterializationUsesCurrentBudgetAndRetainsCursor(
        string source,
        bool extend
    )
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            "saved = iter(" + source + ")\nhint = saved.__length_hint__\nvalues = []",
            "container_hint_budget_setup.py",
            previous,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            "try:\n    "
                + (extend ? "values.extend(saved)" : "list(saved)")
                + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "container_hint_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.Equal(string.Empty, previous.ToString());
        using var recoveryOutput = new StringWriter();
        var recovery = engine.Execute(
            "remaining = hint()\nprint(9000 < remaining < 10000)\nnext(saved)\nprint(hint() == remaining - 1)\n"
                + (extend ? "print(len(values) == 10000 - remaining)" : "print(values == [])"),
            "container_hint_recovery.py",
            recoveryOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovery.Success);
        Assert.Equal(Lines("True", "True", "True"), recoveryOutput.ToString());
    }

    private static BigInteger Hint(PythonValue method) =>
        Assert.IsType<PythonWholeNumberValue>(ManagedObjectProtocols.Call(method, [])).Value;

    private static string Lines(params string[] values) =>
        string.Join(Environment.NewLine, values) + Environment.NewLine;
}
