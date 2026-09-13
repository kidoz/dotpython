using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class EncodingErrorHandlerExecutionTests
{
    [Fact]
    public void SavedEncodeMethodsApplyHandlersInTheCurrentExecutionAfterSetupCancellation()
    {
        var engine = Prepare("'Aÿ😀B'", out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var original = Global(engine, "values");
            var result = engine.Execute(
                """
                print(encode('ascii', 'ignore'))
                print(encode('ascii', 'replace'))
                print(encode('ascii', 'backslashreplace'))
                print(encode('ascii', 'xmlcharrefreplace'))
                print(list(encode('latin-1', 'backslashreplace')))
                """,
                "encoding_handlers_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(result.Success);
            Assert.Equal(
                Lines(
                    "b'AB'",
                    "b'A??B'",
                    "b'A\\\\xff\\\\U0001f600B'",
                    "b'A&#255;&#128512;B'",
                    "[65, 255, 92, 85, 48, 48, 48, 49, 102, 54, 48, 48, 66]"
                ),
                output.ToString()
            );
            Assert.Empty(previousOutput.ToString());
            Assert.Same(original, Global(engine, "values"));
            Assert.Equal("Aÿ😀B", Assert.IsType<PythonTextValue>(original).Value);
        }
    }

    [Fact]
    public void InvalidCharacterScansChargeWorkForEveryRecoveryHandlerAndRecover()
    {
        foreach (
            var handler in new[] { "ignore", "replace", "backslashreplace", "xmlcharrefreplace" }
        )
        {
            var length = handler switch
            {
                "ignore" => 0,
                "replace" => 100000,
                "backslashreplace" => 1000000,
                _ => 900000,
            };
            AssertBudgetAndRecovery("'😀' * 100000", "ascii", handler, length);
        }
        AssertBudgetAndRecovery("chr(0xdc80) * 100000", "ascii", "surrogateescape", 100000);
        AssertBudgetAndRecovery("chr(0xd800) * 100000", "utf-8", "surrogatepass", 300000);
    }

    [Fact]
    public void ValidCharacterScansChargeWorkEvenWhenTheSelectedHandlerIsNeverNeeded()
    {
        foreach (
            var handler in new[]
            {
                "ignore",
                "replace",
                "backslashreplace",
                "xmlcharrefreplace",
                "surrogatepass",
                "surrogateescape",
                "missing",
            }
        )
            AssertBudgetAndRecovery("'a' * 200000", "utf-8", handler, 200000);
    }

    [Fact]
    public void EveryRecoveryHandlerCountsEncodedBytesAgainstTheSharedMaterializationCap()
    {
        foreach (
            var handler in new[]
            {
                "ignore",
                "replace",
                "backslashreplace",
                "xmlcharrefreplace",
                "surrogatepass",
                "surrogateescape",
            }
        )
            AssertCapAndRecovery("'😀' * 2500001", "utf-8", handler, "values[:-1]", 10000000);
    }

    [Fact]
    public void ReplacementExpansionCountsAllEscapeBytesAndAllowsTheLargestFittingPrefix()
    {
        AssertCapAndRecovery(
            "'😀' * 1111112",
            "ascii",
            "backslashreplace",
            "values[:1000000]",
            10000000
        );
        AssertCapAndRecovery(
            "'😀' * 1111112",
            "ascii",
            "xmlcharrefreplace",
            "values[:1111111]",
            9999999
        );
    }

    [Fact]
    public void NativeUtf16BomCountsAgainstTheCapBeforeAnyResultIsPublished()
    {
        var engine = AssertCapAndRecovery(
            "'a' * 5000000",
            "utf-16",
            "ignore",
            "values[:-1]",
            10000000
        );
        var encoded = Assert.IsType<PythonByteSequenceValue>(Global(engine, "target")).Value;
        Assert.Equal(new byte[] { 255, 254, 97, 0 }, encoded[..4]);
        Assert.Equal(new byte[] { 97, 0 }, encoded[^2..]);
    }

    [Fact]
    public void ChunkBoundariesPreserveSupplementaryInvalidRunsAndSurrogateRecoveryBytes()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            values = 'a' * 255 + '😀ÿ😀' + 'b'
            ignored = values.encode('ascii', 'ignore')
            replaced = values.encode('ascii', 'replace')
            escaped = values.encode('ascii', 'backslashreplace')
            xml = values.encode('ascii', 'xmlcharrefreplace')
            surrogate = 'a' * 255 + chr(0xdc80) + chr(0xdcff) + 'b'
            escaped_ascii = surrogate.encode('ascii', 'surrogateescape')
            escaped_latin = surrogate.encode('latin-1', 'surrogateescape')
            escaped_utf8 = surrogate.encode('utf-8', 'surrogateescape')
            passed_utf8 = ('a' * 255 + chr(0xd800) + 'b').encode('utf-8', 'surrogatepass')
            passed_utf16 = ('A' * 127 + chr(0xd800) + 'B').encode('utf-16-le', 'surrogatepass')
            try: surrogate.encode('utf-16-le', 'surrogateescape')
            except UnicodeEncodeError as error: print(error.start, error.end, error.object is surrogate)
            """,
            "encoding_handlers_chunks.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal(Lines("255 256 True"), output.ToString());
        Assert.Equal(
            System.Text.Encoding.ASCII.GetBytes(new string('a', 255) + "b"),
            Bytes(engine, "ignored")
        );
        Assert.Equal(
            System.Text.Encoding.ASCII.GetBytes(new string('a', 255) + "???b"),
            Bytes(engine, "replaced")
        );
        Assert.Equal(
            System.Text.Encoding.ASCII.GetBytes(
                new string('a', 255) + "\\U0001f600\\xff\\U0001f600b"
            ),
            Bytes(engine, "escaped")
        );
        Assert.Equal(
            System.Text.Encoding.ASCII.GetBytes(new string('a', 255) + "&#128512;&#255;&#128512;b"),
            Bytes(engine, "xml")
        );
        var raw = Enumerable.Repeat((byte)97, 255).Concat(new byte[] { 128, 255, 98 }).ToArray();
        foreach (var name in new[] { "escaped_ascii", "escaped_latin", "escaped_utf8" })
            Assert.Equal(raw, Bytes(engine, name));
        Assert.Equal(
            Enumerable.Repeat((byte)97, 255).Concat(new byte[] { 237, 160, 128, 98 }),
            Bytes(engine, "passed_utf8")
        );
        Assert.Equal(
            System
                .Text.Encoding.Unicode.GetBytes(new string('A', 127))
                .Concat(new byte[] { 0, 216, 66, 0 }),
            Bytes(engine, "passed_utf16")
        );
    }

    [Fact]
    public void SavedEncodeReportsCurrentHandlerLookupSpansAndRecoversAfterNameErrors()
    {
        var engine = Prepare("'A😀B'", out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalTarget = Global(engine, "target");
            const string source = "padding = 1\n\n\ntarget = encode('ascii', 'missing')";
            var failed = engine.Execute(
                source,
                "encoding_handlers_lookup_current.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            var diagnostic = Assert.Single(failed.Diagnostics);
            Assert.Equal("unknown error handler name 'missing'", diagnostic.Message);
            Assert.Equal(
                source.IndexOf("encode(", StringComparison.Ordinal),
                diagnostic.Span.Start
            );
            Assert.Equal("encoding_handlers_lookup_current.py", failed.Source.FilePath);
            Assert.Same(originalTarget, Global(engine, "target"));
            var recovered = engine.Execute(
                """
                print(list(encode('utf-8', 'missing')))
                for encoding, errors in (('ascii', 'strict\0'), ('ascii\0', 'ignore')):
                    try: target = encode(encoding, errors)
                    except ValueError as error: print(str(error))
                print(encode('ascii', 'ignore'))
                """,
                "encoding_handlers_lookup_recovery.py",
                output,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(
                Lines(
                    "[65, 240, 159, 152, 128, 66]",
                    "embedded null character",
                    "embedded null character",
                    "b'AB'"
                ),
                output.ToString()
            );
            Assert.Empty(previousOutput.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
        }
    }

    private static void AssertBudgetAndRecovery(
        string source,
        string encoding,
        string handler,
        int expectedLength
    )
    {
        var engine = Prepare(source, out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalTarget = Global(engine, "target");
            var originalSource = Global(engine, "values");
            var expression = "encode('" + encoding + "', '" + handler + "')";
            var limited = engine.Execute(
                "try:\n    target = "
                    + expression
                    + "\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
                "encoding_handlers_budget.py",
                output,
                new ManagedExecutionOptions { InstructionLimit = 100 },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
            Assert.Equal(Lines("cleanup"), output.ToString());
            Assert.Same(originalTarget, Global(engine, "target"));
            Assert.Same(originalSource, Global(engine, "values"));
            Assert.Empty(previousOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "target = " + expression,
                "encoding_handlers_budget_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(expectedLength, Bytes(engine, "target").Length);
            Assert.Same(originalSource, Global(engine, "values"));
        }
    }

    private static ManagedPythonEngine AssertCapAndRecovery(
        string source,
        string encoding,
        string handler,
        string recoverySource,
        int expectedLength
    )
    {
        var engine = Prepare(source, out var previousOutput);
        using (previousOutput)
        using (var output = new StringWriter())
        {
            var originalTarget = Global(engine, "target");
            var originalSource = Global(engine, "values");
            var limited = engine.Execute(
                "try: target = encode('"
                    + encoding
                    + "', '"
                    + handler
                    + "')\nexcept OverflowError as error: print(str(error))",
                "encoding_handlers_materialization_limit.py",
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
            Assert.Empty(previousOutput.ToString());
            using var recoveredOutput = new StringWriter();
            var recovered = engine.Execute(
                "target = (" + recoverySource + ").encode('" + encoding + "', '" + handler + "')",
                "encoding_handlers_materialization_recovery.py",
                recoveredOutput,
                cancellationToken: TestContext.Current.CancellationToken
            );
            Assert.True(recovered.Success);
            Assert.Equal(expectedLength, Bytes(engine, "target").Length);
            Assert.Same(originalSource, Global(engine, "values"));
        }
        return engine;
    }

    private static ManagedPythonEngine Prepare(string source, out StringWriter previousOutput)
    {
        var engine = new ManagedPythonEngine();
        previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var setup = engine.Execute(
            "values = " + source + "\nencode = values.encode\ntarget = 'original'",
            "encoding_handlers_saved_setup.py",
            previousOutput,
            cancellationToken: cancellation.Token
        );
        Assert.True(setup.Success);
        cancellation.Cancel();
        return engine;
    }

    private static byte[] Bytes(ManagedPythonEngine engine, string name) =>
        Assert.IsType<PythonByteSequenceValue>(Global(engine, name)).Value;

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
