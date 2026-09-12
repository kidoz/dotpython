using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class DictionaryCursorMutationExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedMethodsObserveEntryPositionsAcrossExecutions(bool reverse)
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        using var previousCancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            "values = {'a': 1, 'b': 2, 'c': 3}\nsaved = "
                + (reverse ? "reversed(values)" : "iter(values)")
                + "\nadvance = saved.__next__\nhint = saved.__length_hint__\nprint(advance(), hint())",
            "dictionary_cursor_setup.py",
            previous,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            del values['b']
            values['d'] = 4
            print(advance(), hint())
            values['a'] = 10
            print(list(saved), hint())
            values['e'] = 5
            print(next(saved, 'end'), hint())
            """,
            "dictionary_cursor_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines(reverse ? "c 2" : "a 2"), previous.ToString());
        Assert.Equal(
            reverse ? Lines("a 1", "[] 0", "end 0") : Lines("c 1", "['d'] 0", "end 0"),
            output.ToString()
        );
    }

    [Fact]
    public void SavedForwardMethodPermanentlyExhaustsAfterKeysChangedFailure()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var setup = engine.Execute(
            "values = {'a': 1, 'b': 2}\nsaved = iter(values)\nadvance = saved.__next__\nhint = saved.__length_hint__\nnext(saved)",
            "dictionary_keys_error_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var result = engine.Execute(
            """
            del values['a']
            values['c'] = 3
            print(advance(), hint())
            try: advance()
            except RuntimeError as error: print(str(error))
            values.clear()
            print(hint(), next(saved, 'end'))
            """,
            "dictionary_keys_error_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(
            Lines("b 0", "dictionary keys changed during iteration", "0 end"),
            output.ToString()
        );
    }

    [Fact]
    public void DeletedEntriesReleaseItemsAndPopTailAndClearResetStorage()
    {
        var first = Item(1);
        var removed = Item(2);
        var tail = Item(3);
        var dictionary = new PythonDictionaryValue([first, removed, tail]);

        dictionary.RemoveItem(removed);

        Assert.Null(dictionary.GetEntry(1));
        Assert.Equal(3, dictionary.EntryCount);
        Assert.Equal(new[] { first, tail }, dictionary.Items);
        var iterator = ManagedObjectProtocols.GetIterator(dictionary);
        Assert.True(ManagedObjectProtocols.TryGetNext(iterator, out var value));
        Assert.Same(first.Key, value);
        Assert.True(ManagedObjectProtocols.TryGetNext(iterator, out value));
        Assert.Same(tail.Key, value);
        Assert.False(ManagedObjectProtocols.TryGetNext(iterator, out _));

        dictionary.RemoveItem(tail, trimTail: true);
        Assert.Equal(2, dictionary.EntryCount);
        Assert.Null(dictionary.GetEntry(1));
        Assert.Same(first, Assert.Single(dictionary.Items));
        dictionary.ClearItems();
        Assert.Empty(dictionary.Items);
        Assert.Equal(0, dictionary.EntryCount);
        var replacement = Item(4);
        dictionary.AddItem(replacement);
        Assert.Equal(1, dictionary.EntryCount);
        Assert.Same(replacement, dictionary.GetEntry(0));
    }

    [Fact]
    public void ValueReplacementAndCompactionPreserveExistingItemAndCachedHash()
    {
        var key = new PythonTextValue("a");
        var dictionary = new PythonDictionaryValue([new(key, PythonNoneValue.Instance)]);
        var item = Assert.Single(dictionary.Items);
        var hash = item.KeyHash;
        var iterator = ManagedObjectProtocols.GetIterator(
            new PythonDictionaryViewValue("dict_values", dictionary)
        );
        var replacement = new PythonTextValue("replacement");

        ManagedObjectProtocols.SetItem(dictionary, key, replacement);

        Assert.Same(item, Assert.Single(dictionary.Items));
        Assert.Same(item, dictionary.GetEntry(0));
        Assert.Equal(hash, item.KeyHash);
        Assert.True(ManagedObjectProtocols.TryGetNext(iterator, out var value));
        Assert.Same(replacement, value);
        for (var index = 0; index < 12; index++)
            dictionary.AddItem(new(new PythonTextValue("key" + index), PythonNoneValue.Instance));
        Assert.Same(item, dictionary.GetEntry(0));
        Assert.Equal(hash, item.KeyHash);

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
            values = {key: 1}
            values[key] = 2
            before = key.calls
            for index in range(20): values[index] = index
            print(key.calls == before, list(values.values())[0])
            """,
            "dictionary_compaction_hash.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("True 2"), output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TombstoneScanUsesCurrentBudgetAndPreservesProgressForRecovery(bool reverse)
    {
        var engine = new ManagedPythonEngine();
        var iterator = PrepareSparseDictionary(engine, reverse);
        using var output = new StringWriter();
        var result = engine.Execute(
            "try:\n    advance()\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "dictionary_tombstone_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("cleanup"), output.ToString());
        Assert.False(iterator.IsExhausted);
        Assert.Equal(0, iterator.Index);
        var skipped = reverse
            ? 4999
                - (int)Assert.IsType<PythonReverseIteratorSourceValue>(iterator.Iterable).NextIndex
            : iterator.DictionaryPosition;
        Assert.InRange(skipped, 1, 999);
        AssertRecovery(engine, reverse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedSparseCursorUsesCurrentCancellationAndRemainsUsable(bool reverse)
    {
        var engine = new ManagedPythonEngine();
        var iterator = PrepareSparseDictionary(engine, reverse);
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                "print('cancel')\nadvance()",
                "dictionary_tombstone_cancel.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(Lines("cancel"), output.ToString());
        Assert.False(iterator.IsExhausted);
        Assert.Equal(0, iterator.Index);
        AssertRecovery(engine, reverse);
    }

    private static PythonIteratorValue PrepareSparseDictionary(
        ManagedPythonEngine engine,
        bool reverse
    )
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "values = {index: index for index in range(5000)}\nfor index in "
                + (reverse ? "range(1, 5000)" : "range(4999)")
                + ":\n    del values[index]\nsaved = "
                + (reverse ? "reversed(values)" : "iter(values)")
                + "\nadvance = saved.__next__\nhint = saved.__length_hint__",
            "dictionary_tombstone_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, output.ToString());
        var globals = Assert.IsType<PythonGlobalNamespace>(
            typeof(ManagedPythonEngine)
                .GetField("_globals", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine)
        );
        Assert.True(globals.TryGetValue("saved", out var iterator));
        return Assert.IsType<PythonIteratorValue>(iterator);
    }

    private static void AssertRecovery(ManagedPythonEngine engine, bool reverse)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "print(hint(), advance(), hint(), next(saved, 'end'))",
            "dictionary_tombstone_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines(reverse ? "1 0 0 end" : "1 4999 0 end"), output.ToString());
    }

    private static PythonDictionaryItemValue Item(int value) =>
        new(PythonWholeNumberValue.Create(value), new PythonListValue([]));

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
