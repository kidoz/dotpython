using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DictionaryCopyStorageExecutionTests
{
    [Fact]
    public void ShallowCopyOwnsItemsButPreservesKeyValueAndCachedHashIdentity()
    {
        var key = new PythonTextValue("key");
        var shared = new PythonListValue([]);
        var source = new PythonDictionaryValue([new(key, shared)]);
        var original = Assert.Single(source.Items);

        var copy = source.ShallowCopy();

        var copied = Assert.Single(copy.Items);
        Assert.NotSame(source, copy);
        Assert.NotSame(original, copied);
        Assert.Same(key, copied.Key);
        Assert.Same(shared, copied.Value);
        Assert.Equal(original.KeyHash, copied.KeyHash);
        ManagedObjectProtocols.SetItem(copy, key, PythonNoneValue.Instance);
        Assert.Same(shared, original.Value);
        Assert.Same(PythonNoneValue.Instance, copied.Value);
        copy.RemoveItem(copied);
        Assert.Empty(copy.Items);
        Assert.Same(original, Assert.Single(source.Items));
    }

    [Fact]
    public void CopyAndDirectDictionaryMergesReuseCachedHashes()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Key:
                calls = 0
                def __hash__(self):
                    self.calls += 1
                    return 42
            key = Key()
            shared = []
            source = {key: shared}
            key.calls = 0
            copied = source.copy()
            constructed = dict(source)
            empty = {}
            empty.update(source)
            populated = {'prefix': 0}
            populated.update(source)
            print(key.calls)
            for value in (copied, constructed, empty, populated):
                print(list(value.keys())[-1] is key, list(value.values())[-1] is shared)
            copied[key] = 'replacement'
            print(list(source.values())[0] is shared, list(empty.values())[0] is shared)
            """,
            "dictionary_copy_hash.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            Lines("0", "True True", "True True", "True True", "True True", "True True"),
            output.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedCopyCursorsUseCurrentExecutionDuringUpdateCompaction(bool reverse)
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            "values = {'a': 1, 'b': 2, 'c': 3, 'd': 4, 'e': 5}\ndel values['a']\n"
                + "copied = values.copy()\nsaved = "
                + (reverse ? "reversed(copied)" : "iter(copied)")
                + "\nadvance = saved.__next__\nmerge = copied.update\nprint(advance())",
            "dictionary_copy_cursor_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();

        var result = engine.Execute(
            "del copied['b']\nmerge({'z': 9})\nprint(list(saved))\nprint(list(values))",
            "dictionary_copy_cursor_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines(reverse ? "e" : "b"), previous.ToString());
        Assert.Equal(
            Lines(reverse ? "['z', 'e', 'd', 'c']" : "['e', 'z']", "['b', 'c', 'd', 'e']"),
            output.ToString()
        );
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("clone")]
    [InlineData("merge")]
    public void LargeSavedOperationsUseCurrentBudgetAndPermitRecovery(string operation)
    {
        var engine = PrepareLargeOperation(operation);
        using var output = new StringWriter();

        var result = engine.Execute(
            "try:\n    "
                + Invocation(operation)
                + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "dictionary_copy_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.Equal(5000, DictionaryGlobal(engine, "source").Items.Count);
        var target = DictionaryGlobal(engine, "target");
        if (operation == "merge")
            Assert.InRange(target.Items.Count, 2, 5000);
        else
            Assert.Empty(target.Items);
        AssertRecovery(engine, operation);
    }

    [Fact]
    public void SavedCopyAndMergeMethodsUseCurrentCancellationAndPermitRecovery()
    {
        foreach (var operation in new[] { "copy", "clone", "merge" })
        {
            var engine = PrepareLargeOperation(operation);
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelAfterFirstLineWriter(cancellation);

            Assert.Throws<OperationCanceledException>(() =>
                engine.Execute(
                    "print('cancel')\n" + Invocation(operation),
                    "dictionary_copy_cancel.py",
                    output,
                    cancellationToken: cancellation.Token
                )
            );

            Assert.Equal(Lines("cancel"), output.ToString());
            Assert.Equal(5000, DictionaryGlobal(engine, "source").Items.Count);
            Assert.Equal(
                operation == "merge" ? 1 : 0,
                DictionaryGlobal(engine, "target").Items.Count
            );
            AssertRecovery(engine, operation);
        }
    }

    private static ManagedPythonEngine PrepareLargeOperation(string operation)
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var setup = engine.Execute(
            "source = {index: index for index in range(5000)}\ntarget = "
                + (operation == "merge" ? "{-1: -1}" : "{}")
                + "\noperation = "
                + (operation == "copy" ? "source.copy" : "target.update"),
            "dictionary_copy_large_setup.py",
            output,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        return engine;
    }

    private static void AssertRecovery(ManagedPythonEngine engine, string operation)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            Invocation(operation)
                + "\nprint(len(source), len("
                + (operation == "copy" ? "result" : "target")
                + "), source[4999])",
            "dictionary_copy_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(operation == "merge" ? "5000 5001 4999" : "5000 5000 4999"),
            output.ToString()
        );
    }

    private static string Invocation(string operation) =>
        operation == "copy" ? "result = operation()" : "operation(source)";

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
