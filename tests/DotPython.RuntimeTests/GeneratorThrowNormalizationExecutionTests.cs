using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class GeneratorThrowNormalizationExecutionTests
{
    [Fact]
    public void SavedUserIteratorDelegationUsesCurrentExecution()
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            """
            class Delegate:
                def __iter__(self): return self
                def __next__(self):
                    print('next')
                    return 'value'
                def throw(self, error):
                    print('throw')
                    return 'caught'
            def outer(): yield from Delegate()
            target = outer()
            next(target)
            """,
            "saved_iterator_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        Assert.Equal(Lines("next"), previousOutput.ToString());
        using var currentOutput = new StringWriter();
        var result = engine.Execute(
            "print(target.throw(ValueError))\nprint(next(target))",
            "saved_iterator_resume.py",
            currentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("next"), previousOutput.ToString());
        Assert.Equal(Lines("throw", "caught", "next", "value"), currentOutput.ToString());
    }

    private const string AsyncDriver = """
        async def await_one(step):
            return await step
        def drive(step):
            pending = await_one(step)
            try:
                pending.send(None)
            except StopIteration as completed:
                return completed.value
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedThrowUsesCurrentOutputForConstructorAndResumedBody(bool asynchronous)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            AsyncDriver
                + "\nclass Delivery(Exception):\n"
                + "    def __init__(self): print('constructor')\n"
                + (asynchronous ? "async " : "")
                + "def body():\n"
                + "    try:\n"
                + "        yield 'ready'\n"
                + "    except Delivery:\n"
                + "        print('body')\n"
                + "        yield 'delivered'\n"
                + "target = body()\n"
                + (
                    asynchronous
                        ? "drive(target.__anext__())\nsaved = target.athrow(Delivery)\n"
                        : "next(target)\nsaved = target.throw\n"
                ),
            "saved_throw_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        using var currentOutput = new StringWriter();

        var result = engine.Execute(
            asynchronous ? "print(drive(saved))" : "print(saved(Delivery))",
            "saved_throw_resume.py",
            currentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, previousOutput.ToString());
        Assert.Equal(Lines("constructor", "body", "delivered"), currentOutput.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorInstructionLimitIsNotInjectedAndSuspendedGeneratorRemainsUsable(
        bool asynchronous
    )
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        PrepareRecoverableGenerator(engine, previousOutput, asynchronous);
        using var output = new StringWriter();

        var result = engine.Execute(
            GuardedThrow(asynchronous),
            "throw_constructor_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            Lines("constructor", "constructor cleanup", "outer cleanup"),
            output.ToString()
        );
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine, asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorCancellationIsNotInjectedAndSuspendedGeneratorRemainsUsable(
        bool asynchronous
    )
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        PrepareRecoverableGenerator(engine, previousOutput, asynchronous);
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstLineWriter(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            engine.Execute(
                GuardedThrow(asynchronous),
                "throw_constructor_cancellation.py",
                output,
                cancellationToken: cancellation.Token
            )
        );

        Assert.Equal(
            Lines("constructor", "constructor cleanup", "outer cleanup"),
            output.ToString()
        );
        Assert.Equal(string.Empty, previousOutput.ToString());
        AssertRecovery(engine, asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumedGeneratorBodyUsesCurrentInstructionLimit(bool asynchronous)
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            AsyncDriver
                + "\nclass Delivery(Exception): pass\n"
                + (asynchronous ? "async " : "")
                + "def body():\n"
                + "    try:\n"
                + "        yield 'ready'\n"
                + "    except Delivery:\n"
                + "        try:\n"
                + "            for index in range(10000): pass\n"
                + "            print('escaped current budget')\n"
                + "        except BaseException:\n"
                + "            print('body swallowed')\n"
                + "        finally:\n"
                + "            print('body cleanup')\n"
                + "target = body()\n"
                + (
                    asynchronous
                        ? "drive(target.__anext__())\nsaved = target.athrow(Delivery)\n"
                        : "next(target)\nsaved = target.throw\n"
                ),
            "resumed_body_setup.py",
            previousOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        using var output = new StringWriter();

        var result = engine.Execute(
            "try:\n"
                + (asynchronous ? "    drive(saved)\n" : "    saved(Delivery)\n")
                + "except BaseException:\n"
                + "    print('outer swallowed')\n"
                + "finally:\n"
                + "    print('outer cleanup')\n",
            "resumed_body_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 500 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(Lines("body cleanup", "outer cleanup"), output.ToString());
        Assert.Equal(string.Empty, previousOutput.ToString());
    }

    [Fact]
    public void SavedGeneratorAndConstructorRetainTheEnginesExceptionGroupIdentity()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var setup = engine.Execute(
            """
            expected_type = ExceptionGroup
            expected_type.marker = 'first engine'
            class Delivery(Exception):
                def __init__(self):
                    group = BaseExceptionGroup('constructed', [ValueError()])
                    print(type(group) is expected_type, type(group).marker)
            def body():
                try:
                    yield None
                except Delivery:
                    yield BaseExceptionGroup('resumed', [TypeError()])
            target = body()
            next(target)
            """,
            "saved_group_setup.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var otherEngine = new ManagedPythonEngine();
        using var otherOutput = new StringWriter();
        var other = otherEngine.Execute(
            "ExceptionGroup.marker = 'other engine'",
            "other_engine.py",
            otherOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(other.Success);
        using var output = new StringWriter();

        var result = engine.Execute(
            """
            ExceptionGroup.__name__ = 'RenamedGroup'
            group = target.throw(Delivery)
            print(type(group) is expected_type, type(group).marker, type(group).__name__)
            """,
            "saved_group_resume.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        Assert.Equal(string.Empty, firstOutput.ToString());
        Assert.Equal(string.Empty, otherOutput.ToString());
        Assert.Equal(
            Lines("True first engine", "True first engine RenamedGroup"),
            output.ToString()
        );
    }

    private static void PrepareRecoverableGenerator(
        ManagedPythonEngine engine,
        StringWriter output,
        bool asynchronous
    )
    {
        var setup = engine.Execute(
            AsyncDriver
                + "\nclass Delivery(Exception):\n"
                + "    def __init__(self):\n"
                + "        try:\n"
                + "            print('constructor')\n"
                + "            while True: pass\n"
                + "        except BaseException:\n"
                + "            print('constructor swallowed')\n"
                + "        finally:\n"
                + "            print('constructor cleanup')\n"
                + (asynchronous ? "async " : "")
                + "def body():\n"
                + "    try:\n"
                + "        yield 'ready'\n"
                + "    except BaseException:\n"
                + "        print('injected host failure')\n"
                + "    yield 'resumed normally'\n"
                + "target = body()\n"
                + (
                    asynchronous
                        ? "drive(target.__anext__())\nsaved = target.athrow(Delivery)\n"
                        : "next(target)\nsaved = target.throw\n"
                ),
            "recoverable_generator_setup.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        Assert.Equal(string.Empty, output.ToString());
    }

    private static string GuardedThrow(bool asynchronous) =>
        "try:\n"
        + (asynchronous ? "    drive(saved)\n" : "    saved(Delivery)\n")
        + "except BaseException:\n"
        + "    print('outer swallowed')\n"
        + "finally:\n"
        + "    print('outer cleanup')\n";

    private static void AssertRecovery(ManagedPythonEngine engine, bool asynchronous)
    {
        using var output = new StringWriter();
        var result = engine.Execute(
            asynchronous ? "print(drive(target.__anext__()))" : "print(next(target))",
            "after_normalization_failure.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("resumed normally"), output.ToString());
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
