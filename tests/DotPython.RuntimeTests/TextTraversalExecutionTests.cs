using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class TextTraversalExecutionTests
{
    [Theory]
    [InlineData("len(values)")]
    [InlineData("values[-1]")]
    [InlineData("values[::-1]")]
    [InlineData("repr(values)")]
    [InlineData("ascii(values)")]
    [InlineData("saved_capitalize()")]
    public void LongScansChargeCurrentWorkAndRecoverWithoutPublishingResults(string expression)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        using var previousCancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            "values = ('😀' + chr(0xd800) + 'x' + chr(0xdcff)) * 20000\nsaved_capitalize = values.capitalize\ntarget = 'unchanged'",
            "text_setup.py",
            previousOutput,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        var source = Global(engine, "values");
        var target = Global(engine, "target");
        using var output = new StringWriter();
        var limited = engine.Execute(
            "try:\n    target = "
                + expression
                + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "text_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
        Assert.Equal("cleanup" + Environment.NewLine, output.ToString());
        Assert.Same(source, Global(engine, "values"));
        Assert.Same(target, Global(engine, "target"));
        Assert.Empty(previousOutput.ToString());
        using var recoveryOutput = new StringWriter();
        var recovery = engine.Execute(
            "target = " + expression,
            "text_recovery.py",
            recoveryOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovery.Success);
        Assert.NotSame(target, Global(engine, "target"));
        Assert.Same(source, Global(engine, "values"));
        if (expression == "len(values)")
            Assert.Equal(
                80000,
                Assert.IsType<PythonWholeNumberValue>(Global(engine, "target")).Value
            );
        if (expression == "values[-1]")
            Assert.Equal("\udcff", Assert.IsType<PythonTextValue>(Global(engine, "target")).Value);
    }

    [Fact]
    public void SavedCallbacksAndTextCursorsUseCurrentExecution()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        using var previousCancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            """
            class Index:
                def __init__(self, name, value): self.name=name; self.value=value
                def __index__(self): print(self.name); return self.value
            values = '😀' + chr(0xd800) + 'Z'
            forward = iter(values)
            backward = reversed(values)
            start = Index('start', 1)
            stop = Index('stop', 3)
            step = Index('step', 1)
            """,
            "text_saved.py",
            previousOutput,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            print(values[start:stop:step].encode('ascii','backslashreplace'))
            print(ord(next(forward)), ord(next(forward)), ord(next(backward)), ord(next(backward)))
            print(forward.__length_hint__(), backward.__length_hint__())
            """,
            "text_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Empty(previousOutput.ToString());
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "step",
                "start",
                "stop",
                "b'\\\\ud800Z'",
                "128512 55296 90 55296",
                "1 1",
                ""
            ),
            output.ToString()
        );
    }

    [Fact]
    public void InterruptedIterationRetainsTheNextUnconsumedCharacter()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "values = ('😀' + chr(0xd800) + 'x') * 20000\ncursor = iter(values)",
                    "text_cursor_setup.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        var cursor = Assert.IsType<PythonIteratorValue>(Global(engine, "cursor"));
        var result = engine.Execute(
            "while True: next(cursor)",
            "text_cursor_limit.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        var consumed = cursor.Index;
        Assert.InRange(consumed, 1, 59999);
        var resumed = engine.Execute(
            "value = next(cursor)",
            "text_cursor_recover.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(resumed.Success);
        Assert.Equal(
            (consumed % 3) switch
            {
                0 => "😀",
                1 => "\ud800",
                _ => "x",
            },
            Assert.IsType<PythonTextValue>(Global(engine, "value")).Value
        );
        Assert.Equal(consumed + 1, cursor.Index);
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
