using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SequenceIterationExecutionTests
{
    private const string Setup = """
        blocking = False
        failing = False
        failure = RuntimeError('getitem failed')
        class Sequence:
            def __getitem__(self, index):
                try:
                    print('getitem', index)
                    while blocking: pass
                    if failing: raise failure
                    if index >= 2: raise IndexError
                    return index
                finally:
                    print('cleanup', index)
        sequence = Sequence()
        saved = iter(sequence)
        """;

    [Fact]
    public void SavedSequenceIteratorUsesCurrentOutputOnEachExecution()
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        Prepare(engine, setupOutput);
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            "print(next(saved))",
            "sequence_first_step.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        using var secondOutput = new StringWriter();
        var second = engine.Execute(
            "print(iter(saved) is saved, next(saved))",
            "sequence_second_step.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(second.Success);
        Assert.Equal(string.Empty, setupOutput.ToString());
        Assert.Equal(Lines("getitem 0", "cleanup 0", "0"), firstOutput.ToString());
        Assert.Equal(Lines("getitem 1", "cleanup 1", "True 1"), secondOutput.ToString());
    }

    [Fact]
    public void SequenceCallbackInstructionLimitPreservesIndexAndEngineRecovers()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();

        var result = engine.Execute(
            GuardedStep,
            "sequence_callback_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("getitem 0", "cleanup 0", "outer cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void SequenceCallbackCancellationPreservesIndexAndEngineRecovers()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                GuardedStep,
                "sequence_callback_cancellation.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(Lines("getitem 0", "cleanup 0", "outer cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void SequenceCallbackFailureRetainsExceptionIdentityAndUnconsumedIndex()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            failing = True
            try: next(saved)
            except RuntimeError as error: print(error is failure)
            """,
            "sequence_callback_failure.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(Lines("getitem 0", "cleanup 0", "True"), output.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void SavedSequenceIteratorResolvesReplacedGetItemOnLaterExecution()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(next(saved))
            def replacement(self, index):
                print('replacement', index)
                return index + 10
            Sequence.__getitem__ = replacement
            print(next(saved))
            """,
            "sequence_replaced_getitem.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(
            Lines("getitem 0", "cleanup 0", "0", "replacement 1", "11"),
            output.ToString()
        );
    }

    [Fact]
    public void ExhaustedSequenceIteratorDoesNotResumeAfterGetItemReplacement()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(next(saved), next(saved))
            print(next(saved, 'exhausted'))
            def replacement(self, index):
                print('unexpected callback')
                return 99
            Sequence.__getitem__ = replacement
            print(next(saved, 'still exhausted'))
            """,
            "sequence_exhaustion.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(
            Lines(
                "getitem 0",
                "cleanup 0",
                "getitem 1",
                "cleanup 1",
                "0 1",
                "getitem 2",
                "cleanup 2",
                "exhausted",
                "still exhausted"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void GroupConstructionAndNotesCopyUseSavedSequencesWithCurrentOutput()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            class Sequence:
                def __init__(self, label, values):
                    self.label = label
                    self.values = values
                def __getitem__(self, index):
                    print(self.label, index)
                    return self.values[index]
            leaves = Sequence('exceptions', [ValueError('yes'), TypeError('no')])
            notes = Sequence('notes', ['note'])
            """,
            "group_sequence_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            group = ExceptionGroup('root', leaves)
            group.__notes__ = notes
            matched = group.subgroup(ValueError)
            print(group.args[1] is leaves, matched.__notes__)
            """,
            "group_sequence_current_output.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(
            Lines(
                "exceptions 0",
                "exceptions 1",
                "exceptions 2",
                "notes 0",
                "notes 1",
                "True ['note']"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void SavedSequenceIteratorUsesItsEnginesCanonicalExceptionGroup()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            expected_type = ExceptionGroup
            expected_type.marker = 'first engine'
            class Sequence:
                def __getitem__(self, index):
                    group = BaseExceptionGroup('value', [ValueError()])
                    print(type(group) is expected_type, type(group).marker, type(group).__name__)
                    return group
            saved = iter(Sequence())
            """,
            "sequence_owner_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var otherEngine = new ManagedPythonEngine();
        using var otherOutput = new StringWriter();
        var other = otherEngine.Execute(
            "ExceptionGroup.marker = 'other engine'\nExceptionGroup.__name__ = 'OtherGroup'",
            "sequence_other_engine.py",
            otherOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(other.Success);
        using var output = new StringWriter();
        var result = engine.Execute(
            "ExceptionGroup.__name__ = 'OwnedGroup'\nprint(type(next(saved)) is expected_type)",
            "sequence_owner_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(string.Empty, otherOutput.ToString());
        Assert.Equal(Lines("True first engine OwnedGroup", "True"), output.ToString());
    }

    private const string GuardedStep = """
        blocking = True
        try:
            next(saved)
        except BaseException:
            print('outer swallowed')
        finally:
            print('outer cleanup')
        """;

    private static void Prepare(ManagedPythonEngine engine, StringWriter output)
    {
        var result = engine.Execute(
            Setup,
            "sequence_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, output.ToString());
    }

    private static void AssertRecovery(ManagedPythonEngine engine)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "blocking = False\nfailing = False\nprint(next(saved), next(saved))",
            "sequence_callback_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines("getitem 0", "cleanup 0", "getitem 1", "cleanup 1", "0 1"),
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
