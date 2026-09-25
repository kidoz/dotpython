using System.Reflection;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CustomCodecErrorHandlerExecutionTests
{
    [Theory]
    [InlineData(false, "call")]
    [InlineData(true, "call")]
    [InlineData(false, "index")]
    [InlineData(true, "index")]
    [InlineData(false, "rewind")]
    [InlineData(true, "rewind")]
    public void SavedRegistrationsUseCurrentBudgetsAndRecover(bool decode, string phase)
    {
        var engine = new ManagedPythonEngine();
        using var priorOutput = new StringWriter();
        using var creator = new CancellationTokenSource();
        Assert.True(
            engine
                .Execute(
                    """
                    import codecs
                    slow = False
                    phase = ''
                    seen = []
                    class Index:
                        def __init__(self,value): self.value = value
                        def __index__(self):
                            if slow and phase == 'index':
                                for i in range(10000): pass
                            return self.value
                    def handler(error):
                        seen.append(error)
                        print('callback')
                        if slow and phase == 'call':
                            for i in range(10000): pass
                        return ('X', error.start if slow and phase == 'rewind' else Index(error.end))
                    codecs.register_error('saved', handler)
                    encode = 'AéB'.encode
                    decode = b'A\xffB'.decode
                    target = 'unchanged'
                    """,
                    "custom_codec_setup.py",
                    priorOutput,
                    cancellationToken: creator.Token
                )
                .Success
        );
        creator.Cancel();
        var original = Global(engine, "target");
        var expression = (decode ? "decode" : "encode") + "('ascii', 'saved')";
        using var output = new StringWriter();
        var result = engine.Execute(
            "slow = True\nphase = '"
                + phase
                + "'\ntry:\n    target = "
                + expression
                + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "custom_codec_limit.py",
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
                    "slow = False\ntarget = " + expression,
                    "custom_codec_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        if (decode)
            Assert.Equal("AXB", Assert.IsType<PythonTextValue>(Global(engine, "target")).Value);
        else
            Assert.Equal(
                new byte[] { 65, 88, 66 },
                Assert.IsType<PythonByteSequenceValue>(Global(engine, "target")).Value
            );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementExpansionStopsAtCapWithoutReplayingCallbacks(bool decode)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    """
                    import codecs
                    calls = []
                    replacement = 'x' * 6000000
                    def handler(error):
                        calls.append(error)
                        return (replacement, error.end)
                    codecs.register_error('expand', handler)
                    target = 'unchanged'
                    """,
                    "custom_codec_cap_setup.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        var original = Global(engine, "target");
        var expression = decode
            ? "b'\\xffA\\xff'.decode('ascii','expand')"
            : "'éAé'.encode('ascii','expand')";
        Assert.True(
            engine
                .Execute(
                    "try: target = "
                        + expression
                        + "\nexcept OverflowError: print(len(calls), calls[0] is calls[1])",
                    "custom_codec_cap.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Same(original, Global(engine, "target"));
        Assert.Equal("2 True" + Environment.NewLine, output.ToString());
        Assert.True(
            engine
                .Execute(
                    "replacement = 'x'\ntarget = " + expression + "\nprint(len(calls))",
                    "custom_codec_cap_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.EndsWith("4" + Environment.NewLine, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationsAreIsolatedAcrossEnginesAndSharedWithImportedModules()
    {
        var first = new ManagedPythonEngine(
            new Dictionary<string, SourceText>
            {
                ["consumer"] = new SourceText("value = 'é'.encode('ascii','owned')", "consumer.py"),
            }
        );
        var second = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            first
                .Execute(
                    "import codecs\ncodecs.register_error('owned',lambda e: ('X',e.end))\nimport consumer\nprint(consumer.value)",
                    "custom_codec_first.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.True(
            second
                .Execute(
                    "import codecs\ntry: codecs.lookup_error('owned')\nexcept LookupError: print('isolated')",
                    "custom_codec_second.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal(
            "b'X'" + Environment.NewLine + "isolated" + Environment.NewLine,
            output.ToString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongCallbackReplacementChargesCurrentWork(bool decode)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "import codecs\nreplacement='x'*100000\ncodecs.register_error('long',lambda e:(replacement,e.end))\ntarget='unchanged'",
                    "custom_codec_long_setup.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        var original = Global(engine, "target");
        var expression = decode ? "b'\\xff'.decode('ascii','long')" : "'é'.encode('ascii','long')";
        var result = engine.Execute(
            "target=" + expression,
            "custom_codec_long.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Same(original, Global(engine, "target"));
        Assert.True(
            engine
                .Execute(
                    "target=" + expression + "\nprint(len(target))",
                    "custom_codec_long_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal("100000" + Environment.NewLine, output.ToString());
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
