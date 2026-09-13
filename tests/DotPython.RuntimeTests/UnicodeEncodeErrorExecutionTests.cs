using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class UnicodeEncodeErrorExecutionTests
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
                "unicode_encode_error_current.py",
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
                    "'encoding' codec can't encode character '\\xff' in position 0: reason"
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
                    "unicode_encode_error_budget.py",
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
                        "unicode_encode_error_cancel.py",
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
            error = UnicodeEncodeError('ascii', 'old', 0, 1, 'old reason')
            original = error
            for fail in ('start', 'end'):
                events.clear()
                start = Index('start', fail == 'start')
                end = Index('end', fail == 'end')
                arguments = ('utf-8', 'new', start, end, 'new reason')
                try: error.__init__(*arguments)
                except ValueError as caught:
                    print(caught is problem, error is original, error.args == arguments, events)
                print(error.encoding, error.object, error.start, error.end, error.reason)
            error.__init__('utf-8', 'new', 1, 2, 'new reason')
            print(error.encoding, error.object, error.start, error.end, error.reason)
            """,
            "unicode_encode_error_reinit.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "True True True ['start']",
                "ascii old 0 1 old reason",
                "True True True ['start', 'end']",
                "ascii old 0 1 old reason",
                "utf-8 new 1 2 new reason"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void EncoderErrorsRetainTheSourceTextAndReportCurrentCallSpansAndCodePointPositions()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            "values = 'A😀B'\nencode = values.encode\ntarget = 'original'",
            "unicode_encode_error_saved.py",
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
            try: target = encode('ascii')
            except UnicodeEncodeError as error:
                saved = error
                print(error.object is values, error.args[1] is values)
                print(error.encoding, error.start, error.end, error.reason)
                print(len(error.args), error.args[0], error.args[2], error.args[3], error.args[4])
            """,
            "unicode_encode_error_caught.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(caught.Success);
        Assert.Equal(
            Lines(
                "True True",
                "ascii 1 2 ordinal not in range(128)",
                "5 ascii 1 2 ordinal not in range(128)"
            ),
            output.ToString()
        );
        Assert.Same(originalTarget, Global(engine, "target"));
        const string source = "padding = 1\n\n\ntarget = encode('ascii')";
        var failed = engine.Execute(
            source,
            "unicode_encode_error_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var diagnostic = Assert.Single(failed.Diagnostics);
        Assert.Equal("unicode_encode_error_current.py", failed.Source.FilePath);
        Assert.Equal(source.IndexOf("encode(", StringComparison.Ordinal), diagnostic.Span.Start);
        Assert.Contains(
            "can't encode character '\\U0001f600' in position 1",
            diagnostic.Message,
            StringComparison.Ordinal
        );
        Assert.Same(original, Global(engine, "values"));
        Assert.Equal("A😀B", Assert.IsType<PythonTextValue>(original).Value);
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
            original = UnicodeEncodeError('utf-8', 'abc', Index(), 2, 'old')
            original.encoding = 'ascii'
            original.start = 0
            original.reason = 'changed'
            original.note = ['before']
            snapshot = pickle.dumps(original)
            original.note.append('after')
            """,
            "unicode_encode_error_snapshot_setup.py",
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
            "unicode_encode_error_snapshot_current.py",
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
    public void UnhandledDiagnosticsFormatCurrentMembersAndContainFormattingFailures()
    {
        var engine = Prepare(out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var result = engine.Execute(
                """
                try: raise error
                except UnicodeEncodeError as caught: print(caught is error, events)
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
                        error.object = 'ABC'
                        error.start = 1
                        error.end = 3
                        return 'changed reason'
                error.reason = Reason()
                raise error
                """,
                "unicode_encode_error_unhandled_callbacks.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("DPY4031", diagnostic.Code);
            Assert.Equal(
                "UnicodeEncodeError: 'changed-codec' codec can't encode character '\\x43' in position 2: changed reason",
                diagnostic.Message
            );
            Assert.Equal(Lines("True []", "reason", "encoding"), output.ToString());
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
                "unicode_encode_error_unhandled_format_failure.py",
                failedOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var failedDiagnostic = Assert.Single(failed.Diagnostics);
            Assert.Equal("DPY4031", failedDiagnostic.Code);
            Assert.Equal("UnicodeEncodeError: <exception str() failed>", failedDiagnostic.Message);
            Assert.Equal(Lines("failed reason"), failedOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "error.reason = 'recovered'\nerror.encoding = 'ascii'\nprint(str(error))",
                "unicode_encode_error_unhandled_format_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(
                Lines("'ascii' codec can't encode character '\\x43' in position 2: recovered"),
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
                    "unicode_encode_error_unhandled_budget.py",
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
                        "unicode_encode_error_unhandled_cancel.py",
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

    [Fact]
    public void StrictEncodingScansChargeWorkBeforeResultsAndPreserveSupplementaryErrorPositions()
    {
        foreach (var encoding in new[] { "ascii", "latin-1", "utf-8" })
        {
            var engine = new ManagedPythonEngine();
            using var previousCancellation = new CancellationTokenSource();
            using var previousOutput = new StringWriter();
            var setup = engine.Execute(
                "values = 'a' * 200000 + '😀'\nencode = values.encode\ntarget = 'original'",
                "unicode_encode_error_scan_setup.py",
                previousOutput,
                cancellationToken: previousCancellation.Token
            );
            Assert.True(setup.Success);
            previousCancellation.Cancel();
            var originalTarget = Global(engine, "target");
            var originalText = Global(engine, "values");
            using var output = new StringWriter();
            var limited = engine.Execute(
                "try:\n    target = encode('"
                    + encoding
                    + "')\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "unicode_encode_error_scan_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 100 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalText, Global(engine, "values"));
            Assert.Empty(previousOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "try:\n    target = encode('"
                    + encoding
                    + "')\nexcept UnicodeEncodeError as error:\n    print('error', error.start, error.end, error.object is values)\nelse:\n    print('ok', len(target))",
                "unicode_encode_error_scan_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(
                Lines(encoding == "utf-8" ? "ok 200004" : "error 200000 200001 True"),
                recoveredOutput.ToString()
            );
            Assert.Same(originalText, Global(engine, "values"));
            Assert.Equal(
                new string('a', 200000) + "😀",
                Assert.IsType<PythonTextValue>(originalText).Value
            );
        }
    }

    [Fact]
    public void StrictEncodingMaterializationCapPreservesSourceAndTargetAndAllowsExactBoundaryRecovery()
    {
        var engine = new ManagedPythonEngine();
        using var setupOutput = new StringWriter();
        var setup = engine.Execute(
            "values = '😀' * 2500001\nencode = values.encode\ntarget = 'original'",
            "unicode_encode_error_materialization_setup.py",
            setupOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var originalTarget = Global(engine, "target");
        var originalSource = Assert.IsType<PythonTextValue>(Global(engine, "values"));
        using var output = new StringWriter();
        var limited = engine.Execute(
            """
            try: target = encode('utf-8', 'strict')
            except OverflowError as error: print(str(error))
            """,
            "unicode_encode_error_materialization_limit.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(limited.Success);
        Assert.Equal(
            Lines("encoded bytes exceed the managed materialization limit"),
            output.ToString()
        );
        Assert.Same(originalTarget, Global(engine, "target"));
        Assert.Same(originalSource, Global(engine, "values"));
        Assert.Equal(5_000_002, originalSource.Value.Length);
        Assert.StartsWith("😀", originalSource.Value, StringComparison.Ordinal);
        Assert.EndsWith("😀", originalSource.Value, StringComparison.Ordinal);
        using var recoveredOutput = new StringWriter();
        var recovered = engine.Execute(
            "target = values[:-1].encode('utf-8', 'strict')\nprint(len(target), len(values))",
            "unicode_encode_error_materialization_recovery.py",
            recoveredOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovered.Success);
        Assert.Equal(Lines("10000000 2500001"), recoveredOutput.ToString());
        Assert.Same(originalSource, Global(engine, "values"));
        var encoded = Assert.IsType<PythonByteSequenceValue>(Global(engine, "target")).Value;
        Assert.Equal(10_000_000, encoded.Length);
        Assert.Equal(new byte[] { 240, 159, 152, 128 }, encoded[..4]);
        Assert.Equal(new byte[] { 240, 159, 152, 128 }, encoded[^4..]);
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
            constructor = UnicodeEncodeError
            start = Index('start', 1)
            end = Index('end', 2)
            newargs = ('utf-8', 'abcd', start, end, 'new reason')
            error = constructor('ascii', 'ÿx', 0, 1, 'old reason')
            error.encoding = Text('encoding')
            error.reason = Text('reason')
            target = 'original'
            """,
            "unicode_encode_error_callback_setup.py",
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
            "unicode_encode_error_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(phase is "start" or "end" ? "UnicodeEncodeError" : "str"),
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
