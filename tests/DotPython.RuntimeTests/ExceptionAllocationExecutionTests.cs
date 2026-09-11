using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ExceptionAllocationExecutionTests
{
    private const string Setup = """
        import copy
        import pickle
        tracing = False
        blocking = False
        class Delivery(Exception):
            def __new__(cls, message='payload'):
                try:
                    if tracing: print('allocate', message)
                    while blocking: pass
                    return BaseException.__new__(cls, message)
                except BaseException:
                    print('allocator swallowed')
                finally:
                    if tracing: print('allocator cleanup')
            def __init__(self, message='payload'):
                if tracing: print('initialize', message)
        original = Delivery()
        snapshot = pickle.dumps(original)
        """;

    [Theory]
    [InlineData("direct")]
    [InlineData("raise")]
    [InlineData("copy")]
    [InlineData("pickle")]
    public void SavedExceptionAllocatorUsesCurrentOutput(string route)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();

        var result = engine.Execute(
            "tracing = True\n"
                + Invoke(route)
                + "\nprint(type(restored) is Delivery, restored.args[0])",
            "exception_allocation_current_output.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(
            Lines("allocate payload", "allocator cleanup", "initialize payload", "True payload"),
            output.ToString()
        );
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("raise")]
    [InlineData("copy")]
    [InlineData("pickle")]
    public void AllocatorInstructionLimitEscapesPythonHandlersAndEngineRecovers(string route)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();

        var result = engine.Execute(
            GuardedInvocation(route),
            "exception_allocation_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            Lines("allocate payload", "allocator cleanup", "outer cleanup"),
            output.ToString()
        );
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("raise")]
    [InlineData("copy")]
    [InlineData("pickle")]
    public void AllocatorCancellationEscapesPythonHandlersAndEngineRecovers(string route)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                GuardedInvocation(route),
                "exception_allocation_cancellation.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(
            Lines("allocate payload", "allocator cleanup", "outer cleanup"),
            output.ToString()
        );
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine);
    }

    [Fact]
    public void PickleSnapshotDefersSavedAllocatorUntilEachLoad()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        Prepare(engine, previousOutput);
        using var output = new StringWriter();

        var result = engine.Execute(
            """
            tracing = True
            snapshot = pickle.dumps(original)
            print('snapshot captured')
            first = pickle.loads(snapshot)
            second = pickle.loads(snapshot)
            print(first is second, type(first) is Delivery, type(second) is Delivery)
            """,
            "exception_allocation_pickle.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(
            Lines(
                "snapshot captured",
                "allocate payload",
                "allocator cleanup",
                "initialize payload",
                "allocate payload",
                "allocator cleanup",
                "initialize payload",
                "False True True"
            ),
            output.ToString()
        );
    }

    [Fact]
    public void SavedAllocatorUsesItsEnginesCanonicalExceptionGroup()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            expected_type = ExceptionGroup
            expected_type.marker = 'first engine'
            class Delivery(Exception):
                def __new__(cls):
                    group = BaseExceptionGroup('allocated', [ValueError()])
                    print(type(group) is expected_type, type(group).marker, type(group).__name__)
                    return BaseException.__new__(cls)
            saved_allocator = Delivery
            """,
            "exception_allocation_owner_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var otherEngine = new ManagedPythonEngine();
        using var otherOutput = new StringWriter();
        var other = otherEngine.Execute(
            "ExceptionGroup.marker = 'other engine'\nExceptionGroup.__name__ = 'OtherGroup'",
            "exception_allocation_other_engine.py",
            otherOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(other.Success);
        using var output = new StringWriter();

        var result = engine.Execute(
            "ExceptionGroup.__name__ = 'OwnedGroup'\nprint(type(saved_allocator()) is Delivery)",
            "exception_allocation_owner_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(string.Empty, otherOutput.ToString());
        Assert.Equal(Lines("True first engine OwnedGroup", "True"), output.ToString());
    }

    private static void Prepare(ManagedPythonEngine engine, StringWriter output)
    {
        var result = engine.Execute(
            Setup,
            "exception_allocation_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(string.Empty, output.ToString());
    }

    private static string Invoke(string route) =>
        route switch
        {
            "direct" => "restored = Delivery()",
            "raise" =>
                "try:\n    raise Delivery\nexcept Delivery as caught:\n    restored = caught",
            "copy" => "restored = copy.deepcopy(original)",
            "pickle" => "restored = pickle.loads(snapshot)",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };

    private static string GuardedInvocation(string route) =>
        "tracing = True\nblocking = True\ntry:\n    "
        + Invoke(route).Replace("\n", "\n    ", StringComparison.Ordinal)
        + "\nexcept BaseException:\n    print('outer swallowed')\nfinally:\n    print('outer cleanup')";

    private static void AssertRecovery(ManagedPythonEngine engine)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            "blocking = False\nrestored = Delivery('recovered')\nprint(type(restored) is Delivery, restored.args[0])",
            "exception_allocation_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(
            Lines(
                "allocate recovered",
                "allocator cleanup",
                "initialize recovered",
                "True recovered"
            ),
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
