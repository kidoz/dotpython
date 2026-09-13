using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class UnicodeDecodeErrorExecutionTests
{
    [Fact]
    public void SavedConstructorsIndicesAndFormattingMembersUseTheCurrentExecution()
    {
        var engine = Prepare(out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var result = engine.Execute(
                """
                Control.trace = True
                created = constructor(*newargs)
                print(created.start, created.end, created.args[2] is start, created.args[3] is end)
                print(str(error))
                """,
                "unicode_decode_error_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Empty(previousOutput.ToString());
            Assert.Equal(
                Lines(
                    "start",
                    "end",
                    "1 2 True True",
                    "reason",
                    "encoding",
                    "'encoding' codec can't decode byte 0xff in position 0: reason"
                ),
                output.ToString()
            );
        }
    }

    [Fact]
    public void ConstructionAndFormattingCallbacksChargeCurrentBudgetsWithoutPublishingResults()
    {
        foreach (var phase in new[] { "start", "end", "reason", "encoding" })
        {
            var engine = Prepare(out var previousOutput);
            using (previousOutput)
            using (var output = new StringWriter())
            {
                var original = Global(engine, "target");
                var result = engine.Execute(
                    "Control.slow = '"
                        + phase
                        + "'\ntry:\n    target = "
                        + Expression(phase)
                        + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                    "unicode_decode_error_budget.py",
                    output,
                    new ManagedExecutionOptions { InstructionLimit = 500 },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
                Assert.Equal(Lines("cleanup"), output.ToString());
                Assert.Empty(previousOutput.ToString());
                Assert.Same(original, Global(engine, "target"));
                Assert.Contains(phase, Events(engine));
                Recover(engine, phase);
            }
        }
    }

    [Fact]
    public void CancelledConstructionAndFormattingKeepBindingsAndRecoverWithCurrentContext()
    {
        foreach (var phase in new[] { "start", "end", "reason", "encoding" })
        {
            var engine = Prepare(out var previousOutput);
            using (previousOutput)
            using (var cancellation = new CancellationTokenSource())
            using (var output = new CancelAfterLineWriter(cancellation))
            {
                var original = Global(engine, "target");
                Assert.Throws<OperationCanceledException>(() =>
                    engine.Execute(
                        "Control.cancel = '" + phase + "'\ntarget = " + Expression(phase),
                        "unicode_decode_error_cancel.py",
                        output,
                        cancellationToken: cancellation.Token
                    )
                );
                Assert.Equal(Lines("cancel"), output.ToString());
                Assert.Empty(previousOutput.ToString());
                Assert.Same(original, Global(engine, "target"));
                Assert.Contains(phase, Events(engine));
                Recover(engine, phase);
            }
        }
    }

    [Fact]
    public void FailedReinitializationPublishesArgumentsButRetainsPreviouslyValidatedFields()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            events = []
            problem = ValueError('index failed')
            class Index:
                def __init__(self, name, fail): self.name = name; self.fail = fail
                def __index__(self):
                    events.append(self.name)
                    if self.fail: raise problem
                    return 1
            error = UnicodeDecodeError('ascii', b'old', 0, 1, 'old reason')
            original = error
            for fail in ('start', 'end'):
                events.clear()
                start = Index('start', fail == 'start')
                end = Index('end', fail == 'end')
                arguments = ('utf-8', b'new', start, end, 'new reason')
                try: error.__init__(*arguments)
                except ValueError as caught:
                    print(caught is problem, error is original, error.args == arguments, events)
                print(error.encoding, error.object, error.start, error.end, error.reason)
            error.__init__('utf-8', b'new', 1, 2, 'new reason')
            print(error.encoding, error.object, error.start, error.end, error.reason)
            """,
            "unicode_decode_error_reinit.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "True True True ['start']",
                "ascii b'old' 0 1 old reason",
                "True True True ['start', 'end']",
                "ascii b'old' 0 1 old reason",
                "utf-8 b'new' 1 2 new reason"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void DecoderErrorsCopyTheInputBytesAndReportTheCurrentCallSpan()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            "values = bytes([65, 255, 66])\ndecode = values.decode\ntarget = 'original'",
            "unicode_decode_error_saved.py",
            previousOutput,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        var original = Global(engine, "values");
        var originalTarget = Global(engine, "target");
        using var output = new StringWriter();
        var caught = engine.Execute(
            """
            try: target = decode()
            except UnicodeDecodeError as error:
                saved = error
                print(error.object is values, error.object == values, error.args[1] is error.object)
                print(error.encoding, error.start, error.end, error.reason)
                print(len(error.args), error.args[0], error.args[2], error.args[3], error.args[4])
            """,
            "unicode_decode_error_caught.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(caught.Success);
        Assert.Equal(
            Lines(
                "False True True",
                "utf-8 1 2 invalid start byte",
                "5 utf-8 1 2 invalid start byte"
            ),
            output.ToString()
        );
        Assert.Same(originalTarget, Global(engine, "target"));
        const string source = "padding = 1\n\n\ntarget = decode()";
        var failed = engine.Execute(
            source,
            "unicode_decode_error_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var diagnostic = Assert.Single(failed.Diagnostics);
        Assert.Equal("unicode_decode_error_current.py", failed.Source.FilePath);
        Assert.Equal(source.IndexOf("decode()", StringComparison.Ordinal), diagnostic.Span.Start);
        Assert.Contains(
            "can't decode byte 0xff in position 1",
            diagnostic.Message,
            StringComparison.Ordinal
        );
        Assert.Same(original, Global(engine, "values"));
        Assert.Equal(
            new byte[] { 65, 255, 66 },
            Assert.IsType<PythonByteSequenceValue>(original).Value
        );
        Assert.Empty(previousOutput.ToString());
    }

    [Fact]
    public void CopyAndPickleReplayConstructorArgumentsAndRestoreTheirCapturedInstanceState()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            import copy
            import pickle
            tracing = False
            class Index:
                def __index__(self):
                    if tracing: print('index')
                    return 1
            original = UnicodeDecodeError('utf-8', b'abc', Index(), 2, 'old')
            original.encoding = 'ascii'
            original.start = 0
            original.reason = 'changed'
            original.note = ['before']
            snapshot = pickle.dumps(original)
            original.note.append('after')
            """,
            "unicode_decode_error_snapshot_setup.py",
            previousOutput,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            tracing = True
            shallow = copy.copy(original)
            deep = copy.deepcopy(original)
            first = pickle.loads(snapshot)
            second = pickle.loads(snapshot)
            for value in (shallow, deep, first, second):
                print(value.encoding, value.start, value.end, value.reason, value.note)
            print(shallow.note is original.note, deep.note is original.note, first is second)
            first.note.append('first')
            print(second.note)
            """,
            "unicode_decode_error_snapshot_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Empty(previousOutput.ToString());
        Assert.Equal(
            Lines(
                "index",
                "index",
                "index",
                "index",
                "utf-8 1 2 old ['before', 'after']",
                "utf-8 1 2 old ['before', 'after']",
                "utf-8 1 2 old ['before']",
                "utf-8 1 2 old ['before']",
                "True False False",
                "['before']"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void RaisingAndCatchingDefersFormattingWhileUnhandledDiagnosticsDisplayFields()
    {
        var engine = Prepare(out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var caught = engine.Execute(
                """
                Control.trace = True
                try: raise error
                except UnicodeDecodeError as caught: print(caught is error, events)
                try:
                    try: raise error
                    except UnicodeDecodeError: raise
                except UnicodeDecodeError as caught: print(caught is error, events)
                """,
                "unicode_decode_error_raise_without_format.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(caught.Success);
            Assert.Equal(Lines("True []", "True []"), output.ToString());
            Assert.Empty(Events(engine));
            Assert.Empty(previousOutput.ToString());
            var unhandled = engine.Execute(
                "raise constructor('utf-8', bytes([65, 255]), 1, 2, 'custom reason')",
                "unicode_decode_error_unhandled_constructor.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var diagnostic = Assert.Single(unhandled.Diagnostics);
            Assert.Equal("DPY4031", diagnostic.Code);
            Assert.Equal(
                "UnicodeDecodeError: 'utf-8' codec can't decode byte 0xff in position 1: custom reason",
                diagnostic.Message
            );
            Assert.Empty(Events(engine));
        }
    }

    [Fact]
    public void UnhandledDiagnosticsFormatCurrentMembersAndContainFormattingFailures()
    {
        var engine = Prepare(out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var result = engine.Execute(
                """
                class Encoding:
                    def __str__(self):
                        print('encoding')
                        error.start = 2
                        error.end = 3
                        return 'changed-codec'
                class Reason:
                    def __str__(self):
                        print('reason')
                        error.encoding = Encoding()
                        error.object = b'ABC'
                        error.start = 1
                        error.end = 3
                        return 'changed reason'
                error.reason = Reason()
                raise error
                """,
                "unicode_decode_error_unhandled_callbacks.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("DPY4031", diagnostic.Code);
            Assert.Equal(
                "UnicodeDecodeError: 'changed-codec' codec can't decode byte 0x43 in position 2: changed reason",
                diagnostic.Message
            );
            Assert.Equal(Lines("reason", "encoding"), output.ToString());
            Assert.Empty(previousOutput.ToString());
            using var failedOutput = new StringWriter();
            var failed = engine.Execute(
                """
                class Failure:
                    def __str__(self):
                        print('failed reason')
                        raise ValueError('format failed')
                error.reason = Failure()
                raise error
                """,
                "unicode_decode_error_unhandled_format_failure.py",
                failedOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var failedDiagnostic = Assert.Single(failed.Diagnostics);
            Assert.Equal("DPY4031", failedDiagnostic.Code);
            Assert.Equal("UnicodeDecodeError: <exception str() failed>", failedDiagnostic.Message);
            Assert.Equal(Lines("failed reason"), failedOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "error.reason = 'recovered'\nerror.encoding = 'ascii'\nprint(str(error))",
                "unicode_decode_error_unhandled_format_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(
                Lines("'ascii' codec can't decode byte 0x43 in position 2: recovered"),
                recoveredOutput.ToString()
            );
        }
    }

    [Fact]
    public void UnhandledFormatterCallbacksPreserveCurrentBudgetsCancellationAndRecovery()
    {
        foreach (var phase in new[] { "reason", "encoding" })
        {
            var engine = Prepare(out var previousOutput);
            using (previousOutput)
            {
                var original = Global(engine, "error");
                using var output = new StringWriter();
                var limited = engine.Execute(
                    "Control.slow = '"
                        + phase
                        + "'\ntry:\n    raise error\nfinally:\n    print('cleanup')",
                    "unicode_decode_error_unhandled_budget.py",
                    output,
                    new ManagedExecutionOptions { InstructionLimit = 500 },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
                Assert.Equal(Lines("cleanup"), output.ToString());
                Assert.Contains(phase, Events(engine));
                Assert.Same(original, Global(engine, "error"));
                Assert.Empty(previousOutput.ToString());
                Recover(engine, phase);
                using var cancellation = new CancellationTokenSource();
                using var cancelledOutput = new CancelAfterLineWriter(cancellation);
                Assert.Throws<OperationCanceledException>(() =>
                    engine.Execute(
                        "Control.cancel = '" + phase + "'\nevents.clear()\nraise error",
                        "unicode_decode_error_unhandled_cancel.py",
                        cancelledOutput,
                        cancellationToken: cancellation.Token
                    )
                );
                Assert.Equal(Lines("cancel"), cancelledOutput.ToString());
                Assert.Contains(phase, Events(engine));
                Assert.Same(original, Global(engine, "error"));
                Assert.Empty(previousOutput.ToString());
                Recover(engine, phase);
            }
        }
    }

    private static ManagedPythonEngine Prepare(out StringWriter previousOutput)
    {
        var engine = new ManagedPythonEngine();
        previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            """
            events = []
            class Control:
                slow = ''
                cancel = ''
                trace = False
            def touch(name):
                events.append(name)
                if Control.trace: print(name)
                if Control.cancel == name: print('cancel')
                if Control.slow == name:
                    for counter in range(5000): pass
            class Index:
                def __init__(self, name, value): self.name = name; self.value = value
                def __index__(self): touch(self.name); return self.value
            class Text:
                def __init__(self, name): self.name = name
                def __str__(self): touch(self.name); return self.name
            constructor = UnicodeDecodeError
            start = Index('start', 1)
            end = Index('end', 2)
            newargs = ('utf-8', b'abcd', start, end, 'new reason')
            error = constructor('ascii', bytes([255, 120]), 0, 1, 'old reason')
            error.encoding = Text('encoding')
            error.reason = Text('reason')
            target = 'original'
            """,
            "unicode_decode_error_callback_setup.py",
            previousOutput,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        return engine;
    }

    private static string Expression(string phase) =>
        phase is "start" or "end" ? "constructor(*newargs)" : "str(error)";

    private static void Recover(ManagedPythonEngine engine, string phase)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "Control.slow = ''\nControl.cancel = ''\ntarget = "
                + Expression(phase)
                + "\nprint(type(target).__name__)",
            "unicode_decode_error_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(phase is "start" or "end" ? "UnicodeDecodeError" : "str"),
            output.ToString()
        );
    }

    private static IEnumerable<string> Events(ManagedPythonEngine engine) =>
        Assert
            .IsType<PythonListValue>(Global(engine, "events"))
            .Elements.Select(value => Assert.IsType<PythonTextValue>(value).Value);

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
