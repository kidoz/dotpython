using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CodecOperationExecutionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SavedEntryPointsUseCurrentBudgetsAndRecover(bool decode, bool callback)
    {
        var engine = new ManagedPythonEngine();
        using var creator = new CancellationTokenSource();
        using var priorOutput = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    """
                    import codecs
                    encode = codecs.encode
                    decode = codecs.decode
                    slow = False
                    def handler(error):
                        print('callback')
                        if slow:
                            for i in range(10000): pass
                        return ('X',error.end)
                    codecs.register_error('entry',handler)
                    text = 'x' * 100000
                    data = b'x' * 100000
                    target = 'unchanged'
                    """,
                    "codec_operation_setup.py",
                    priorOutput,
                    cancellationToken: creator.Token
                )
                .Success
        );
        creator.Cancel();
        var original = Global(engine, "target");
        var expression = callback
            ? decode
                ? "decode(b'\\xff','ascii','entry')"
                : "encode('é','ascii','entry')"
            : decode
                ? "decode(data)"
                : "encode(text)";
        using var output = new StringWriter();
        var result = engine.Execute(
            "slow = True\ntry:\n    target = "
                + expression
                + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "codec_operation_limit.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("swallowed", output.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(
            "cleanup" + Environment.NewLine,
            output.ToString(),
            StringComparison.Ordinal
        );
        Assert.Same(original, Global(engine, "target"));
        Assert.Empty(priorOutput.ToString());
        Assert.True(
            engine
                .Execute(
                    "slow = False\ntarget = " + expression + "\nprint(len(target))",
                    "codec_operation_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.EndsWith(
            (callback ? "1" : "100000") + Environment.NewLine,
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputLimitFailureGetsNoteAndPreservesPriorResult(bool decode)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "import codecs\nreplacement = 'X'*6000000\ncodecs.register_error('large',lambda e:(replacement,e.end))\ntarget='unchanged'",
                    "codec_operation_cap_setup.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        var original = Global(engine, "target");
        var expression = decode
            ? "codecs.decode(b'\\xffA\\xff','ascii','large')"
            : "codecs.encode('éAé','ascii','large')";
        Assert.True(
            engine
                .Execute(
                    "try: target = "
                        + expression
                        + "\nexcept OverflowError as e: print(e.__notes__)",
                    "codec_operation_cap.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Same(original, Global(engine, "target"));
        Assert.Contains(
            (decode ? "decoding" : "encoding") + " with 'ascii' codec failed",
            output.ToString(),
            StringComparison.Ordinal
        );
        Assert.True(
            engine
                .Execute(
                    "replacement='X'\ntarget=" + expression + "\nprint(len(target))",
                    "codec_operation_cap_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.EndsWith("3" + Environment.NewLine, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NotePropertyCallbacksRespectCurrentBudgetWithoutMaskingLimit()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    """
                    import codecs
                    slow = False
                    class Problem(ValueError):
                        @property
                        def __notes__(self):
                            if slow:
                                for i in range(10000): pass
                            return self.notes
                    problem = Problem('original')
                    problem.notes = []
                    def handler(error): raise problem
                    codecs.register_error('note',handler)
                    target = 'unchanged'
                    """,
                    "codec_note_setup.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        var original = Global(engine, "target");
        var result = engine.Execute(
            "slow=True\ntry:\n    target=codecs.encode('é','ascii','note')\nexcept BaseException:\n    print('swallowed')",
            "codec_note_limit.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(output.ToString());
        Assert.Same(original, Global(engine, "target"));
        Assert.True(
            engine
                .Execute(
                    "slow=False\ntry: codecs.encode('é','ascii','note')\nexcept ValueError as e: print(e is problem, len(e.__notes__))",
                    "codec_note_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal("True 1" + Environment.NewLine, output.ToString());
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
