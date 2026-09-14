using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class UnicodeTranslateErrorExecutionTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("reason")]
    public void SavedCallbacksUseCurrentBudgetsAndRecoverWithoutPublishingResults(string phase)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            """
            class Hook:
                slow = False
                phase = ''
                def __init__(self,name,value): self.name=name;self.value=value
                def work(self):
                    print(self.name)
                    if Hook.slow and Hook.phase == self.name:
                        for i in range(10000): pass
                def __index__(self): self.work();return self.value
                def __str__(self): self.work();return 'reason'
            start = Hook('start',0)
            end = Hook('end',1)
            reason = Hook('reason',0)
            error = UnicodeTranslateError('old',0,1,'old reason')
            target = 'unchanged'
            """,
            "translate_setup.py",
            previousOutput,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        var originalTarget = Global(engine, "target");
        var error = Assert.IsType<PythonExceptionValue>(Global(engine, "error"));
        var originalObject = error.UnicodeErrorState!.Object;
        var expression =
            phase == "reason" ? "str(error)" : "error.__init__('new', start, end, 'new reason')";
        using var output = new StringWriter();
        var limited = engine.Execute(
            "Hook.slow = True\nHook.phase = '"
                + phase
                + "'\n"
                + (phase == "reason" ? "error.reason = reason\n" : "")
                + "try:\n    target = "
                + expression
                + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "translate_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
        Assert.DoesNotContain("swallowed", output.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(
            "cleanup" + Environment.NewLine,
            output.ToString(),
            StringComparison.Ordinal
        );
        Assert.Same(originalTarget, Global(engine, "target"));
        Assert.Same(originalObject, error.UnicodeErrorState.Object);
        Assert.Empty(previousOutput.ToString());
        using var recoveryOutput = new StringWriter();
        var recovered = engine.Execute(
            "Hook.slow = False\ntarget = " + expression + "\nprint(str(error))",
            "translate_recovery.py",
            recoveryOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovered.Success);
        Assert.Contains(
            phase == "reason" ? "position 0: reason" : "position 0: new reason",
            recoveryOutput.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void FormattingLongSourcesChargesWorkAndRecoversWithoutChangingState()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "error = UnicodeTranslateError('a' * 100000 + chr(0xd800),100000,100001,'bad')\ntarget = 'unchanged'",
                    "translate_scan_setup.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        var original = Global(engine, "target");
        var limited = engine.Execute(
            "target = str(error)",
            "translate_scan_limit.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
        Assert.Same(original, Global(engine, "target"));
        var recovered = engine.Execute(
            "target = str(error)",
            "translate_scan_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovered.Success);
        Assert.Equal(
            "can't translate character '\\ud800' in position 100000: bad",
            Assert.IsType<PythonTextValue>(Global(engine, "target")).Value
        );
    }

    [Fact]
    public void CopyAndPickleReplaySavedIndexCallbacksInTheCurrentExecution()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        Assert.True(
            engine
                .Execute(
                    """
                    import copy
                    import pickle
                    class Index:
                        def __index__(self): print('index');return 0
                    original = UnicodeTranslateError('x',Index(),1,'bad')
                    original.object = 'changed'
                    original.encoding = 'ignored'
                    original.extra = ['note']
                    frozen = pickle.dumps(original)
                    """,
                    "translate_copy_setup.py",
                    previousOutput,
                    cancellationToken: cancellation.Token
                )
                .Success
        );
        cancellation.Cancel();
        var prior = previousOutput.ToString();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            shallow = copy.copy(original)
            deep = copy.deepcopy(original)
            replay = pickle.loads(frozen)
            print(shallow.object, deep.object, replay.object)
            print(shallow.encoding, deep.encoding, replay.encoding)
            print(shallow.extra is original.extra, deep.extra is original.extra, replay.extra is original.extra)
            """,
            "translate_copy_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(prior, previousOutput.ToString());
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "index",
                "index",
                "index",
                "x x x",
                "None None None",
                "True False False",
                ""
            ),
            output.ToString()
        );
    }

    [Fact]
    public void UnhandledFormattingUsesMutatedStateAndIgnoresEncodingCallbacks()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            error = UnicodeTranslateError('old',0,1,'old')
            class Reason:
                def __str__(self):
                    error.object = '😀Z'
                    error.start = 1
                    error.end = 2
                    print('reason')
                    return 'current'
            class Encoding:
                def __str__(self): raise AssertionError('unused')
            error.reason = Reason()
            error.encoding = Encoding()
            raise error
            """,
            "translate_unhandled.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.Equal("reason" + Environment.NewLine, output.ToString());
        Assert.Equal(
            "UnicodeTranslateError: can't translate character '\\x5a' in position 1: current",
            Assert.Single(result.Diagnostics).Message
        );
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
}
