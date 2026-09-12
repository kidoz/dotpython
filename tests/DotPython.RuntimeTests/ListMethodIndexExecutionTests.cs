using System.Numerics;
using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ListMethodIndexExecutionTests
{
    private static readonly string[] Operations =
    [
        "target = saved_pop(index)",
        "target = saved_insert(index, 99)",
    ];

    [Fact]
    public void SavedMethodsAndIndexUseCurrentExecutionAndMutatedLength()
    {
        foreach (var operation in Operations)
        {
            var engine = PrepareCallbackEngine();
            using var output = new StringWriter();
            var result = engine.Execute(
                "Index.cancel = True\n" + operation + "\nprint(target, values)",
                "list_method_index_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            var expected = operation == Operations[0] ? "9 [1, 2, 3]" : "None [1, 2, 3, 99, 9]";
            Assert.Equal(Lines("cancel", expected), output.ToString());
        }
    }

    [Fact]
    public void CancelledIndexRetainsMutationWithoutPublishingPopOrInsert()
    {
        foreach (var operation in Operations)
        {
            var engine = PrepareCallbackEngine();
            var originalTarget = Global(engine, "target");
            var originalValues = Global(engine, "values");
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelAfterLineWriter(cancellation);
            Assert.Throws<OperationCanceledException>(() =>
                engine.Execute(
                    "Index.cancel = True\n" + operation,
                    "list_method_index_cancel.py",
                    output,
                    cancellationToken: cancellation.Token
                )
            );
            Assert.Equal(Lines("cancel"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalValues, Global(engine, "values"));
            Assert.Same(originalValues, Global(engine, "alias"));
            AssertValues(engine, 1, 2, 3, 9);
            AssertRecovery(engine, operation);
        }
    }

    [Fact]
    public void IndexCallbacksChargeCurrentBudgetAndUnwindBeforePopOrInsert()
    {
        foreach (var operation in Operations)
        {
            var engine = PrepareCallbackEngine();
            var originalTarget = Global(engine, "target");
            using var output = new StringWriter();
            var result = engine.Execute(
                "Index.slow = True\ntry:\n    "
                    + operation
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "list_method_index_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            AssertValues(engine, 1, 2, 3, 8);
            AssertRecovery(engine, operation);
        }
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
                        values.append(8)
                        for counter in range(5000): pass
                    return -1
            index = Index()
            values = [1, 2, 3]
            alias = values
            saved_pop = values.pop
            saved_insert = values.insert
            target = 'original'
            """,
            "list_method_index_callback_setup.py",
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
            "list_method_index_recovery.py",
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
