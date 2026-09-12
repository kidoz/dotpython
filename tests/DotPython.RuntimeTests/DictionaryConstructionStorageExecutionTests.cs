using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DictionaryConstructionStorageExecutionTests
{
    [Fact]
    public void FromKeysWorksWithoutAnExecutionAndOwnsItemsWhileSharingKeysAndFill()
    {
        var removed = new PythonDictionaryItemValue(
            PythonWholeNumberValue.Create(1),
            PythonNoneValue.Instance
        );
        var key = PythonWholeNumberValue.Create(42);
        var retained = new PythonDictionaryItemValue(key, new PythonListValue([]));
        var source = new PythonDictionaryValue([removed, retained]);
        source.RemoveItem(removed);
        var fill = new PythonListValue([]);

        var result = ManagedObjectProtocols.DictionaryFromKeys(source, fill, default);

        var item = Assert.Single(result.Items);
        Assert.NotSame(source, result);
        Assert.NotSame(retained, item);
        Assert.Same(key, item.Key);
        Assert.Same(fill, item.Value);
        Assert.Equal(retained.KeyHash, item.KeyHash);
        Assert.Equal(1, result.EntryCount);
        Assert.Same(retained, Assert.Single(source.Items));
        Assert.Null(source.GetEntry(0));
        ManagedObjectProtocols.SetItem(result, key, PythonNoneValue.Instance);
        Assert.IsType<PythonListValue>(retained.Value);
    }

    [Fact]
    public void SavedFromKeysStreamsCallbacksThroughTheCurrentExecution()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            """
            class Key:
                def __init__(self, value): self.value = value
                def __hash__(self):
                    print('hash', self.value)
                    return 7
                def __eq__(self, other):
                    print('equal', self.value, other.value)
                    return False
            class Keys:
                def __iter__(self):
                    print('iter')
                    return Cursor()
                def __length_hint__(self):
                    raise AssertionError('unexpected length hint')
            class Cursor:
                index = 0
                def __iter__(self): return self
                def __next__(self):
                    value = self.index
                    self.index += 1
                    print('next', value)
                    if value == 2: raise StopIteration
                    return Key(value)
            operation = dict.fromkeys
            """,
            "dictionary_fromkeys_callback_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();

        var result = engine.Execute(
            """
            values = operation(Keys(), [])
            print([key.value for key in values])
            print(list(values.values())[0] is list(values.values())[1])
            """,
            "dictionary_fromkeys_callback_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previous.ToString());
        Assert.Equal(
            Lines(
                "iter",
                "next 0",
                "hash 0",
                "next 1",
                "hash 1",
                "equal 0 1",
                "next 2",
                "[0, 1]",
                "True"
            ),
            output.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedFromKeysUsesCurrentBudgetAndLeavesAssignmentIntactOnFailure(bool sparse)
    {
        var engine = PrepareLargeOperation(sparse);
        var original = DictionaryGlobal(engine, "target");
        using var output = new StringWriter();

        var result = engine.Execute(
            "try:\n    target = operation(source, fill)\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "dictionary_fromkeys_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.Same(original, DictionaryGlobal(engine, "target"));
        if (sparse)
        {
            var source = DictionaryGlobal(engine, "source");
            Assert.Single(source.Items);
            Assert.Equal(5000, source.EntryCount);
        }
        AssertRecovery(engine, sparse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedFromKeysUsesCurrentCancellationAndPermitsRecovery(bool sparse)
    {
        var engine = PrepareLargeOperation(sparse);
        var original = DictionaryGlobal(engine, "target");
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                sparse
                    ? "print('cancel')\ntarget = operation(source, fill)"
                    : "def keys():\n    yield 0\n    print('cancel')\n    yield 1\ntarget = operation(keys(), fill)",
                "dictionary_fromkeys_cancel.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(Lines("cancel"), output.ToString());
        Assert.Same(original, DictionaryGlobal(engine, "target"));
        AssertRecovery(engine, sparse);
    }

    [Fact]
    public void HashCallbackFailureStopsConsumptionAndPreservesCallerAssignment()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Key:
                def __hash__(self):
                    print('hash failure')
                    raise ValueError('bad key')
            def keys():
                yield 'first'
                yield Key()
                print('overconsumed')
                yield 'last'
            target = {'original': 1}
            saved = target
            source = keys()
            try:
                target = dict.fromkeys(source, [])
            except ValueError as error:
                print(str(error))
            print(target is saved, target)
            print(next(source))
            print(dict.fromkeys(['recovered'], 2))
            """,
            "dictionary_fromkeys_hash_failure.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "hash failure",
                "bad key",
                "True {'original': 1}",
                "overconsumed",
                "last",
                "{'recovered': 2}"
            ),
            output.ToString()
        );
    }

    private static ManagedPythonEngine PrepareLargeOperation(bool sparse)
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var setup = engine.Execute(
            (
                sparse
                    ? "source = {index: index for index in range(5000)}\nfor index in range(4999):\n    del source[index]\n"
                    : "source = range(5000)\n"
            ) + "target = {'original': 1}\nfill = []\noperation = dict.fromkeys",
            "dictionary_fromkeys_large_setup.py",
            output,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        return engine;
    }

    private static void AssertRecovery(ManagedPythonEngine engine, bool sparse)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "target = operation(source, fill)\nprint(len(source), len(target), target[4999] is fill)",
            "dictionary_fromkeys_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines(sparse ? "1 1 True" : "5000 5000 True"), output.ToString());
    }

    private static PythonDictionaryValue DictionaryGlobal(ManagedPythonEngine engine, string name)
    {
        var globals = Assert.IsType<PythonGlobalNamespace>(
            typeof(ManagedPythonEngine)
                .GetField("_globals", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine)
        );
        Assert.True(globals.TryGetValue(name, out var value));
        return Assert.IsType<PythonDictionaryValue>(value);
    }

    private static string Lines(params string[] values) =>
        string.Join(Environment.NewLine, values) + Environment.NewLine;

    private sealed class CancelAfterFirstLineWriter(CancellationTokenSource cancellation)
        : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            cancellation.Cancel();
        }
    }
}
