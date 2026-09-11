using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ExceptionGroupDerivationExecutionTests
{
    [Theory]
    [InlineData("split")]
    [InlineData("subgroup")]
    [InlineData("exceptstar")]
    public void SavedGroupCallbacksUseCurrentOutput(string operation)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            class Group(ExceptionGroup):
                def derive(self, children):
                    print('derive', len(children))
                    return Group(self.message, children)
            target = Group('root', [ValueError('yes'), TypeError('no')])
            def predicate(error):
                print('predicate', type(error).__name__)
                return isinstance(error, ValueError)
            """,
            "group_callback_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();
        var source = operation switch
        {
            "split" =>
                "matched, rest = target.split(predicate)\nprint(type(matched) is Group, type(rest) is Group)",
            "subgroup" => "matched = target.subgroup(predicate)\nprint(type(matched) is Group)",
            _ =>
                "try:\n    try: raise target\n    except* ValueError as matched: print('handled', type(matched) is Group)\nexcept Group as rest: print('rest', type(rest) is Group)",
        };

        var result = engine.Execute(
            source,
            "group_callback_current_output.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        var expected = operation switch
        {
            "split" => Lines(
                "predicate Group",
                "predicate ValueError",
                "predicate TypeError",
                "derive 1",
                "derive 1",
                "True True"
            ),
            "subgroup" => Lines(
                "predicate Group",
                "predicate ValueError",
                "predicate TypeError",
                "derive 1",
                "True"
            ),
            _ => Lines("derive 1", "derive 1", "handled True", "derive 1", "rest True"),
        };
        Assert.Equal(expected, output.ToString());
    }

    [Theory]
    [InlineData("predicate", "split")]
    [InlineData("predicate", "subgroup")]
    [InlineData("derive", "split")]
    [InlineData("derive", "exceptstar")]
    public void CallbackInstructionLimitEscapesPythonHandlersAndEngineRecovers(
        string callback,
        string operation
    )
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        PrepareRecoverableGroup(engine, previousOutput, callback);
        using var output = new StringWriter();

        var result = engine.Execute(
            "blocking = True\n" + GuardedOperation(operation),
            "group_callback_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines(callback, callback + " cleanup", "outer cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Theory]
    [InlineData("predicate", "split")]
    [InlineData("predicate", "subgroup")]
    [InlineData("derive", "split")]
    [InlineData("derive", "exceptstar")]
    public void CallbackCancellationEscapesPythonHandlersAndEngineRecovers(
        string callback,
        string operation
    )
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        PrepareRecoverableGroup(engine, previousOutput, callback);
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                "blocking = True\n" + GuardedOperation(operation),
                "group_callback_cancellation.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(Lines(callback, callback + " cleanup", "outer cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Theory]
    [InlineData("predicate", "split")]
    [InlineData("predicate", "subgroup")]
    [InlineData("derive", "split")]
    [InlineData("derive", "exceptstar")]
    public void CallbackPythonFailurePreservesIdentityAndOriginalGroupRemainsUsable(
        string callback,
        string operation
    )
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        PrepareRecoverableGroup(engine, previousOutput, callback);
        using var output = new StringWriter();

        var result = engine.Execute(
            "failing = True\ntry:\n    "
                + Operation(operation).Replace("\n", "\n    ", StringComparison.Ordinal)
                + "\nexcept RuntimeError as error:\n    print(error is failure)",
            "group_callback_failure.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(Lines(callback, callback + " cleanup", "True"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void SavedGroupDerivationUsesItsEnginesCanonicalExceptionGroup()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            expected_type = ExceptionGroup
            expected_type.marker = 'first engine'
            class Group(ExceptionGroup):
                def derive(self, children):
                    result = BaseExceptionGroup(self.message, children)
                    print(type(result) is expected_type, type(result).marker, type(result).__name__)
                    return result
            target = Group('root', [ValueError('yes'), TypeError('no')])
            saved = target.split
            """,
            "group_owner_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var otherEngine = new ManagedPythonEngine();
        using var otherOutput = new StringWriter();
        var other = otherEngine.Execute(
            "ExceptionGroup.marker = 'other engine'\nExceptionGroup.__name__ = 'OtherGroup'",
            "group_other_engine.py",
            otherOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(other.Success);
        using var output = new StringWriter();

        var result = engine.Execute(
            "ExceptionGroup.__name__ = 'OwnedGroup'\nmatched, rest = saved(ValueError)\nprint(type(matched) is expected_type, type(rest) is expected_type)",
            "group_owner_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(string.Empty, otherOutput.ToString());
        Assert.Equal(
            Lines("True first engine OwnedGroup", "True first engine OwnedGroup", "True True"),
            output.ToString()
        );
    }

    private static void PrepareRecoverableGroup(
        ManagedPythonEngine engine,
        StringWriter output,
        string callback
    )
    {
        var result = engine.Execute(
            """
            enabled = True
            blocking = False
            failing = False
            failure = RuntimeError('callback failed')
            def check(label):
                if enabled and label == selected:
                    try:
                        print(label)
                        while blocking: pass
                        if failing: raise failure
                    finally:
                        print(label, 'cleanup')
            class Group(ExceptionGroup):
                def derive(self, children):
                    check('derive')
                    return Group(self.message, children)
            def predicate(error):
                check('predicate')
                return isinstance(error, ValueError)
            first = ValueError('yes')
            second = TypeError('no')
            target = Group('root', [first, second])
            """
                + "\nselected = '"
                + callback
                + "'",
            "group_recovery_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, output.ToString());
    }

    private static string Operation(string operation) =>
        operation switch
        {
            "split" => "target.split(predicate)",
            "subgroup" => "target.subgroup(predicate)",
            _ => "try:\n    raise target\nexcept* ValueError:\n    pass",
        };

    private static string GuardedOperation(string operation) =>
        "try:\n    "
        + Operation(operation).Replace("\n", "\n    ", StringComparison.Ordinal)
        + "\nexcept BaseException:\n    print('outer swallowed')\nfinally:\n    print('outer cleanup')";

    private static void AssertRecovery(ManagedPythonEngine engine)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            enabled = False
            blocking = False
            failing = False
            matched, rest = target.split(predicate)
            print(type(matched) is Group, type(rest) is Group)
            print(matched.exceptions[0] is first, rest.exceptions[0] is second)
            print(target.exceptions[0] is first, target.exceptions[1] is second)
            """,
            "group_callback_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("True True", "True True", "True True"), output.ToString());
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
