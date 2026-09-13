using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BytesConstructionExecutionTests
{
    [Fact]
    public void SavedConstructionCallbacksUseCurrentOutputAndCancellation()
    {
        var engine = PrepareCallbackEngine(out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var result = engine.Execute(
                """
                Control.trace = True
                print(bytes(hook))
                print(list(bytes(count)))
                print(bytes(source))
                """,
                "bytes_construction_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Empty(previousOutput.ToString());
            Assert.Equal(
                Lines(
                    "hook",
                    "b'ab'",
                    "count",
                    "[0, 0]",
                    "iter",
                    "hint",
                    "next",
                    "element",
                    "next",
                    "b'A'"
                ),
                output.ToString()
            );
        }
    }

    [Fact]
    public void ConstructionCallbacksChargeCurrentBudgetAndKeepUnpublishedResults()
    {
        foreach (var phase in CallbackPhases())
        {
            var engine = PrepareCallbackEngine(out var previousOutput);
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
                    "bytes_construction_callback_budget.py",
                    output,
                    new ManagedExecutionOptions { InstructionLimit = 500 },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
                Assert.Equal(Lines("cleanup"), output.ToString());
                Assert.Empty(previousOutput.ToString());
                Assert.Same(original, Global(engine, "target"));
                Assert.Contains(phase, Events(engine));
                AssertCallbackRecovery(engine, phase);
            }
        }
    }

    [Fact]
    public void CancelledConstructionCallbacksPreserveEffectsAndConsumedCursorForRecovery()
    {
        foreach (var phase in CallbackPhases())
        {
            var engine = PrepareCallbackEngine(out var previousOutput);
            using (previousOutput)
            using (var cancellation = new CancellationTokenSource())
            using (var output = new CancelAfterLineWriter(cancellation))
            {
                var original = Global(engine, "target");
                Assert.Throws<OperationCanceledException>(() =>
                    engine.Execute(
                        "Control.cancel = '" + phase + "'\ntarget = " + Expression(phase),
                        "bytes_construction_callback_cancel.py",
                        output,
                        cancellationToken: cancellation.Token
                    )
                );
                Assert.Equal(Lines("cancel"), output.ToString());
                Assert.Empty(previousOutput.ToString());
                Assert.Same(original, Global(engine, "target"));
                Assert.Contains(phase, Events(engine));
                AssertCallbackRecovery(engine, phase);
            }
        }
    }

    [Fact]
    public void CallbackFreeAllocationAndElementScansChargeWorkWithoutPublishingPartialBytes()
    {
        foreach (var kind in new[] { "count", "list", "tuple", "iterator" })
        {
            var engine = new ManagedPythonEngine();
            var expression = kind switch
            {
                "count" => "200000",
                "list" => "[65] * 4096",
                "tuple" => "(65,) * 4096",
                _ => "iter(range(256))",
            };
            using var setupOutput = new StringWriter();
            var setup = engine.Execute(
                "source = " + expression + "\ntarget = 'original'",
                "bytes_construction_scan_setup.py",
                setupOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(setup.Success);
            var original = Global(engine, "target");
            using var output = new StringWriter();
            var result = engine.Execute(
                "try:\n    target = bytes(source)\nfinally:\n    print('cleanup')",
                "bytes_construction_scan_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 100 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(original, Global(engine, "target"));
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                (
                    kind == "iterator"
                        ? "position = next(source)\nprint(0 < position < 256)\nsource = iter(range(256))\n"
                        : string.Empty
                ) + "target = bytes(source)\nprint(len(target))",
                "bytes_construction_scan_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(
                kind switch
                {
                    "count" => Lines("200000"),
                    "iterator" => Lines("True", "256"),
                    _ => Lines("4096"),
                },
                recoveredOutput.ToString()
            );
            var bytes = Assert.IsType<PythonByteSequenceValue>(Global(engine, "target"));
            Assert.Equal(
                kind == "count" || kind == "iterator" ? (byte)0 : (byte)65,
                bytes.Value[0]
            );
        }
    }

    [Fact]
    public void HugeLengthHintsStayAdvisoryWhileConcreteCountsRespectTheAllocationCap()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            class Source:
                def __iter__(self): return iter([65])
                def __length_hint__(self): return 1000000000
            print(bytes(Source()))
            target = b'original'
            alias = target
            class Count:
                def __index__(self): return 10000001
            for value in (10000001, Count()):
                try: target = bytes(value)
                except OverflowError as error: print(str(error), target is alias)
            print(bytes([66]))
            """,
            "bytes_construction_allocation_limits.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 1000 },
            TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "b'A'",
                "The bytes result exceeds the supported size. True",
                "The bytes result exceeds the supported size. True",
                "b'B'"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void FailedElementConversionPreservesLiveListEffectsAndResumesIteratorAfterTheBadItem()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            problem = ValueError('element failed')
            events = []
            class Element:
                fail = True
                def __index__(self):
                    events.append('element')
                    if self.fail:
                        values.append(67)
                        raise problem
                    return 65
            element = Element()
            values = [element, 66]
            source = iter(values)
            target = b'original'
            alias = target
            try: target = bytes(source)
            except ValueError as error: print(error is problem, target is alias, len(values), events)
            print(bytes(source), events)
            Element.fail = False
            print(bytes(values), events)
            """,
            "bytes_construction_element_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines("True True 3 ['element']", "b'BC' ['element']", "b'ABC' ['element', 'element']"),
            output.ToString()
        );
    }

    private static string[] CallbackPhases() =>
        ["hook", "count", "iter", "hint", "next", "element"];

    private static string Expression(string phase) =>
        phase switch
        {
            "hook" => "bytes(hook)",
            "count" => "bytes(count)",
            _ => "bytes(source)",
        };

    private static ManagedPythonEngine PrepareCallbackEngine(out StringWriter previousOutput)
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
            class Hook:
                def __bytes__(self): touch('hook'); return b'ab'
            class Count:
                def __index__(self): touch('count'); return 2
            class Element:
                def __index__(self): touch('element'); return 65
            class Source:
                def __init__(self): self.position = 0
                def __iter__(self): touch('iter'); return self
                def __length_hint__(self): touch('hint'); return 1
                def __next__(self):
                    if self.position == 1:
                        touch('next')
                        raise StopIteration
                    self.position += 1
                    touch('next')
                    return Element()
            hook = Hook()
            count = Count()
            source = Source()
            target = 'original'
            """,
            "bytes_construction_callback_setup.py",
            previousOutput,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        return engine;
    }

    private static void AssertCallbackRecovery(ManagedPythonEngine engine, string phase)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "Control.slow = ''\nControl.cancel = ''\nprint(source.position)\nsource.position = 0\ntarget = "
                + Expression(phase)
                + "\nprint(list(target))",
            "bytes_construction_callback_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                phase is "next" or "element" ? "1" : "0",
                phase switch
                {
                    "hook" => "[97, 98]",
                    "count" => "[0, 0]",
                    _ => "[65]",
                }
            ),
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
