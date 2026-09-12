using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SetCachedHashExecutionTests
{
    [Fact]
    public void SetCopiesAndFromKeysOwnStorageAndPreserveKeysAndInsertionHashesWithoutAnExecution()
    {
        var removed = PythonWholeNumberValue.Create(13);
        var key = PythonWholeNumberValue.Create(42);
        var source = new PythonSetValue([]);
        source.AddEntry(removed, 13);
        source.AddEntry(key, 42);
        source.RemoveEntry(0);
        var fill = new PythonListValue([]);

        var copy = source.Copy();
        var frozen = source.Copy(frozen: true);
        var dictionary = ManagedObjectProtocols.DictionaryFromKeys(frozen, fill, default);

        Assert.NotSame(source.Elements, copy.Elements);
        Assert.NotSame(source.Entries, copy.Entries);
        Assert.Same(key, Assert.Single(copy.Elements));
        Assert.Same(key, Assert.Single(frozen.Entries).Value);
        Assert.Equal(42, Assert.Single(copy.Entries).Hash);
        Assert.True(frozen.IsFrozen);
        var item = Assert.Single(dictionary.Items);
        Assert.Same(key, item.Key);
        Assert.Equal(42, item.KeyHash);
        Assert.Same(fill, item.Value);
        source.ClearEntries();
        Assert.Empty(source.Elements);
        Assert.Empty(source.Entries);
        Assert.Same(key, Assert.Single(copy.Elements));
        copy.RemoveEntry(0);
        Assert.Empty(copy.Elements);
        Assert.Empty(copy.Entries);
        Assert.Same(key, Assert.Single(frozen.Elements));
        Assert.Same(item, Assert.Single(dictionary.Items));
    }

    [Fact]
    public void SavedSetMethodsAndFromKeysUseCurrentCallbacksAndPreserveCachedHashes()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            """
            class Key:
                armed = False
                def __init__(self, value): self.value = value
                def __hash__(self):
                    if self.armed: print('hash', self.value)
                    return self.value
            key = Key(1)
            source = {key}
            add = source.add
            discard = source.discard
            clone = source.copy
            make = dict.fromkeys
            Key.armed = True
            """,
            "set_cached_hash_callback_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();

        var result = engine.Execute(
            """
            add(Key(2))
            copied = clone()
            fill = []
            mapped = make(copied, fill)
            discard(key)
            print(len(source), len(copied), len(mapped))
            print(all(value is fill for value in mapped.values()))
            print(any(value is key for value in copied))
            """,
            "set_cached_hash_callback_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previous.ToString());
        Assert.Equal(Lines("hash 2", "hash 1", "1 2 2", "True", "True"), output.ToString());
    }

    [Theory]
    [InlineData("fromkeys")]
    [InlineData("copy")]
    [InlineData("union")]
    public void LargeSavedOperationsUseCurrentBudgetAndPreserveAssignmentOnFailure(string operation)
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            "source = set(range(5000))\nother = {5000}\nfill = []\ntarget = 'original'\noperation = "
                + (operation == "fromkeys" ? "dict.fromkeys" : "source." + operation),
            "set_cached_hash_budget_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        var original = Global(engine, "target");
        using var output = new StringWriter();
        var invocation = operation switch
        {
            "fromkeys" => "target = operation(source, fill)",
            "copy" => "target = operation()",
            _ => "target = operation(other)",
        };

        var result = engine.Execute(
            "try:\n    "
                + invocation
                + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "set_cached_hash_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.Same(original, Global(engine, "target"));
        Assert.Equal(5000, Assert.IsType<PythonSetValue>(Global(engine, "source")).Elements.Count);
        using var recoveredOutput = new StringWriter();
        var recovered = engine.Execute(
            invocation + "\nprint(len(source), len(target), 4999 in target)",
            "set_cached_hash_budget_recovery.py",
            recoveredOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovered.Success);
        Assert.Equal(
            Lines(operation == "union" ? "5000 5001 True" : "5000 5000 True"),
            recoveredOutput.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedAddUsesCurrentCancellationInsideCallbacksAndPermitsRecovery(bool equality)
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            """
            class Key:
                cancel = False
                equality = False
                def __init__(self, value): self.value = value
                def __hash__(self):
                    if self.cancel and not self.equality: print('cancel')
                    return 1
                def __eq__(self, other):
                    if self.cancel and self.equality: print('cancel')
                    return False
            source = {Key(1)}
            query = Key(2)
            operation = source.add
            Key.cancel = True
            """ + (equality ? "\nKey.equality = True" : string.Empty),
            "set_cached_hash_cancel_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                "operation(query)",
                "set_cached_hash_cancel.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(string.Empty, previous.ToString());
        Assert.Equal(Lines("cancel"), output.ToString());
        Assert.Single(Assert.IsType<PythonSetValue>(Global(engine, "source")).Elements);
        using var recoveredOutput = new StringWriter();
        var recovered = engine.Execute(
            "Key.cancel = False\noperation(query)\nprint(len(source), query in source)",
            "set_cached_hash_cancel_recovery.py",
            recoveredOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovered.Success);
        Assert.Equal(Lines("2 True"), recoveredOutput.ToString());
    }

    [Fact]
    public void FailedEqualityThatMutatesStoredSetStillInsertsTheIncomingKey()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Key:
                mutate = False
                def __init__(self, value): self.value = value
                def __hash__(self): return 1
                def __eq__(self, other):
                    if self.mutate:
                        self.mutate = False
                        source.clear()
                        source.add(replacement)
                    return False
            stored = Key('stored')
            query = Key('query')
            replacement = Key('replacement')
            source = {stored}
            stored.mutate = True
            source.add(query)
            print(len(source))
            print(sorted(key.value for key in source))
            print(any(key is query for key in source))
            """,
            "set_cached_hash_reentrant_equality.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines("2", "['query', 'replacement']", "True"), output.ToString());
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
