using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SurrogateDecodingExecutionTests
{
    [Theory]
    [InlineData("bytes([237,160,128]) * 20000", "utf8", "surrogatepass", 20000, 0xd800)]
    [InlineData("bytes([0,216]) * 20000", "utf-16-le", "surrogatepass", 20000, 0xd800)]
    [InlineData("bytes([216,0]) * 20000", "utf-16-be", "surrogatepass", 20000, 0xd800)]
    [InlineData("bytes([255]) * 60000", "ascii", "surrogateescape", 60000, 0xdcff)]
    [InlineData("bytes([255]) * 60000", "utf8", "surrogateescape", 60000, 0xdcff)]
    [InlineData("bytes([255,220]) * 40000", "utf-16-le", "surrogateescape", 80000, 0xdcdc)]
    public void SavedDecodersChargeCurrentWorkAndRecoverWithoutPublishingPartialText(
        string source,
        string codec,
        string handler,
        int length,
        int lastCharacter
    )
    {
        var engine = Prepare(source, out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalSource = Global(engine, "values");
            var originalTarget = Global(engine, "target");
            var expression = "decode('" + codec + "', '" + handler + "')";
            var result = engine.Execute(
                "try:\n    target = "
                    + expression
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "surrogate_decode_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 100 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
            Assert.Equal("cleanup" + Environment.NewLine, output.ToString());
            Assert.Same(originalSource, Global(engine, "values"));
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Empty(previousOutput.ToString());
            using var recoveryOutput = new StringWriter();
            var recovered = engine.Execute(
                "target = " + expression,
                "surrogate_decode_recovery.py",
                recoveryOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            var text = Assert.IsType<PythonTextValue>(Global(engine, "target")).Value;
            Assert.Equal(length, text.Length);
            Assert.Equal((char)lastCharacter, text[^1]);
            Assert.Same(originalSource, Global(engine, "values"));
        }
    }

    [Theory]
    [InlineData(65001, "surrogateescape", 1, 0xdcff)]
    [InlineData(1200, "surrogatepass", 2, 0xd800)]
    public void DecodedTextLimitAllowsTheExactBoundaryAndRejectsOneMoreCharacter(
        int codePage,
        string handler,
        int width,
        int character
    )
    {
        // Exercise the decoder's own cap with host-created byte buffers; Python
        // byte construction has a separate ten-million-byte materialization cap.
        const int limit = 10_000_000;
        var source = new byte[(limit + 1) * width];
        for (var offset = 0; offset < source.Length; offset += width)
        {
            if ((offset & 65535) == 0)
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (width == 1)
                source[offset] = 255;
            else
                source[offset + 1] = 216;
        }
        var error = Assert.Throws<PythonRuntimeException>(() =>
            PythonBytesDecoding.Decode(source, codePage, false, handler, default)
        );
        Assert.Equal("OverflowError", error.PythonExceptionTypeName);
        Assert.Contains(
            "decoded text exceeds the supported size",
            error.Message,
            StringComparison.Ordinal
        );
        var recovered = PythonBytesDecoding.Decode(
            source[..(limit * width)],
            codePage,
            false,
            handler,
            default
        );
        Assert.Equal(limit, recovered.Length);
        Assert.Equal((char)character, recovered[0]);
        Assert.Equal((char)character, recovered[^1]);
    }

    [Fact]
    public void FailedSurrogateRecoveryUsesTheCurrentSpanAndOriginalErrorState()
    {
        var engine = Prepare("bytes([0,216,255])", out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalTarget = Global(engine, "target");
            const string source = "padding = 1\n\n\ntarget = decode('utf-16-le', 'surrogatepass')";
            var result = engine.Execute(
                source,
                "surrogate_decode_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(
                source.IndexOf("decode(", StringComparison.Ordinal),
                diagnostic.Span.Start
            );
            Assert.Contains("position 2", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("truncated data", diagnostic.Message, StringComparison.Ordinal);
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Empty(previousOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "target = decode('utf8', 'surrogateescape')\nprint([ord(c) for c in target])",
                "surrogate_decode_after_error.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal("[0, 56536, 56575]" + Environment.NewLine, recoveredOutput.ToString());
        }
    }

    private static ManagedPythonEngine Prepare(string source, out StringWriter previousOutput)
    {
        var engine = new ManagedPythonEngine();
        previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        Assert.True(
            engine
                .Execute(
                    "values = " + source + "\ndecode = values.decode\ntarget = 'original'",
                    "surrogate_decode_setup.py",
                    previousOutput,
                    cancellationToken: cancellation.Token
                )
                .Success
        );
        cancellation.Cancel();
        return engine;
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
