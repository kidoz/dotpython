using System.Numerics;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BuiltinIteratorProtocolExecutionTests
{
    [Fact]
    public void ExplicitMethodsShareCursorAndWorkWithoutAnActiveVirtualMachine()
    {
        PythonValue[] sources =
        [
            new PythonListValue([PythonWholeNumberValue.Create(7)]),
            new PythonTupleValue([PythonWholeNumberValue.Create(7)]),
            new PythonRangeValue(7, 8, 1),
        ];
        foreach (var source in sources)
        {
            var iterator = ManagedObjectProtocols.GetIterator(source);
            var iter = ManagedObjectProtocols.GetAttribute(iterator, "__iter__");
            var next = ManagedObjectProtocols.GetAttribute(iterator, "__next__");
            Assert.Same(iterator, ManagedObjectProtocols.Call(iter, []));
            Assert.Equal(
                new BigInteger(7),
                Assert.IsType<PythonWholeNumberValue>(ManagedObjectProtocols.Call(next, [])).Value
            );
            var exhausted = Assert.Throws<PythonRaisedException>(() =>
                ManagedObjectProtocols.Call(next, [])
            );
            Assert.Equal("StopIteration", exhausted.Value.TypeName);
            Assert.Empty(exhausted.Value.EffectiveArguments);
            Assert.Same(iterator, ManagedObjectProtocols.Call(iter, []));
            Assert.Throws<PythonRaisedException>(() => ManagedObjectProtocols.Call(next, []));
        }
    }

    [Fact]
    public void BoundMethodsUseSharedFunctionsAndReportTheCurrentCallSpan()
    {
        var first = ManagedObjectProtocols.GetIterator(new PythonRangeValue(0, 2, 1));
        var second = ManagedObjectProtocols.GetIterator(new PythonRangeValue(3, 5, 1));
        foreach (var name in new[] { "__iter__", "__next__" })
        {
            var firstMethod = Assert.IsType<PythonBoundMethodValue>(
                ManagedObjectProtocols.GetAttribute(first, name, new TextSpan(2, 3))
            );
            var secondMethod = Assert.IsType<PythonBoundMethodValue>(
                ManagedObjectProtocols.GetAttribute(second, name)
            );
            Assert.Same(firstMethod.Function, secondMethod.Function);
            Assert.Same(first, firstMethod.Target);
            var span = new TextSpan(20, 4);
            var error = Assert.Throws<PythonRuntimeException>(() =>
                ManagedObjectProtocols.Call(firstMethod, [PythonNoneValue.Instance], span)
            );
            Assert.Equal(span, error.Span);
        }
        Assert.Equal(BigInteger.Zero, first.RangeIndex);
    }

    [Fact]
    public void SavedBuiltinMethodsObserveMutationsAndExhaustionAcrossExecutions()
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            "values = [1]\nsaved = iter(values)\nidentity = saved.__iter__\nadvance = saved.__next__",
            "iterator_methods_setup.py",
            previous,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(identity() is saved, advance())
            values.append(2)
            print(advance())
            try: advance()
            except StopIteration as error: print(error.args, error.value)
            values.append(3)
            print(next(identity(), 'done'))
            del saved
            print(identity().__iter__() is identity())
            try: advance()
            except StopIteration: print('still done')
            """,
            "iterator_methods_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, previous.ToString());
        Assert.Equal(
            Lines("True 1", "2", "() None", "done", "True", "still done"),
            output.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedSequenceNextUsesCurrentControlFlowAndRecoversWithoutAdvancing(bool cancel)
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            """
            blocking = True
            class Sequence:
                def __getitem__(self, index):
                    try:
                        print('getitem', index)
                        while blocking: pass
                        if index >= 2: raise IndexError
                        return index
                    finally: print('cleanup', index)
            saved = iter(Sequence())
            advance = saved.__next__
            identity = saved.__iter__
            """,
            "iterator_callback_setup.py",
            previous,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var cancellation = new CancellationTokenSource();
        using StringWriter output = cancel
            ? new CancellingWriter(cancellation)
            : new StringWriter();
        const string source = """
            try: advance()
            except BaseException: print('swallowed')
            finally: print('outer cleanup')
            """;
        if (cancel)
            Assert.Throws<OperationCanceledException>(() =>
                engine.Execute(
                    source,
                    "iterator_callback_cancel.py",
                    output,
                    cancellationToken: cancellation.Token
                )
            );
        else
        {
            var result = engine.Execute(
                source,
                "iterator_callback_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 500 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        }
        Assert.Equal(string.Empty, previous.ToString());
        Assert.Equal(Lines("getitem 0", "cleanup 0", "outer cleanup"), output.ToString());
        using var recoveredOutput = new StringWriter();
        var recovered = engine.Execute(
            "blocking = False\nprint(identity() is saved, advance(), next(saved))",
            "iterator_callback_recovery.py",
            recoveredOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovered.Success);
        Assert.Equal(
            Lines("getitem 0", "cleanup 0", "getitem 1", "cleanup 1", "True 0 1"),
            recoveredOutput.ToString()
        );
    }

    [Fact]
    public void SavedNextMethodResolvesReplacementSequenceCallbackInCurrentExecution()
    {
        var engine = new ManagedPythonEngine();
        using var previous = new StringWriter();
        var setup = engine.Execute(
            """
            failure = RuntimeError('retry')
            class Sequence:
                def __getitem__(self, index): raise failure
            saved = iter(Sequence())
            advance = saved.__next__
            """,
            "iterator_replacement_setup.py",
            previous,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            try: advance()
            except RuntimeError as error: print(error is failure)
            def replacement(self, index):
                print('replacement', index)
                return index + 10
            Sequence.__getitem__ = replacement
            print(advance(), next(saved))
            """,
            "iterator_replacement_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, previous.ToString());
        Assert.Equal(Lines("True", "replacement 0", "replacement 1", "10 11"), output.ToString());
    }

    private static string Lines(params string[] values) =>
        string.Join(Environment.NewLine, values) + Environment.NewLine;

    private sealed class CancellingWriter(CancellationTokenSource cancellation) : StringWriter
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
