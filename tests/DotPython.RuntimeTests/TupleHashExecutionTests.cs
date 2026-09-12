using System.Numerics;
using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TupleHashExecutionTests
{
    [Fact]
    public void TupleGoldenHashesUseFullWidthChildrenWithoutAnExecution()
    {
        (PythonValue[] Elements, long Hash)[] cases =
        [
            ([], 5740354900026072187L),
            ([Number(0)], -8753497827991233192L),
            ([Number(1)], -6644214454873602895L),
            ([Number(-1)], 8078679518589016365L),
            ([Number(1), Number(2), Number(3)], 529344067295497451L),
            ([new PythonFloatingPointValue(0.5)], -408149959306781352L),
        ];
        foreach (var (elements, expected) in cases)
        {
            var tuple = new PythonTupleValue(elements);
            Assert.Equal(-1, tuple.CachedHash);
            Assert.Equal(new BigInteger(expected), ManagedObjectProtocols.ComputePythonHash(tuple));
            Assert.Equal(expected, tuple.CachedHash);
            Assert.Equal(
                new BigInteger(expected).GetHashCode(),
                ManagedObjectProtocols.GetPythonHash(tuple)
            );
        }
    }

    [Fact]
    public void NativeTupleFillInvalidatesOnlyTheModifiedTupleCache()
    {
        var tuple = new PythonTupleValue([Number(1)]);
        Assert.Equal(
            new BigInteger(-6644214454873602895L),
            ManagedObjectProtocols.ComputePythonHash(tuple)
        );
        ManagedObjectProtocols.SetTupleItem(tuple, 0, Number(0));
        Assert.Equal(-1, tuple.CachedHash);
        Assert.Equal(
            new BigInteger(-8753497827991233192L),
            ManagedObjectProtocols.ComputePythonHash(tuple)
        );
        Assert.Throws<PythonRuntimeException>(() =>
            ManagedObjectProtocols.SetTupleItem(tuple, 2, Number(1))
        );
        Assert.Equal(-8753497827991233192L, tuple.CachedHash);
    }

    [Fact]
    public void FailedOuterHashRetainsSuccessfulInnerHashAndRetriesUnhashableChild()
    {
        var inner = new PythonTupleValue([Number(1)]);
        var outer = new PythonTupleValue([inner, new PythonListValue([])]);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<PythonRuntimeException>(() =>
                ManagedObjectProtocols.ComputePythonHash(outer)
            );
            Assert.Equal("TypeError", error.PythonExceptionTypeName);
            Assert.Contains("list", error.Message, StringComparison.Ordinal);
            Assert.Equal(-1, outer.CachedHash);
            Assert.Equal(-6644214454873602895L, inner.CachedHash);
        }
    }

    [Fact]
    public void FirstHashChargesCurrentIterationBudgetAndCachedHashAvoidsChildWork()
    {
        var engine = new ManagedPythonEngine();
        Execute(engine, "value = tuple(range(2000))\nresult = 'original'");
        var tuple = Assert.IsType<PythonTupleValue>(Global(engine, "value"));
        using var output = new StringWriter();
        var failed = engine.Execute(
            "result = hash(value)",
            "tuple_hash_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(failed.Diagnostics).Code);
        Assert.Equal(-1, tuple.CachedHash);
        Assert.Equal("original", Assert.IsType<PythonTextValue>(Global(engine, "result")).Value);
        Execute(engine, "result = hash(value)");
        Assert.NotEqual(-1, tuple.CachedHash);
        var cached = engine.Execute(
            "print(hash(value) == result)",
            "tuple_hash_cached_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.True(cached.Success);
        Assert.Equal(Lines("True"), output.ToString());
    }

    [Fact]
    public void SavedTupleHashUsesCurrentExecutionThenSkipsReplacedChildHash()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            class Key:
                def __hash__(self):
                    print('current callback')
                    return 123
            key = Key()
            value = (key,)
            operation = hash
            """,
            "tuple_hash_setup.py",
            previousOutput,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            first = operation(value)
            Key.__hash__ = None
            print(first == operation(value))
            print(len({value}), {value: 1}[value])
            """,
            "tuple_hash_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("current callback", "True", "1 1"), output.ToString());
        Assert.Empty(previousOutput.ToString());
    }

    [Fact]
    public void CancellationDuringChildHashDoesNotPublishOuterCacheAndAllowsRetry()
    {
        var engine = new ManagedPythonEngine();
        Execute(
            engine,
            """
            class Key:
                cancel = True
                def __hash__(self):
                    if self.cancel: print('cancel')
                    return 123
            key = Key()
            inner = (1,)
            value = (inner, key)
            result = 'original'
            """
        );
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterLineWriter(cancellation);
        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                "result = hash(value)",
                "tuple_hash_cancel.py",
                output,
                cancellationToken: cancellation.Token
            )
        );
        var tuple = Assert.IsType<PythonTupleValue>(Global(engine, "value"));
        Assert.Equal(-1, tuple.CachedHash);
        Assert.NotEqual(-1, Assert.IsType<PythonTupleValue>(Global(engine, "inner")).CachedHash);
        Assert.Equal("original", Assert.IsType<PythonTextValue>(Global(engine, "result")).Value);
        Execute(engine, "key.cancel = False\nresult = hash(value)");
        Assert.NotEqual(-1, tuple.CachedHash);
    }

    private static PythonWholeNumberValue Number(int value) => PythonWholeNumberValue.Create(value);

    private static void Execute(ManagedPythonEngine engine, string source)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "tuple_hash.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
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
