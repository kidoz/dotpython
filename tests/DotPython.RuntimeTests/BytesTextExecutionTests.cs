using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BytesTextExecutionTests
{
    [Fact]
    public void SavedDecodeMethodsUseTheCurrentExecutionAfterTheirCreatingTokenIsCancelled()
    {
        var engine = new ManagedPythonEngine();
        using var previousCancellation = new CancellationTokenSource();
        using var previousOutput = new StringWriter();
        var setup = engine.Execute(
            "values = bytes([65, 226, 130, 172, 66])\ndecode = values.decode",
            "bytes_text_setup.py",
            previousOutput,
            cancellationToken: previousCancellation.Token
        );
        Assert.True(setup.Success);
        var original = Global(engine, "values");
        previousCancellation.Cancel();
        using var output = new StringWriter();
        var result = engine.Execute(
            "print(decode())\nprint(decode('utf-8', 'strict'))\nprint(str(values, 'utf-8'))",
            "bytes_text_current.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("A€B", "A€B", "A€B"), output.ToString());
        Assert.Empty(previousOutput.ToString());
        Assert.Same(original, Global(engine, "values"));
        Assert.Equal(
            new byte[] { 65, 226, 130, 172, 66 },
            Assert.IsType<PythonByteSequenceValue>(original).Value
        );
    }

    [Fact]
    public void DecodeAndRepresentationWorkChargeTheCurrentBudgetBeforePublishingResults()
    {
        foreach (
            var expression in new[]
            {
                "decode()",
                "str(values, 'ascii')",
                "repr(values)",
                "str(values)",
            }
        )
        {
            var engine = PrepareByteEngine("b'a' * 200000", out var previousOutput);
            using (previousOutput)
            {
                var originalTarget = Global(engine, "target");
                var originalValues = Global(engine, "values");
                using var output = new StringWriter();
                var result = engine.Execute(
                    "try:\n    target = "
                        + expression
                        + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                    "bytes_text_work_budget.py",
                    output,
                    new ManagedExecutionOptions { InstructionLimit = 100 },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
                Assert.Equal(Lines("cleanup"), output.ToString());
                Assert.Empty(previousOutput.ToString());
                Assert.Same(originalTarget, Global(engine, "target"));
                Assert.Same(originalValues, Global(engine, "values"));
                using var recoveredOutput = new StringWriter();
                var recovered = engine.Execute(
                    "target = " + expression + "\nprint(len(target))",
                    "bytes_text_work_recovery.py",
                    recoveredOutput,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                Assert.True(recovered.Success);
                Assert.Equal(
                    Lines(expression is "repr(values)" or "str(values)" ? "200003" : "200000"),
                    recoveredOutput.ToString()
                );
                Assert.Same(originalValues, Global(engine, "values"));
                Assert.All(
                    Assert.IsType<PythonByteSequenceValue>(originalValues).Value,
                    value => Assert.Equal((byte)'a', value)
                );
            }
        }
    }

    [Fact]
    public void InvalidByteReplacementScansChargeWorkAndKeepTheOriginalInputForRecovery()
    {
        foreach (var errors in new[] { "ignore", "replace", "backslashreplace" })
        {
            var engine = PrepareByteEngine("bytes([255]) * 100000", out var previousOutput);
            using (previousOutput)
            {
                var originalTarget = Global(engine, "target");
                var originalValues = Global(engine, "values");
                var expression = "decode('utf-8', '" + errors + "')";
                using var output = new StringWriter();
                var result = engine.Execute(
                    "try:\n    target = " + expression + "\nfinally:\n    print('cleanup')",
                    "bytes_text_invalid_budget.py",
                    output,
                    new ManagedExecutionOptions { InstructionLimit = 100 },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
                Assert.Equal(Lines("cleanup"), output.ToString());
                Assert.Empty(previousOutput.ToString());
                Assert.Same(originalTarget, Global(engine, "target"));
                using var recoveredOutput = new StringWriter();
                var recovered = engine.Execute(
                    "target = " + expression,
                    "bytes_text_invalid_recovery.py",
                    recoveredOutput,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                Assert.True(recovered.Success);
                var text = Assert.IsType<PythonTextValue>(Global(engine, "target")).Value;
                Assert.Equal(
                    errors switch
                    {
                        "ignore" => string.Empty,
                        "replace" => new string('\uFFFD', 100000),
                        _ => string.Concat(Enumerable.Repeat("\\xff", 100000)),
                    },
                    text
                );
                Assert.Same(originalValues, Global(engine, "values"));
                Assert.All(
                    Assert.IsType<PythonByteSequenceValue>(originalValues).Value,
                    value => Assert.Equal((byte)255, value)
                );
            }
        }
    }

    [Fact]
    public void StrictDecodingRaisesPythonErrorsAtTheCurrentSavedMethodCallSpan()
    {
        foreach (var encoding in new[] { "ascii", "utf-8", "utf-16-le" })
        {
            var engine = PrepareByteEngine("bytes([255])", out var previousOutput);
            using (previousOutput)
            {
                var original = Global(engine, "target");
                using var output = new StringWriter();
                var caught = engine.Execute(
                    "try:\n    target = decode('"
                        + encoding
                        + "')\nexcept UnicodeDecodeError as error:\n    print(isinstance(error, UnicodeError), isinstance(error, ValueError))",
                    "bytes_text_strict_caught.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                Assert.True(caught.Success);
                Assert.Equal(Lines("True True"), output.ToString());
                Assert.Same(original, Global(engine, "target"));
                const string padding = "padding = 123\n\n\n";
                var source = padding + "target = decode('" + encoding + "')";
                var failed = engine.Execute(
                    source,
                    "bytes_text_strict_current.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                var diagnostic = Assert.Single(failed.Diagnostics);
                Assert.Equal("bytes_text_strict_current.py", failed.Source.FilePath);
                Assert.Equal(
                    source.IndexOf("decode(", StringComparison.Ordinal),
                    diagnostic.Span.Start
                );
                Assert.Contains("can't decode", diagnostic.Message, StringComparison.Ordinal);
                Assert.Same(original, Global(engine, "target"));
                Assert.Empty(previousOutput.ToString());
                using var recoveredOutput = new StringWriter();
                var recovered = engine.Execute(
                    "target = decode('latin-1')\nprint(ord(target))",
                    "bytes_text_strict_recovery.py",
                    recoveredOutput,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                Assert.True(recovered.Success);
                Assert.Equal(Lines("255"), recoveredOutput.ToString());
            }
        }
    }

    [Fact]
    public void DecoderChunksPreserveMultibyteSequencesAndMalformedSequenceGrouping()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            valid = b'a' * 255 + bytes([226, 130, 172]) + b'b'
            utf8 = valid.decode()
            malformed = b'a' * 255 + bytes([226, 130, 40]) + b'b'
            replaced = malformed.decode('utf-8', 'replace')
            ignored = malformed.decode('utf-8', 'ignore')
            escaped = malformed.decode('utf-8', 'backslashreplace')
            utf16bytes = bytes([65, 0]) * 127 + bytes([61, 216, 0, 222]) + bytes([66, 0])
            utf16 = utf16bytes.decode('utf-16-le')
            utf16bad = bytes([65, 0]) * 127 + bytes([61, 216, 66, 0])
            utf16replaced = utf16bad.decode('utf-16-le', 'replace')
            utf16escaped = utf16bad.decode('utf-16-le', 'backslashreplace')
            """,
            "bytes_text_decoder_chunks.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(new string('a', 255) + "€b", Text(engine, "utf8"));
        Assert.Equal(new string('a', 255) + "\uFFFD(b", Text(engine, "replaced"));
        Assert.Equal(new string('a', 255) + "(b", Text(engine, "ignored"));
        Assert.Equal(new string('a', 255) + "\\xe2\\x82(b", Text(engine, "escaped"));
        Assert.Equal(new string('A', 127) + "😀B", Text(engine, "utf16"));
        Assert.Equal(new string('A', 127) + "\uFFFDB", Text(engine, "utf16replaced"));
        Assert.Equal(new string('A', 127) + "\\x3d\\xd8B", Text(engine, "utf16escaped"));
    }

    [Fact]
    public void RepresentationOutputCapRejectsEscapeExpansionWithoutChangingTheInputOrTarget()
    {
        var engine = PrepareByteEngine("bytes(2500000)", out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalTarget = Global(engine, "target");
            var originalValues = Global(engine, "values");
            var result = engine.Execute(
                """
                try: target = repr(values)
                except OverflowError as error: print(str(error))
                print(len(values))
                """,
                "bytes_text_representation_limit.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Equal(
                Lines("The bytes representation exceeds the supported size.", "2500000"),
                output.ToString()
            );
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalValues, Global(engine, "values"));
            Assert.Empty(previousOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "target = repr(values[:2])\nprint(target)",
                "bytes_text_representation_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(Lines("b'\\x00\\x00'"), recoveredOutput.ToString());
        }
    }

    [Fact]
    public void DecodedOutputCapRejectsEscapeExpansionBeforePublishingTheResult()
    {
        var engine = PrepareByteEngine("bytes([255]) * 2500001", out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalTarget = Global(engine, "target");
            var originalValues = Global(engine, "values");
            var result = engine.Execute(
                """
                try: target = decode('ascii', 'backslashreplace')
                except OverflowError as error: print(str(error))
                print(len(values))
                """,
                "bytes_text_decoded_limit.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Equal(
                Lines("The decoded text exceeds the supported size.", "2500001"),
                output.ToString()
            );
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalValues, Global(engine, "values"));
            Assert.Empty(previousOutput.ToString());
            Assert.All(
                Assert.IsType<PythonByteSequenceValue>(originalValues).Value,
                value => Assert.Equal((byte)255, value)
            );
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "target = values[:2].decode('ascii', 'backslashreplace')\nprint(target)",
                "bytes_text_decoded_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(Lines("\\xff\\xff"), recoveredOutput.ToString());
        }
    }

    private static ManagedPythonEngine PrepareByteEngine(
        string expression,
        out StringWriter previousOutput
    )
    {
        var engine = new ManagedPythonEngine();
        previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            "values = " + expression + "\ndecode = values.decode\ntarget = 'original'",
            "bytes_text_saved_setup.py",
            previousOutput,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        return engine;
    }

    private static string Text(ManagedPythonEngine engine, string name) =>
        Assert.IsType<PythonTextValue>(Global(engine, name)).Value;

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
}
